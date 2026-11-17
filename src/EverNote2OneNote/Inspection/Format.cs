using System.Globalization;
using System.Text.RegularExpressions;
using EverNote2OneNote.Enex;

namespace EverNote2OneNote.Inspection;

public static partial class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Binary units (1 KB = 1024 bytes), as shown by Windows Explorer.</summary>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0)
        {
            return $"{bytes} B";
        }
        var pattern = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(pattern, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>Parses sizes such as "1.12GB", "1,12 GB", "950MB" or "123456" (binary units).</summary>
    public static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        var m = SizePattern().Match(text.Trim());
        if (!m.Success)
        {
            return false;
        }
        var number = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : "B";
        int power = Array.IndexOf(Units, unit);
        bytes = (long)Math.Round(number * Math.Pow(1024, power));
        return true;
    }

    /// <summary>ENEX timestamp as "yyyy-MM-dd HH:mm:ssZ", or the raw value if it cannot be parsed.</summary>
    public static string Date(string? enexTimestamp) =>
        EnexTimestamp.TryParse(enexTimestamp, out var utc)
            ? utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : enexTimestamp ?? "";

    /// <summary>A name that is safe to use as a file name on Windows, macOS and Linux.</summary>
    public static string FileName(string name, int maxLength = 60)
    {
        var chars = name.Select(c => c < 32 || "\\/:*?\"<>|".Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim().TrimEnd('.');
        if (safe.Length > maxLength)
        {
            safe = safe[..maxLength].TrimEnd();
        }
        return safe.Length == 0 ? "_" : safe;
    }

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)\s*(B|KB|MB|GB|TB)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();
}
