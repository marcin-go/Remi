using System.Globalization;

namespace Remi.Application;

public static class SubmissionTimestampParser
{
    private static readonly CultureInfo BritishEnglish = CultureInfo.GetCultureInfo("en-GB");
    private static readonly string[] UtcFormats =
    [
        "d MMMM yyyy HH:mm 'UTC'",
        "d MMMM yyyy 'at' HH:mm 'UTC'",
        "d MMM yyyy HH:mm 'UTC'",
        "d MMM yyyy 'at' HH:mm 'UTC'",
        "yyyy-MM-dd HH:mm 'UTC'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
    ];

    public static bool TryParseUtc(string? input, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var normalized = input.Trim();
        if (normalized.EndsWith(" UTC", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"{normalized[..^3]}UTC";
        }

        return DateTimeOffset.TryParseExact(
            normalized,
            UtcFormats,
            BritishEnglish,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    public static string FormatUtc(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("d MMMM yyyy HH:mm 'UTC'", BritishEnglish);
}
