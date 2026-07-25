using System.Text;

namespace ScrcpyDeck.Services;

/// <summary>
/// Deterministic, seed-free hashing for cosmetic bucketing (badge tints).
/// <para>
/// .NET randomises <see cref="string.GetHashCode()"/> per process as a hash-DoS mitigation,
/// so it must never be persisted or compared across runs. These helpers are stable across
/// processes, machines and .NET versions.
/// </para>
/// <b>Not</b> for security or integrity use.
/// </summary>
public static class StableHash
{
    private const uint Fnv32Offset = 2166136261;
    private const uint Fnv32Prime = 16777619;

    /// <summary>
    /// 32-bit FNV-1a over the string's <b>UTF-8 bytes</b>.
    /// <para>
    /// UTF-8 is chosen deliberately over UTF-16 code units: it is the encoding-independent
    /// byte form, so the same package id hashes identically regardless of platform
    /// endianness, and non-BMP characters contribute their real bytes rather than surrogate
    /// halves.
    /// </para>
    /// </summary>
    public static uint Fnv1a32(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Fnv32Offset;

        var byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        var buffer = byteCount <= 256
            ? stackalloc byte[256]
            : (rented = new byte[byteCount]).AsSpan();

        var written = Encoding.UTF8.GetBytes(value, buffer);
        var hash = Fnv1a32(buffer[..written]);

        if (rented != null) Array.Clear(rented);
        return hash;
    }

    /// <summary>32-bit FNV-1a over raw bytes.</summary>
    public static uint Fnv1a32(ReadOnlySpan<byte> bytes)
    {
        var hash = Fnv32Offset;
        foreach (var b in bytes)
        {
            hash ^= b;
            hash *= Fnv32Prime;
        }
        return hash;
    }
}
