using Microsoft.Data.Sqlite;

namespace ToastMonitor.Core;

/// <summary>
/// Mac Database turn_rollup port: per-15-minute sums of `turns`, kept by
/// SQLite triggers on every INSERT/UPDATE/DELETE so no write path can
/// forget it. 15 minutes divides every real UTC offset, so each bucket
/// lies inside one local day and day grouping stays exact in any zone.
/// Aggregates read whole buckets from the rollup plus partial edges from
/// raw `turns` (Mac usageSource). When the rollup is unavailable the span
/// collapses to (to+1, to+1) and the raw branches cover the whole range,
/// so the same query is the correctness reference in both modes.
/// </summary>
public sealed partial class ToastDatabase
{
    public const long RollupBucketSeconds = 900;
    private const string RollupSchemaVersion = "1";

    /// <summary>Test hook (Mac aggregatesUseRollup): false routes every aggregate through raw `turns`.</summary>
    public bool AggregatesUseRollup { get; set; } = true;

    /// <summary>Grouping key of a `turns` row, qualified by `row` (NEW/OLD/turns).</summary>
    private static string[] RollupKey(string row) => new[]
    {
        $"{row}.ts - (({row}.ts % 900) + 900) % 900",
        $"{row}.tool",
        $"COALESCE({row}.model, '')",
        $"{row}.model IS NULL",
        $"{row}.cost_quality",
        $"COALESCE(lower(trim({row}.provider)) = 'deepseek', 0)",
    };

    private static readonly string[] RollupKeyColumns =
        { "bucket", "tool", "model", "model_null", "cost_quality", "deepseek" };
    private static readonly string[] RollupSumColumns =
        { "input_tokens", "output_tokens", "cache_read", "cache_write", "cost" };

    private static string RollupMatch(string row) =>
        string.Join(" AND ", RollupKeyColumns.Zip(RollupKey(row), (col, expr) => $"{col} = ({expr})"));

    private static string RollupAdd(string row)
    {
        var columns = string.Join(", ", RollupKeyColumns.Concat(RollupSumColumns).Append("n"));
        var values = string.Join(", ", RollupKey(row)
            .Concat(RollupSumColumns.Select(c => $"{row}.{c}")).Append("1"));
        var updates = string.Join(", ", RollupSumColumns.Append("n").Select(c => $"{c} = {c} + excluded.{c}"));
        return $"INSERT INTO turn_rollup ({columns}) VALUES ({values}) " +
               $"ON CONFLICT({string.Join(", ", RollupKeyColumns)}) DO UPDATE SET {updates};";
    }

    private static string RollupRemove(string row)
    {
        var updates = string.Join(", ", RollupSumColumns.Select(c => $"{c} = {c} - {row}.{c}").Append("n = n - 1"));
        return $"UPDATE turn_rollup SET {updates} WHERE {RollupMatch(row)}; " +
               $"DELETE FROM turn_rollup WHERE {RollupMatch(row)} AND n <= 0;";
    }

    private static string RollupSchemaSql
    {
        get
        {
            var watched = string.Join(", ", new[] { "ts", "tool", "model", "provider", "cost_quality" }.Concat(RollupSumColumns));
            return $@"
        CREATE TABLE turn_rollup (
          bucket INTEGER NOT NULL,
          tool TEXT NOT NULL,
          model TEXT NOT NULL,
          model_null INTEGER NOT NULL,
          cost_quality TEXT NOT NULL,
          deepseek INTEGER NOT NULL,
          input_tokens INTEGER NOT NULL,
          output_tokens INTEGER NOT NULL,
          cache_read INTEGER NOT NULL,
          cache_write INTEGER NOT NULL,
          cost REAL NOT NULL,
          n INTEGER NOT NULL,
          PRIMARY KEY ({string.Join(", ", RollupKeyColumns)})
        ) WITHOUT ROWID;
        CREATE TRIGGER turn_rollup_insert AFTER INSERT ON turns BEGIN
        {RollupAdd("NEW")}
        END;
        CREATE TRIGGER turn_rollup_delete AFTER DELETE ON turns BEGIN
        {RollupRemove("OLD")}
        END;
        CREATE TRIGGER turn_rollup_update AFTER UPDATE OF {watched} ON turns BEGIN
        {RollupRemove("OLD")}
        {RollupAdd("NEW")}
        END;";
        }
    }

    private static string RollupRebuildSql
    {
        get
        {
            var key = string.Join(", ", RollupKey("turns"));
            var groups = string.Join(", ", Enumerable.Range(1, 6));
            return $@"
        DELETE FROM turn_rollup;
        INSERT INTO turn_rollup ({string.Join(", ", RollupKeyColumns.Concat(RollupSumColumns).Append("n"))})
        SELECT {key}, {string.Join(", ", RollupSumColumns.Select(c => $"SUM({c})"))}, COUNT(*)
        FROM turns GROUP BY {groups};";
        }
    }

    /// <summary>Whole-bucket span [lo, hi) inside [from, to]; (to+1, to+1) when the rollup is off.</summary>
    public static (long Lo, long Hi) RollupSpan(long from, long to, bool useRollup)
    {
        const long b = RollupBucketSeconds;
        to = Math.Min(to, long.MaxValue / 4);
        long floorBucket(long x) => x - ((x % b) + b) % b;
        var lo = floorBucket(from) == from ? from : floorBucket(from) + b;
        var hi = floorBucket(to + 1);
        return useRollup && lo < hi ? (lo, hi) : (to + 1, to + 1);
    }

    /// <summary>Row source for aggregates over ts in [from, to]. Bind @lo, @hi, @from, @to.</summary>
    private const string UsageSource = @"
    SELECT bucket, tool, CASE WHEN model_null THEN NULL ELSE model END AS model,
           cost_quality, deepseek, input_tokens, output_tokens, cache_read, cache_write, cost, n
    FROM turn_rollup WHERE bucket >= @lo AND bucket < @hi
    UNION ALL
    SELECT ts - ((ts % 900) + 900) % 900, tool, model, cost_quality,
           COALESCE(lower(trim(provider)) = 'deepseek', 0),
           input_tokens, output_tokens, cache_read, cache_write, cost, 1
    FROM turns WHERE ts >= @from AND ts < @lo
    UNION ALL
    SELECT ts - ((ts % 900) + 900) % 900, tool, model, cost_quality,
           COALESCE(lower(trim(provider)) = 'deepseek', 0),
           input_tokens, output_tokens, cache_read, cache_write, cost, 1
    FROM turns WHERE ts >= @hi AND ts <= @to";

    private SqliteCommand UsageCommand(string selectList, string groupBy, string? orderBy, long from, long to)
    {
        var (lo, hi) = RollupSpan(from, to, AggregatesUseRollup);
        var sql = $"SELECT {selectList} FROM ({UsageSource}) GROUP BY {groupBy}";
        if (orderBy is not null) sql += " ORDER BY " + orderBy;
        sql += ";";
        var cmd = Command(sql);
        cmd.Parameters.AddWithValue("@lo", lo);
        cmd.Parameters.AddWithValue("@hi", hi);
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", to);
        return cmd;
    }

    /// <summary>
    /// Installs the rollup on first run or after a definition change, and
    /// otherwise checks it against `turns`, rebuilding on any mismatch.
    /// On failure the rollup is disabled and aggregates read raw turns.
    /// </summary>
    public void EnsureRollup()
    {
        lock (_lock)
        {
            EnsureOpen();
            AggregatesUseRollup = true;
            try
            {
                if (GetSetting("turn_rollup_schema") != RollupSchemaVersion || !RollupTriggersPresent())
                {
                    using var tx = _connection!.BeginTransaction();
                    try
                    {
                        ExecTx(tx, @"DROP TRIGGER IF EXISTS turn_rollup_insert;
                            DROP TRIGGER IF EXISTS turn_rollup_delete;
                            DROP TRIGGER IF EXISTS turn_rollup_update;
                            DROP TABLE IF EXISTS turn_rollup;");
                        ExecTx(tx, RollupSchemaSql);
                        ExecTx(tx, RollupRebuildSql);
                        ExecTx(tx, "INSERT INTO settings (k, v) VALUES ('turn_rollup_schema', '1') " +
                                   "ON CONFLICT(k) DO UPDATE SET v=excluded.v;");
                        tx.Commit();
                        _settingsCache["turn_rollup_schema"] = RollupSchemaVersion;
                    }
                    catch { try { tx.Rollback(); } catch { /* already rolled back */ } throw; }
                    return;
                }
                if (RollupMismatchCount() != 0)
                {
                    DebugLog.Trace("rollup drift detected; rebuilding");
                    using var tx = _connection!.BeginTransaction();
                    try { ExecTx(tx, RollupRebuildSql); tx.Commit(); }
                    catch { try { tx.Rollback(); } catch { /* already rolled back */ } throw; }
                }
            }
            catch
            {
                AggregatesUseRollup = false;
                DebugLog.Trace("rollup install failed; aggregates read raw turns");
            }
        }
    }

    private void ExecTx(SqliteTransaction tx, string sql)
    {
        using var cmd = Command(sql, tx);
        cmd.ExecuteNonQuery();
    }

    private bool RollupTriggersPresent()
    {
        using var cmd = Command(@"
            SELECT COUNT(*) FROM sqlite_master
            WHERE (type='trigger' AND name IN ('turn_rollup_insert','turn_rollup_delete','turn_rollup_update'))
               OR (type='table' AND name='turn_rollup');");
        return Convert.ToInt64(cmd.ExecuteScalar()) == 4;
    }

    /// <summary>Number of rollup groups whose sums differ from raw `turns` (0 = consistent, -1 = check failed). Cost tolerance 1e-6.</summary>
    public int RollupMismatchCount()
    {
        lock (_lock)
        {
            EnsureOpen();
            try
            {
                var keys = string.Join(", ", RollupKeyColumns);
                var rawKey = string.Join(", ", RollupKey("turns")
                    .Zip(RollupKeyColumns, (expr, col) => $"{expr} AS {col}"));
                var sums = string.Join(", ", RollupSumColumns.Select(c => $"SUM({c}) AS {c}"));
                using var cmd = Command($@"
                    SELECT COUNT(*) FROM (
                      SELECT {sums}, SUM(n) AS n FROM (
                        SELECT {rawKey}, {string.Join(", ", RollupSumColumns)}, 1 AS n FROM turns
                        UNION ALL
                        SELECT {keys}, {string.Join(", ", RollupSumColumns.Select(c => $"-{c}"))}, -n FROM turn_rollup
                      ) GROUP BY {keys}
                    ) WHERE n <> 0 OR input_tokens <> 0 OR output_tokens <> 0
                         OR cache_read <> 0 OR cache_write <> 0 OR abs(cost) > 1e-6;");
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
            catch { return -1; }
        }
    }

    /// <summary>Rebuild the rollup from raw turns (used after bulk maintenance that bypasses triggers).</summary>
    public void RebuildRollup()
    {
        lock (_lock)
        {
            EnsureOpen();
            Exec(RollupRebuildSql);
        }
    }
}
