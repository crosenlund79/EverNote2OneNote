using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace EverNote2OneNote.Text;

public sealed record EncryptedBlock(string? Hint, string? Cipher, int CipherTextLength);

/// <summary>What was found in a note body.</summary>
public sealed class EnmlExtraction
{
    /// <summary>All visible text, in document order, one line per block.</summary>
    public string Text { get; internal set; } = "";

    /// <summary>Hashes of &lt;en-media&gt; references, in document order (lowercase).</summary>
    public List<string> MediaHashes { get; } = [];

    public int TodoCount { get; internal set; }
    public int TodoCheckedCount { get; internal set; }
    public List<EncryptedBlock> Encrypted { get; } = [];
    public SortedSet<string> UnknownEntities { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> UnexpectedElements { get; } = new(StringComparer.Ordinal);
    public int MergedCells { get; internal set; }
    public int ExternalImages { get; internal set; }
    public int Links { get; internal set; }

    /// <summary>Set when the body was not well-formed and its text was recovered from the raw markup instead.</summary>
    public string? FallbackReason { get; internal set; }
}

/// <summary>
/// Extracts the text of an ENML note body. Never contacts the network and never loads a DTD:
/// HTML named entities (&amp;nbsp; etc.) are resolved from .NET's built-in table instead.
/// If the body is not well-formed, falls back to <see cref="RawText"/> so no text is dropped.
/// </summary>
public static partial class EnmlText
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static EnmlExtraction Extract(string enml)
    {
        var result = new EnmlExtraction();
        var prepared = ResolveEntities(enml, result.UnknownEntities);
        try
        {
            Walk(prepared, result);
            return result;
        }
        catch (XmlException ex)
        {
            return Fallback(enml, ex.Message, result.UnknownEntities);
        }
    }

    private static void Walk(string enml, EnmlExtraction result)
    {
        using var reader = XmlReader.Create(new StringReader(enml), Settings);
        var text = new TextBuilder();
        int preDepth = 0;

        reader.Read();
        while (!reader.EOF)
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var name = reader.LocalName.ToLowerInvariant();
                    if (name == "en-crypt")
                    {
                        var hint = reader.GetAttribute("hint");
                        var cipher = reader.GetAttribute("cipher");
                        string cipherText;
                        if (reader.IsEmptyElement)
                        {
                            cipherText = "";
                            reader.Read();
                        }
                        else
                        {
                            cipherText = reader.ReadElementContentAsString(); // advances past </en-crypt>
                        }
                        result.Encrypted.Add(new EncryptedBlock(hint, cipher, cipherText.Trim().Length));
                        continue;
                    }

                    if (!HtmlElements.Enml.Contains(name))
                    {
                        result.UnexpectedElements.Add(name);
                    }
                    StartElement(reader, name, text, result, ref preDepth);
                    if (reader.IsEmptyElement)
                    {
                        EndElement(name, text, ref preDepth);
                    }
                    break;
                }
                case XmlNodeType.EndElement:
                    EndElement(reader.LocalName.ToLowerInvariant(), text, ref preDepth);
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    text.Append(reader.Value, preserveWhitespace: preDepth > 0);
                    break;
            }
            reader.Read();
        }
        result.Text = text.ToString();
    }

    private static void StartElement(XmlReader reader, string name, TextBuilder text, EnmlExtraction result, ref int preDepth)
    {
        if (HtmlElements.Block.Contains(name))
        {
            text.SoftBreak();
        }
        switch (name)
        {
            case "br":
                text.HardBreak();
                break;
            case "pre" or "xmp" when !reader.IsEmptyElement:
                preDepth++;
                break;
            case "en-todo":
                bool done = string.Equals(reader.GetAttribute("checked"), "true", StringComparison.OrdinalIgnoreCase);
                text.AppendMarker(done ? "[x] " : "[ ] ");
                result.TodoCount++;
                if (done)
                {
                    result.TodoCheckedCount++;
                }
                break;
            case "en-media":
                if (reader.GetAttribute("hash") is { Length: > 0 } hash)
                {
                    result.MediaHashes.Add(hash.Trim().ToLowerInvariant());
                }
                break;
            case "a" when reader.GetAttribute("href") is { Length: > 0 }:
                result.Links++;
                break;
            case "img":
                result.ExternalImages++;
                break;
            case "td" or "th" when IsMerged(reader.GetAttribute("colspan")) || IsMerged(reader.GetAttribute("rowspan")):
                result.MergedCells++;
                break;
        }
    }

    private static void EndElement(string name, TextBuilder text, ref int preDepth)
    {
        if (name is "td" or "th")
        {
            text.CellBreak();
        }
        else if (name is "pre" or "xmp" && preDepth > 0)
        {
            preDepth--;
        }
        if (HtmlElements.Block.Contains(name))
        {
            text.SoftBreak();
        }
    }

    private static bool IsMerged(string? span) =>
        int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 1;

    private static EnmlExtraction Fallback(string enml, string reason, SortedSet<string> unknownEntities)
    {
        var result = new EnmlExtraction
        {
            FallbackReason = reason,
            Text = RawText.FromMarkup(enml),
        };
        result.UnknownEntities.UnionWith(unknownEntities);
        foreach (Match m in MediaHashPattern().Matches(enml))
        {
            result.MediaHashes.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        foreach (Match m in TodoPattern().Matches(enml))
        {
            result.TodoCount++;
            if (m.Groups[1].Success)
            {
                result.TodoCheckedCount++;
            }
        }
        foreach (Match m in CryptPattern().Matches(enml))
        {
            result.Encrypted.Add(new EncryptedBlock(m.Groups[1].Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null, null, 0));
        }
        return result;
    }

    /// <summary>
    /// Rewrites HTML named entities as numeric character references, so the body parses without its DTD.
    /// Unknown entity names are kept literally (as text) and reported. Stray '&amp;' characters are escaped.
    /// </summary>
    internal static string ResolveEntities(string enml, ISet<string> unknownEntities)
    {
        return EntityPattern().Replace(enml, m =>
        {
            var name = m.Groups["name"];
            if (!name.Success)
            {
                return m.Length > 1 ? m.Value : "&amp;"; // numeric reference, or a stray '&'
            }
            if (name.Value is "amp" or "lt" or "gt" or "quot" or "apos")
            {
                return m.Value;
            }
            var decoded = WebUtility.HtmlDecode(m.Value);
            if (decoded == m.Value)
            {
                unknownEntities.Add(m.Value);
                return "&amp;" + name.Value + ";";
            }
            var numeric = new StringBuilder();
            foreach (var rune in decoded.EnumerateRunes())
            {
                numeric.Append("&#x").Append(rune.Value.ToString("X", CultureInfo.InvariantCulture)).Append(';');
            }
            return numeric.ToString();
        });
    }

    [GeneratedRegex(@"&(?:(?<name>[A-Za-z][A-Za-z0-9]*);|#(?:[0-9]+|[xX][0-9A-Fa-f]+);)?")]
    private static partial Regex EntityPattern();

    [GeneratedRegex(@"<en-media\b[^>]*?\bhash\s*=\s*[""']([0-9a-fA-F]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MediaHashPattern();

    [GeneratedRegex(@"<en-todo\b(?=[^>]*?(\bchecked\s*=\s*[""']true[""']))?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TodoPattern();

    [GeneratedRegex(@"<en-crypt\b(?=[^>]*?\bhint\s*=\s*""([^""]*)"")?[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex CryptPattern();
}
