namespace AgentHQ.Infrastructure.Stores;

/// <summary>
/// Explicit column lists: snake_case columns aliased to the row models' property names.
/// Dapper only matches column names to members, so aliasing here keeps every store query
/// unambiguous instead of relying on convention mapping.
/// </summary>
internal static class Columns
{
    public const string Repository = """
        id AS Id, root_path AS RootPath, provider AS Provider, external_id AS ExternalId,
        plans_dir AS PlansDir, is_default AS IsDefault, first_seen_at AS FirstSeenAt
        """;

    public const string Session = """
        provider AS Provider, external_id AS ExternalId, repository_id AS RepositoryId, title AS Title,
        task_type AS TaskType, last_outcome AS LastOutcome, external_time_created AS ExternalTimeCreated,
        external_time_updated AS ExternalTimeUpdated, first_user_message_excerpt AS FirstUserMessageExcerpt,
        archived AS Archived, deleted AS Deleted, ingested_at AS IngestedAt
        """;

    public const string Plan = """
        id AS Id, slug AS Slug, name AS Name, repository_id AS RepositoryId, file_path AS FilePath,
        status AS Status, priority AS Priority, is_blocked AS IsBlocked, blocked_reason AS BlockedReason,
        session_external_id AS SessionExternalId, implemented_by_session_id AS ImplementedBySessionId,
        content_hash AS ContentHash, checked AS Checked, total AS Total, version AS Version,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    public const string History = """
        seq AS Seq, plan_id AS PlanId, from_status AS FromStatus, to_status AS ToStatus,
        actor_type AS ActorType, actor_session_id AS ActorSessionId, command AS Command,
        git_head AS GitHead, note AS Note, separation_of_duties_warning AS SeparationOfDutiesWarning,
        via_shortcut AS ViaShortcut, at AS At
        """;

    public const string Lesson = """
        entry_id AS EntryId, title AS Title, category AS Category, source_plan_id AS SourcePlanId,
        content_hash AS ContentHash, first_seen_at AS FirstSeenAt
        """;

    public const string ExchangeItem = """
        id AS Id, kind AS Kind, state AS State, latency_class AS LatencyClass, thread_id AS ThreadId,
        hop_count AS HopCount, sender_actor_type AS SenderActorType, sender_actor_id AS SenderActorId,
        recipient_actor_type AS RecipientActorType, recipient_actor_id AS RecipientActorId,
        repository_id AS RepositoryId, subject AS Subject, body AS Body, context_refs AS ContextRefs,
        purpose AS Purpose, claimed_by_session_id AS ClaimedBySessionId, claimed_at AS ClaimedAt,
        created_at AS CreatedAt, expires_at AS ExpiresAt, answered_at AS AnsweredAt,
        acknowledged_at AS AcknowledgedAt, killed_at AS KilledAt, idempotency_key AS IdempotencyKey,
        blob_path AS BlobPath, blob_size AS BlobSize, blob_content_type AS BlobContentType,
        blob_sha256 AS BlobSha256
        """;

    public const string ExchangePresence = """
        actor_type AS ActorType, actor_id AS ActorId, last_seen_at AS LastSeenAt,
        latency_class AS LatencyClass
        """;

    public const string ExchangeCursor = """
        actor_type AS ActorType, actor_id AS ActorId, last_seq AS LastSeq, updated_at AS UpdatedAt
        """;

    public static string WithPrefix(string list, string prefix) =>
        string.Join(", ", list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => prefix + p));

    public const string Identity = """
        id AS Id, username AS Username, role AS Role, password_hash AS PasswordHash, created_at AS CreatedAt
        """;

    public const string AuthToken = """
        id AS Id, identity_id AS IdentityId, token_hash AS TokenHash, label AS Label,
        created_at AS CreatedAt, revoked_at AS RevokedAt
        """;

    public const string AuthAudit = """
        seq AS Seq, at AS At, kind AS Kind, username AS Username, route AS Route,
        source AS Source, detail AS Detail
        """;

    public const string Worker = """
        id AS Id, identity_id AS IdentityId, name AS Name, machine AS Machine, version AS Version,
        capabilities AS Capabilities, registered_at AS RegisteredAt, last_heartbeat_at AS LastHeartbeatAt,
        last_accepted_seq AS LastAcceptedSeq, next_expected_seq AS NextExpectedSeq
        """;

    public const string WorkerRepo = """
        worker_id AS WorkerId, repo_name AS RepoName, root_path AS RootPath
        """;

    public const string WorkerPlan = """
        worker_id AS WorkerId, id AS Id, slug AS Slug, name AS Name, repository_id AS RepositoryId,
        file_path AS FilePath, status AS Status, priority AS Priority, is_blocked AS IsBlocked,
        blocked_reason AS BlockedReason, session_external_id AS SessionExternalId,
        implemented_by_session_id AS ImplementedBySessionId, content_hash AS ContentHash,
        checked AS Checked, total AS Total, version AS Version, created_at AS CreatedAt,
        updated_at AS UpdatedAt, markdown AS Markdown
        """;

    public const string WorkerHistory = """
        worker_id AS WorkerId, seq AS Seq, plan_id AS PlanId, from_status AS FromStatus,
        to_status AS ToStatus, actor_type AS ActorType, actor_session_id AS ActorSessionId,
        command AS Command, git_head AS GitHead, note AS Note,
        separation_of_duties_warning AS SeparationOfDutiesWarning, via_shortcut AS ViaShortcut, at AS At
        """;

    public const string WorkerSession = """
        worker_id AS WorkerId, provider AS Provider, external_id AS ExternalId,
        repository_id AS RepositoryId, title AS Title, task_type AS TaskType,
        last_outcome AS LastOutcome, external_time_created AS ExternalTimeCreated,
        external_time_updated AS ExternalTimeUpdated, first_user_message_excerpt AS FirstUserMessageExcerpt,
        archived AS Archived, deleted AS Deleted, ingested_at AS IngestedAt
        """;

    public const string WorkerLesson = """
        worker_id AS WorkerId, entry_id AS EntryId, title AS Title, category AS Category,
        source_plan_id AS SourcePlanId, content_hash AS ContentHash, first_seen_at AS FirstSeenAt
        """;

    public const string WorkerCommand = """
        id AS Id, worker_id AS WorkerId, kind AS Kind, repo AS Repo, actor AS Actor,
        payload AS Payload, issued_at AS IssuedAt, lease_until AS LeaseUntil, result_ok AS ResultOk,
        result_error AS ResultError, result_hash AS ResultHash, finished_at AS FinishedAt
        """;

    public const string WorkerPairing = """
        nonce AS Nonce, created_at AS CreatedAt, expires_at AS ExpiresAt, state AS State,
        identity_id AS IdentityId, token_raw AS TokenRaw, token_id AS TokenId
        """;

    public const string HubState = """
        key AS Key, value AS Value
        """;
}
