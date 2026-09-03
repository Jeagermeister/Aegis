using System.Text.RegularExpressions;

namespace Aegis.Collectors;

/// <summary>
/// Harvests an owner from the free text teams already keep in job descriptions and tags
/// ("harvest, don't ask"). Deliberately tolerant and deliberately simple: this is the v0 profile,
/// and per-scheduler parser profiles are roadmap task 3.4. Whatever it decides,
/// <c>JobOwnership.RawEvidence</c> keeps the source text, so a better parser can be replayed over
/// history without re-polling any scheduler.
/// </summary>
internal static partial class OwnerParser
{
    /// <summary>What a successful parse found and whether it was a ticket reference rather than a team.</summary>
    public readonly record struct OwnerMatch(string Owner, bool IsTicket);

    // A declared value runs until a separator (; ,), a line break, a tag marker (# @), a sentence-ending
    // period, or the end of the text. "Owner: ETL Team. Loads BCBS." yields "ETL Team"; "data.platform" survives.
    private const string DeclaredValue = @"([^;,\r\n#@]+?)\s*(?:(?=[;,#@\r\n])|(?=\.\s)|\.?$)";

    // Explicit key/value declarations win over bare tags, so "Owner: ETL #nightly" yields ETL, not nightly.
    [GeneratedRegex(@"\b(?:owner|team)\s*[:=]\s*" + DeclaredValue, RegexOptions.IgnoreCase)]
    private static partial Regex OwnerOrTeam();

    [GeneratedRegex(@"\bticket\s*[:=]\s*" + DeclaredValue, RegexOptions.IgnoreCase)]
    private static partial Regex Ticket();

    // A leading letter is required so a bare ticket number like "#4821" is not mistaken for a team tag.
    [GeneratedRegex(@"(?<!\w)[#@]([A-Za-z][\w-]*)")]
    private static partial Regex HashOrMention();

    public static OwnerMatch? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = OwnerOrTeam().Match(text);
        if (match.Success)
        {
            return new OwnerMatch(Clean(match.Groups[1].Value), IsTicket: false);
        }

        match = Ticket().Match(text);
        if (match.Success)
        {
            return new OwnerMatch(Clean(match.Groups[1].Value), IsTicket: true);
        }

        match = HashOrMention().Match(text);
        if (match.Success)
        {
            return new OwnerMatch(match.Groups[1].Value, IsTicket: false);
        }

        return null;
    }

    private static string Clean(string value) => value.Trim().TrimEnd('.', ',').Trim();
}
