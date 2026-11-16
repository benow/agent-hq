using AgentHQ.Core.Plans;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>Plan registration, content, dependencies, blocks, history and lessons index.
/// Implemented for SQLite (spoke) and PostgreSQL (hub) — the SQL is never shared.</summary>
public interface IPlanStore
{
    Task<PlanRow> InsertAsync(PlanRow plan);
    Task<PlanRow?> GetAsync(string id);
    Task<PlanRow?> GetBySlugAsync(string repositoryId, string slug);
    Task<IReadOnlyList<PlanRow>> ListAsync(string? repoId = null, string? status = null,
        bool? ready = null, bool? blocked = null);
    Task<bool> SlugTakenAsync(string repositoryId, string slug);
    Task<PlanRow?> FindBySlugAsync(string repositoryId, string slug);
    Task<PlanRow?> FindByFilePathAsync(string repositoryId, string filePath);
    Task<PlanRow?> RepointAsync(string id, string filePath, string contentHash, PlanProgress progress);
    Task DeleteAsync(string id);
    Task<IReadOnlyList<PlanRow>> DependentsAsync(string planId);
    Task<IReadOnlyList<PlanRow>> ResolveFileArgAsync(RepositoryRow repo, string fileArg);
    Task<PlanRow?> UpdateContentAsync(string id, string contentHash, PlanProgress progress, bool externalEdit);
    Task UpdateMetaAsync(string id, long? priority = null, string? sessionExternalId = null);
    Task SetStatusAsync(string id, string status);
    Task SetBlockedAsync(string id, bool blocked, string? reason);
    Task SetImplementedByAsync(string id, string? sessionId);
    Task AddBlockAsync(string planId, string reason, string actor);
    Task ResolveOpenBlockAsync(string planId, string? resolution);
    Task AddHistoryAsync(HistoryRow row);
    Task<IReadOnlyList<HistoryRow>> HistoryAsync(string planId);
    Task<bool> AddDependencyAsync(string planId, string requiresPlanId);
    Task<IReadOnlyList<PlanRow>> DependenciesAsync(string planId);
    Task UpsertLessonsAsync(string repositoryId, IReadOnlyList<LessonEntry> entries, string contentHash);
    Task<IReadOnlyList<LessonRow>> LessonsAsync(string repositoryId);
}
