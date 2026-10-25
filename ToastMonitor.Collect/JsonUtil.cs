using System.Globalization;
using System.Text.Json;

namespace ToastMonitor.Collect;

/// <summary>Small System.Text.Json helpers mirroring the Mac parsers' NSDictionary access.</summary>
public static class JsonUtil
{
    public static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    public static JsonElement? Get(JsonElement obj, string name) =>
        TryGet(obj, name, out var v) ? v : null;

    public static string? GetString(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static long GetLong(JsonElement obj, string name, long fallback = 0)
    {
        if (!TryGet(obj, name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)) return (long)d;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) return p;
        return fallback;
    }

    public static double GetDouble(JsonElement obj, string name, double fallback = 0)
    {
        if (!TryGet(obj, name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) return p;
        return fallback;
    }

    public static bool GetBool(JsonElement obj, string name, bool fallback = false)
    {
        if (!TryGet(obj, name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            _ => fallback,
        };
    }

    /// <summary>ISO-8601 (with or without fractional seconds) to unix seconds, like FileScanner.parseISO.</summary>
    public static long? ParseIso(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.ToUnixTimeSeconds();
        return null;
    }

    /// <summary>Epoch seconds, or epoch milliseconds when the value looks like ms.</summary>
    public static long Sec(long v) => v > 1_000_000_000_000L ? v / 1000 : v;

    public static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Parse one JSON line; returns null for blank/malformed lines (Mac skips them silently).</summary>
    public static JsonElement? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    /// <summary>Serialize a string dictionary to JSON (for scan-state contexts).</summary>
    public static string? ContextJson(IDictionary<string, string> values)
    {
        try { return JsonSerializer.Serialize(values); } catch { return null; }
    }

    public static Dictionary<string, string> ParseContext(string? context)
    {
        var outMap = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(context)) return outMap;
        try
        {
            using var doc = JsonDocument.Parse(context);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outMap;
            foreach (var p in doc.RootElement.EnumerateObject())
                outMap[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        }
        catch { /* malformed context is treated as empty, like the Mac code */ }
        return outMap;
    }

    public static string? ContextWithFullRescan(string? context, bool pending)
    {
        var map = ParseContext(context);
        if (pending) map["_full_rescan"] = "true"; else map.Remove("_full_rescan");
        return ContextJson(map);
    }

    public static bool ContextNeedsFullRescan(string? context) =>
        ParseContext(context).TryGetValue("_full_rescan", out var v) && v == "true";

    public static int ContextStallCount(string? context) =>
        ParseContext(context).TryGetValue("_stall_count", out var v) && int.TryParse(v, out var n) ? n : 0;

    public static string? ContextWithStallCount(string? context, int count)
    {
        var map = ParseContext(context);
        if (count > 0) map["_stall_count"] = count.ToString(CultureInfo.InvariantCulture);
        else map.Remove("_stall_count");
        return ContextJson(map);
    }
}
