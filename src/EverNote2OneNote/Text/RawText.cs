using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EverNote2OneNote.Text;

/// <summary>
/// Last-resort text recovery for markup that is not well-formed XML.
/// Only tags with recognised element names are removed; anything else is kept as text,
/// so recovery may keep stray markup but never silently drops text.
/// </summary>
public static partial class RawText
{
    private static ReadOnlySpan<byte> Lt => "<"u8;

    private static readonly (byte[] Open, byte[] Close)[] BinaryElements =
    [
        ("<data"u8.ToArray(), "</data>"u8.ToArray()),
        ("<alternate-data"u8.ToArray(), "</alternate-data>"u8.ToArray()),
        ("<recognition"u8.ToArray(), "</recognition>"u8.ToArray()),
    ];

    /// <summary>Extracts text from (possibly damaged) HTML/ENML/XML markup.</summary>
    public static string FromMarkup(string markup)
    {
        var s = CDataPattern().Replace(markup, m => m.Groups[1].Value);
        s = CommentPattern().Replace(s, " ");
        s = ProcessingInstructionPattern().Replace(s, " ");
        s = DoctypePattern().Replace(s, " ");
        s = TagPattern().Replace(s, m =>
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            if (!HtmlElements.Known.Contains(name))
            {
                return m.Value; // not recognisable markup: keep it as text
            }
            if (name is "td" or "th")
            {
                return " ";
            }
            bool inlineFormatting = HtmlElements.Enml.Contains(name) && !HtmlElements.Block.Contains(name) && name != "br";
            return inlineFormatting ? "" : "\n"; // blocks, line breaks and ENEX structure start a new line
        });
        s = WebUtility.HtmlDecode(s);

        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => WhitespacePattern().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }

    /// <summary>Recovers the body text of a damaged &lt;note&gt; element.</summary>
    internal static string FromNoteSegment(string segment)
    {
        var open = ContentOpenPattern().Match(segment);
        if (!open.Success)
        {
            return FromMarkup(segment);
        }
        int innerStart = open.Index + open.Length;
        int close = segment.IndexOf("</content>", innerStart, StringComparison.Ordinal);
        var inner = close >= 0 ? segment[innerStart..close] : segment[innerStart..];
        inner = inner.Contains("<![CDATA[", StringComparison.Ordinal)
            ? CDataPattern().Replace(inner, m => m.Groups[1].Value)
            : WebUtility.HtmlDecode(inner); // body stored as escaped text instead of CDATA
        return FromMarkup(inner);
    }

    /// <summary>Text of the first &lt;name&gt;...&lt;/name&gt; element in raw markup, or null.</summary>
    internal static string? FindElement(string segment, string name) => FindElements(segment, name).FirstOrDefault();

    /// <summary>Text of every &lt;name&gt;...&lt;/name&gt; element outside the note body.</summary>
    internal static List<string> FindElements(string segment, string name)
    {
        var body = ContentOpenPattern().Match(segment);
        var head = body.Success ? segment[..body.Index] : segment;
        var tail = body.Success && segment.IndexOf("</content>", body.Index, StringComparison.Ordinal) is var end and >= 0
            ? segment[end..]
            : "";
        var pattern = new Regex($"<{Regex.Escape(name)}>(.*?)</{Regex.Escape(name)}>", RegexOptions.Singleline);
        return pattern.Matches(head + tail)
            .Select(m => WebUtility.HtmlDecode(CDataPattern().Replace(m.Groups[1].Value, c => c.Groups[1].Value)).Trim())
            .ToList();
    }

    /// <summary>Number of &lt;resource&gt; elements in raw note markup.</summary>
    internal static int CountResources(string segment) => ResourcePattern().Count(segment);

    /// <summary>
    /// Decodes a note's raw bytes as UTF-8, leaving out the base64 payload of attachments
    /// (&lt;data&gt;, &lt;alternate-data&gt;) and OCR data (&lt;recognition&gt;), which are not note text.
    /// </summary>
    internal static string DecodeWithoutBinaryData(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(false);
        var span = bytes.AsSpan();
        var result = new StringBuilder();
        int pos = 0;
        while (pos < bytes.Length)
        {
            int start = FindBinaryElement(span, pos, out var closeTag);
            if (start < 0)
            {
                break;
            }
            int gt = span[start..].IndexOf((byte)'>');
            if (gt < 0)
            {
                break;
            }
            int contentStart = start + gt + 1;
            result.Append(utf8.GetString(span[pos..contentStart]));
            if (span[contentStart - 2] == (byte)'/')
            {
                pos = contentStart; // self-closing, no payload
                continue;
            }
            int close = span[contentStart..].IndexOf(closeTag);
            pos = close < 0 ? bytes.Length : contentStart + close;
        }
        if (pos < bytes.Length)
        {
            result.Append(utf8.GetString(span[pos..]));
        }
        return result.ToString();
    }

    private static int FindBinaryElement(ReadOnlySpan<byte> span, int from, out byte[] closeTag)
    {
        int pos = from;
        while (pos < span.Length)
        {
            int lt = span[pos..].IndexOf(Lt);
            if (lt < 0)
            {
                break;
            }
            pos += lt;
            foreach (var (open, close) in BinaryElements)
            {
                var rest = span[pos..];
                if (rest.StartsWith(open) && rest.Length > open.Length &&
                    rest[open.Length] is (byte)'>' or (byte)'/' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
                {
                    closeTag = close;
                    return pos;
                }
            }
            pos++;
        }
        closeTag = [];
        return -1;
    }

    [GeneratedRegex(@"<!\[CDATA\[(.*?)(?:\]\]>|$)", RegexOptions.Singleline)]
    private static partial Regex CDataPattern();

    [GeneratedRegex(@"<!--.*?(?:-->|$)", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<\?.*?\?>", RegexOptions.Singleline)]
    private static partial Regex ProcessingInstructionPattern();

    [GeneratedRegex(@"<!DOCTYPE[^\[>]*(\[.*?\])?\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DoctypePattern();

    [GeneratedRegex(@"</?([A-Za-z][A-Za-z0-9:-]*)(?:\s[^<>]*)?/?>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"<content\b[^>]*>")]
    private static partial Regex ContentOpenPattern();

    [GeneratedRegex(@"<resource(?=[\s>/])")]
    private static partial Regex ResourcePattern();
}
