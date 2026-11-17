using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EverNote2OneNote.Enex;
using EverNote2OneNote.Text;

namespace EverNote2OneNote.OneNote;

public enum PageMode
{
    /// <summary>Text with lists, links, emphasis, images, file attachments and the Evernote creation date.</summary>
    Full,

    /// <summary>Conservative fallback: title and plain paragraphs only.</summary>
    TextOnly,
}

/// <summary>An attachment placed on the page.</summary>
public sealed record PlacedAttachment(int ResourceIndex, string FileName, string Mime, long Size, bool IsImage, string Placement);

/// <summary>A page in OneNote's XML format (2013 schema), ready for UpdatePageContent.</summary>
public sealed class PageXml
{
    public required string Xml { get; init; }
    public required string Title { get; init; }
    public List<PlacedAttachment> Attachments { get; } = [];

    /// <summary>Text the migration added (header, markers, headings), so verification can tell it apart.</summary>
    public List<AddedText> Added { get; } = [];

    /// <summary>Content of the note that could not be migrated as-is.</summary>
    public List<string> Notices { get; } = [];
}

/// <summary>What the page contains, as read back from OneNote.</summary>
public sealed record PageContent(string Title, string Text, int Images, IReadOnlyList<string> Files);

/// <summary>
/// Converts an Evernote note (ENML) into a OneNote desktop page (the XML used by the OneNote COM API).
/// Every piece of text is kept; formatting is simplified to paragraphs, lists, links and basic emphasis.
/// </summary>
public static class PageXmlBuilder
{
    public const string Namespace = "http://schemas.microsoft.com/office/onenote/2013/onenote";
    public const string EncryptedMarker = "[Encrypted Evernote content - not migrated]";
    private static readonly XNamespace One = Namespace;

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/bmp", "image/x-ms-bmp", "image/tiff",
    };

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <param name="pageId">ID of the (new, empty) page to fill.</param>
    /// <param name="data">Decoded attachment data by resource index; attachments without data are left out.</param>
    /// <param name="saveFile">Saves a non-image attachment to a temporary file and returns its path (OneNote copies it into the page).</param>
    /// <param name="fallbackText">Text to use if the note body cannot be parsed (the inspector's recovered text).</param>
    public static PageXml Build(string pageId, EnexNote note, string notebook, IReadOnlyDictionary<int, byte[]> data,
        Func<int, string> saveFile, PageMode mode, string fallbackText)
    {
        var title = string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : note.Title.Trim();
        var writer = new BlockWriter(note, data, mode);

        var header = Header(note, notebook);
        writer.Added.Add(new AddedText(header, AddedTextPosition.Start));
        writer.Blocks.Add(new Block(0, ListKind.None, mode == PageMode.Full ? $"<span style='color:#595959;font-size:9pt'>{Encode(header)}</span>" : Encode(header)));

        bool parsed = false;
        if (note.Content is not null)
        {
            int mark = writer.Blocks.Count;
            try
            {
                writer.Walk(EnmlText.ResolveEntities(note.Content.TrimStart().TrimStart('﻿').TrimStart(), new HashSet<string>()));
                parsed = true;
            }
            catch (XmlException)
            {
                writer.Reset(mark);
                writer.Notices.Add("The note body is not valid ENML; its text was migrated as plain paragraphs.");
            }
        }
        if (!parsed)
        {
            foreach (var line in fallbackText.Split('\n').Where(l => l.Trim().Length > 0))
            {
                writer.Blocks.Add(new Block(0, ListKind.None, Encode(line)));
            }
        }
        writer.AddRemainingAttachments();

        // Serialise: blocks become OE elements; list levels become nested OEChildren.
        var top = new XElement(One + "OEChildren");
        var lastAtLevel = new List<XElement>();
        var result = new PageXml { Xml = "", Title = title };
        foreach (var block in writer.Blocks)
        {
            int level = Math.Min(block.Level, lastAtLevel.Count);
            var parent = level == 0 ? top : Children(lastAtLevel[level - 1]);
            var oe = new XElement(One + "OE");
            if (block.List != ListKind.None)
            {
                oe.Add(new XElement(One + "List", block.List == ListKind.Bullet
                    ? new XElement(One + "Bullet", new XAttribute("bullet", 2))
                    : new XElement(One + "Number", new XAttribute("numberSequence", 0), new XAttribute("numberFormat", "##."))));
            }
            if (block.MediaIndex >= 0)
            {
                oe.Add(Media(block.MediaIndex, note, data, saveFile, result, writer));
            }
            else
            {
                oe.Add(new XElement(One + "T", new XCData(XmlSafe(block.Html!, writer))));
            }
            parent.Add(oe);
            lastAtLevel.RemoveRange(level, lastAtLevel.Count - level);
            lastAtLevel.Add(oe);
        }

        var page = new XElement(One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", Namespace),
            new XAttribute("ID", pageId));
        if (mode == PageMode.Full && EnexTimestamp.TryParse(note.Created, out var created))
        {
            page.Add(new XAttribute("dateTime", created.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)));
        }
        page.Add(
            new XElement(One + "Title", new XElement(One + "OE", new XElement(One + "T", new XCData(XmlSafe(Encode(title), writer))))),
            new XElement(One + "Outline", top));

        if (writer.RemovedControlCharacters > 0)
        {
            writer.Notices.Add($"{writer.RemovedControlCharacters} invisible control character(s) that XML cannot hold were left out.");
        }
        var pageXml = new PageXml { Xml = page.ToString(SaveOptions.DisableFormatting), Title = title };
        pageXml.Attachments.AddRange(result.Attachments);
        pageXml.Added.AddRange(writer.Added);
        pageXml.Notices.AddRange(writer.Notices);
        return pageXml;
    }

    /// <summary>Extracts the title, visible text and attachments from page XML returned by OneNote.</summary>
    public static PageContent Read(string pageXml)
    {
        var page = XDocument.Parse(pageXml).Root!;
        var title = string.Join(" ", page.Element(One + "Title")?.Descendants(One + "T").Select(t => RawText.FromMarkup(t.Value)) ?? []);
        var outlines = page.Elements(One + "Outline").ToList();
        var text = string.Join("\n", outlines.SelectMany(o => o.Descendants(One + "T")).Select(t => RawText.FromMarkup(t.Value)));
        int images = outlines.Sum(o => o.Descendants(One + "Image").Count());
        var files = outlines.SelectMany(o => o.Descendants(One + "InsertedFile"))
            .Select(f => (string?)f.Attribute("preferredName") ?? "")
            .ToList();
        return new PageContent(title.Trim(), text, images, files);
    }

    private static XElement Children(XElement oe)
    {
        var children = oe.Element(One + "OEChildren");
        if (children is null)
        {
            children = new XElement(One + "OEChildren");
            oe.Add(children);
        }
        return children;
    }

    private static XElement Media(int index, EnexNote note, IReadOnlyDictionary<int, byte[]> data, Func<int, string> saveFile,
        PageXml result, BlockWriter writer)
    {
        var resource = note.Resources[index];
        var bytes = data[index];
        var mime = string.IsNullOrWhiteSpace(resource.Mime) ? "application/octet-stream" : resource.Mime.Trim();
        var fileName = string.IsNullOrWhiteSpace(resource.FileName) ? $"attachment-{index + 1}{Extension(mime)}" : resource.FileName.Trim();
        var placement = writer.AtEnd.Contains(index) ? "End of page" : "In place";

        if (ImageTypes.Contains(mime))
        {
            result.Attachments.Add(new PlacedAttachment(index, fileName, mime, bytes.Length, true, placement));
            var image = new XElement(One + "Image");
            if (double.TryParse(resource.Width, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) &&
                double.TryParse(resource.Height, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && w > 600 && h > 0)
            {
                // Large images are shown 600 pixels wide (450 points); the full image is kept.
                image.Add(new XElement(One + "Size",
                    new XAttribute("width", 450.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("height", (450 * h / w).ToString("0.##", CultureInfo.InvariantCulture)),
                    new XAttribute("isSetByUser", "true")));
            }
            image.Add(new XElement(One + "Data", Convert.ToBase64String(bytes)));
            return image;
        }

        result.Attachments.Add(new PlacedAttachment(index, fileName, mime, bytes.Length, false, placement));
        return new XElement(One + "InsertedFile",
            new XAttribute("pathSource", saveFile(index)),
            new XAttribute("preferredName", fileName));
    }

    private static string Header(EnexNote note, string notebook)
    {
        var parts = new List<string> { $"Migrated from Evernote notebook \"{notebook}\"" };
        if (note.Created is not null)
        {
            parts.Add($"created {Inspection.Format.Date(note.Created)}");
        }
        if (note.Updated is not null)
        {
            parts.Add($"updated {Inspection.Format.Date(note.Updated)}");
        }
        if (note.Tags.Count > 0)
        {
            parts.Add($"tags: {string.Join(", ", note.Tags)}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Escapes text for the HTML inside a OneNote T element.</summary>
    internal static string Encode(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Removes characters XML cannot contain (e.g. stray control characters), counting them.</summary>
    private static string XmlSafe(string text, BlockWriter writer)
    {
        if (text.All(XmlConvert.IsXmlChar) && !text.Any(char.IsSurrogate))
        {
            return text;
        }
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(text[i]).Append(text[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(text[i]))
            {
                sb.Append(text[i]);
            }
            else
            {
                writer.RemovedControlCharacters++;
            }
        }
        return sb.ToString();
    }

    private static string Extension(string mime) => mime.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/tiff" => ".tif",
        "image/bmp" or "image/x-ms-bmp" => ".bmp",
        "image/heic" => ".heic",
        "application/pdf" => ".pdf",
        "audio/mpeg" => ".mp3",
        "audio/wav" => ".wav",
        _ => ".bin",
    };

    private enum ListKind { None, Bullet, Number }

    /// <summary>One OE element: a paragraph (inline HTML) or an attachment, at a list nesting level.</summary>
    private sealed record Block(int Level, ListKind List, string? Html, int MediaIndex = -1);

    /// <summary>Walks the ENML and produces blocks. Inline content is buffered until a block boundary.</summary>
    private sealed class BlockWriter(EnexNote note, IReadOnlyDictionary<int, byte[]> data, PageMode mode)
    {
        private readonly Stack<ListKind> _lists = new();
        private readonly List<string> _styles = [];
        private readonly Stack<string?> _links = new();
        private readonly StringBuilder _inline = new();
        private readonly HashSet<int> _placed = [];
        private bool _hasContent;
        private bool _pendingBullet;
        private int _preDepth;
        private int _tables;
        private int _skippedLinks;
        private int _duplicateReferences;

        public List<Block> Blocks { get; } = [];
        public List<AddedText> Added { get; } = [];
        public List<string> Notices { get; } = [];
        public HashSet<int> AtEnd { get; } = [];
        public int RemovedControlCharacters { get; set; }

        private bool Plain => mode == PageMode.TextOnly;

        public void Reset(int blockCount)
        {
            Blocks.RemoveRange(blockCount, Blocks.Count - blockCount);
            Added.RemoveAll(a => a.Position == AddedTextPosition.Inline);
            Notices.Clear();
            _lists.Clear();
            _styles.Clear();
            _links.Clear();
            _inline.Clear();
            _placed.Clear();
            _hasContent = false;
            _pendingBullet = false;
            _preDepth = 0;
        }

        public void Walk(string enml)
        {
            using var reader = XmlReader.Create(new StringReader(enml), Settings);
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
                            if (reader.IsEmptyElement)
                            {
                                reader.Read();
                            }
                            else
                            {
                                reader.ReadElementContentAsString();
                            }
                            AppendText(EncryptedMarker);
                            Added.Add(new AddedText(EncryptedMarker, AddedTextPosition.Inline));
                            continue;
                        }
                        bool empty = reader.IsEmptyElement;
                        Start(reader, name, empty);
                        if (empty)
                        {
                            End(name, wasEmpty: true);
                        }
                        break;
                    }
                    case XmlNodeType.EndElement:
                        End(reader.LocalName.ToLowerInvariant(), wasEmpty: false);
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        AppendText(reader.Value);
                        break;
                }
                reader.Read();
            }
            Boundary();

            if (_tables > 0)
            {
                Notices.Add($"{_tables} table(s) were migrated as lines of text with cells separated by tabs (table layout is not kept).");
            }
            if (_skippedLinks > 0)
            {
                Notices.Add($"{_skippedLinks} link(s) to Evernote notes or non-web addresses were kept as plain text.");
            }
            if (_duplicateReferences > 0)
            {
                Notices.Add($"{_duplicateReferences} repeated reference(s) to the same attachment were shown once.");
            }
            if (Added.Any(a => a.Text == EncryptedMarker))
            {
                Notices.Add("Encrypted text cannot be read without its passphrase; a marker was placed where it was.");
            }
        }

        private void Start(XmlReader reader, string name, bool empty)
        {
            switch (name)
            {
                case "br":
                    Boundary();
                    return;
                case "en-todo":
                    AppendText(string.Equals(reader.GetAttribute("checked"), "true", StringComparison.OrdinalIgnoreCase) ? "[x] " : "[ ] ");
                    return;
                case "en-media":
                    MediaReference(reader.GetAttribute("hash"));
                    return;
                case "a":
                    if (!empty)
                    {
                        var href = reader.GetAttribute("href");
                        bool web = href is not null && (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                                        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                                        href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
                        if (href is not null && !web)
                        {
                            _skippedLinks++;
                        }
                        _links.Push(web ? href : null);
                    }
                    return;
                case "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "del":
                    if (!empty)
                    {
                        _styles.Add(name switch
                        {
                            "b" or "strong" => "font-weight:bold",
                            "i" or "em" => "font-style:italic",
                            "u" => "text-decoration:underline",
                            _ => "text-decoration:line-through",
                        });
                    }
                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Boundary();
                    if (!empty)
                    {
                        _styles.Add("font-weight:bold;font-size:13pt");
                    }
                    return;
                case "ul" or "ol":
                    Boundary();
                    if (!empty)
                    {
                        _lists.Push(name == "ul" ? ListKind.Bullet : ListKind.Number);
                    }
                    return;
                case "li":
                    Boundary();
                    _pendingBullet = _lists.Count > 0;
                    return;
                case "table":
                    Boundary();
                    _tables++;
                    return;
                case "pre" or "xmp":
                    Boundary();
                    if (!empty)
                    {
                        _preDepth++;
                        _styles.Add("font-family:Consolas");
                    }
                    return;
            }
            if (HtmlElements.Block.Contains(name))
            {
                Boundary();
            }
        }

        private void End(string name, bool wasEmpty)
        {
            switch (name)
            {
                case "a":
                    if (!wasEmpty && _links.Count > 0)
                    {
                        _links.Pop();
                    }
                    return;
                case "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "del":
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" when !wasEmpty:
                    if (name.StartsWith('h'))
                    {
                        Boundary();
                    }
                    if (!wasEmpty && _styles.Count > 0)
                    {
                        _styles.RemoveAt(_styles.Count - 1);
                    }
                    return;
                case "ul" or "ol":
                    Boundary();
                    if (!wasEmpty && _lists.Count > 0)
                    {
                        _lists.Pop();
                    }
                    return;
                case "li":
                    Boundary();
                    _pendingBullet = false;
                    return;
                case "td" or "th":
                    _inline.Append('\t');
                    return;
                case "pre" or "xmp":
                    Boundary();
                    if (!wasEmpty && _preDepth > 0)
                    {
                        _preDepth--;
                        _styles.RemoveAt(_styles.Count - 1);
                    }
                    return;
            }
            if (HtmlElements.Block.Contains(name))
            {
                Boundary();
            }
        }

        private void AppendText(string text)
        {
            if (_preDepth > 0 && text.Contains('\n'))
            {
                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0)
                    {
                        Boundary();
                    }
                    AppendText(lines[i]);
                }
                return;
            }
            if (text.Length == 0)
            {
                return;
            }
            var encoded = Encode(text);
            var href = _links.Count > 0 ? _links.Peek() : null;
            if (!Plain && _styles.Count > 0)
            {
                encoded = $"<span style='{string.Join(";", _styles)}'>{encoded}</span>";
            }
            if (!Plain && href is not null)
            {
                encoded = $"<a href=\"{Encode(href).Replace("\"", "&quot;")}\">{encoded}</a>";
            }
            _inline.Append(encoded);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _hasContent = true;
            }
        }

        private void Boundary()
        {
            if (_hasContent)
            {
                var list = _pendingBullet && !Plain ? _lists.Peek() : ListKind.None;
                Blocks.Add(new Block(Plain ? 0 : _lists.Count, list, _inline.ToString().TrimEnd('\t')));
                _pendingBullet = false;
            }
            _inline.Clear();
            _hasContent = false;
        }

        private void MediaReference(string? hash)
        {
            int index = hash is null ? -1 : note.Resources.FindIndex(r => string.Equals(r.Md5, hash.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                Notices.Add($"The note body refers to an attachment that is not in the export ({hash}).");
                return;
            }
            if (!_placed.Add(index))
            {
                _duplicateReferences++;
                return;
            }
            if (Plain || !data.ContainsKey(index))
            {
                return; // not placed in text-only mode or without data; the caller reports it
            }
            Boundary();
            Blocks.Add(new Block(_lists.Count, ListKind.None, null, index));
        }

        /// <summary>Adds attachments that the note body does not show at the end of the page, under a heading.</summary>
        public void AddRemainingAttachments()
        {
            if (Plain)
            {
                return;
            }
            var remaining = Enumerable.Range(0, note.Resources.Count).Where(i => !_placed.Contains(i) && data.ContainsKey(i)).ToList();
            if (remaining.Count == 0)
            {
                return;
            }
            const string heading = "Attachments:";
            Added.Add(new AddedText(heading, AddedTextPosition.End));
            Blocks.Add(new Block(0, ListKind.None, $"<span style='font-weight:bold'>{heading}</span>"));
            foreach (var index in remaining)
            {
                AtEnd.Add(index);
                Blocks.Add(new Block(0, ListKind.None, null, index));
            }
        }
    }
}
