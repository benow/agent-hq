using Dapper;
using System.Text.Json;
using AgentHQ.Core.Events;
using AgentHQ.Core.Exchange;
using AgentHQ.Core.Plans;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Stores;
using Microsoft.Data.Sqlite;

namespace AgentHQ.Infrastructure.Exchange;

/// <summary>The Exchange's one error shape: a code plus a sentence a caller can act on.</summary>
public sealed record ExchangeError(string Code, string Detail);

/// <summary>Outcome of a write: the item row as it is now, or an error code.</summary>
public sealed record ExchangeResult(ExchangeItem? Item, ExchangeError? Error, bool Deduplicated = false)
{
    public bool Ok => Error is null;
    public static ExchangeResult Fail(string code, string detail) => new(null, new ExchangeError(code, detail));
}

/// <summary>What a caller asks for when posting an item.</summary>
public sealed record PostRequest(
    ExchangeActor Sender,
    string Kind,
    ExchangeActor? Recipient,
    string? RecipientText,
    string? Repository,
    string? Subject,
    string? Purpose,
    string? Body,
    string? ContextRefs,
    DateTimeOffset? ExpiresAt,
    string? IdempotencyKey,
    string? LatencyClass);

/// <summary>One presence row as the dashboard renders it: offline is derived, never stored.</summary>
public sealed record PresenceView(string Actor, string LatencyClass, long LastSeenAt,
    long SecondsSinceSeen, bool Online);

/// <summary>An open question with its claimant, age and hop count (Andy's list).</summary>
public sealed record OpenQuestionView(ExchangeItem Item, int HopCount, string? ClaimedBy,
    long AgeSeconds);

/// <summary>
/// The only place an Exchange item moves state. REST endpoints, MCP tools, the hook digest and
/// the expiry sweep all call this service; none of them talk to the store directly.
///
/// Order inside every write is: item row, then event, inside one transaction. If the event
/// append fails the transaction rolls back, so a caller never sees a success for a row whose
/// event was lost. Events carry metadata only — never a body or a byte.
/// </summary>
public sealed class ExchangeService
{
    private readonly AgentHqDb _db;
    private readonly ExchangeStore _store;
    private readonly RepositoryStore _repositories;
    private readonly EventStore _events;
    private readonly BlobStore _blobs;
    private readonly ExchangeOptions _options;
    private readonly Serilog.ILogger _log;
    private readonly TimeProvider _time;

    public ExchangeService(AgentHqDb db, ExchangeStore store, RepositoryStore repositories, EventStore events,
        BlobStore blobs, ExchangeOptions options, TimeProvider? time = null, Serilog.ILogger? logger = null)
    {
        _db = db;
        _store = store;
        _repositories = repositories;
        _events = events;
        _blobs = blobs;
        _options = options;
        _time = time ?? TimeProvider.System;
        _log = (logger ?? Serilog.Log.ForContext<ExchangeService>()).ForContext("Subsystem", "exchange");
    }

    public ExchangeOptions Options => _options;
    public BlobStore Blobs => _blobs;

    private DateTimeOffset Now => _time.GetUtcNow();

    private sealed record TxOutcome(ExchangeItem? Item, ExchangeError? Error, bool Deduplicated,
        ExchangeItem? EventItem = null, string? EventType = null, object? EventPayload = null,
        string? Warning = null, AgentHqEvent? AppendedEvent = null);

    /// <summary>
    /// Runs <paramref name="work"/> against an open transaction; a thrown exception rolls back,
    /// and on success the event (if any) is appended in the same transaction before commit. Only
    /// after the commit are live subscribers told, so a rolled-back event never reaches SSE.
    /// </summary>
    private async Task<TxOutcome> InTransactionAsync(Func<SqliteConnection, SqliteTransaction, Task<TxOutcome>> work)
    {
        await using var conn = _db.Open();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        var outcome = await work(conn, tx);
        if (outcome.Error is null && outcome.EventType is not null)
        {
            try
            {
                outcome = outcome with { AppendedEvent = await _events.AppendAsync(outcome.EventType, outcome.EventPayload!, tx) };
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _log.Error(ex, "exchange event append failed for {Type}; rolled back", outcome.EventType);
                return new TxOutcome(null, new ExchangeError("illegal_transition",
                    $"the item was not saved because its {outcome.EventType} event could not be recorded; retry"), false);
            }
        }
        await tx.CommitAsync();
        if (outcome.AppendedEvent is not null) await _events.PublishedAfterCommitAsync(outcome.AppendedEvent);
        if (outcome.Warning is not null) _log.Warning("{Warning}", outcome.Warning);
        return outcome;
    }

    // ---- post ----

    /// <summary>
    /// Posts a question, message, file or status. Idempotency, rate cap and mention routing are
    /// decided here, inside one transaction.
    /// </summary>
    public async Task<ExchangeResult> PostAsync(PostRequest req, CancellationToken ct = default)
    {
        if (!ExchangeActorTypes.ValidateActorType(req.Sender.ActorType, out var actorError))
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.UnknownActor, actorError!);
        if (!ExchangeKinds.IsPostable(req.Kind))
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.IllegalTransition,
                $"kind must be one of question, message, file, status" +
                (req.Kind == ExchangeKinds.Answer ? " (answers are created by a reply)" : ""));

        var key = req.IdempotencyKey?.Trim();
        if (key is { Length: 0 }) key = null;
        if (key is { Length: > 200 })
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.BadIdempotencyKey,
                "idempotencyKey must be at most 200 characters");
        if (req.Body is { } body && System.Text.Encoding.UTF8.GetByteCount(body) > _options.BodyMaxBytes)
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.BodyTooLarge,
                $"body is {System.Text.Encoding.UTF8.GetByteCount(body)} bytes; the limit is {_options.BodyMaxBytes}");
        if (req.Kind == ExchangeKinds.Question && string.IsNullOrWhiteSpace(req.Purpose))
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.PurposeRequired,
                "a question needs a purpose: say what the answer will unblock");

        // A subject is one line: in the digest it sits between fields, so a newline in a subject
        // would forge a second line of server structure. It is hashed down to one line for storage.
        var subject = OneLine(req.Subject);
        if (subject.Length > _options.SubjectMaxChars) subject = subject[.._options.SubjectMaxChars];
        if (subject.Length == 0)
        {
            // The only path body text becomes a digest field is this fallback (§2.7: a body
            // containing an instruction-shaped string can never appear in digest output), so a
            // marker-bearing first line is not used — the item is titled generically instead.
            var fallback = OneLine(FirstLine(req.Body));
            var carriesMarker = ExchangeDigest.ForbiddenMarkers.Any(m =>
                fallback.Contains(m, StringComparison.OrdinalIgnoreCase));
            subject = fallback is { Length: > 0 } && !carriesMarker ? fallback : $"(untitled {req.Kind})";
        }

        var latency = string.IsNullOrWhiteSpace(req.LatencyClass) ? ExchangeLatency.Turn : req.LatencyClass.Trim();
        if (!ExchangeLatency.IsKnown(latency))
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.IllegalTransition,
                $"latencyClass must be one of {string.Join(", ", ExchangeLatency.All)}");

        // ---- recipient resolution: explicit, mentions, or refused (never a broadcast) ----
        ExchangeActor? recipient = req.Recipient;
        string? recipientText = req.RecipientText;
        var mentionedTypes = MentionedTypes(req.Body);
        if (recipient is null && !string.IsNullOrWhiteSpace(recipientText))
        {
            if (!ExchangeActor.TryParse(recipientText, out var parsed, out var parseError))
                return ExchangeResult.Fail(ExchangeStateMachine.Codes.UnknownActor, parseError!);
            recipient = parsed;
        }
        if (recipient is null && req.Kind is ExchangeKinds.Message or ExchangeKinds.Status && mentionedTypes.Count > 0)
            recipient = ExchangeActorTypes.Canonical(mentionedTypes[^1]); // last mention wins
        if (recipient is null)
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.UnknownActor,
                "a recipient is required: pass recipient as type:id (andy:andy, openclaw:linda_v2, " +
                "linda_v1:linda_v1, zcode:<session id>, or zcode:* for any session in a repository with 'repository' set)");

        RepositoryRow? repo = null;
        if (!string.IsNullOrWhiteSpace(req.Repository))
        {
            repo = await _repositories.GetAsync(req.Repository.Trim());
            if (repo is null)
                return ExchangeResult.Fail(ExchangeStateMachine.Codes.NotFound, $"unknown repository '{req.Repository}'");
        }
        // Any row addressed to zcode (role zcode:* or a concrete session) carries the repository
        // it is scoped to; every other recipient carries none.
        var ctx = req.ContextRefs?.Trim();
        if (ctx is { Length: > 0 })
        {
            if (!IsJsonObject(ctx))
                return ExchangeResult.Fail(ExchangeStateMachine.Codes.IllegalTransition,
                    "contextRefs must be a JSON object, e.g. {\"repo\":\"<repository id>\",\"plan\":\"<plan id>\"}");
        }
        else ctx = null;
        var ctxPending = RepositoryInContext(ctx);
        RepositoryRow? scopeRepo = recipient.Value.ActorType == ExchangeActorTypes.ZCode ? repo : null;
        if (recipient.Value.ActorType == ExchangeActorTypes.ZCode && scopeRepo is null &&
            !string.IsNullOrWhiteSpace(ctxPending = RepositoryInContext(req.ContextRefs)))
            scopeRepo = await _repositories.GetAsync(ctxPending);

        // D13 / schema rule: an item whose recipient is zcode must carry the repository it routes
        // by (role zcode:* uses it to find sessions; a concrete session id is scoped to it). It
        // defaults to the named repository, then to contextRefs.repo; otherwise it is refused.
        if (recipient.Value.ActorType == ExchangeActorTypes.ZCode && scopeRepo is null)
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.UnknownActor,
                "an item addressed to zcode needs the repository it is scoped to: pass 'repository' " +
                "(the registered repository id) or contextRefs {\"repo\":\"<repository id>\"}");

        var now = Now;
        var senderKey = new ExchangeStore.ExchangeActorKey(req.Sender.ActorType, req.Sender.ActorId);
        var windowStart = now.AddMinutes(-1).ToUnixTimeMilliseconds();
        var nowMs = now.ToUnixTimeMilliseconds();

        var outcome = await InTransactionAsync(async (conn, tx) =>
        {
            if (key is not null)
            {
                var original = await _store.FindByIdempotencyAsync(senderKey, key, tx);
                if (original is not null)
                    return new TxOutcome(original.ToModel(), null, true);
            }

            if (!req.Sender.IsAndy)
            {
                var recent = await _store.CountRecentPostsAsync(senderKey, windowStart, tx);
                if (recent >= _options.RatePerMinute)
                {
                    var oldest = await _store.OldestRecentPostAsync(senderKey, windowStart, tx);
                    var retry = oldest is { } o
                        ? Math.Max(1, (long)Math.Ceiling((o + 60_000 - nowMs) / 1000.0))
                        : 60;
                    return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.RateLimited,
                        $"{req.Sender.ToDisplayString()} posted {recent} items in the last minute; the limit is " +
                        $"{_options.RatePerMinute}. Retry in {retry}s"), false);
                }
            }

            if (mentionedTypes.Count > 0)
                _log.Information("exchange mention routing {Sender} → {Mentions} (addressed to {Recipient})",
                    req.Sender.ToDisplayString(), string.Join(",", mentionedTypes), recipient.Value.ToDisplayString());

            var id = Guid.NewGuid().ToString();
            var state = ExchangeStateMachine.InitialState(req.Kind);
            var row = new ExchangeItemRow
            {
                Id = id,
                Kind = req.Kind,
                State = state,
                LatencyClass = latency,
                ThreadId = id,
                HopCount = 0,
                SenderActorType = req.Sender.ActorType,
                SenderActorId = req.Sender.ActorId,
                RecipientActorType = recipient.Value.ActorType,
                RecipientActorId = recipient.Value.ActorId,
                RepositoryId = scopeRepo?.Id,
                Subject = subject,
                Body = req.Body,
                ContextRefs = ctx ?? (scopeRepo is not null ? JsonSerializer.Serialize(new { repo = scopeRepo.Id }) : null),
                Purpose = string.IsNullOrWhiteSpace(req.Purpose) ? null : req.Purpose.Trim(),
                CreatedAt = nowMs,
                ExpiresAt = req.ExpiresAt?.ToUnixTimeMilliseconds(),
                IdempotencyKey = key
            };
            await _store.InsertAsync(row, tx);
            await _store.RecordPostAsync(senderKey, nowMs, now.AddMinutes(-5).ToUnixTimeMilliseconds(), tx);
            return new TxOutcome(row.ToModel(), null, false, row.ToModel(), EventTypes.ExchangeCreated,
                CreatedPayload(row));
        });
        var result = new ExchangeResult(outcome.Item, outcome.Error, outcome.Deduplicated);
        if (outcome.Item is not null && !outcome.Deduplicated)
            _log.Information("exchange created {Id} {Kind} {Sender} → {Recipient}",
                outcome.Item.Id, outcome.Item.Kind, outcome.Item.Sender.ToDisplayString(),
                outcome.Item.Recipient.ToDisplayString());
        return result;
    }

    /// <summary>
    /// Routes a <c>cwd</c> to its repository through the same longest-prefix rule every other
    /// path uses (D13). The new code calls this; it never re-implements the prefix rule.
    /// </summary>
    public Task<RepositoryRow?> ResolveRepositoryByCwdAsync(string cwd) => _repositories.ResolveByCwdAsync(cwd);

    // ---- reply ----

    /// <summary>
    /// Replies to a question (creates an answer item on the same thread). Only the claimant may
    /// reply to a claimed question; nobody may reply after the thread is killed; the fourth hop
    /// is refused with <c>hop_limit</c> naming the thread.
    /// </summary>
    public async Task<ExchangeResult> ReplyAsync(string parentId, ExchangeActor sender, string? subject,
        string? body, string? idempotencyKey, CancellationToken ct = default)
    {
        if (!ExchangeActorTypes.ValidateActorType(sender.ActorType, out var actorError))
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.UnknownActor, actorError!);
        if (body is { } b && System.Text.Encoding.UTF8.GetByteCount(b) > _options.BodyMaxBytes)
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.BodyTooLarge,
                $"body is {System.Text.Encoding.UTF8.GetByteCount(b)} bytes; the limit is {_options.BodyMaxBytes}");
        var key = idempotencyKey?.Trim();
        if (key is { Length: 0 }) key = null;
        if (key is { Length: > 200 })
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.BadIdempotencyKey, "idempotencyKey must be at most 200 characters");

        var now = Now;
        var nowMs = now.ToUnixTimeMilliseconds();
        var senderKey = new ExchangeStore.ExchangeActorKey(sender.ActorType, sender.ActorId);
        var windowStart = now.AddMinutes(-1).ToUnixTimeMilliseconds();

        ExchangeItem? newAnswer = null;
        ExchangeItem? parentItem = null;
        var outcome = await InTransactionAsync(async (conn, tx) =>
        {
            if (key is not null)
            {
                var original = await _store.FindByIdempotencyAsync(senderKey, key, tx);
                if (original is not null)
                    return new TxOutcome(original.ToModel(), null, true);
            }
            var row = await _store.GetAsync(parentId, tx);
            if (row is null)
                return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {parentId}"), false);
            parentItem = row.ToModel();

            var thread = await ThreadRowsAsync(tx, row.ThreadId);
            var root = thread[0].ToModel();
            var isKilled = root.State == ExchangeStates.Killed || thread.Any(t => t.State == ExchangeStates.Killed);
            if (parentItem.Kind is not (ExchangeKinds.Question or ExchangeKinds.Answer))
                return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.IllegalTransition,
                    $"item {parentId} is a {parentItem.Kind}; only a question or an answer in its thread takes a reply"), false);
            // The claimant rule (D14) guards the whole thread from the claim onwards, not just the
            // root row. While the question is claimed only the claimant may reply; once it is
            // answered the thread's depth still belongs to its two parties — the claimant and the
            // asker. Anyone else is a third party injecting into a conversation that is not theirs.
            var rootQuestion = thread.Select(r => r.ToModel())
                .FirstOrDefault(r => r.Kind == ExchangeKinds.Question && r.ClaimedBySessionId is not null);
            if (rootQuestion is not null)
            {
                var isClaimant = sender.ActorType == ExchangeActorTypes.ZCode &&
                                 sender.ActorId == rootQuestion.ClaimedBySessionId;
                var isAsker = sender.ActorType == rootQuestion.Sender.ActorType &&
                              sender.ActorId == rootQuestion.Sender.ActorId;
                var allowed = rootQuestion.State == ExchangeStates.Claimed
                    ? isClaimant
                    : rootQuestion.State == ExchangeStates.Answered && (isClaimant || isAsker);
                if (!allowed)
                    return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.IllegalTransition,
                        rootQuestion.State == ExchangeStates.Claimed
                            ? $"question {rootQuestion.Id} is claimed by {ExchangeActorTypes.ZCode}:{rootQuestion.ClaimedBySessionId}; only the claimant may reply"
                            : $"thread {parentItem.ThreadId} belongs to {ExchangeActorTypes.ZCode}:{rootQuestion.ClaimedBySessionId} and {rootQuestion.Sender.ToDisplayString()}; reply from the asker's side instead"), false);
            }

            // Any hop of a question thread is a reply to the asker; only the question itself moves
            // to answered (an answer row is delivered mail, not a second question). The state
            // machine's reply edge (queued/claimed → answered) is the question's edge.
            var decision = parentItem.Kind == ExchangeKinds.Question
                ? ExchangeStateMachine.Evaluate(parentItem, ExchangeStateMachine.Actions.Reply, now, isKilled)
                : isKilled ? ExchangeStateMachine.Decision.Refuse(ExchangeStateMachine.Codes.ThreadKilled,
                    $"thread {parentItem.ThreadId} is killed; no reply may enter it")
                : ExchangeStateMachine.Decision.Allow(ExchangeStates.Delivered);
            if (!decision.Allowed)
                return new TxOutcome(null, new ExchangeError(decision.Code!, decision.Detail!), false);

            if (ExchangeStateMachine.IsHopOverLimit(parentItem.HopCount, _options.HopLimit, out _))
                return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.HopLimit,
                    $"thread {parentItem.ThreadId} is at its hop limit ({_options.HopLimit} replies to the root); " +
                    "continue in a new question rather than a deeper reply"), false);

            if (!sender.IsAndy)
            {
                var recent = await _store.CountRecentPostsAsync(senderKey, windowStart, tx);
                if (recent >= _options.RatePerMinute)
                {
                    var oldest = await _store.OldestRecentPostAsync(senderKey, windowStart, tx);
                    var retry = oldest is { } o ? Math.Max(1, (long)Math.Ceiling((o + 60_000 - nowMs) / 1000.0)) : 60;
                    return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.RateLimited,
                        $"{sender.ToDisplayString()} posted {recent} items in the last minute; the limit is " +
                        $"{_options.RatePerMinute}. Retry in {retry}s"), false);
                }
            }

            // Who receives the answer: the question's asker when this hop answers the question,
            // otherwise the actor whose row is being replied to. A role recipient (zcode:*) is
            // never inherited: it is an address for new work, not a person to answer back to.
            var (recipientType, recipientId) = parentItem.Kind == ExchangeKinds.Question
                ? (root.Sender.ActorType, root.Sender.ActorId)
                : (parentItem.Sender.ActorType, parentItem.Sender.ActorId);
            if (recipientType == ExchangeActorTypes.ZCode && recipientId == ExchangeActorTypes.RoleWildcard)
                (recipientType, recipientId) = (sender.ActorType, sender.ActorId);
            // The schema requires a repository on any row whose recipient is zcode (a concrete
            // session id included), so an answer to a session carries the thread's repository.
            var answerRecipientIsZCode = recipientType == ExchangeActorTypes.ZCode;
            var answerRepoId = answerRecipientIsZCode ? (root.RepositoryId ?? thread.First().RepositoryId) : null;

            // The answer is its own row; the question moves to answered in the same transaction.
            var answer = new ExchangeItemRow
            {
                Id = Guid.NewGuid().ToString(),
                Kind = ExchangeKinds.Answer,
                State = ExchangeStates.Delivered,
                LatencyClass = parentItem.LatencyClass,
                ThreadId = parentItem.ThreadId,
                HopCount = parentItem.HopCount + 1,
                SenderActorType = sender.ActorType,
                SenderActorId = sender.ActorId,
                // An answer goes to whoever should read it: the question's asker when this hop
                // answers the question, otherwise the partner on the other side of the hop (the
                // row being replied to). Either way it never inherits the role recipient zcode:*.
                RecipientActorType = recipientType,
                RecipientActorId = recipientId,
                RepositoryId = answerRepoId,
                Subject = string.IsNullOrWhiteSpace(subject)
                    ? $"Re: {parentItem.Subject}"
                    : OneLine(subject)[..Math.Min(OneLine(subject).Length, _options.SubjectMaxChars)],
                Body = body,
                ContextRefs = root.ContextRefs,
                Purpose = root.Purpose,
                CreatedAt = nowMs,
                // An answer is live mail in its own right: it is not born expired because the
                // question it answers had a deadline (an expired answer leaves every digest at once).
                ExpiresAt = null,
                IdempotencyKey = key
            };
            await _store.InsertAsync(answer, tx);
            if (parentItem.Kind == ExchangeKinds.Question)
            {
                var moved = await _store.TransitionAsync(parentItem.Id, parentItem.State, ExchangeStates.Answered,
                    null, nowMs, answered: true, tx: tx);
                if (!moved)
                    return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.IllegalTransition,
                        $"question {parentId} changed state while the reply was being written; re-read it and retry"), false);
            }
            await _store.RecordPostAsync(senderKey, nowMs, now.AddMinutes(-5).ToUnixTimeMilliseconds(), tx);
            newAnswer = answer.ToModel();
            return new TxOutcome(newAnswer, null, false, newAnswer, EventTypes.ExchangeAnswered,
                StatePayload(answer, "answered"));
        });
        // A refusal carries the parent row back so the caller can report who holds it (the
        // already-claimed 409 the REST route builds needs ClaimedBy, not just the code).
        if (outcome.Error is not null) return new ExchangeResult(parentItem, outcome.Error);
        return new ExchangeResult(outcome.Item, null, outcome.Deduplicated);
    }

    // ---- claim / ack / kill ----

    public async Task<ExchangeResult> ClaimAsync(string id, ExchangeActor claimant, CancellationToken ct = default)
    {
        var now = Now;
        var nowMs = now.ToUnixTimeMilliseconds();
        var outcome = await InTransactionAsync(async (conn, tx) =>
        {
            var row = await _store.GetAsync(id, tx);
            if (row is null) return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {id}"), false);
            var item = row.ToModel();
            if (item.State == ExchangeStates.Claimed)
                return new TxOutcome(item, new ExchangeError(ExchangeStateMachine.Codes.AlreadyClaimed,
                    $"already claimed by {ExchangeActorTypes.ZCode}:{item.ClaimedBySessionId}"), false);
            var decision = ExchangeStateMachine.Evaluate(item, ExchangeStateMachine.Actions.Claim, now, false);
            if (!decision.Allowed)
                return new TxOutcome(null, new ExchangeError(decision.Code!, decision.Detail!), false);
            var moved = await _store.TransitionAsync(id, item.State, decision.To!, claimant.ActorId, nowMs, claim: true, tx: tx);
            if (!moved)
            {
                var current = (await _store.GetAsync(id, tx))!;
                return new TxOutcome(current.ToModel(), new ExchangeError(ExchangeStateMachine.Codes.AlreadyClaimed,
                    $"already claimed by {ExchangeActorTypes.ZCode}:{current.ClaimedBySessionId}"), false);
            }
            var updated = (await _store.GetAsync(id, tx))!;
            return new TxOutcome(updated.ToModel(), null, false, updated.ToModel(), EventTypes.ExchangeClaimed,
                StatePayload(updated, "claimed"));
        });
        return new ExchangeResult(outcome.Item, outcome.Error, outcome.Deduplicated);
    }

    public async Task<ExchangeResult> AckAsync(string id, ExchangeActor by, CancellationToken ct = default)
    {
        var now = Now;
        var nowMs = now.ToUnixTimeMilliseconds();
        var outcome = await InTransactionAsync(async (conn, tx) =>
        {
            var row = await _store.GetAsync(id, tx);
            if (row is null) return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {id}"), false);
            var item = row.ToModel();
            if (item.IsRoot && item.Kind == ExchangeKinds.Question)
            {
                // Acknowledging a question acknowledges the whole thread: the chain's end is the answer.
                var threadRows = await ThreadRowsAsync(tx, item.ThreadId);
                var next = threadRows.Where(r => r.Id != item.Id && r.State != ExchangeStates.Acknowledged)
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
                if (next is not null && item.State == ExchangeStates.Answered)
                {
                    var nextItem = next.ToModel();
                    var nextDecision = ExchangeStateMachine.Evaluate(nextItem, ExchangeStateMachine.Actions.Ack, now, false);
                    if (nextDecision.Allowed)
                    {
                        await _store.TransitionAsync(nextItem.Id, nextItem.State, nextDecision.To!, null, nowMs, acknowledged: true, tx: tx);
                        var nextUpdated = (await _store.GetAsync(nextItem.Id, tx))!;
                        return new TxOutcome(nextUpdated.ToModel(), null, false, nextUpdated.ToModel(),
                            EventTypes.ExchangeAcknowledged, StatePayload(nextUpdated, "acknowledged"));
                    }
                }
            }
            var decision = ExchangeStateMachine.Evaluate(item, ExchangeStateMachine.Actions.Ack, now, false);
            if (!decision.Allowed)
            {
                // An item whose expiry has passed but that the sweep has not reached yet is
                // expired first (it already left every digest), then acknowledged — that edge exists.
                if (item.ExpiresAt is { } exp && now > exp && item.State is ExchangeStates.Queued or ExchangeStates.Claimed)
                {
                    var expiry = ExchangeStateMachine.Evaluate(item, ExchangeStateMachine.Actions.Expire, now, false);
                    if (expiry.Allowed)
                    {
                        await _store.TransitionAsync(id, item.State, expiry.To!, null, nowMs, tx: tx);
                        item = (await _store.GetAsync(id, tx))!.ToModel();
                        decision = ExchangeStateMachine.Evaluate(item, ExchangeStateMachine.Actions.Ack, now, false);
                    }
                }
                if (!decision.Allowed)
                    return new TxOutcome(null, new ExchangeError(decision.Code!, decision.Detail!), false);
            }
            var moved = await _store.TransitionAsync(id, item.State, decision.To!, null, nowMs, acknowledged: true, tx: tx);
            if (!moved)
                return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.IllegalTransition,
                    $"item {id} changed state while it was being acknowledged; re-read it"), false);
            var updated = (await _store.GetAsync(id, tx))!;
            return new TxOutcome(updated.ToModel(), null, false, updated.ToModel(),
                EventTypes.ExchangeAcknowledged, StatePayload(updated, "acknowledged"));
        });
        return new ExchangeResult(outcome.Item, outcome.Error);
    }

    /// <summary>Andy's kill switch. The whole thread dies together.</summary>
    public async Task<ExchangeResult> KillAsync(string id, ExchangeActor by, string? reason, CancellationToken ct = default)
    {
        if (!by.IsAndy)
            return ExchangeResult.Fail(ExchangeStateMachine.Codes.ScopeDenied, "only andy may kill an item");
        var nowMs = Now.ToUnixTimeMilliseconds();
        ExchangeItemRow? killedRoot = null;
        var outcome = await InTransactionAsync(async (conn, tx) =>
        {
            var row = await _store.GetAsync(id, tx);
            if (row is null) return new TxOutcome(null, new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {id}"), false);
            var decision = ExchangeStateMachine.Evaluate(row.ToModel(), ExchangeStateMachine.Actions.Kill, Now, false);
            if (!decision.Allowed)
                return new TxOutcome(null, new ExchangeError(decision.Code!, decision.Detail!), false);
            var threadRows = await ThreadRowsAsync(tx, row.ThreadId);
            foreach (var member in threadRows.Where(r => r.State != ExchangeStates.Acknowledged && r.State != ExchangeStates.Killed))
                await _store.TransitionAsync(member.Id, member.State, ExchangeStates.Killed, null, nowMs, killed: true, tx: tx);
            killedRoot = (await _store.GetAsync(row.ThreadId, tx))!;
            var payload = StatePayload(killedRoot, "killed");
            return new TxOutcome(killedRoot.ToModel(), null, false, killedRoot.ToModel(), EventTypes.ExchangeKilled,
                new
                {
                    id = killedRoot.Id, threadId = killedRoot.ThreadId, kind = killedRoot.Kind,
                    state = "killed", by = by.ToDisplayString(), reason
                });
        });
        if (outcome.Error is not null) return new ExchangeResult(null, outcome.Error);
        _log.Information("exchange thread {Thread} killed by andy ({Reason})", killedRoot?.ThreadId, reason);
        return new ExchangeResult(outcome.Item, null);
    }

    /// <summary>Actor-level kill: every non-terminal thread the item's sender opened.</summary>
    public async Task<IReadOnlyList<ExchangeItem>> KillSenderAsync(string senderText, string? reason, CancellationToken ct = default)
    {
        if (!ExchangeActor.TryParse(senderText, out var sender, out _))
            return Array.Empty<ExchangeItem>();
        var killed = new List<ExchangeItem>();
        await using var conn = _db.Open();
        var roots = (await conn.QueryAsync<ExchangeItemRow>(
            @"SELECT id AS Id FROM exchange_items WHERE sender_actor_type = @T AND sender_actor_id = @I AND thread_id = id",
            new { T = sender.ActorType, I = sender.ActorId })).ToList();
        foreach (var root in roots)
        {
            var res = await KillAsync(root.Id, ExchangeActor.Andy, reason, ct);
            if (res.Ok) killed.Add(res.Item!);
        }
        return killed;
    }

    // ---- expiry ----

    /// <summary>
    /// Expiry sweep: runs at read time (inbox, digest, open list) and on the host's 60 s timer.
    /// Expired items leave every digest, emit <c>exchange.expired</c>, and stay readable by id.
    /// </summary>
    public async Task<int> SweepExpiredAsync(CancellationToken ct = default)
    {
        var due = await _store.DueForExpiryAsync(Now.ToUnixTimeMilliseconds());
        var expired = 0;
        foreach (var row in due)
        {
            var nowMs = Now.ToUnixTimeMilliseconds();
            var outcome = await InTransactionAsync(async (conn, tx) =>
            {
                var fresh = await _store.GetAsync(row.Id, tx);
                if (fresh is null) return new TxOutcome(null, new ExchangeError("not_found", "gone"), false);
                var decision = ExchangeStateMachine.Evaluate(fresh.ToModel(), ExchangeStateMachine.Actions.Expire, Now, false);
                if (!decision.Allowed) return new TxOutcome(null, new ExchangeError(decision.Code!, decision.Detail!), false);
                var moved = await _store.TransitionAsync(fresh.Id, fresh.State, ExchangeStates.Expired, null, nowMs, tx: tx);
                if (!moved) return new TxOutcome(null, new ExchangeError("illegal_transition", "raced"), false);
                return new TxOutcome(null, null, false, fresh.ToModel(), EventTypes.ExchangeExpired, StatePayload(fresh, "expired"));
            });
            if (outcome.Error is null) expired++;
        }
        if (expired > 0) _log.Information("exchange expiry sweep: {Count} item(s) expired", expired);
        return expired;
    }

    // ---- cursors and watch ----

    public async Task<ExchangeCursorRow?> GetCursorAsync(ExchangeActor actor) =>
        await _store.GetCursorAsync(new ExchangeStore.ExchangeActorKey(actor.ActorType, actor.ActorId));

    /// <summary>
    /// Advances a consumer's cursor. Moving it backwards is a request to replay, so it clears the
    /// stored cursor instead of rewriting it; <c>cleared</c> tells the caller that happened.
    /// </summary>
    public async Task<(long Set, bool Cleared)> SetCursorAsync(ExchangeActor actor, long lastSeq)
    {
        var key = new ExchangeStore.ExchangeActorKey(actor.ActorType, actor.ActorId);
        var current = await _store.GetCursorAsync(key);
        if (lastSeq < 0) return (current?.LastSeq ?? 0, false);
        if (lastSeq == 0)
        {
            await _store.ClearCursorAsync(key);
            return (0, true);
        }
        await _store.SetCursorAsync(key, lastSeq, Now.ToUnixTimeMilliseconds());
        return (lastSeq, false);
    }

    /// <summary>
    /// Catch-up starting point for a consumer whose cursor is <paramref name="fromSeq"/>. When the
    /// cursor is older than the replay window (D8), an <c>exchange.resync</c> event is appended
    /// first, announcing the gap and the floor it resumes from — the catch-up is never silent.
    /// </summary>
    public async Task<(bool IsResync, long FromSeq)> PrepareCatchUpAsync(long fromSeq)
    {
        var windowMs = _options.EventReplayWindowSeconds * 1000;
        var oldest = await _events.OldestSeqSinceAsync(Now.AddMilliseconds(-windowMs).ToUnixTimeMilliseconds());
        if (oldest is null || fromSeq >= oldest.Value) return (false, fromSeq);
        var resync = await _events.AppendAsync(EventTypes.ExchangeResync, new
        {
            requestedFromSeq = fromSeq, resumesAtSeq = oldest.Value,
            windowSeconds = _options.EventReplayWindowSeconds,
            reason = "cursor is older than the exchange replay window"
        });
        return (true, resync.Seq - 1);
    }

    /// <summary>Records a blob's metadata on its item after a verified, complete deposit.</summary>
    public async Task SaveBlobAsync(string itemId, string sha256, long size, string contentType) =>
        await _store.SetBlobAsync(itemId, _blobs.PathForItem(itemId), size, contentType, sha256);

    /// <summary>A blob whose magic bytes contradict its declared type is stored sniffed and
    /// logged here, naming the item, so a mislabelled file is diagnosable rather than silent.</summary>
    public void BlobsLogWarning(string itemId, string declared, string sniffed) =>
        _log.Warning("blob {ItemId}: declared content type {Declared} does not match the bytes ({Sniffed}); stored as {Sniffed}",
            itemId, declared, sniffed, sniffed);

    // ---- reads ----

    public async Task<ExchangeItem?> GetAsync(string id, CancellationToken ct = default)
    {
        await SweepExpiredAsync(ct);
        return (await _store.GetAsync(id))?.ToModel();
    }

    public async Task<IReadOnlyList<ExchangeItem>> ThreadAsync(string threadId, CancellationToken ct = default)
    {
        await SweepExpiredAsync(ct);
        return (await _store.ThreadAsync(threadId)).Select(r => r.ToModel()).ToList();
    }

    /// <summary>
    /// A consumer's inbox. <paramref name="actor"/> is the reader: it sees items addressed to it,
    /// and — for a ZCode session — role-addressed items in the repositories its cwd resolves to.
    /// </summary>
    public async Task<IReadOnlyList<ExchangeItem>> InboxAsync(ExchangeActor actor, string? repository,
        string? states, string? kinds, int limit, CancellationToken ct = default)
    {
        await SweepExpiredAsync(ct);
        var repos = await RepositoriesForAsync(actor, repository);
        var stateList = string.IsNullOrWhiteSpace(states)
            ? null
            : states.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kind = string.IsNullOrWhiteSpace(kinds) ? null : kinds.Trim();
        var rows = await _store.InboxAsync(new ExchangeStore.ExchangeActorKey(actor.ActorType, actor.ActorId),
            repos, stateList, kind, limit);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<OpenQuestionView>> OpenAsync(string? repository, CancellationToken ct = default)
    {
        await SweepExpiredAsync(ct);
        var now = Now;
        var repoId = repository is null ? null : (await _repositories.GetAsync(repository))?.Id ?? repository;
        var rows = await _store.OpenQuestionsAsync(repoId);
        var nowMs = now.ToUnixTimeMilliseconds();
        var hopsByThread = new Dictionary<string, int>();
        foreach (var r in rows)
        {
            if (hopsByThread.ContainsKey(r.ThreadId)) continue;
            var thread = await _store.ThreadAsync(r.ThreadId);
            hopsByThread[r.ThreadId] = thread.Count == 0 ? 0 : thread.Max(t => (int)t.HopCount);
        }
        return rows.Select(r => new OpenQuestionView(r.ToModel(), hopsByThread[r.ThreadId],
            r.ClaimedBySessionId, Math.Max(0, (nowMs - r.CreatedAt) / 1000))).ToList();
    }

    public async Task<IReadOnlyList<PresenceView>> PresenceAsync(CancellationToken ct = default)
    {
        // Presence is a heartbeat: an actor past the staleness window is offline, derived here.
        var now = Now;
        var rows = await _store.PresenceAsync();
        return rows.Select(r =>
        {
            var seconds = Math.Max(0, (now.ToUnixTimeMilliseconds() - r.LastSeenAt) / 1000);
            return new PresenceView($"{r.ActorType}:{r.ActorId}", r.LatencyClass, r.LastSeenAt, seconds,
                seconds <= _options.PresenceStaleSeconds);
        }).ToList();
    }

    public async Task HeartbeatAsync(ExchangeActor actor, string latencyClass, CancellationToken ct = default)
    {
        if (!ExchangeLatency.IsKnown(latencyClass)) latencyClass = ExchangeLatency.Minutes;
        await _store.UpsertPresenceAsync(new ExchangeStore.ExchangeActorKey(actor.ActorType, actor.ActorId),
            latencyClass, Now.ToUnixTimeMilliseconds());
    }

    // ---- digest ----

    /// <summary>
    /// The text a ZCode hook prints for one session (D6, D15). Empty string when nothing is
    /// addressed to the session. The caller is filtered out of its own inbox — a session that
    /// posted a question never sees it in its own digest — and closed threads (killed) render as
    /// killed, never as open.
    /// </summary>
    public async Task<string> DigestAsync(string sessionId, string cwd, CancellationToken ct = default)
    {
        var repo = await ResolveRepositoryByCwdAsync(cwd);
        if (repo is null) return "";
        await SweepExpiredAsync(ct);
        var rows = await _store.DigestItemsAsync(sessionId, repo.Id);
        var now = Now;
        // Closed threads leave the digest: acknowledged and expired never render. A killed item
        // stays visible as killed (criterion 6) — it is how a session learns to stop replying.
        // A role-addressed question claimed by another session is a claim loser's view of mail
        // that is no longer theirs: it is never re-injected as open (D14, criterion 17).
        // A session's own posts never come back to it (D6): the digest is what others ask of it.
        var visible = rows
            .Where(r => r.State is not (ExchangeStates.Acknowledged or ExchangeStates.Expired))
            .Where(r => !(r.SenderActorType == ExchangeActorTypes.ZCode && r.SenderActorId == sessionId))
            .Where(r => !(r.RecipientActorType == ExchangeActorTypes.ZCode && r.RecipientActorId == ExchangeActorTypes.RoleWildcard &&
                          r.ClaimedBySessionId is not null &&
                          r.ClaimedBySessionId != sessionId))
            .Select(r => r.ToModel())
            .ToList();
        return new ExchangeDigest().Build(visible, now, _options.DigestBudgetChars, repo.Id);
    }

    // ---- config / admin reads ----

    public async Task<IReadOnlyList<RepositoryRow>> RepositoriesAsync() => await _repositories.ListAsync();

    private async Task<List<string>> RepositoriesForAsync(ExchangeActor actor, string? repository)
    {
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var row = await _repositories.GetAsync(repository.Trim());
            return new List<string> { row?.Id ?? repository.Trim() };
        }
        // Without a repository filter a ZCode session sees role items for every registered
        // repository; other actors receive no role items (and none exists for them).
        return (await _repositories.ListAsync()).Select(r => r.Id).ToList();
    }

    private async Task<List<ExchangeItemRow>> ThreadRowsAsync(SqliteTransaction tx, string threadId)
    {
        var rows = await tx.Connection!.QueryAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + " FROM exchange_items WHERE thread_id = @T ORDER BY created_at, id",
            new { T = threadId }, tx);
        return rows.ToList();
    }

    // ---- payloads: metadata only ----

    private static object CreatedPayload(ExchangeItemRow row) => new
    {
        id = row.Id, kind = row.Kind, state = row.State, threadId = row.ThreadId, hopCount = row.HopCount,
        sender = new { type = row.SenderActorType, id = row.SenderActorId },
        recipient = new { type = row.RecipientActorType, id = row.RecipientActorId },
        repositoryId = row.RepositoryId, purpose = row.Purpose, subject = row.Subject,
        latencyClass = row.LatencyClass, expiresAt = row.ExpiresAt
    };

    private static object StatePayload(ExchangeItemRow row, string state) => new
    {
        id = row.Id, kind = row.Kind, state, threadId = row.ThreadId, hopCount = row.HopCount,
        sender = new { type = row.SenderActorType, id = row.SenderActorId },
        recipient = new { type = row.RecipientActorType, id = row.RecipientActorId },
        repositoryId = row.RepositoryId, claimedBy = row.ClaimedBySessionId, subject = row.Subject
    };

    private static string? RepositoryInContext(string? contextRefs)
    {
        if (string.IsNullOrWhiteSpace(contextRefs)) return null;
        try
        {
            using var doc = JsonDocument.Parse(contextRefs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, "repo", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Collapses every whitespace run (newlines included) to one space and trims.</summary>
    private static string OneLine(string? text) =>
        string.IsNullOrEmpty(text) ? "" : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static string? FirstLine(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var line = body.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length == 0 ? null : line[..Math.Min(line.Length, 80)];
    }

    /// <summary>Mentions in a message body, in order of appearance: andy, openclaw, linda_v1.</summary>
    public static IReadOnlyList<string> MentionedTypes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        var found = new List<(int Index, string Type)>();
        foreach (var type in ExchangeActorTypes.All.Where(t => t != ExchangeActorTypes.ZCode))
        {
            var idx = body.IndexOf("@" + type, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) found.Add((idx, type));
        }
        return found.OrderBy(f => f.Index).Select(f => f.Type).ToList();
    }
}
