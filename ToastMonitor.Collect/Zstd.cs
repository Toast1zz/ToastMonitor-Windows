namespace ToastMonitor.Collect;

/// <summary>Zstandard decompression for DSH .jsonl.zstd files (Mac Zstd.swift
/// shells out to the zstd CLI; on Windows we use the ZstdSharp library — no
/// hand-written decompression). The library is referenced only from this file,
/// so a missing package degrades to "no decompressor", like Mac without the CLI.</summary>
public static class Zstd
{
    public static bool IsAvailable
    {
        get
        {
            try { _ = typeof(ZstdSharp.Decompressor); return true; }
            catch { return false; }
        }
    }

    /// <summary>Decompress a whole zstd buffer (concatenated frames are handled
    /// by the library). Returns null on any failure.</summary>
    public static byte[]? Decompress(byte[] data)
    {
        try
        {
            return new ZstdSharp.Decompressor().Unwrap(data).ToArray();
        }
        catch { return null; }
    }

    public static byte[]? DecompressFile(string path)
    {
        try { return Decompress(File.ReadAllBytes(path)); }
        catch { return null; }
    }
}
