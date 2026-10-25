using System.Text.RegularExpressions;

namespace ToastMonitor.Core;

/// <summary>
/// Mac DataMaintenance port (managed backups, restore, clear, repair).
/// Directory: %APPDATA%/ToastMonitor/Backups (Mac: Application
/// Support/ToastMonitor/Backups). Windows has no POSIX file modes; files
/// live under the user's profile, private to that user by default.
/// </summary>
public static class DataMaintenance
{
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToastMonitor", "Backups");

    public sealed record RepairReceipt(string BackupPath, int RemovedTurns, int RemovedSessions, long RemovedTokens);

    public sealed class MaintenanceException : Exception
    {
        public MaintenanceException(string message) : base(message) { }
    }

    /// <summary>Creates a managed backup, keeps seven, reserving one slot for the newest routine snapshot.</summary>
    public static string? CreateBackup(string label, ToastDatabase db,
        string? backupDirectory = null, DateTimeOffset? date = null)
    {
        var directory = backupDirectory ?? Directory;
        var database = db;
        try { System.IO.Directory.CreateDirectory(directory); } catch { return null; }
        if (System.Text.Encoding.UTF8.GetByteCount(label) > 64 ||
            label.Any(c => c < 0x20 || c == 0x7f)) return null;
        var safeLabel = new string(label.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        var stamp = (date ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
            var path = Path.Combine(directory, $"toastmonitor-{safeLabel}-{stamp}-{suffix}.db");
            if (database.BackupTo(path, overwrite: false))
            {
                PruneBackups(keeping: 7, directory: directory);
                return path;
            }
            if (!File.Exists(path)) return null;
        }
        return null;
    }

    /// <summary>Exports a compact snapshot with credential-bearing settings keys removed. Destination must not be the live DB path.</summary>
    public static bool ExportDatabase(string path, ToastDatabase db)
    {
        if (string.IsNullOrEmpty(path) || System.Text.Encoding.UTF8.GetByteCount(path) > 4096 ||
            path.Any(c => c < 0x20 || c == 0x7f)) return false;
        var destination = Path.GetFullPath(path);
        if (string.Equals(destination, Path.GetFullPath(db.DbPath), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(parent)) System.IO.Directory.CreateDirectory(parent);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.Directory) != 0)
                return false;
            return db.ExportRedacted(destination);
        }
        catch { return false; }
    }

    /// <summary>Pre-rebuild backup is mandatory; removes local Claude/Codex usage (tools must currently be local).</summary>
    public static RepairReceipt Repair(ToastDatabase db, IReadOnlyList<(string ToolRaw, IReadOnlyList<string> Roots)> specs,
        string? backupDirectory = null)
    {
        if (specs.Count == 0) throw new MaintenanceException("Claude and Codex are both using remote sources");
        var preview = db.PreviewLocalRebuild(specs.Select(s => s.ToolRaw).ToList());
        var backup = CreateBackup("pre-rebuild", db, backupDirectory)
            ?? throw new MaintenanceException("Could not create the pre-maintenance backup");
        if (!db.ResetLocalUsage(specs))
            throw new MaintenanceException("Rebuild failed; original data retained. Backup: " + backup);
        return new RepairReceipt(backup, preview.Turns, preview.Sessions, preview.Tokens);
    }

    /// <summary>Clears usage/history while preserving settings and DPAPI credentials. A managed pre-clear backup is mandatory and returned.</summary>
    public static string ClearAllData(ToastDatabase db, string? backupDirectory = null)
    {
        var backup = CreateBackup("pre-clear", db, backupDirectory)
            ?? throw new MaintenanceException("Could not create the pre-maintenance backup");
        if (!db.ClearAllData())
            throw new MaintenanceException("Clear failed; original data retained. Backup: " + backup);
        return backup;
    }

    /// <summary>Restores only a regular, non-empty managed backup, with a pre-restore snapshot taken first.</summary>
    public static string? RestoreWithReceipt(string backupPath, ToastDatabase db, string? backupDirectory = null)
    {
        var directory = backupDirectory ?? Directory;
        if (!IsManagedBackup(backupPath, directory) || !IsSafeDatabaseFile(backupPath)) return null;
        string? snapshot = null;
        var restored = db.Restore(backupPath, beforeReplace: () =>
        {
            snapshot = CreateBackup("pre-restore", db, directory);
            return snapshot;
        });
        return restored ? snapshot : null;
    }

    public static bool Restore(string backupPath, ToastDatabase db, string? backupDirectory = null) =>
        RestoreWithReceipt(backupPath, db, backupDirectory) is not null;

    /// <summary>
    /// Self-throttled weekly snapshot: callers invoke unconditionally on
    /// every scan tick; the throttle keeps actual backup+prune work to
    /// once per 7 days. The stamp is written even on failure, so a
    /// persistent failure waits for next week's tick instead of retrying
    /// on every scan. This is the only automatic backup in the app.
    /// </summary>
    public static void MaybeCreateRoutineBackupIfDue(ToastDatabase db, string? backupDirectory = null)
    {
        const string key = "routine_backup_last";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (long.TryParse(db.GetSetting(key) ?? "0", out var last) && now - last < 7 * 86400) return;
        db.SetSetting(key, now.ToString(System.Globalization.CultureInfo.InvariantCulture));
        CreateBackup("weekly", db, backupDirectory);
    }

    /// <summary>Managed backups sorted newest first; only real SQLite files (magic header) are listed.</summary>
    public static List<string> AvailableBackups(string? directory = null)
    {
        var dir = directory ?? Directory;
        if (!System.IO.Directory.Exists(dir)) return new List<string>();
        return System.IO.Directory.GetFiles(dir, "*.db")
            .Where(IsSQLiteFile)
            .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
            .ToList();
    }

    /// <summary>True when the file carries the SQLite magic header ("SQLite format 3\0").</summary>
    public static bool IsSQLiteFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var magic = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            var buf = new byte[magic.Length];
            return fs.Read(buf, 0, buf.Length) == magic.Length && buf.SequenceEqual(magic);
        }
        catch { return false; }
    }

    public static void PruneBackups(int keeping, string? directory = null)
    {
        var dir = directory ?? Directory;
        var backups = AvailableBackups(dir);
        var routine = backups.FirstOrDefault(p => ManagedBackupLabel(p) is "weekly" or "routine");
        var capacity = Math.Max(keeping, routine is null ? 0 : 1);
        var keepOps = Math.Max(capacity - (routine is null ? 0 : 1), 0);
        var retained = backups.Where(p => p != routine).Take(keepOps).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (routine is not null) retained.Add(routine);
        foreach (var path in backups.Where(p => !retained.Contains(p)))
            try { File.Delete(path); } catch { /* best effort */ }
    }

    public static string ManagedBackupLabel(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, @"^toastmonitor-(.+)-[0-9]{8}-[0-9]{6}(?:-[a-f0-9]{8})?$");
        return m.Success ? m.Groups[1].Value : name;
    }

    private static bool IsManagedBackup(string path, string directory)
    {
        var candidate = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeDatabaseFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.Directory) == 0 && info.Length > 0;
        }
        catch { return false; }
    }
}
