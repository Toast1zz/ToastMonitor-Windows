using Microsoft.Data.Sqlite;

namespace ToastMonitor.Core;

/// <summary>One scan_state row (Mac ParserStateStore scanState).</summary>
public sealed record ScanState(long Size, long Mtime, long Identity, string? Context);

/// <summary>One session_totals row, keyed "tool|sessionId" like the Mac store.</summary>
public sealed record SessionTotalsEntry(
    string Tool, string SessionId, long Input, long Output, long Reasoning,
    long CacheRead, long CacheWrite, double Cost, long Updated);

/// <summary>One perf_samples row.</summary>
public sealed record PerfSample(
    ToolKind Tool, string Key, string? Model, long? RequestMs, long? FirstMs,
    long? LastMs, long OutputTokens)
{
    /// <summary>Mac PerfSample.merge: first non-null model/request/first wins;
    /// last only grows (max); output tokens only grow (max).</summary>
    public PerfSample Merge(PerfSample other) => new(
        Tool, Key, Model ?? other.Model,
        RequestMs ?? other.RequestMs, FirstMs ?? other.FirstMs,
        LastMs is null ? other.LastMs : other.LastMs is null ? LastMs : Math.Max(LastMs.Value, other.LastMs.Value),
        Math.Max(OutputTokens, other.OutputTokens));
}

/// <summary>
/// Collector-stage database APIs (stage 2). These live in a partial class so
/// the stage-1 file is untouched. All methods take the same re-entrant lock
/// as the rest of ToastDatabase.
/// </summary>
public sealed partial class ToastDatabase
{
    public ScanState GetScanState(string source)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("SELECT size, mtime, identity, context FROM scan_state WHERE source=@p0;");
            cmd.Parameters.AddWithValue("@p0", source);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return new ScanState(0, 0, 0, null);
            return new ScanState(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
    }

    public bool SetScanState(string source, long size, long mtime, long identity, string? context)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO scan_state (source, size, mtime, identity, context, last_scan)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5)
                ON CONFLICT(source) DO UPDATE SET size=excluded.size, mtime=excluded.mtime,
                  identity=excluded.identity, context=excluded.context, last_scan=excluded.last_scan;");
            cmd.Parameters.AddWithValue("@p0", source);
            cmd.Parameters.AddWithValue("@p1", size);
            cmd.Parameters.AddWithValue("@p2", mtime);
            cmd.Parameters.AddWithValue("@p3", identity);
            cmd.Parameters.AddWithValue("@p4", (object?)context ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p5", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return cmd.ExecuteNonQuery() >= 0;
        }
    }

    public Dictionary<string, SessionTotalsEntry> GetSessionTotals()
    {
        lock (_lock)
        {
            EnsureOpen();
            var outMap = new Dictionary<string, SessionTotalsEntry>();
            using var cmd = Command("SELECT tool, session_id, input, output, reasoning, cache_read, cache_write, cost, updated FROM session_totals;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var tool = reader.GetString(0);
                var sid = reader.GetString(1);
                outMap[tool + "|" + sid] = new SessionTotalsEntry(tool, sid,
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetDouble(7), reader.GetInt64(8));
            }
            return outMap;
        }
    }

    public bool SetSessionTotals(string key, string tool, long input, long output, long reasoning,
        long cacheRead, long cacheWrite, double cost, long updated)
    {
        // Mac keys are "tool|sessionId"; the session_id column holds the part after the first '|'.
        var sid = key;
        var bar = key.IndexOf('|');
        if (bar >= 0) sid = key[(bar + 1)..];
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO session_totals (tool, session_id, input, output, reasoning, cache_read, cache_write, cost, updated)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
                ON CONFLICT(tool, session_id) DO UPDATE SET input=excluded.input, output=excluded.output,
                  reasoning=excluded.reasoning, cache_read=excluded.cache_read, cache_write=excluded.cache_write,
                  cost=excluded.cost, updated=excluded.updated;");
            cmd.Parameters.AddWithValue("@p0", tool);
            cmd.Parameters.AddWithValue("@p1", sid);
            cmd.Parameters.AddWithValue("@p2", input);
            cmd.Parameters.AddWithValue("@p3", output);
            cmd.Parameters.AddWithValue("@p4", reasoning);
            cmd.Parameters.AddWithValue("@p5", cacheRead);
            cmd.Parameters.AddWithValue("@p6", cacheWrite);
            cmd.Parameters.AddWithValue("@p7", cost);
            cmd.Parameters.AddWithValue("@p8", updated);
            return cmd.ExecuteNonQuery() >= 0;
        }
    }

    public bool UpsertSessions(IReadOnlyList<SessionInfo> sessions)
    {
        if (sessions.Count == 0) return true;
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO sessions (tool, session_id, title, project, model, created, updated)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)
                ON CONFLICT(tool, session_id) DO UPDATE SET title=excluded.title, project=excluded.project,
                  model=excluded.model, created=excluded.created, updated=MAX(sessions.updated, excluded.updated);");
            foreach (var s in sessions)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@p0", s.Tool.RawValue());
                cmd.Parameters.AddWithValue("@p1", s.SessionId);
                cmd.Parameters.AddWithValue("@p2", (object?)s.Title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p3", (object?)s.Project ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p4", (object?)s.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p5", s.Created);
                cmd.Parameters.AddWithValue("@p6", s.Updated);
                cmd.ExecuteNonQuery();
            }
            return true;
        }
    }

    /// <summary>
    /// Single-transaction commit for one collector scan: turns + sessions +
    /// staged scan-state / totals / setting writes. The validator runs under
    /// the database lock first (all ToastDatabase writers share it, so no
    /// other writer can interleave between validation and the transaction).
    /// Returns false when validation fails or any write fails; nothing is
    /// written in that case.
    /// </summary>
    public bool ApplyScan(
        IReadOnlyList<TurnRecord> turns, IReadOnlyList<SessionInfo> sessions,
        IReadOnlyDictionary<string, ScanState> scanUpdates,
        IReadOnlyDictionary<string, SessionTotalsEntry> totalsUpdates,
        IReadOnlyDictionary<string, string?> settingUpdates,
        Func<bool>? validate = null)
    {
        lock (_lock)
        {
            EnsureOpen();
            if (validate is not null && !validate()) return false;
            using var tx = _connection!.BeginTransaction();
            try
            {
                InsertTurnsInTransaction(tx, turns);
                UpsertSessionsInTransaction(tx, sessions);
                foreach (var (source, st) in scanUpdates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    SetScanStateInTransaction(tx, source, st);
                foreach (var (key, t) in totalsUpdates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    SetSessionTotalsInTransaction(tx, key, t);
                foreach (var (k, v) in settingUpdates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    SetSettingInTransaction(tx, k, v);
                tx.Commit();
                // Keep the settings cache consistent with the committed values.
                foreach (var (k, v) in settingUpdates) _settingsCache[k] = v;
                return true;
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                return false;
            }
        }
    }

    private void InsertTurnsInTransaction(SqliteTransaction tx, IReadOnlyList<TurnRecord> turns)
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
              project = excluded.project, model = excluded.model, provider = excluded.provider,
              pricing_version = excluded.pricing_version, cost_quality = excluded.cost_quality;", tx);
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
    }

    private void UpsertSessionsInTransaction(SqliteTransaction tx, IReadOnlyList<SessionInfo> sessions)
    {
        using var cmd = Command(@"
            INSERT INTO sessions (tool, session_id, title, project, model, created, updated)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)
            ON CONFLICT(tool, session_id) DO UPDATE SET title=excluded.title, project=excluded.project,
              model=excluded.model, created=excluded.created, updated=MAX(sessions.updated, excluded.updated);", tx);
        foreach (var s in sessions)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@p0", s.Tool.RawValue());
            cmd.Parameters.AddWithValue("@p1", s.SessionId);
            cmd.Parameters.AddWithValue("@p2", (object?)s.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p3", (object?)s.Project ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p4", (object?)s.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p5", s.Created);
            cmd.Parameters.AddWithValue("@p6", s.Updated);
            cmd.ExecuteNonQuery();
        }
    }

    private void SetScanStateInTransaction(SqliteTransaction tx, string source, ScanState st)
    {
        using var cmd = Command(@"
            INSERT INTO scan_state (source, size, mtime, identity, context, last_scan)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5)
            ON CONFLICT(source) DO UPDATE SET size=excluded.size, mtime=excluded.mtime,
              identity=excluded.identity, context=excluded.context, last_scan=excluded.last_scan;", tx);
        cmd.Parameters.AddWithValue("@p0", source);
        cmd.Parameters.AddWithValue("@p1", st.Size);
        cmd.Parameters.AddWithValue("@p2", st.Mtime);
        cmd.Parameters.AddWithValue("@p3", st.Identity);
        cmd.Parameters.AddWithValue("@p4", (object?)st.Context ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@p5", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    private void SetSessionTotalsInTransaction(SqliteTransaction tx, string key, SessionTotalsEntry t)
    {
        using var cmd = Command(@"
            INSERT INTO session_totals (tool, session_id, input, output, reasoning, cache_read, cache_write, cost, updated)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
            ON CONFLICT(tool, session_id) DO UPDATE SET input=excluded.input, output=excluded.output,
              reasoning=excluded.reasoning, cache_read=excluded.cache_read, cache_write=excluded.cache_write,
              cost=excluded.cost, updated=excluded.updated;", tx);
        cmd.Parameters.AddWithValue("@p0", t.Tool);
        cmd.Parameters.AddWithValue("@p1", t.SessionId);
        cmd.Parameters.AddWithValue("@p2", t.Input);
        cmd.Parameters.AddWithValue("@p3", t.Output);
        cmd.Parameters.AddWithValue("@p4", t.Reasoning);
        cmd.Parameters.AddWithValue("@p5", t.CacheRead);
        cmd.Parameters.AddWithValue("@p6", t.CacheWrite);
        cmd.Parameters.AddWithValue("@p7", t.Cost);
        cmd.Parameters.AddWithValue("@p8", t.Updated);
        cmd.ExecuteNonQuery();
    }

    private void SetSettingInTransaction(SqliteTransaction tx, string key, string? value)
    {
        using var cmd = Command(
            "INSERT INTO settings (k, v) VALUES (@p0, @p1) ON CONFLICT(k) DO UPDATE SET v=excluded.v;", tx);
        cmd.Parameters.AddWithValue("@p0", key);
        cmd.Parameters.AddWithValue("@p1", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Reprice turns that still carry cost 0 (Mac backfillCosts).</summary>
    public void BackfillCosts()
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("SELECT id, model, input_tokens, output_tokens, cache_read, cache_write, tool FROM turns WHERE cost = 0 AND model IS NOT NULL AND model != '';");
            var rows = new List<(long Id, string? Model, long In, long Out, long Cr, long Cw, string Tool)>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6)));
            foreach (var r in rows)
            {
                var cost = Pricing.Estimate(r.Model, r.In, r.Out, r.Cr, r.Cw, inputIncludesCache: r.Tool == "codex");
                if (cost is null) continue;
                using var upd = Command("UPDATE turns SET cost=@p0, cost_quality='estimated', pricing_version=@p1 WHERE id=@p2;");
                upd.Parameters.AddWithValue("@p0", cost.Value);
                upd.Parameters.AddWithValue("@p1", Pricing.Version);
                upd.Parameters.AddWithValue("@p2", r.Id);
                upd.ExecuteNonQuery();
            }
        }
    }

    // MARK: - Perf / DeepSeek / quota sample writers

    public (long Size, long Identity)? GetPerfCursor(string source)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("SELECT size, identity FROM perf_cursors WHERE source=@p0;");
            cmd.Parameters.AddWithValue("@p0", source);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt64(0), reader.GetInt64(1));
        }
    }

    public bool SetPerfCursor(string source, long size, long identity)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO perf_cursors (source, size, identity) VALUES (@p0, @p1, @p2)
                ON CONFLICT(source) DO UPDATE SET size=excluded.size, identity=excluded.identity;");
            cmd.Parameters.AddWithValue("@p0", source);
            cmd.Parameters.AddWithValue("@p1", size);
            cmd.Parameters.AddWithValue("@p2", identity);
            return cmd.ExecuteNonQuery() >= 0;
        }
    }

    public bool UpsertPerfSamples(IReadOnlyList<PerfSample> samples)
    {
        if (samples.Count == 0) return true;
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO perf_samples (tool, key, model, request_ms, first_ms, last_ms, output_tokens)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)
                ON CONFLICT(tool, key) DO UPDATE SET model=COALESCE(perf_samples.model, excluded.model),
                  request_ms=COALESCE(perf_samples.request_ms, excluded.request_ms),
                  first_ms=COALESCE(perf_samples.first_ms, excluded.first_ms),
                  last_ms=CASE WHEN perf_samples.last_ms IS NULL THEN excluded.last_ms
                               WHEN excluded.last_ms IS NULL THEN perf_samples.last_ms
                               ELSE MAX(perf_samples.last_ms, excluded.last_ms) END,
                  output_tokens=MAX(perf_samples.output_tokens, excluded.output_tokens);");
            foreach (var s in samples)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@p0", s.Tool.RawValue());
                cmd.Parameters.AddWithValue("@p1", s.Key);
                cmd.Parameters.AddWithValue("@p2", (object?)s.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p3", (object?)s.RequestMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p4", (object?)s.FirstMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p5", (object?)s.LastMs ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@p6", s.OutputTokens);
                cmd.ExecuteNonQuery();
            }
            return true;
        }
    }

    public void PruneIncompletePerfSamples(long beforeMs)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command("DELETE FROM perf_samples WHERE first_ms IS NULL AND last_ms < @p0;");
            cmd.Parameters.AddWithValue("@p0", beforeMs);
            cmd.ExecuteNonQuery();
        }
    }

    public bool InsertDeepSeekBalanceSample(long ts, string currency, double amount)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO deepseek_balance_samples (ts, currency, amount) VALUES (@p0, @p1, @p2)
                ON CONFLICT(ts, currency) DO UPDATE SET amount=excluded.amount;");
            cmd.Parameters.AddWithValue("@p0", ts);
            cmd.Parameters.AddWithValue("@p1", currency);
            cmd.Parameters.AddWithValue("@p2", amount);
            return cmd.ExecuteNonQuery() >= 0;
        }
    }

    public bool InsertClaudeQuotaSample(long ts, int? weeklyPct, long? weeklyReset, int? fiveHourPct, long? fiveHourReset)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO claude_quota_samples (ts, weekly_pct, weekly_reset, five_hour_pct, five_hour_reset)
                VALUES (@p0, @p1, @p2, @p3, @p4)
                ON CONFLICT(ts) DO UPDATE SET weekly_pct=excluded.weekly_pct, weekly_reset=excluded.weekly_reset,
                  five_hour_pct=excluded.five_hour_pct, five_hour_reset=excluded.five_hour_reset;");
            cmd.Parameters.AddWithValue("@p0", ts);
            cmd.Parameters.AddWithValue("@p1", (object?)weeklyPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p2", (object?)weeklyReset ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p3", (object?)fiveHourPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p4", (object?)fiveHourReset ?? DBNull.Value);
            return cmd.ExecuteNonQuery() >= 0;
        }
    }

    public bool InsertOpenRouterSnapshotFull(long ts, double usageDaily, double usageWeekly, double usageMonthly,
        double? limitAmount, double? limitRemaining, string? limitReset, bool isFreeTier,
        double? creditsTotal, double? creditsUsage, double? accountUsage, double? accountBalance, bool isManagementKey)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO openrouter_snapshots (ts, usage_daily, usage_weekly, usage_monthly, limit_amount, limit_remaining,
                  limit_reset, is_free_tier, credits_total, credits_usage, account_usage, account_balance, is_management_key)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12)
                ON CONFLICT(ts) DO UPDATE SET usage_daily=excluded.usage_daily, usage_weekly=excluded.usage_weekly,
                  usage_monthly=excluded.usage_monthly, limit_amount=excluded.limit_amount, limit_remaining=excluded.limit_remaining,
                  limit_reset=excluded.limit_reset, is_free_tier=excluded.is_free_tier, credits_total=excluded.credits_total,
                  credits_usage=excluded.credits_usage, account_usage=excluded.account_usage, account_balance=excluded.account_balance,
                  is_management_key=excluded.is_management_key;");
            cmd.Parameters.AddWithValue("@p0", ts);
            cmd.Parameters.AddWithValue("@p1", usageDaily);
            cmd.Parameters.AddWithValue("@p2", usageWeekly);
            cmd.Parameters.AddWithValue("@p3", usageMonthly);
            cmd.Parameters.AddWithValue("@p4", (object?)limitAmount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p5", (object?)limitRemaining ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p6", (object?)limitReset ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p7", isFreeTier ? 1 : 0);
            cmd.Parameters.AddWithValue("@p8", (object?)creditsTotal ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p9", (object?)creditsUsage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p10", (object?)accountUsage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p11", (object?)accountBalance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p12", isManagementKey ? 1 : 0);
            return cmd.ExecuteNonQuery() >= 0;
        }
    }

    public bool InsertOpencodeGoSnapshot(long ts, double? rollingPct, long? rollingReset,
        double? weeklyPct, long? weeklyReset, double? monthlyPct, long? monthlyReset)
    {
        lock (_lock)
        {
            EnsureOpen();
            using var cmd = Command(@"
                INSERT INTO opencodego_snapshots (ts, rolling_pct, rolling_reset, weekly_pct, weekly_reset, monthly_pct, monthly_reset)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6)
                ON CONFLICT(ts) DO UPDATE SET rolling_pct=excluded.rolling_pct, rolling_reset=excluded.rolling_reset,
                  weekly_pct=excluded.weekly_pct, weekly_reset=excluded.weekly_reset,
                  monthly_pct=excluded.monthly_pct, monthly_reset=excluded.monthly_reset;");
            cmd.Parameters.AddWithValue("@p0", ts);
            cmd.Parameters.AddWithValue("@p1", (object?)rollingPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p2", (object?)rollingReset ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p3", (object?)weeklyPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p4", (object?)weeklyReset ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p5", (object?)monthlyPct ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p6", (object?)monthlyReset ?? DBNull.Value);
            return cmd.ExecuteNonQuery() >= 0;
        }
    }
}
