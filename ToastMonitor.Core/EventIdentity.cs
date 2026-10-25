using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToastMonitor.Core;

/// <summary>
/// Stable identity keys for usage events, ported from Mac
/// Sources/ToastMonitor/Core/EventIdentity.swift.
/// Callers that have structured usage/content objects must canonicalise
/// them with <see cref="CanonicalJson(JsonElement)"/> (or the object
/// overload) before passing them in, exactly as the Mac code passes
/// canonicalJSON(usage) / canonicalJSON(message content).
/// </summary>
public static class EventIdentity
{
    public static string Digest(IEnumerable<string> components)
    {
        var payload = string.Join("\u001f", components);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Canonical JSON with sorted object keys, mirroring Mac JSONSerialization(.sortedKeys).</summary>
    public static string CanonicalJson(JsonElement value) => CanonicalJsonElement(value);

    public static string CanonicalJson(object? value)
    {
        if (value is null) return "";
        if (value is JsonElement element) return CanonicalJsonElement(element);
        if (value is string s) return JsonSerializer.Serialize(s);
        if (value is bool b) return b ? "true" : "false";
        if (value is IDictionary<string, object?> dict)
        {
            var parts = dict.Keys.OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => JsonSerializer.Serialize(k) + ":" + CanonicalJson(dict[k]));
            return "{" + string.Join(",", parts) + "}";
        }
        if (value is System.Collections.IEnumerable enumerable)
        {
            var parts = new List<string>();
            foreach (var item in enumerable) parts.Add(CanonicalJson(item));
            return "[" + string.Join(",", parts) + "]";
        }
        if (value is IFormattable formattable)
            return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(value.ToString());
    }

    private static string CanonicalJsonElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",",
            value.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalJsonElement(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",",
            value.EnumerateArray().Select(CanonicalJsonElement)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => "null",
    };

    /// <summary>
    /// Claude event key. When a message id exists it is the key; otherwise
    /// the fallback digest is anchored on the stable byte offset, NOT the
    /// timestamp (Mac comment: untimestamped rows resolve to now, which
    /// would mint a new digest on every replay).
    /// </summary>
    public static string Claude(
        string sessionId, string? messageId, string? messageIdAlt,
        long offset, string? model, string usageCanonicalJson, string contentCanonicalJson)
    {
        if (!string.IsNullOrEmpty(messageId)) return $"claude:{sessionId}:{messageId}";
        if (!string.IsNullOrEmpty(messageIdAlt)) return $"claude:{sessionId}:{messageIdAlt}";
        var stable = Digest(new[] { sessionId, offset.ToString(System.Globalization.CultureInfo.InvariantCulture), model ?? "", usageCanonicalJson, contentCanonicalJson });
        return $"claude:{sessionId}:fallback:{stable}";
    }

    /// <summary>
    /// Codex event key. Two token_count events in the same second with
    /// identical last_token_usage collide without the byte offset, so the
    /// offset is part of the digest (Mac comment).
    /// NOTE: this key alone does not implement the Codex "total delta"
    /// rule (repeated rate-limit events re-reporting the same total must
    /// be collapsed by the parser, Mac audit S3). That rule lives in the
    /// parser stage, not here; see the stage-1 report.
    /// </summary>
    public static string Codex(
        string sessionId, long timestamp, string? model, string usageCanonicalJson, long offset)
    {
        return $"codex:{sessionId}:{Digest(new[] { sessionId, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), model ?? "", usageCanonicalJson, offset.ToString(System.Globalization.CultureInfo.InvariantCulture) })}";
    }

    public static string Omp(
        string relativePath, string sessionId, string? messageId,
        string usageCanonicalJson, string messageCanonicalJson, string timestamp)
    {
        if (!string.IsNullOrEmpty(messageId)) return $"omp:{relativePath}:{messageId}";
        return $"omp:{relativePath}:fallback:{Digest(new[] { sessionId, usageCanonicalJson, messageCanonicalJson, timestamp })}";
    }
}
