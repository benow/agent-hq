namespace AgentHQ.Commands.ZCode;

/// <summary>CLI surface: `dotnet run --project AgentHQ.Api -- install-zcode [--home DIR] [--port N] [--repo DIR]`
/// and `sync-workflow [--repo DIR] [--force]` (install the /plan:* workflow pack into a repository).</summary>
public static class Runner
{
    public static int Run(string[] args, string zcodeDbPath)
    {
        string? home = null;
        string? repo = null;
        var port = Environment.GetEnvironmentVariable("AGENTHQ_PORT") ?? "5317";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--home" when i + 1 < args.Length: home = args[++i]; break;
                case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
                case "--port" when i + 1 < args.Length: port = args[++i]; break;
            }
        }
        return Installer.Run(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            repo ?? Directory.GetCurrentDirectory(), port);
    }

    /// <summary>The sync verb is separate from install-zcode: it targets a repository's
    /// project scope and never touches the user-scope state pack or the hook.</summary>
    public static int SyncWorkflow(string[] args)
    {
        string? repo = null;
        var force = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo" when i + 1 < args.Length: repo = args[++i]; break;
                case "--force": force = true; break;
            }
        }
        return WorkflowSync.Run(repo ?? Directory.GetCurrentDirectory(), force);
    }
}
