namespace AgentHQ.Models;

// Row models mirror hub/spoke table columns (snake_case columns are aliased to these property
// names by each store's column list). They must be properties: Dapper does not materialize
// public fields. This project is I/O-free — shared by the SQLite (spoke) and PostgreSQL (hub)
// store layers, which never share SQL.

public sealed class RepositoryRow
{
    public string Id { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? ExternalId { get; set; }
    public string PlansDir { get; set; } = "docs/plans";
    public long IsDefault { get; set; }
    public long FirstSeenAt { get; set; }

    public bool IsDefaultBool => IsDefault != 0;
    public DateTimeOffset FirstSeen => DateTimeOffset.FromUnixTimeMilliseconds(FirstSeenAt);
}

public sealed class SessionRow
{
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string? RepositoryId { get; set; }
    public string Title { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string? LastOutcome { get; set; }
    public long ExternalTimeCreated { get; set; }
    public long ExternalTimeUpdated { get; set; }
    public string? FirstUserMessageExcerpt { get; set; }
    public long Archived { get; set; }
    public long Deleted { get; set; }
    public long IngestedAt { get; set; }

    public DateTimeOffset Created => DateTimeOffset.FromUnixTimeMilliseconds(ExternalTimeCreated);
    public DateTimeOffset Updated => DateTimeOffset.FromUnixTimeMilliseconds(ExternalTimeUpdated);
    public DateTimeOffset Ingested => DateTimeOffset.FromUnixTimeMilliseconds(IngestedAt);
    public bool ArchivedBool => Archived != 0;
    public bool DeletedBool => Deleted != 0;
}

public sealed class PlanRow
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Status { get; set; } = "submitted";
    public long Priority { get; set; }
    public long IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public string? SessionExternalId { get; set; }
    public string? ImplementedBySessionId { get; set; }
    public string ContentHash { get; set; } = "";
    public long Checked { get; set; }
    public long Total { get; set; }
    public long Version { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }

    public bool IsBlockedBool => IsBlocked != 0;
    public DateTimeOffset Created => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
    public DateTimeOffset Updated => DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAt);
}

public sealed class HistoryRow
{
    public long Seq { get; set; }
    public string PlanId { get; set; } = "";
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string? ActorSessionId { get; set; }
    public string? Command { get; set; }
    public string? GitHead { get; set; }
    public string? Note { get; set; }
    public long SeparationOfDutiesWarning { get; set; }
    public long ViaShortcut { get; set; }
    public long At { get; set; }

    public bool SodWarning => SeparationOfDutiesWarning != 0;

    /// <summary>True on the intermediate → implemented row a short-cut verification records.</summary>
    public bool ViaShortcutBool => ViaShortcut != 0;
}

public sealed class LessonRow
{
    public string EntryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    public string? SourcePlanId { get; set; }
    public string ContentHash { get; set; } = "";
    public long FirstSeenAt { get; set; }
}

public sealed class ExchangeItemRow
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string State { get; set; } = "";
    public string LatencyClass { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public long HopCount { get; set; }
    public string SenderActorType { get; set; } = "";
    public string SenderActorId { get; set; } = "";
    public string RecipientActorType { get; set; } = "";
    public string RecipientActorId { get; set; } = "";
    public string? RepositoryId { get; set; }
    public string Subject { get; set; } = "";
    public string? Body { get; set; }
    public string? ContextRefs { get; set; }
    public string? Purpose { get; set; }
    public string? ClaimedBySessionId { get; set; }
    public long? ClaimedAt { get; set; }
    public long CreatedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public long? AnsweredAt { get; set; }
    public long? AcknowledgedAt { get; set; }
    public long? KilledAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? BlobPath { get; set; }
    public long? BlobSize { get; set; }
    public string? BlobContentType { get; set; }
    public string? BlobSha256 { get; set; }

    public AgentHQ.Core.Exchange.ExchangeItem ToModel() => new(
        Id, Kind, State, LatencyClass, ThreadId, (int)HopCount,
        new Core.Exchange.ExchangeActor(SenderActorType, SenderActorId),
        new Core.Exchange.ExchangeActor(RecipientActorType, RecipientActorId),
        RepositoryId, Subject, Body, ContextRefs, Purpose, ClaimedBySessionId,
        DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt),
        ClaimedAt is { } ca ? DateTimeOffset.FromUnixTimeMilliseconds(ca) : null,
        ExpiresAt is { } ea ? DateTimeOffset.FromUnixTimeMilliseconds(ea) : null,
        AnsweredAt is { } aa ? DateTimeOffset.FromUnixTimeMilliseconds(aa) : null,
        AcknowledgedAt is { } ack ? DateTimeOffset.FromUnixTimeMilliseconds(ack) : null,
        KilledAt is { } ka ? DateTimeOffset.FromUnixTimeMilliseconds(ka) : null,
        IdempotencyKey, BlobPath, BlobSize, BlobContentType, BlobSha256);
}

public sealed class ExchangePresenceRow
{
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public long LastSeenAt { get; set; }
    public string LatencyClass { get; set; } = "";

    public DateTimeOffset LastSeen => DateTimeOffset.FromUnixTimeMilliseconds(LastSeenAt);
}

public sealed class ExchangeCursorRow
{
    public string ActorType { get; set; } = "";
    public string ActorId { get; set; } = "";
    public long LastSeq { get; set; }
    public long UpdatedAt { get; set; }
}

// ---- auth (part 2 of distributed-agent-hq): identities, tokens, audit ----

public sealed class IdentityRow
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string Role { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public long CreatedAt { get; set; }

    public DateTimeOffset Created => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
}

public sealed class AuthTokenRow
{
    public string Id { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string? Label { get; set; }
    public long CreatedAt { get; set; }
    public long? RevokedAt { get; set; }

    public bool RevokedBool => RevokedAt is not null;
}

public sealed class AuthAuditRow
{
    public long Seq { get; set; }
    public long At { get; set; }
    public string Kind { get; set; } = "";
    public string? Username { get; set; }
    public string? Route { get; set; }
    public string? Source { get; set; }
    public string? Detail { get; set; }
}

// ---- workers (part 3 of distributed-agent-hq): spokes, projections, queue, pairing ----

public sealed class WorkerRow
{
    public string Id { get; set; } = "";
    public string IdentityId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>JSON array of typed capabilities ({kind, ...fields}); stored, surfaced and
    /// routed — unknown kinds stored and surfaced but never routed (rule 7).</summary>
    public string Capabilities { get; set; } = "[]";
    public long RegisteredAt { get; set; }
    public long? LastHeartbeatAt { get; set; }
    /// <summary>The hub's global seq of the last accepted event from this worker (0 = none).</summary>
    public long LastAcceptedSeq { get; set; }
    /// <summary>Next originSeq the hub expects from this worker (rule 2's cursor).</summary>
    public long NextExpectedSeq { get; set; }

    public DateTimeOffset Registered => DateTimeOffset.FromUnixTimeMilliseconds(RegisteredAt);
    public DateTimeOffset? LastHeartbeat => LastHeartbeatAt is { } v
        ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;
}

public sealed class WorkerRepoRow
{
    public string WorkerId { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string RootPath { get; set; } = "";
}

public sealed class WorkerPlanRow
{
    public string WorkerId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string RepositoryId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Status { get; set; } = "submitted";
    public long Priority { get; set; }
    public long IsBlocked { get; set; }
    public string? BlockedReason { get; set; }
    public string? SessionExternalId { get; set; }
    public string? ImplementedBySessionId { get; set; }
    public string ContentHash { get; set; } = "";
    public long Checked { get; set; }
    public long Total { get; set; }
    public long Version { get; set; } = 1;
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public string Markdown { get; set; } = "";
}

public sealed class WorkerHistoryRow
{
    public string WorkerId { get; set; } = "";
    public long Seq { get; set; }
    public string PlanId { get; set; } = "";
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string ActorType { get; set; } = "";
    public string? ActorSessionId { get; set; }
    public string? Command { get; set; }
    public string? GitHead { get; set; }
    public string? Note { get; set; }
    public long SeparationOfDutiesWarning { get; set; }
    public long ViaShortcut { get; set; }
    public long At { get; set; }
}

public sealed class WorkerSessionRow
{
    public string WorkerId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string? RepositoryId { get; set; }
    public string Title { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string? LastOutcome { get; set; }
    public long ExternalTimeCreated { get; set; }
    public long ExternalTimeUpdated { get; set; }
    public string? FirstUserMessageExcerpt { get; set; }
    public long Archived { get; set; }
    public long Deleted { get; set; }
    public long IngestedAt { get; set; }
}

public sealed class WorkerLessonRow
{
    public string WorkerId { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Category { get; set; }
    public string? SourcePlanId { get; set; }
    public string ContentHash { get; set; } = "";
    public long FirstSeenAt { get; set; }
}

public sealed class WorkerCommandRow
{
    public string Id { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Repo { get; set; }
    public string Actor { get; set; } = "";
    /// <summary>JSON — type-specific, camelCase (the contract's payload shape).</summary>
    public string Payload { get; set; } = "{}";
    public long IssuedAt { get; set; }
    public long? LeaseUntil { get; set; }
    public long? ResultOk { get; set; }
    public string? ResultError { get; set; }
    public string? ResultHash { get; set; }
    public long? FinishedAt { get; set; }

    public bool IsLeased => LeaseUntil is { } v && DateTimeOffset.FromUnixTimeMilliseconds(v) > DateTimeOffset.UtcNow;
    public bool HasResult => ResultOk is not null;
    public DateTimeOffset Issued => DateTimeOffset.FromUnixTimeMilliseconds(IssuedAt);
}

public sealed class WorkerPairingRow
{
    public string Nonce { get; set; } = "";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public string State { get; set; } = "pending";
    public string? IdentityId { get; set; }
    /// <summary>The raw worker token, held only until the one delivery (rule 10) — cleared
    /// when the poll returns it, never logged, never stored anywhere else in clear.</summary>
    public string? TokenRaw { get; set; }
    public string? TokenId { get; set; }

    public bool IsExpired => DateTimeOffset.FromUnixTimeMilliseconds(ExpiresAt) < DateTimeOffset.UtcNow;
}

public sealed class HubStateRow
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
