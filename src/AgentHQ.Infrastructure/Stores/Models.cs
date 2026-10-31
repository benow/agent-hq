namespace AgentHQ.Infrastructure.Stores;

// Row models mirror bridge table columns (snake_case columns are aliased to these names in
// Columns.cs). They must be properties: Dapper does not materialize public fields.

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

    public Core.Exchange.ExchangeItem ToModel() => new(
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
