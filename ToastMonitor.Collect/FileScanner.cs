using System.Text;
using System.Text.Json;

namespace ToastMonitor.Collect;

/// <summary>Incremental JSONL utilities, ported from Mac FileScanner.swift.</summary>
public static class FileScanner
{
    public sealed record Stat(long Size, long Mtime, long Identity);

    /// <summary>
    /// Windows has no inode: identity is the file's creation-time ticks, which
    /// changes on rotation/replacement and stays stable on append. Mtime is
    /// UTC ticks (100ns), stable within this local database.
    /// </summary>
    public static Stat? FileStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return new Stat(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
        }
        catch { return null; }
    }

    public static long? DirMtime(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return null;
            return info.LastWriteTimeUtc.Ticks;
        }
        catch { return null; }
    }

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<string, (List<string> Files, Dictionary<string, long> DirMtimes)> _listCache = new();

    /// <summary>Lists *.jsonl files, skipping dot-prefixed entries, with a directory-mtime cache.</summary>
    public static List<string> ListFiles(string root, int maxDepth = 3)
    {
        var key = root + "\u001f" + maxDepth;
        lock (_cacheLock)
        {
            if (_listCache.TryGetValue(key, out var cached)
                && cached.DirMtimes.TryGetValue(root, out _)
                && cached.DirMtimes.All(kv => DirMtime(kv.Key) == kv.Value))
                return new List<string>(cached.Files);
        }
        var files = new List<string>();
        var dirMtimes = new Dictionary<string, long>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            var mt = DirMtime(dir);
            if (mt is not null) dirMtimes[dir] = mt.Value;
            if (depth >= maxDepth) continue;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch { continue; }
            foreach (var full in entries)
            {
                var name = Path.GetFileName(full);
                if (name.StartsWith('.')) continue;
                if (Directory.Exists(full)) stack.Push((full, depth + 1));
                else if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) files.Add(full);
            }
        }
        lock (_cacheLock) _listCache[key] = (files, dirMtimes);
        return files;
    }

    /// <summary>
    /// Streams parsed JSON objects from <paramref name="path"/> with absolute byte
    /// offsets. Partial trailing lines are not consumed unless they parse.
    /// Returns the new cursor (start + consumed bytes).
    /// </summary>
    public static long ForEachNewJsonLine(string path, long fromOffset, Action<long, JsonElement> body)
    {
        if (!File.Exists(path)) return fromOffset;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var total = stream.Length;
        long start = 0;
        if (fromOffset > 0 && total >= fromOffset) start = fromOffset;
        if (total <= start) return total;
        stream.Seek(start, SeekOrigin.Begin);
        var pending = new List<byte>();
        long consumed = 0;
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            pending.AddRange(buffer.AsSpan(0, read).ToArray());
            var lineStart = 0;
            while (true)
            {
                var nl = pending.IndexOf((byte)'\n', lineStart);
                if (nl < 0) break;
                var lineBytes = pending.GetRange(lineStart, nl - lineStart).ToArray();
                var parsed = JsonUtil.ParseLine(Encoding.UTF8.GetString(lineBytes));
                if (parsed is not null) body(start + consumed + lineStart, parsed.Value);
                lineStart = nl + 1;
            }
            if (lineStart > 0)
            {
                pending.RemoveRange(0, lineStart);
                consumed += lineStart;
            }
        }
        if (pending.Count > 0)
        {
            var parsed = JsonUtil.ParseLine(Encoding.UTF8.GetString(pending.ToArray()));
            if (parsed is not null)
            {
                body(start + consumed, parsed.Value);
                consumed += pending.Count;
            }
        }
        return start + consumed;
    }

    public static bool IsLineBoundary(string path, long offset)
    {
        if (offset <= 0) return true;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < offset) return false;
            stream.Seek(offset - 1, SeekOrigin.Begin);
            return stream.ReadByte() == (byte)'\n';
        }
        catch { return false; }
    }

    public static bool IsAppendOnly(string path, Stat st, (long Size, long Mtime, long Identity, string? Context) prev, bool checkLineBoundary = true)
    {
        return prev.Identity == st.Identity
            && st.Size > prev.Size
            && prev.Mtime != 0
            && !JsonUtil.ContextNeedsFullRescan(prev.Context)
            && (!checkLineBoundary || prev.Size == 0 || IsLineBoundary(path, prev.Size));
    }

    /// <summary>"…-Users-me-Projects-App" -&gt; "App" (last path component, "-" = "/").</summary>
    public static string LastComponentOfEncodedPath(string s)
    {
        var parts = s.Split('-');
        var last = parts.LastOrDefault();
        return !string.IsNullOrEmpty(last) ? last : s;
    }

    public static long? ParseIso(string? s) => JsonUtil.ParseIso(s);
}
