namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// A plan file read that distinguishes "read it" from "the registration outlived the file"
/// (D12). A missing file is a result, not an exception: callers answer <c>not_found</c> with
/// the path and the remedy instead of surfacing a stack trace.
/// </summary>
public sealed record PlanContent(string? Markdown, string? MissingPath, string? Detail)
{
    public bool IsFound => Markdown is not null;

    public static PlanContent Ok(string markdown) => new(markdown, null, null);

    public static PlanContent Missing(string path, string? detail = null) =>
        new(null, path, detail is null
            ? $"plan file is missing on disk: {path} — re-point the registration with PATCH /plans/{{id}}/file or delete it with DELETE /plans/{{id}}"
            : $"plan file is missing or unreadable on disk: {path} ({detail}) — re-point the registration with PATCH /plans/{{id}}/file or delete it with DELETE /plans/{{id}}");
}
