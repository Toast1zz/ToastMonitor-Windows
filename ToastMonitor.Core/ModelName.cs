namespace ToastMonitor.Core;

/// <summary>
/// Reader-friendly model names for display ("claude-opus-5-5" → "Opus 5.5").
/// Ported from Mac Sources/ToastMonitor/Core/ModelName.swift.
/// Display only: the raw id stays in the database, where pricing and
/// aggregation depend on it.
/// </summary>
public static class ModelName
{
    private static readonly Dictionary<string, (string Display, bool Hyphenated)> Brands = new()
    {
        ["gpt"] = ("GPT", true),
        ["glm"] = ("GLM", false),
        ["qwen"] = ("Qwen", false),
        ["grok"] = ("Grok", false),
        ["mimo"] = ("MiMo", false),
        ["minimax"] = ("MiniMax", false),
        ["deepseek"] = ("DeepSeek", false),
    };

    private static readonly HashSet<string> ClaudeFamilies = new() { "opus", "sonnet", "haiku", "fable" };

    public static string Display(string raw)
    {
        var trimmed = raw.Trim();
        // "deepseek/deepseek-v4-flash": the part after the last slash is the model.
        var slash = trimmed.LastIndexOf('/');
        var id = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        if (id.Length == 0) return trimmed;

        var tokens = id.ToLowerInvariant()
            .Split(new[] { '-', '_', ':', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        // A trailing snapshot date ("-20250929") says nothing a reader needs.
        if (tokens.Count > 1 && tokens[^1].Length == 8 && tokens[^1].All(char.IsDigit))
            tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0) return id;

        // "qwen3.7-plus": split the glued version off a known brand.
        var glued = SplitBrand(tokens[0]);
        if (glued is not null) tokens = glued.Concat(tokens.Skip(1)).ToList();

        if (tokens[0] == "claude")
        {
            var claude = Claude(tokens.Skip(1).ToList());
            if (claude is not null) return claude;
        }

        var words = new List<string>();
        var versionRun = "";
        void FlushVersion()
        {
            if (versionRun.Length > 0) { words.Add(versionRun); versionRun = ""; }
        }
        foreach (var token in tokens)
        {
            if (IsVersion(token))
            {
                if (char.IsDigit(token[0]) && versionRun.Length > 0 && char.IsDigit(versionRun[^1]))
                {
                    if (token.Contains('.') || versionRun.Contains('.'))
                    {
                        FlushVersion();
                        versionRun = token;
                    }
                    else
                    {
                        versionRun += "." + token;
                    }
                }
                else
                {
                    FlushVersion();
                    versionRun = char.IsLetter(token[0]) ? char.ToUpperInvariant(token[0]) + token[1..] : token;
                }
            }
            else if (token.All(char.IsLetter))
            {
                FlushVersion();
                words.Add(Brands.TryGetValue(token, out var brand) ? brand.Display : Capitalize(token));
            }
            else
            {
                return id; // shape we do not know: keep it as it was
            }
        }
        FlushVersion();

        // "GPT-5.6": a hyphenated brand sits against its version.
        if (Brands.TryGetValue(tokens[0], out var firstBrand) && firstBrand.Hyphenated
            && words.Count > 1 && char.IsDigit(words[1][0]))
        {
            words = new List<string> { words[0] + "-" + words[1] }.Concat(words.Skip(2)).ToList();
        }
        return string.Join(" ", words);
    }

    /// <summary>"Opus 5.5" from the tokens after "claude". Nil when no family.</summary>
    private static string? Claude(List<string> tokens)
    {
        var family = tokens.FirstOrDefault(t => ClaudeFamilies.Contains(t));
        if (family is null) return null;
        var version = "";
        var extras = new List<string>();
        foreach (var token in tokens)
        {
            if (token == family) continue;
            if (token.All(char.IsDigit))
                version += version.Length == 0 ? token : "." + token;
            else if (token.All(char.IsLetter))
                extras.Add(Capitalize(token));
            else
                return null;
        }
        var name = Capitalize(family);
        var parts = new List<string> { name };
        if (version.Length > 0) parts.Add(version);
        parts.AddRange(extras);
        return string.Join(" ", parts);
    }

    /// <summary>"qwen3.7" → ["qwen", "3.7"] for a known brand; nil otherwise.</summary>
    private static List<string>? SplitBrand(string token)
    {
        var letters = new string(token.TakeWhile(char.IsLetter).ToArray());
        var rest = token[letters.Length..];
        if (rest.Length == 0 || !char.IsDigit(rest[0]) || !Brands.ContainsKey(letters)) return null;
        return new List<string> { letters, rest };
    }

    /// <summary>"5.6", "6", "v4.1", "m3": digits with at most one letter in front.</summary>
    private static bool IsVersion(string token)
    {
        var body = token;
        if (body.Length > 0 && char.IsLetter(body[0])) body = body[1..];
        if (body.Length == 0 || !char.IsDigit(body[0])) return false;
        return body.All(c => char.IsDigit(c) || c == '.') && !body.EndsWith('.');
    }

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
