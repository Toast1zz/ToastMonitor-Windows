using Microsoft.Data.Sqlite;

namespace ToastMonitor.Core;

/// <summary>One row of the subscriptions table (Mac Database.Subscription).</summary>
public sealed record Subscription(
    long Id, string Name, string Plan, long StartDate, long EndDate,
    string Cycle, double Price, string Currency);

/// <summary>
/// Thread-safe SQLite store, ported from Mac Sources/ToastMonitor/Core/Database.swift.
/// WAL mode, single writer (all public methods take the same lock, which is
/// re-entrant on the calling thread, mirroring Mac's NSRecursiveLock).
///
/// Stage-1 scope (deliberate, see the stage-1 report):
/// - Base tables + v5/v6 tables, indexes, settings, subscriptions, turns,
///   OpenRouter spend and API value. Fresh Windows databases are created
///   directly at schema version 6.
/// - NOT ported in stage 1: the legacy turns-table rebuild
///   (migrateDropLegacyTurnsUnique — a Windows database never has the
///   legacy compound UNIQUE constraint), turn_rollup and the rollup-based
///   usageSource (stage-1 aggregates query raw turns), sessions/scan_state
///   APIs (tables exist, writers arrive with the collector stage), restore,
///   perf/deepseek sample writers, and the settings-change notification
///   centre (an App-layer concern on Windows).
/// </summary>
public sealed partial class ToastDatabase : IDisposable
{
    public const int SchemaVersion = 6;

    public static string DefaultPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TM_DATABASE_PATH");
            if (!string.IsNullOrEmpty(env)) return env;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToastMonitor");
            return Path.Combine(dir, "toastmonitor.db");
        }
    }

    public static ToastDatabase CreateDefault() => new(DefaultPath);

    public string DbPath { get; }

    private readonly object _lock = new();
    private SqliteConnection? _connection;
    private readonly Dictionary<string, string?> _settingsCache = new();
    private bool _disposed;

    public ToastDatabase(string path)
    {
        DbPath = path;
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_connection is not null) return;
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            };
            _connection = new SqliteConnection(builder.ToString());
            _connection.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=NORMAL;");
            Exec("PRAGMA foreign_keys=ON;");
            Exec("PRAGMA busy_timeout=3000;");
            EnsureSchema();
            LoadSettingsCache();
            EnsureRollup();
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _connection?.Close();
            _connection?.Dispose();
            _connection = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    public int UserVersion()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("PRAGMA user_version;");
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // MARK: - Schema

    private void EnsureSchema()
    {
        Exec(@"
        CREATE TABLE IF NOT EXISTS turns (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          tool TEXT NOT NULL,
          session_id TEXT NOT NULL,
          project TEXT,
          model TEXT,
          ts INTEGER NOT NULL,
          input_tokens INTEGER NOT NULL DEFAULT 0,
          output_tokens INTEGER NOT NULL DEFAULT 0,
          reasoning_tokens INTEGER NOT NULL DEFAULT 0,
          cache_read INTEGER NOT NULL DEFAULT 0,
          cache_write INTEGER NOT NULL DEFAULT 0,
          cost REAL NOT NULL DEFAULT 0,
          provider TEXT,
          source_instance TEXT NOT NULL DEFAULT 'local',
          pricing_version TEXT,
          event_id TEXT,
          cost_quality TEXT NOT NULL DEFAULT 'estimated'
        );
        CREATE TABLE IF NOT EXISTS sessions (
          tool TEXT NOT NULL,
          session_id TEXT NOT NULL,
          title TEXT,
          project TEXT,
          model TEXT,
          created INTEGER NOT NULL DEFAULT 0,
          updated INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(tool, session_id)
        );
        CREATE TABLE IF NOT EXISTS scan_state (
          source TEXT PRIMARY KEY,
          size INTEGER NOT NULL DEFAULT 0,
          mtime INTEGER NOT NULL DEFAULT 0,
          identity INTEGER NOT NULL DEFAULT 0,
          context TEXT,
          last_scan INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS session_totals (
          tool TEXT NOT NULL,
          session_id TEXT NOT NULL,
          input INTEGER NOT NULL DEFAULT 0,
          output INTEGER NOT NULL DEFAULT 0,
          reasoning INTEGER NOT NULL DEFAULT 0,
          cache_read INTEGER NOT NULL DEFAULT 0,
          cache_write INTEGER NOT NULL DEFAULT 0,
          cost REAL NOT NULL DEFAULT 0,
          updated INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(tool, session_id)
        );
        CREATE TABLE IF NOT EXISTS openrouter_snapshots (
          ts INTEGER PRIMARY KEY,
          usage REAL NOT NULL DEFAULT 0,
          usage_daily REAL NOT NULL DEFAULT 0,
          usage_weekly REAL NOT NULL DEFAULT 0,
          usage_monthly REAL NOT NULL DEFAULT 0,
          limit_amount REAL,
          limit_remaining REAL,
          limit_reset TEXT,
          is_free_tier INTEGER NOT NULL DEFAULT 0,
          credits_total REAL,
          credits_usage REAL,
          account_usage REAL,
          account_balance REAL,
          is_management_key INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS opencodego_snapshots (
          ts INTEGER PRIMARY KEY,
          rolling_pct REAL,
          rolling_reset INTEGER,
          weekly_pct REAL,
          weekly_reset INTEGER,
          monthly_pct REAL,
          monthly_reset INTEGER
        );
        CREATE TABLE IF NOT EXISTS subscriptions (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          name TEXT NOT NULL,
          plan TEXT NOT NULL DEFAULT '',
          start_date INTEGER NOT NULL DEFAULT 0,
          end_date INTEGER NOT NULL DEFAULT 0,
          cycle TEXT NOT NULL DEFAULT 'monthly',
          price REAL NOT NULL DEFAULT 0,
          currency TEXT NOT NULL DEFAULT 'USD'
        );
        CREATE TABLE IF NOT EXISTS settings (
          k TEXT PRIMARY KEY,
          v TEXT
        );
        CREATE TABLE IF NOT EXISTS claude_quota_samples (
          ts INTEGER PRIMARY KEY,
          weekly_pct INTEGER NOT NULL,
          weekly_reset INTEGER,
          five_hour_pct INTEGER,
          five_hour_reset INTEGER
        );
        CREATE TABLE IF NOT EXISTS perf_samples (
          tool TEXT NOT NULL,
          key TEXT NOT NULL,
          model TEXT,
          request_ms INTEGER,
          first_ms INTEGER,
          last_ms INTEGER,
          output_tokens INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (tool, key)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_perf_first ON perf_samples(first_ms) WHERE first_ms IS NOT NULL;
        CREATE TABLE IF NOT EXISTS perf_cursors (
          source TEXT PRIMARY KEY,
          size INTEGER NOT NULL,
          identity INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS deepseek_balance_samples (
          ts INTEGER NOT NULL,
          currency TEXT NOT NULL,
          amount REAL NOT NULL,
          PRIMARY KEY (ts, currency)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS idx_turns_ts ON turns(ts);
        CREATE INDEX IF NOT EXISTS idx_turns_tool ON turns(tool, ts);
        CREATE INDEX IF NOT EXISTS idx_turns_session ON turns(tool, session_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_turns_event ON turns(tool, event_id) WHERE event_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_turns_backfill ON turns(id) WHERE cost = 0 AND tool != 'hermes' AND model IS NOT NULL AND model != '';
        CREATE INDEX IF NOT EXISTS idx_turns_source ON turns(tool, source_instance, ts);
        CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(tool, updated);
        ");
        // Column migrations for databases created by an older schema.
        EnsureColumn("turns", "event_id", "TEXT");
        EnsureColumn("turns", "cost_quality", "TEXT NOT NULL DEFAULT 'estimated'");
        EnsureColumn("turns", "cache_read", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("turns", "cache_write", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("turns", "reasoning_tokens", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("turns", "provider", "TEXT");
        EnsureColumn("turns", "source_instance", "TEXT NOT NULL DEFAULT 'local'");
        EnsureColumn("turns", "pricing_version", "TEXT");
        EnsureColumn("scan_state", "context", "TEXT");
        EnsureColumn("scan_state", "identity", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("subscriptions", "end_date", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("session_totals", "reasoning", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("openrouter_snapshots", "credits_total", "REAL");
        EnsureColumn("openrouter_snapshots", "credits_usage", "REAL");
        EnsureColumn("openrouter_snapshots", "account_usage", "REAL");
        EnsureColumn("openrouter_snapshots", "account_balance", "REAL");
        EnsureColumn("openrouter_snapshots", "is_management_key", "INTEGER NOT NULL DEFAULT 0");
        if (UserVersion() < SchemaVersion)
            Exec($"PRAGMA user_version = {SchemaVersion};");
    }

    private void EnsureColumn(string table, string column, string decl)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var cmd = Command($"PRAGMA table_info({table});"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read()) existing.Add(reader.GetString(1));
        }
        if (!existing.Contains(column)) Exec($"ALTER TABLE {table} ADD COLUMN {column} {decl};");
    }

    // MARK: - Settings

    private void LoadSettingsCache()
    {
        _settingsCache.Clear();
        using var cmd = Command("SELECT k, v FROM settings;");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            _settingsCache[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
    }

    public string? GetSetting(string key)
    {
        lock (_lock)
        {
            EnsureOpen();
            return _settingsCache.TryGetValue(key, out var v) ? v : null;
        }
    }

    public bool SetSetting(string key, string? value)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "INSERT INTO settings (k, v) VALUES (@p0, @p1) ON CONFLICT(k) DO UPDATE SET v=excluded.v;");
            cmd.Parameters.AddWithValue("@p0", key);
            cmd.Parameters.AddWithValue("@p1", (object?)value ?? DBNull.Value);
            var ok = cmd.ExecuteNonQuery() >= 0;
            if (ok) _settingsCache[key] = value;
            return ok;
        }
    }

    // MARK: - Subscriptions

    public List<Subscription> GetSubscriptions()
    {
        lock (_lock)
        {
            EnsureOpen();
            var outList = new List<Subscription>();
            using var cmd = Command(
                "SELECT id, name, plan, start_date, end_date, cycle, price, currency FROM subscriptions ORDER BY start_date;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                outList.Add(new Subscription(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5),
                    reader.GetDouble(6), reader.GetString(7)));
            }
            return outList;
        }
    }

    /// <summary>Same persistence-layer validation as the Mac upsert.</summary>
    public bool UpsertSubscription(Subscription s)
    {
        if (!double.IsFinite(s.Price) || s.Price < 0 || s.Price >= 1_000_000
            || s.StartDate <= 0
            || (s.EndDate != 0 && s.EndDate <= s.StartDate)
            || (s.Cycle != "monthly" && s.Cycle != "yearly" && s.Cycle != "weekly")
            || s.StartDate >= 4_102_444_800L)
            return false;
        lock (_lock)
        {
            EnsureOpen();
            if (s.Id == 0)
            {
                using var cmd = Command(
                    "INSERT INTO subscriptions (name, plan, start_date, end_date, cycle, price, currency) VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6);");
                BindSubscription(cmd, s);
                return cmd.ExecuteNonQuery() == 1;
            }
            using var cmd2 = Command(@"
                INSERT INTO subscriptions (id, name, plan, start_date, end_date, cycle, price, currency)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name, plan=excluded.plan,
                  start_date=excluded.start_date, end_date=excluded.end_date,
                  cycle=excluded.cycle, price=excluded.price, currency=excluded.currency;");
            cmd2.Parameters.AddWithValue("@p0", s.Id);
            BindSubscription(cmd2, s, startIndex: 1);
            return cmd2.ExecuteNonQuery() == 1;
        }
    }

    private static void BindSubscription(SqliteCommand cmd, Subscription s, int startIndex = 0)
    {
        cmd.Parameters.AddWithValue($"@p{startIndex}", s.Name);
        cmd.Parameters.AddWithValue($"@p{startIndex + 1}", s.Plan);
        cmd.Parameters.AddWithValue($"@p{startIndex + 2}", s.StartDate);
        cmd.Parameters.AddWithValue($"@p{startIndex + 3}", s.EndDate);
        cmd.Parameters.AddWithValue($"@p{startIndex + 4}", s.Cycle);
        cmd.Parameters.AddWithValue($"@p{startIndex + 5}", s.Price);
        cmd.Parameters.AddWithValue($"@p{startIndex + 6}", s.Currency);
    }

    public bool DeleteSubscription(long id)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("DELETE FROM subscriptions WHERE id=@p0;");
            cmd.Parameters.AddWithValue("@p0", id);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    // MARK: - Turns

    /// <summary>
    /// Inserts turn rows. Returns false when the write failed.
    /// DEVIATION from Mac, deliberate and documented (Mac audit S1):
    /// the Mac insert is ON CONFLICT DO NOTHING, so for a Claude
    /// message id that first arrives as a streaming intermediate and
    /// later as the final row, the intermediate (incomplete) values win
    /// and the final values are dropped — the audit measured ~2.74x
    /// undercounting. The Windows insert instead takes the MAX of each
    /// numeric field on conflict (count stays 1, no double counting),
    /// and descriptive fields take the latest insert. The Mac "derived"
    /// cleanup (a real event id replaces a derived row for the same
    /// session/ts/tokens/model) is preserved verbatim.
    /// </summary>
    public bool InsertTurns(IReadOnlyList<TurnRecord> turns)
    {
        if (turns.Count == 0) return true;
        lock (_lock)
        {
            EnsureOpen();
            using var tx = _connection!.BeginTransaction();
            try
            {
                using var insert = Command(@"
                    INSERT INTO turns (
                      tool, session_id, project, model, ts, input_tokens, output_tokens,
                      reasoning_tokens, cache_read, cache_write, cost, provider,
                      source_instance, pricing_version, event_id, cost_quality
                    ) VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15)
                    ON CONFLICT(tool, event_id) WHERE event_id IS NOT NULL DO UPDATE SET
                      input_tokens = MAX(turns.input_tokens, excluded.input_tokens),
                      output_tokens = MAX(turns.output_tokens, excluded.output_tokens),
                      reasoning_tokens = MAX(turns.reasoning_tokens, excluded.reasoning_tokens),
                      cache_read = MAX(turns.cache_read, excluded.cache_read),
                      cache_write = MAX(turns.cache_write, excluded.cache_write),
                      cost = MAX(turns.cost, excluded.cost),
                      ts = MAX(turns.ts, excluded.ts),
                      project = excluded.project,
                      model = excluded.model,
                      provider = excluded.provider,
                      pricing_version = excluded.pricing_version,
                      cost_quality = excluded.cost_quality;", tx);
                using var removeDerived = Command(@"
                    DELETE FROM turns
                    WHERE tool=@p0 AND event_id LIKE 'derived:%' AND event_id<>@p1
                      AND session_id=@p2 AND ts=@p3 AND input_tokens=@p4 AND output_tokens=@p5
                      AND cache_read=@p6 AND cache_write=@p7 AND COALESCE(model, '')=@p8;", tx);
                foreach (var t in turns)
                {
                    var eid = t.EventId ?? $"derived:{t.Tool.RawValue()}:{t.SessionId}:{t.Ts}:{t.InputTokens}:{t.OutputTokens}:{t.CacheRead}:{t.CacheWrite}:{t.Model ?? ""}";
                    insert.Parameters.Clear();
                    insert.Parameters.AddWithValue("@p0", t.Tool.RawValue());
                    insert.Parameters.AddWithValue("@p1", t.SessionId);
                    insert.Parameters.AddWithValue("@p2", (object?)t.Project ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@p3", (object?)t.Model ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@p4", t.Ts);
                    insert.Parameters.AddWithValue("@p5", t.InputTokens);
                    insert.Parameters.AddWithValue("@p6", t.OutputTokens);
                    insert.Parameters.AddWithValue("@p7", t.ReasoningTokens);
                    insert.Parameters.AddWithValue("@p8", t.CacheRead);
                    insert.Parameters.AddWithValue("@p9", t.CacheWrite);
                    insert.Parameters.AddWithValue("@p10", t.Cost);
                    insert.Parameters.AddWithValue("@p11", (object?)t.Provider ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@p12", t.SourceInstance);
                    insert.Parameters.AddWithValue("@p13", (object?)t.PricingVersion ?? DBNull.Value);
                    insert.Parameters.AddWithValue("@p14", eid);
                    insert.Parameters.AddWithValue("@p15", t.CostQuality);
                    insert.ExecuteNonQuery();

                    if (t.EventId is not null)
                    {
                        removeDerived.Parameters.Clear();
                        removeDerived.Parameters.AddWithValue("@p0", t.Tool.RawValue());
                        removeDerived.Parameters.AddWithValue("@p1", eid);
                        removeDerived.Parameters.AddWithValue("@p2", t.SessionId);
                        removeDerived.Parameters.AddWithValue("@p3", t.Ts);
                        removeDerived.Parameters.AddWithValue("@p4", t.InputTokens);
                        removeDerived.Parameters.AddWithValue("@p5", t.OutputTokens);
                        removeDerived.Parameters.AddWithValue("@p6", t.CacheRead);
                        removeDerived.Parameters.AddWithValue("@p7", t.CacheWrite);
                        removeDerived.Parameters.AddWithValue("@p8", t.Model ?? "");
                        removeDerived.ExecuteNonQuery();
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

    public long TurnCount()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("SELECT COUNT(*) FROM turns;");
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public (long Input, long Output, long CacheRead, long CacheWrite, double Cost, long Count) GetTotals(
        long from, long to, string? tool = null)
    {
        lock (_lock)
        {
            EnsureOpen();
            var sql = tool is null
                ? "SELECT COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(cache_read),0), COALESCE(SUM(cache_write),0), COALESCE(SUM(cost),0), COUNT(*) FROM turns WHERE ts >= @p0 AND ts <= @p1;"
                : "SELECT COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0), COALESCE(SUM(cache_read),0), COALESCE(SUM(cache_write),0), COALESCE(SUM(cost),0), COUNT(*) FROM turns WHERE ts >= @p0 AND ts <= @p1 AND tool = @p2;";
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue("@p0", from);
            cmd.Parameters.AddWithValue("@p1", to);
            if (tool is not null) cmd.Parameters.AddWithValue("@p2", tool);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (0, 0, 0, 0, 0, 0);
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetInt64(5));
        }
    }

    /// <summary>
    /// API value: turns repriced at list prices, grouped by tool/model
    /// (Mac apiValue). Stage 1 queries raw turns, not the rollup union;
    /// see the stage-1 report.
    /// </summary>
    public double ApiValue(long from, long to, string? tool = null)
    {
        lock (_lock)
        {
            EnsureOpen();
            var sql = tool is null
                ? "SELECT model, SUM(input_tokens), SUM(output_tokens), SUM(cache_read), SUM(cache_write), tool FROM turns WHERE ts >= @p0 AND ts <= @p1 GROUP BY tool, model, (tool = 'codex' AND input_tokens < cache_read);"
                : "SELECT model, SUM(input_tokens), SUM(output_tokens), SUM(cache_read), SUM(cache_write), tool FROM turns WHERE ts >= @p0 AND ts <= @p1 AND tool = @p2 GROUP BY tool, model, (tool = 'codex' AND input_tokens < cache_read);";
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue("@p0", from);
            cmd.Parameters.AddWithValue("@p1", to);
            if (tool is not null) cmd.Parameters.AddWithValue("@p2", tool);
            var total = 0.0;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var model = reader.IsDBNull(0) ? null : reader.GetString(0);
                var toolValue = reader.GetString(5);
                var cost = Pricing.Estimate(model,
                    reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    inputIncludesCache: toolValue == "codex");
                if (cost is not null) total += cost.Value;
            }
            return total;
        }
    }

    // MARK: - OpenRouter snapshots (minimal stage-1 writer, for OrSpendSince)

    public void InsertOpenRouterSnapshot(long ts, double usageDaily)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(
                "INSERT INTO openrouter_snapshots (ts, usage_daily) VALUES (@p0, @p1) ON CONFLICT(ts) DO UPDATE SET usage_daily=excluded.usage_daily;");
            cmd.Parameters.AddWithValue("@p0", ts);
            cmd.Parameters.AddWithValue("@p1", usageDaily);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// OpenRouter spend since a timestamp: sum of per-UTC-day maxima from
    /// the snapshot history, with the pre-cycle baseline subtracted from
    /// the first day (Mac orSpendSince, verbatim).
    /// </summary>
    public double OrSpendSince(long start)
    {
        lock (_lock)
        {
            EnsureOpen();
            var startDay = start / 86400;
            double baseline = 0;
            using (var cmd = Command("SELECT usage_daily FROM openrouter_snapshots WHERE ts < @p0 ORDER BY ts DESC LIMIT 1;"))
            {
                cmd.Parameters.AddWithValue("@p0", start);
                var v = cmd.ExecuteScalar();
                if (v is not null && v is not DBNull) baseline = Convert.ToDouble(v);
            }
            using var cmd2 = Command(@"
                SELECT ts / 86400 AS utc_day, MAX(usage_daily) FROM openrouter_snapshots
                WHERE ts >= @p0 GROUP BY utc_day;");
            cmd2.Parameters.AddWithValue("@p0", start);
            var total = 0.0;
            using var reader = cmd2.ExecuteReader();
            while (reader.Read())
            {
                var day = reader.GetInt64(0);
                var maxDaily = reader.GetDouble(1);
                total += day == startDay ? Math.Max(maxDaily - baseline, 0) : maxDaily;
            }
            return total;
        }
    }

    // MARK: - Helpers

    private void EnsureOpen()
    {
        if (_connection is null) Open();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }
}
