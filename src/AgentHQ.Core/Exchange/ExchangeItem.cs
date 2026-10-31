namespace AgentHQ.Core.Exchange;

/// <summary>An item as the Exchange service reads it (one <c>exchange_items</c> row).</summary>
public sealed record ExchangeItem(
    string Id,
    string Kind,
    string State,
    string LatencyClass,
    string ThreadId,
    int HopCount,
    ExchangeActor Sender,
    ExchangeActor Recipient,
    string? RepositoryId,
    string Subject,
    string? Body,
    string? ContextRefs,
    string? Purpose,
    string? ClaimedBySessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClaimedAt = null,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? AnsweredAt = null,
    DateTimeOffset? AcknowledgedAt = null,
    DateTimeOffset? KilledAt = null,
    string? IdempotencyKey = null,
    string? BlobPath = null,
    long? BlobSize = null,
    string? BlobContentType = null,
    string? BlobSha256 = null)
{
    public bool IsRoot => ThreadId == Id;
    public bool IsRoleAddressed => Recipient.IsZCodeRole;
    public bool IsOpenQuestion => Kind == ExchangeKinds.Question && State is ExchangeStates.Queued or ExchangeStates.Claimed;
    public DateTimeOffset LastActivityAt => new[]
    {
        CreatedAt, ClaimedAt, AnsweredAt, AcknowledgedAt, KilledAt, ExpiresAt
    }.Where(t => t is not null).Select(t => t!.Value).Max();
}
