using AgentHQ.Models;
using System.Security.Cryptography;
using System.Text;
using AgentHQ.Core.Plans;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// Watches a repository's plans directory. On change: hash the file; a hash equal to the
/// indexed content_hash is a self-write (bridge-initiated) and is ignored; otherwise the
/// plan's version bumps, progress recomputes, lessons re-index, and a callback fires so the
/// host can emit plan.updated.
/// </summary>
public sealed class PlanDirWatcher : IDisposable
{
    private readonly RepositoryRow _repo;
    private readonly IPlanStore _plans;
    private readonly Func<PlanRow, Task> _onExternalChange;
    private readonly Func<string, string, Task>? _onMoved;
    private readonly Serilog.ILogger _log;
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _fsw;
    private DateTime _settleUntilUtc = DateTime.MinValue;

    public PlanDirWatcher(RepositoryRow repo, IPlanStore plans,
        Func<PlanRow, Task> onExternalChange, TimeSpan? debounce = null, Serilog.ILogger? logger = null,
        Func<string, string, Task>? onMoved = null)
    {
        _repo = repo;
        _plans = plans;
        _onExternalChange = onExternalChange;
        _onMoved = onMoved;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _log = (logger ?? Serilog.Log.ForContext<PlanDirWatcher>()).ForContext("Subsystem", "plans");
    }

    public string PlansDirAbsolute => Path.Combine(_repo.RootPath, _repo.PlansDir);

    /// <summary>Bridge writes call this to stretch the settle window, suppressing the
    /// watcher burst they cause beyond the hash check.</summary>
    public void NoteSelfWrite() => _settleUntilUtc = DateTime.UtcNow + _debounce + TimeSpan.FromMilliseconds(200);

    public void Start()
    {
        Directory.CreateDirectory(PlansDirAbsolute);
        _fsw = new FileSystemWatcher(PlansDirAbsolute)
        {
            Filter = "*.md",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            // Recursive: a plan may be a directory (docs/plans/<name>/*.md) — part-file edits must
            // reach the registered row just like edits to the registered file itself.
            IncludeSubdirectories = true,
            // Recursive watching raises more events per burst; the default 8 KB buffer drops some.
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };
        _fsw.Changed += (_, e) => _ = OnChangeAsync(e.FullPath);
        _fsw.Created += (_, e) => _ = OnChangeAsync(e.FullPath);
        // Recursive watching pairs a move within the tree into one Renamed event (no Deleted
        // fires for the source), so the done/ archive re-point hangs off the rename as well.
        _fsw.Renamed += (_, e) => _ = OnRenamedAsync(e.OldFullPath, e.FullPath);
        _fsw.Deleted += (_, e) => _ = OnDeletedAsync(e.FullPath);
        _log.Information("plan dir watcher started on {Dir}", PlansDirAbsolute);
    }

    private async Task OnChangeAsync(string fullPath)
    {
        try
        {
            await Task.Delay(_debounce);
            if (DateTime.UtcNow < _settleUntilUtc)
            {
                // Still verify via hash — suppression is hash-based, the window only limits churn.
            }
            if (!File.Exists(fullPath)) return;
            var all = await _plans.ListAsync(repoId: _repo.Id);
            var affected = AffectedPlans(all, fullPath, PlansDirAbsolute);
            if (affected.Count == 0) return;

            foreach (var plan in affected)
            {
                // Exactly one row per plan directory aggregates the directory — the aggregator;
                // every other registered row there is single-file. Both shapes compare against
                // content_hash, so self-write suppression works identically for both.
                string hash;
                PlanProgress progress;
                var dir = Path.GetDirectoryName(plan.FilePath);
                var aggregator = dir is null ? null : AggregatorIn(all, dir, PlansDirAbsolute);
                if (aggregator?.Id == plan.Id)
                {
                    var state = await DirectoryStateAsync(dir!, all, plan.FilePath);
                    hash = state.Hash;
                    progress = state.Progress;
                }
                else
                {
                    if (!File.Exists(plan.FilePath)) continue;   // mid-move; the rename handler owns it
                    var content = await File.ReadAllTextAsync(plan.FilePath);
                    hash = ContentHash(content);
                    progress = PlanDocument.Progress(content);
                }
                if (hash == plan.ContentHash) continue;   // self-write suppression

                // Compare-and-set inside the UPDATE: the hash and progress (and the version this
                // edit produced) come back from the same statement that wrote them, so a newer
                // row cannot be read back in their place by a concurrent writer.
                var updated = await _plans.UpdateContentAsync(plan.Id, hash, progress, externalEdit: true);
                if (updated is not null) await _onExternalChange(updated);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "plan watcher change handling failed for {Path}", fullPath);
        }
    }

    /// <summary>
    /// A move into <c>done/</c> observed as one paired rename: the archived copy exists at the
    /// destination and the row still points at the source — hand both paths to the host so the
    /// registration re-points instead of silently going stale.
    /// </summary>
    private async Task OnRenamedAsync(string oldFullPath, string newFullPath)
    {
        try
        {
            await Task.Delay(_debounce);
            var dir = Path.GetDirectoryName(newFullPath);
            if (dir is not null
                && string.Equals(Path.GetFileName(dir), "done", StringComparison.OrdinalIgnoreCase)
                && File.Exists(newFullPath))
            {
                var plan = (await _plans.ListAsync(repoId: _repo.Id)).FirstOrDefault(p =>
                    string.Equals(p.FilePath, oldFullPath, StringComparison.OrdinalIgnoreCase));
                if (plan is not null && _onMoved is not null)
                {
                    await _onMoved(oldFullPath, newFullPath);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "plan watcher rename handling failed for {Path}", newFullPath);
        }
        await OnChangeAsync(newFullPath);
    }

    /// <summary>
    /// A plan file that disappears from the watched tree while its archive copy exists at
    /// <c>done/&lt;name&gt;</c> is the done/abandon archive move: hand both paths to the host so
    /// the registration re-points instead of silently going stale.
    /// </summary>
    private async Task OnDeletedAsync(string oldFullPath)
    {
        try
        {
            if (_onMoved is null) return;
            await Task.Delay(_debounce);
            var archivePath = Path.Combine(PlansDirAbsolute, "done", Path.GetFileName(oldFullPath));
            if (!File.Exists(archivePath)) return;
            var plan = (await _plans.ListAsync(repoId: _repo.Id)).FirstOrDefault(p =>
                string.Equals(p.FilePath, oldFullPath, StringComparison.OrdinalIgnoreCase));
            if (plan is null) return;
            await _onMoved(oldFullPath, archivePath);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "plan watcher archive-move handling failed for {Path}", oldFullPath);
        }
    }

    /// <summary>
    /// Every registered row an edit to <paramref name="fullPath"/> affects: the row whose file it
    /// is (exact path, then filename — a renamed plan file still matches its row), plus the
    /// aggregator of the file's plan directory (a part-file edit bumps the umbrella).
    /// </summary>
    internal static List<PlanRow> AffectedPlans(IReadOnlyList<PlanRow> all, string fullPath, string plansRoot)
    {
        var affected = new List<PlanRow>();
        var exact = MatchPlan(all, fullPath);
        if (exact is not null) affected.Add(exact);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null)
        {
            var aggregator = AggregatorIn(all, dir, plansRoot);
            if (aggregator is not null && aggregator.Id != exact?.Id) affected.Add(aggregator);
        }
        return affected;
    }

    private static PlanRow? MatchPlan(IReadOnlyList<PlanRow> all, string fullPath) =>
        all.FirstOrDefault(p =>
            string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase))
        ?? all.FirstOrDefault(p => Path.GetFileName(p.FilePath) == Path.GetFileName(fullPath));

    /// <summary>
    /// Within a plan directory (a direct subdirectory of the plans directory that is not
    /// <c>done/</c>), exactly one registered row aggregates the whole directory — the
    /// alphabetically-first file path, which is the <c>00-OVERVIEW.md</c> convention. Every other
    /// registered row in that directory is single-file and tracks only its own checkboxes.
    /// </summary>
    internal static PlanRow? AggregatorIn(IReadOnlyList<PlanRow> all, string dir, string plansRoot)
    {
        if (string.Equals(Path.GetFileName(dir), "done", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(Path.GetDirectoryName(dir), plansRoot, StringComparison.OrdinalIgnoreCase)) return null;
        return all
            .Where(p => string.Equals(Path.GetDirectoryName(p.FilePath), dir, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.FilePath, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The aggregate state of a directory plan: every <c>*.md</c> directly beside the registered
    /// file (deterministic name order), excluding files registered as their own rows — those
    /// track their own progress — except <paramref name="selfPath"/>, the row being computed.
    /// The hash covers file names and per-file hashes, so a rename or a content edit anywhere in
    /// the directory changes it.
    /// </summary>
    internal static async Task<(string Hash, PlanProgress Progress)> DirectoryStateAsync(
        string planDir, IReadOnlyList<PlanRow> registered, string selfPath)
    {
        var self = Path.GetFullPath(selfPath);
        var excluded = registered
            .Select(p => Path.GetFullPath(p.FilePath))
            .Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();
        var checkedCount = 0;
        var total = 0;
        foreach (var file in Directory.GetFiles(planDir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (excluded.Contains(Path.GetFullPath(file))) continue;
            var content = await File.ReadAllTextAsync(file);
            var progress = PlanDocument.Progress(content);
            checkedCount += progress.Checked;
            total += progress.Total;
            sb.Append(Path.GetFileName(file)).Append('\0').Append(ContentHash(content)).Append('\n');
        }
        return (ContentHash(sb.ToString()), new PlanProgress(checkedCount, total));
    }

    public static string ContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose() => _fsw?.Dispose();
}
