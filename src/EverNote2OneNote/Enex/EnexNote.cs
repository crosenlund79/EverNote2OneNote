using System.Globalization;

namespace EverNote2OneNote.Enex;

/// <summary>Attributes of the &lt;en-export&gt; root element.</summary>
public sealed record EnexHeader(string? Application, string? Version, string? ExportDate);

/// <summary>One note as read from an ENEX file. Values are kept exactly as exported.</summary>
public sealed class EnexNote
{
    /// <summary>1-based position of the note in its file.</summary>
    public required int Index { get; init; }

    /// <summary>Byte offset of the note's &lt;note&gt; tag in the source file.</summary>
    public required long ByteOffset { get; init; }

    /// <summary>Line number of the note's &lt;note&gt; tag in the source file.</summary>
    public required long Line { get; init; }

    public string? Title { get; set; }

    /// <summary>The note body (ENML) exactly as exported; null when no complete &lt;content&gt; element was read.</summary>
    public string? Content { get; set; }

    /// <summary>Raw ENEX timestamp, e.g. 20120315T081500Z.</summary>
    public string? Created { get; set; }

    /// <summary>Raw ENEX timestamp, e.g. 20120315T081500Z.</summary>
    public string? Updated { get; set; }

    public List<string> Tags { get; } = [];

    /// <summary>All &lt;note-attributes&gt; children, in export order (author, source-url, reminders, ...).</summary>
    public List<KeyValuePair<string, string>> Attributes { get; } = [];

    public List<EnexResource> Resources { get; } = [];

    /// <summary>Evernote tasks, which are stored outside the note body.</summary>
    public List<EnexTask> Tasks { get; } = [];

    /// <summary>Elements or text the reader does not recognise, preserved verbatim.</summary>
    public List<string> UnknownElements { get; } = [];

    /// <summary>Set when the note's XML could not be fully parsed.</summary>
    public string? ParseError { get; set; }

    /// <summary>When the note body could not be read as XML: text recovered from the raw markup.</summary>
    public string? RecoveredText { get; set; }

    /// <summary>Attachments present in a damaged note that could not be read (and are therefore not in <see cref="Resources"/>).</summary>
    public int UnreadAttachments { get; set; }
}

/// <summary>An attachment or embedded image. Only metadata and checksums are kept; the data itself is not stored.</summary>
public sealed class EnexResource
{
    public string? Mime { get; set; }

    /// <summary>Decoded size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Lowercase hex MD5 of the decoded data. This is what &lt;en-media hash="..."&gt; refers to.</summary>
    public string? Md5 { get; set; }

    public string? Width { get; set; }
    public string? Height { get; set; }

    /// <summary>True when Evernote stored OCR data for the resource.</summary>
    public bool HasRecognition { get; set; }

    /// <summary>Decoded size of &lt;alternate-data&gt;, if any.</summary>
    public long AlternateDataSize { get; set; }

    /// <summary>All &lt;resource-attributes&gt; children (file-name, source-url, timestamp, ...).</summary>
    public List<KeyValuePair<string, string>> Attributes { get; } = [];

    public string? FileName => Attributes.FirstOrDefault(a => a.Key == "file-name").Value;
}

/// <summary>An Evernote task. All fields are kept; the most useful ones are surfaced.</summary>
public sealed record EnexTask(string? Title, string? Status, IReadOnlyList<KeyValuePair<string, string>> Fields);

public static class EnexTimestamp
{
    /// <summary>Parses the fixed ENEX timestamp format yyyyMMddTHHmmssZ (UTC).</summary>
    public static bool TryParse(string? value, out DateTime utc)
    {
        utc = default;
        return value is not null && DateTime.TryParseExact(
            value.Trim(),
            "yyyyMMdd'T'HHmmss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out utc);
    }
}
