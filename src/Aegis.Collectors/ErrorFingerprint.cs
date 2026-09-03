using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aegis.Collectors;

/// <summary>
/// Sentry-style grouping: strip the parts of an error message that vary from run to run
/// (timestamps, ids, numbers, quoted values), keep its shape, hash the shape. Two failures with
/// the same fingerprint are the same problem and should be one alert, not forty emails.
/// </summary>
internal static partial class ErrorFingerprint
{
    /// <summary>Width of <c>JobRun.FingerprintId</c> (CHAR(32)): the first 128 bits of the SHA-256, hex encoded.</summary>
    public const int FingerprintLength = 32;

    // Dates, times, hex, quoted values, and standalone numbers including a short unit suffix (30s, 5ms, 2GB).
    // Digits inside identifiers (Table1, svc_etl2) are kept: those distinguish errors rather than repeat them.
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}|\d{2}:\d{2}:\d{2}|0x[0-9a-fA-F]+|\b\d+(?:\.\d+)?[a-zA-Z]{0,2}\b|'[^']*'|""[^""]*""")]
    private static partial Regex VolatileTokens();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Reduces an error message to its shape. Exposed so tests can see what is being hashed.</summary>
    public static string Normalise(string errorText)
    {
        var shape = VolatileTokens().Replace(errorText, string.Empty);
        return Whitespace().Replace(shape, " ").Trim().ToUpperInvariant();
    }

    public static string Compute(string errorText)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(errorText)));
        return Convert.ToHexString(hash)[..FingerprintLength];
    }

    /// <summary>Alert identity: SHA-256 hex (64 chars) of its parts, matching <c>Alert.DedupKey CHAR(64)</c>.</summary>
    public static string DedupKey(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)));
        return Convert.ToHexString(hash);
    }
}
