namespace ToastMonitor.Collect;

/// <summary>
/// Windows data-directory mapping for every local source. Mac paths are in
/// comments; Windows equivalents keep the same dot-directory names under
/// %USERPROFILE% (the tools themselves use those locations on Windows), except
/// where the Mac path is an OS directory (Application Support -&gt; %APPDATA%).
/// Env overrides mirror the Mac parsers (HERMES_HOME, DSH_HOME, OPENCODE_HOME, GROK_HOME).
/// </summary>
public static class SourceRoots
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string JoinUser(string relative)
    {
        // Split on both separators: a literal ".claude/projects" segment must not
        // survive as a mixed-separator path on Windows.
        var parts = new[] { UserProfile }.Concat(
            relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return Path.Combine(parts);
    }
    public static string EnvOr(string name, string fallback) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))
            ? Environment.GetEnvironmentVariable(name)! : fallback;

    // Claude Code: Mac ~/.claude/projects  ->  Windows %USERPROFILE%/.claude/projects
    public static string ClaudeProjects => JoinUser(".claude/projects");
    // Cowork local-agent sessions: Mac ~/Library/Application Support/Claude/local-agent-mode-sessions
    public static string ClaudeCoworkRoot => Path.Combine(AppData, "Claude", "local-agent-mode-sessions");
    public static string ClaudeCredentialsFile => JoinUser(".claude/.credentials.json");

    // Codex: Mac ~/.codex/sessions, ~/.codex/state_5.sqlite, ~/.codex/auth.json
    public static string CodexSessions => JoinUser(".codex/sessions");
    public static string CodexStateDb => JoinUser(".codex/state_5.sqlite");
    public static string CodexAuthFile => JoinUser(".codex/auth.json");

    // OpenCode: Mac ~/.local/share/opencode/opencode.db  (env OPENCODE_HOME)
    public static string OpenCodeDb => Path.Combine(EnvOr("OPENCODE_HOME", JoinUser(".local/share/opencode")), "opencode.db");

    // Hermes: Mac ~/.hermes  (env HERMES_HOME)
    public static string HermesHome => EnvOr("HERMES_HOME", JoinUser(".hermes"));
    public static string HermesDb => Path.Combine(HermesHome, "state.db");

    // OMP: Mac ~/.omp/agent/sessions
    public static string OmpSessions => JoinUser(".omp/agent/sessions");

    // DeepSeek Harness: Mac ~/.dsh  (env DSH_HOME)
    public static string DshHome => EnvOr("DSH_HOME", JoinUser(".dsh"));
    public static string DshSessions => Path.Combine(DshHome, "sessions");
    public static string DshProjCache => Path.Combine(DshHome, "storages/session_projcache.json");

    // Grok: Mac ~/.grok/auth.json  (env GROK_HOME is a directory override on Mac; here it may be a file or dir)
    public static string GrokAuthFile
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("GROK_HOME");
            if (!string.IsNullOrEmpty(custom))
                return Directory.Exists(custom) ? Path.Combine(custom, "auth.json") : custom;
            return JoinUser(".grok/auth.json");
        }
    }

    /// <summary>Is this tool's source remote (VPS feed) rather than local? Mac ToolKind.sourceIsRemote, key "src_&lt;tool&gt;".</summary>
    public static bool SourceIsRemote(IParserStateStore store, string toolRaw) =>
        (store.GetSetting("src_" + toolRaw) ?? "local") == "remote";

    public static bool SourceIsRemote(ToastMonitor.Core.ToastDatabase db, string toolRaw) =>
        (db.GetSetting("src_" + toolRaw) ?? "local") == "remote";
}
