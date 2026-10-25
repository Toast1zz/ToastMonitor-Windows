using Microsoft.Data.Sqlite;

namespace ToastMonitor.Core;

/// <summary>One local-rebuild preview row (Mac Database.LocalRebuildPreview).</summary>
public sealed record LocalRebuildPreview(int Turns, int Sessions, long Tokens);

/// <summary>
/// Mac Database maintenance primitives (DataMaintenance calls these).
/// Backups use VACUUM INTO (Microsoft.Data.Sqlite has no sqlite3_backup
/// API); the result is a compact, consistent snapshot of the live DB.
/// POSIX file modes from the Mac code have no Windows equivalent and are
/// not applied — the file lives under the user's profile, which is
/// private to that user by default.
/// </summary>
public sealed partial class ToastDatabase
{
    public static readonly string[] RestoreTables =
        { "turns", "sessions", "scan_state", "session_totals",
          "openrouter_snapshots", "opencodego_snapshots", "subscriptions", "settings" };

    /// <summary>Snapshot the live DB to <paramref name="destination"/>; when <paramref name="overwrite"/> is false an existing file fails the call.</summary>
    public bool BackupTo(string destination, bool overwrite = true)
    {
        lock (_lock)
        {
            EnsureOpen();
            try
            {
                if (!overwrite && File.Exists(destination)) return false;
                var dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // VACUUM INTO refuses an existing destination, which gives
                // us the overwrite:false behaviour for free; for overwrite
                // we remove the target first.
                if (overwrite && File.Exists(destination)) File.Delete(destination);
                using var cmd = Command("VACUUM INTO @p0;");
                cmd.Parameters.AddWithValue("@p0", destination);
                cmd.ExecuteNonQuery();
                return File.Exists(destination) && new FileInfo(destination).Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>Export a compact snapshot with credential-bearing settings keys removed.</summary>
    public bool ExportRedacted(string destination)
    {
        lock (_lock)
        {
            EnsureOpen();
            var workspace = Path.Combine(Path.GetTempPath(), "tm-export-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(workspace);
                var raw = Path.Combine(workspace, "raw.db");
                var compact = Path.Combine(workspace, "compact.db");
                if (!BackupTo(raw)) return false;
                using (var copy = new ToastDatabase(raw))
                {
                    copy.Open();
                    using var cmd = copy.Command(
                        "DELETE FROM settings WHERE k IN ('or_keys','openrouter_key','openrouter_keys','go_auth_cookie','go_workspace_id');");
                    cmd.ExecuteNonQuery();
                    copy.Close();
                }
                // Reopen the copy and vacuum it into the compact file.
                using (var copy = new ToastDatabase(raw))
                {
                    copy.Open();
                    using var cmd = copy.Command("VACUUM INTO @p0;");
                    cmd.Parameters.AddWithValue("@p0", compact);
                    cmd.ExecuteNonQuery();
                    copy.Close();
                }
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(compact, destination);
                return true;
            }
            catch { return false; }
            finally { try { Directory.Delete(workspace, recursive: true); } catch { /* best effort */ } }
        }
    }

    public LocalRebuildPreview PreviewLocalRebuild(IReadOnlyList<string> toolRawValues)
    {
        lock (_lock)
        {
            EnsureOpen();
            var turns = 0;
            var sessions = 0;
            long tokens = 0;
            foreach (var raw in toolRawValues)
            {
                using (var cmd = Command(
                    "SELECT COUNT(*), COALESCE(SUM(input_tokens + output_tokens + CASE WHEN tool='codex' THEN 0 ELSE cache_read END),0) " +
                    "FROM turns WHERE tool=@p0;"))
                {
                    cmd.Parameters.AddWithValue("@p0", raw);
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read()) { turns += (int)reader.GetInt64(0); tokens += reader.GetInt64(1); }
                }
                using (var cmd = Command("SELECT COUNT(*) FROM sessions WHERE tool=@p0;"))
                {
                    cmd.Parameters.AddWithValue("@p0", raw);
                    sessions += Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            return new LocalRebuildPreview(turns, sessions, tokens);
        }
    }

    /// <summary>
    /// Removes local usage for selected tools and their file cursors in one
    /// transaction. Callers must first create a backup and must only pass
    /// tools currently configured as local; the next collector scan
    /// rebuilds truth from canonical raw events.
    /// </summary>
    public bool ResetLocalUsage(IReadOnlyList<(string ToolRaw, IReadOnlyList<string> Roots)> specs)
    {
        if (specs.Count == 0) return false;
        lock (_lock)
        {
            EnsureOpen();
            using var tx = _connection!.BeginTransaction();
            try
            {
                foreach (var (raw, roots) in specs)
                {
                    foreach (var table in new[] { "turns", "sessions", "session_totals", "perf_samples" })
                    {
                        using var cmd = Command($"DELETE FROM {table} WHERE tool=@p0;", tx);
                        cmd.Parameters.AddWithValue("@p0", raw);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var root in roots)
                    {
                        foreach (var cursorTable in new[] { "scan_state", "perf_cursors" })
                        {
                            using var cmd = Command(
                                $"DELETE FROM {cursorTable} WHERE substr(source,1,length(@p0))=@p0;", tx);
                            cmd.Parameters.AddWithValue("@p0", root);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
                tx.Commit();
                return true;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                return false;
            }
        }
    }

    /// <summary>
    /// Clears usage/history tables without touching settings or the DPAPI
    /// credential file. The caller must create a protected backup first.
    /// </summary>
    public bool ClearAllData()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var tx = _connection!.BeginTransaction();
            try
            {
                foreach (var table in new[]
                {
                    "turns", "sessions", "scan_state", "session_totals",
                    "openrouter_snapshots", "opencodego_snapshots", "subscriptions",
                    "claude_quota_samples", "perf_samples", "perf_cursors",
                    "deepseek_balance_samples", "sqlite_sequence",
                })
                {
                    using var cmd = Command($"DELETE FROM {table};", tx);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                _settingsCache.Clear();
                LoadSettingsCache();
                return true;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                return false;
            }
        }
    }

    /// <summary>
    /// Restores the live DB from <paramref name="source"/> after a schema /
    /// integrity check of a temp candidate copy. <paramref name="beforeReplace"/>
    /// must create the mandatory pre-restore snapshot and return its path
    /// (or null to abort). On post-replace verification failure the live
    /// DB is rolled back from that snapshot.
    /// </summary>
    public bool Restore(string source, Func<string?>? beforeReplace = null)
    {
        lock (_lock)
        {
            EnsureOpen();
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(DbPath), StringComparison.OrdinalIgnoreCase))
                return false;
            var workspace = Path.Combine(Path.GetTempPath(), "tm-restore-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(workspace);
                var candidatePath = Path.Combine(workspace, "candidate.db");
                File.Copy(source, candidatePath);
                if (!CandidateUsable(candidatePath)) return false;
                var recovery = beforeReplace?.Invoke();
                if (recovery is null) return false;
                // Replacing the .db file while a -wal exists is silent
                // corruption: SQLite replays the stale WAL onto the new
                // file on next open. Closing alone does NOT reliably
                // release the WAL on this stack (verified: -wal/-shm
                // survive Close+Dispose and stay locked), so switch the
                // live connection to DELETE journal mode first — that
                // checkpoints the WAL into the main file and removes the
                // sidecars while the connection is still open. If the
                // sidecars survive that, abort rather than replace.
                if (!CheckpointAndClose()) { Open(); return false; }
                try
                {
                    if (!DeleteWalSidecars()) { Open(); return false; }
                    File.Copy(candidatePath, DbPath, overwrite: true);
                }
                catch { Open(); return false; }
                Open();
                if (!LiveUsable())
                {
                    if (CheckpointAndClose())
                    {
                        try
                        {
                            if (DeleteWalSidecars()) File.Copy(recovery, DbPath, overwrite: true);
                        }
                        catch { /* best effort; operator must restore from the snapshot manually */ }
                    }
                    Open();
                    return false;
                }
                return true;
            }
            catch { return false; }
            finally { try { Directory.Delete(workspace, recursive: true); } catch { /* best effort */ } }
        }
    }

    /// <summary>Checkpoint the WAL into the main file (via DELETE journal mode) and close. False on any failure.</summary>
    private bool CheckpointAndClose()
    {
        try
        {
            using var cmd = Command("PRAGMA journal_mode=DELETE;");
            cmd.ExecuteNonQuery();
        }
        catch { return false; }
        try { Close(); return true; }
        catch { return false; }
    }

    /// <summary>Delete -wal/-shm sidecars. Returns false if either still exists (a stale WAL must never survive a file replacement).</summary>
    private bool DeleteWalSidecars()
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = DbPath + suffix;
            if (!File.Exists(sidecar)) continue;
            try { File.Delete(sidecar); } catch { /* fall through to the existence check */ }
            if (File.Exists(sidecar)) return false;
        }
        return true;
    }

    // Raw-connection check on purpose: opening via ToastDatabase.Open
    // would CREATE any missing table and defeat the check. Backups made
    // by this app always carry the full v6 schema; older/foreign schemas
    // are not migrated on Windows (fresh v6-only databases).
    private static bool CandidateUsable(string path)
    {
        try
        {
            using var conn = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite }.ToString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version;";
                var version = Convert.ToInt32(cmd.ExecuteScalar());
                if (version < 0 || version > SchemaVersion) return false;
            }
            foreach (var table in RestoreTables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info({table});";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return false;
            }
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            return check.ExecuteScalar() as string == "ok";
        }
        catch { return false; }
    }

    private bool LiveUsable()
    {
        try
        {
            using var cmd = Command("PRAGMA integrity_check;");
            return cmd.ExecuteScalar() as string == "ok";
        }
        catch { return false; }
    }
}
