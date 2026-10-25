namespace AgentHQ.Core.Domain;

/// <summary>Provider health as determined by the schema guard at startup.</summary>
public enum ProviderHealth
{
    Ok,
    SchemaAhead,
    Missing
}

public interface IAgentProvider
{
    string Id { get; }
    ProviderHealth Health { get; }
    string? HealthDetail { get; }

    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync();
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(SessionQuery q);
    Task<SessionDetail?> GetSessionAsync(string externalId);
    Task<SessionDelta> GetMessagesAsync(string externalId, SessionMessageQuery q);
    ValueTask SubscribeAsync(Func<ProviderEvent, Task> onEvent, CancellationToken ct);
}
