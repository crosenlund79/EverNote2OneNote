using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using EverNote2OneNote.Enex;
using EverNote2OneNote.Text;

namespace EverNote2OneNote.OneNote;

/// <summary>An attachment uploaded with a page (a multipart "name:..." part).</summary>
public sealed record PagePart(string Name, int ResourceIndex, string Mime, string FileName, bool IsImage, byte[] Data);

/// <summary>Where text added by the migration (not part of the original note) appears on the page.</summary>
public enum AddedTextPosition { Start, Inline, End }

public sealed record AddedText(string Text, AddedTextPosition Position);

/// <summary>A OneNote page ready to be created.</summary>
public sealed class PagePlan
{
    public required string Title { get; init; }

    /// <summary>The complete page HTML; attachments are referenced as "name:partN".</summary>
    public required string Html { get; init; }

    /// <summary>Attachments uploaded together with <see cref="Html"/>.</summary>
    public List<PagePart> Parts { get; } = [];

    /// <summary>Attachments to append to the end of the page after it has been created.</summary>
    public List<PagePart> Deferred { get; } = [];

    /// <summary>Text the migration added (header, markers, headings), so verification can tell it apart.</summary>
    public List<AddedText> Added { get; } = [];

    /// <summary>Content of the note that could not be migrated as-is.</summary>
    public List<string> Notices { get; } = [];
}

/// <summary>
/// Converts an Evernote note (ENML) into the small HTML subset OneNote accepts: paragraphs, headings, lists,
/// tables, links, basic emphasis, images and file attachments. All text is kept; formatting is simplified.
/// </summary>
public static class PageBuilder
{
    public const long MaxPartBytes = 25L * 1024 * 1024;
    public const string EncryptedMarker = "[Encrypted Evernote content - not migrated]";

    private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/tiff", "image/bmp", "image/x-ms-bmp",
    };

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <param name="note">The note, as read from the ENEX file.</param>
    /// <param name="notebook">Evernote notebook name, shown in the page header.</param>
    /// <param name="data">Decoded attachment data by resource index; attachments without data are left out.</param>
    /// <param name="embedMedia">True: attachments are uploaded with the page at their original position.
    /// False: the page is text only and all attachments are listed in <see cref="PagePlan.Deferred"/>.</param>
    /// <param name="fallbackText">Text to use if the note body cannot be parsed (the inspector's recovered text).</param>
    public static PagePlan Build(EnexNote note, string notebook, IReadOnlyDictionary<int, byte[]> data, bool embedMedia, string fallbackText)
    {
        var title = string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : note.Title.Trim();
        var body = new BodyWriter(note, data, embedMedia);

        var header = Header(note, notebook);
        body.Plan.Added.Add(new AddedText(header, AddedTextPosition.Start));
        body.Out.Append("<p style=\"font-size:9pt;color:#595959\">").Append(Encode(header)).Append("</p>\n");

        bool parsed = false;
        if (note.Content is not null)
        {
            var enml = EnmlText.ResolveEntities(note.Content.TrimStart().TrimStart('﻿').TrimStart(), new HashSet<string>());
            var mark = body.Out.Length;
            try
            {
                body.Walk(enml);
                parsed = true;
            }
            catch (XmlException)
            {
                body.Reset(mark);
                body.Plan.Notices.Add("The note body is not valid ENML; its text was migrated as plain paragraphs.");
            }
        }
        if (!parsed)
        {
            foreach (var line in fallbackText.Split('\n'))
            {
                if (line.Trim().Length > 0)
                {
                    body.Out.Append("<p>").Append(Encode(line)).Append("</p>\n");
                }
            }
        }

        body.WriteRemainingAttachments();

        var created = EnexTimestamp.TryParse(note.Created, out var utc)
            ? $"<meta name=\"created\" content=\"{utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}+00:00\" />\n"
            : "";
        var html = new StringBuilder()
            .Append("<!DOCTYPE html>\n<html>\n<head>\n<title>").Append(Encode(title)).Append("</title>\n")
            .Append(created)
            .Append("</head>\n<body>\n")
            .Append(body.Out)
            .Append("</body>\n</html>\n")
            .ToString();

        var plan = new PagePlan { Title = title, Html = html };
        plan.Parts.AddRange(body.Plan.Parts);
        plan.Deferred.AddRange(body.Plan.Deferred);
        plan.Added.AddRange(body.Plan.Added);
        plan.Notices.AddRange(body.Plan.Notices);
        return plan;
    }

    /// <summary>HTML for one attachment, used both inline and when appending to an existing page.</summary>
    public static string MediaElement(PagePart part) => part.IsImage
        ? $"<img src=\"name:{part.Name}\" alt=\"{Encode(part.FileName)}\" />"
        : $"<object data-attachment=\"{Encode(part.FileName)}\" data=\"name:{part.Name}\" type=\"{Encode(part.Mime)}\" />";

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

    internal static string Encode(string text) => WebUtility.HtmlEncode(text);

    private enum Kind { Body, List, ListItem, Table, Row, Cell, Heading, Pre }

    private sealed class Container(Kind kind, string closeTag)
    {
        public Kind Kind { get; } = kind;
        public string CloseTag { get; } = closeTag;
        public bool NeedsBreak { get; set; }
    }

    /// <summary>Walks the ENML and writes OneNote HTML. Inline content is buffered until a block boundary.</summary>
    private sealed class BodyWriter
    {
        private readonly EnexNote _note;
        private readonly IReadOnlyDictionary<int, byte[]> _data;
        private readonly bool _embed;
        private readonly Stack<Container> _containers = new();
        private readonly List<string> _styles = [];
        private readonly Stack<string?> _links = new();
        private readonly StringBuilder _inline = new();
        private readonly HashSet<int> _placed = [];
        private readonly Dictionary<int, PagePart> _parts = [];
        private bool _hasContent;
        private int _skippedLinks;
        private int _mergedCells;
        private int _duplicateReferences;

        public BodyWriter(EnexNote note, IReadOnlyDictionary<int, byte[]> data, bool embed)
        {
            _note = note;
            _data = data;
            _embed = embed;
            _containers.Push(new Container(Kind.Body, ""));
        }

        public StringBuilder Out { get; } = new();
        public PagePlan Plan { get; } = new() { Title = "", Html = "" };

        public void Reset(int length)
        {
            Out.Length = length;
            _inline.Clear();
            _hasContent = false;
            _containers.Clear();
            _containers.Push(new Container(Kind.Body, ""));
            _styles.Clear();
            _links.Clear();
            _placed.Clear();
            Plan.Parts.Clear();
            Plan.Deferred.Clear();
            Plan.Notices.Clear();
            Plan.Added.RemoveAll(a => a.Position == AddedTextPosition.Inline);
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
                            Plan.Added.Add(new AddedText(EncryptedMarker, AddedTextPosition.Inline));
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

            if (_skippedLinks > 0)
            {
                Plan.Notices.Add($"{_skippedLinks} link(s) to Evernote notes or non-web addresses were kept as plain text.");
            }
            if (_mergedCells > 0)
            {
                Plan.Notices.Add($"{_mergedCells} merged table cell(s) were split, because OneNote does not support merged cells.");
            }
            if (_duplicateReferences > 0)
            {
                Plan.Notices.Add($"{_duplicateReferences} repeated reference(s) to the same attachment were shown once.");
            }
        }

        private void Start(XmlReader reader, string name, bool empty)
        {
            var top = _containers.Peek();
            switch (name)
            {
                case "br":
                    if (top.Kind == Kind.Pre)
                    {
                        Out.Append('\n');
                    }
                    else
                    {
                        _inline.Append("<br />");
                        _hasContent = true;
                    }
                    return;
                case "en-todo":
                    bool done = string.Equals(reader.GetAttribute("checked"), "true", StringComparison.OrdinalIgnoreCase);
                    AppendText(done ? "[x] " : "[ ] ");
                    return;
                case "en-media":
                    Media(reader.GetAttribute("hash"));
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
                case "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "del" or "sub" or "sup":
                    if (!empty)
                    {
                        _styles.Add(name switch { "strong" => "b", "em" => "i", "s" or "strike" => "del", _ => name });
                    }
                    return;
                case "ul" or "ol":
                    Boundary();
                    Out.Append('<').Append(name).Append('>');
                    _containers.Push(new Container(Kind.List, $"</{name}>\n"));
                    return;
                case "li":
                    if (top.Kind != Kind.List)
                    {
                        Boundary(); // stray list item: treat as a paragraph
                        return;
                    }
                    Out.Append("<li>");
                    _containers.Push(new Container(Kind.ListItem, "</li>"));
                    return;
                case "table":
                    Boundary();
                    Out.Append("<table border=\"1\">");
                    _containers.Push(new Container(Kind.Table, "</table>\n"));
                    return;
                case "tr":
                    if (top.Kind != Kind.Table)
                    {
                        Boundary();
                        return;
                    }
                    Out.Append("<tr>");
                    _containers.Push(new Container(Kind.Row, "</tr>"));
                    return;
                case "td" or "th":
                    if (top.Kind != Kind.Row)
                    {
                        Boundary();
                        return;
                    }
                    if (IsMerged(reader.GetAttribute("colspan")) || IsMerged(reader.GetAttribute("rowspan")))
                    {
                        _mergedCells++;
                    }
                    Out.Append("<td>");
                    _containers.Push(new Container(Kind.Cell, "</td>"));
                    return;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Boundary();
                    if (top.Kind == Kind.Body)
                    {
                        Out.Append('<').Append(name).Append('>');
                        _containers.Push(new Container(Kind.Heading, $"</{name}>\n"));
                    }
                    return;
                case "pre" or "xmp":
                    Boundary();
                    if (top.Kind == Kind.Body && !empty)
                    {
                        Out.Append("<pre>");
                        _containers.Push(new Container(Kind.Pre, "</pre>\n"));
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
            var top = _containers.Peek();
            switch (name)
            {
                case "a":
                    if (!wasEmpty && _links.Count > 0)
                    {
                        _links.Pop();
                    }
                    return;
                case "b" or "strong" or "i" or "em" or "u" or "s" or "strike" or "del" or "sub" or "sup":
                    if (!wasEmpty && _styles.Count > 0)
                    {
                        _styles.RemoveAt(_styles.Count - 1);
                    }
                    return;
                case "ul" or "ol" when top.Kind == Kind.List:
                case "li" when top.Kind == Kind.ListItem:
                case "table" when top.Kind == Kind.Table:
                case "tr" when top.Kind == Kind.Row:
                case "td" or "th" when top.Kind == Kind.Cell:
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" when top.Kind == Kind.Heading:
                case "pre" or "xmp" when top.Kind == Kind.Pre:
                    Boundary();
                    Out.Append(_containers.Pop().CloseTag);
                    return;
            }
            if (HtmlElements.Block.Contains(name))
            {
                Boundary();
            }
        }

        private void AppendText(string text)
        {
            if (_containers.Peek().Kind == Kind.Pre)
            {
                Out.Append(Encode(text));
                return;
            }
            if (text.Length == 0)
            {
                return;
            }
            var encoded = Encode(text);
            var open = new StringBuilder();
            var close = new StringBuilder();
            var href = _links.Count > 0 ? _links.Peek() : null;
            if (href is not null)
            {
                open.Append("<a href=\"").Append(Encode(href)).Append("\">");
            }
            foreach (var style in _styles)
            {
                open.Append('<').Append(style).Append('>');
            }
            for (int i = _styles.Count - 1; i >= 0; i--)
            {
                close.Append("</").Append(_styles[i]).Append('>');
            }
            if (href is not null)
            {
                close.Append("</a>");
            }
            _inline.Append(open).Append(encoded).Append(close);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _hasContent = true;
            }
        }

        /// <summary>Ends the current line of inline content and writes it in a form valid for the current container.</summary>
        private void Boundary()
        {
            if (!_hasContent)
            {
                _inline.Clear();
                return;
            }
            var content = _inline.ToString();
            _inline.Clear();
            _hasContent = false;
            var top = _containers.Peek();
            switch (top.Kind)
            {
                case Kind.Body:
                    Out.Append("<p>").Append(content).Append("</p>\n");
                    break;
                case Kind.List:
                    Out.Append("<li>").Append(content).Append("</li>");
                    break;
                case Kind.Table:
                    Out.Append("<tr><td>").Append(content).Append("</td></tr>");
                    break;
                case Kind.Row:
                    Out.Append("<td>").Append(content).Append("</td>");
                    break;
                default: // list item, cell, heading
                    if (top.NeedsBreak)
                    {
                        Out.Append("<br />");
                    }
                    Out.Append(content);
                    top.NeedsBreak = true;
                    break;
            }
        }

        private void Media(string? hash)
        {
            int index = hash is null ? -1 : _note.Resources.FindIndex(r => string.Equals(r.Md5, hash.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                Plan.Notices.Add($"The note body refers to an attachment that is not in the export ({hash}).");
                return;
            }
            if (!_placed.Add(index))
            {
                _duplicateReferences++;
                return;
            }
            var part = PartFor(index);
            if (part is null)
            {
                return; // no data: the caller reports why
            }
            if (!_embed)
            {
                Plan.Deferred.Add(part);
                return;
            }
            var top = _containers.Peek().Kind;
            if (part.IsImage)
            {
                _inline.Append(MediaElement(part));
                _hasContent = true;
                Plan.Parts.Add(part);
            }
            else if (top == Kind.Body)
            {
                Boundary();
                Out.Append(MediaElement(part)).Append('\n');
                Plan.Parts.Add(part);
            }
            else
            {
                _placed.Remove(index); // files inside lists or tables go to the end of the page
            }
        }

        /// <summary>Adds attachments that were not placed in the body (not referenced, or deferred) at the end.</summary>
        public void WriteRemainingAttachments()
        {
            var remaining = Enumerable.Range(0, _note.Resources.Count)
                .Where(i => !_placed.Contains(i))
                .Select(PartFor)
                .OfType<PagePart>()
                .ToList();
            if (_embed && remaining.Count > 0)
            {
                const string heading = "Attachments:";
                Plan.Added.Add(new AddedText(heading, AddedTextPosition.End));
                Out.Append("<p><b>").Append(heading).Append("</b></p>\n");
                foreach (var part in remaining)
                {
                    Out.Append(part.IsImage ? $"<p>{MediaElement(part)}</p>\n" : MediaElement(part) + "\n");
                    Plan.Parts.Add(part);
                }
            }
            else if (!_embed)
            {
                Plan.Deferred.AddRange(remaining);
                if (Plan.Deferred.Count > 0)
                {
                    const string heading = "Attachments:";
                    Plan.Added.Add(new AddedText(heading, AddedTextPosition.End));
                    Out.Append("<p><b>").Append(heading).Append("</b></p>\n");
                }
            }
        }

        private PagePart? PartFor(int index)
        {
            if (_parts.TryGetValue(index, out var existing))
            {
                return existing;
            }
            if (!_data.TryGetValue(index, out var bytes))
            {
                return null;
            }
            var resource = _note.Resources[index];
            var mime = string.IsNullOrWhiteSpace(resource.Mime) ? "application/octet-stream" : resource.Mime.Trim();
            var fileName = string.IsNullOrWhiteSpace(resource.FileName) ? $"attachment-{index + 1}{Extension(mime)}" : resource.FileName.Trim();
            var part = new PagePart($"part{index + 1}", index, mime, fileName, InlineImageTypes.Contains(mime), bytes);
            _parts[index] = part;
            return part;
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

        private static bool IsMerged(string? span) =>
            int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 1;
    }
}
