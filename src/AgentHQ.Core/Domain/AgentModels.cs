namespace AgentHQ.Core.Domain;

public sealed record RepositoryInfo(string ExternalProjectId, string RootPath, DateTimeOffset LastSeenAt);

public sealed record SessionQuery(
    string? RepositoryRootPath = null,
    DateTimeOffset? UpdatedSince = null,
    string? Status = null,
    int Limit = 5000);

public sealed record SessionInfo(
    string Id,
    string RootPath,
    string Title,
    string TaskType,
    string? LastOutcome,
    DateTimeOffset TimeCreated,
    DateTimeOffset TimeUpdated,
    bool Archived,
    bool Deleted)
{
    /// <summary>Derived status per the digest model: active/idle/archived/deleted.</summary>
    public string Status(DateTimeOffset now)
    {
        if (Deleted) return "deleted";
        if (Archived) return "archived";
        return now - TimeUpdated <= TimeSpan.FromMinutes(30) ? "active" : "idle";
    }
}

public sealed record SessionDetail(
    SessionInfo Info,
    string? FirstUserMessageExcerpt);

public sealed record SessionMessageQuery(
    long? AfterSequence = null,
    bool IncludeReasoning = false,
    bool IncludeTool = false,
    bool IncludeSteps = false);

public sealed record SessionPart(string Type, string? Text, string? ToolName, string? ToolSummary, long Sequence);

public sealed record SessionMessage(
    string Id,
    string Role,
    DateTimeOffset TimeCreated,
    long Sequence,
    IReadOnlyList<SessionPart> Parts);

public sealed record SessionDelta(string ExternalId, IReadOnlyList<SessionMessage> Messages, long MaxSequence);

public abstract record ProviderEvent
{
    public record SessionChanged(string ExternalId) : ProviderEvent;
}
