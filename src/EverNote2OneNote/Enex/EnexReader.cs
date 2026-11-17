using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using EverNote2OneNote.Text;

namespace EverNote2OneNote.Enex;

/// <summary>A structural problem in an ENEX file that is not tied to a single note.</summary>
public sealed record FileProblem(string Code, string Message, long Offset);

/// <summary>
/// Reads an ENEX file strictly read-only, one note at a time.
/// The file is first scanned for note boundaries; each note is then parsed independently,
/// so a damaged note is reported (with its text recovered where possible) and the rest are still read.
/// No DTDs or external resources are ever loaded.
/// </summary>
public sealed partial class EnexReader : IDisposable
{
    internal const int DefaultBufferSize = 1 << 20;
    private const int MaxGapBytes = 64 * 1024;
    private const long MaxRecoveryBytes = 1L << 30;

    private static readonly XmlReaderSettings SegmentSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    private readonly FileStream _stream;
    private readonly List<NoteSegment> _segments;

    private EnexReader(string path, FileStream stream, List<NoteSegment> segments, EnexHeader? header, List<FileProblem> problems)
    {
        Path = path;
        _stream = stream;
        _segments = segments;
        Header = header;
        Problems = problems;
    }

    public string Path { get; }
    public long Length => _stream.Length;
    public EnexHeader? Header { get; }
    public IReadOnlyList<FileProblem> Problems { get; }
    public int NoteCount => _segments.Count;

    public static EnexReader Open(string path) => Open(path, DefaultBufferSize);

    internal static EnexReader Open(string path, int bufferSize)
    {
        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
            BufferSize = 64 * 1024,
        });
        try
        {
            var problems = new List<FileProblem>();
            var start = ReadBytes(stream, 0, (int)Math.Min(4, stream.Length));
            if (start.Length >= 2 && ((start[0] == 0xFF && start[1] == 0xFE) || (start[0] == 0xFE && start[1] == 0xFF)))
            {
                problems.Add(new FileProblem(IssueCodes.UnsupportedEncoding,
                    "The file is UTF-16 encoded; Evernote exports are UTF-8. The file was not read.", 0));
                return new EnexReader(path, stream, [], null, problems);
            }

            stream.Position = 0;
            var segments = EnexScanner.Scan(stream, bufferSize);
            var header = CheckStructure(stream, segments, problems);
            return new EnexReader(path, stream, segments, header, problems);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Parses the notes one by one. Damaged notes are returned with <see cref="EnexNote.ParseError"/> set.</summary>
    public IEnumerable<EnexNote> ReadNotes()
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            var segment = _segments[i];
            var note = new EnexNote { Index = i + 1, ByteOffset = segment.Start, Line = segment.Line };
            try
            {
                _stream.Position = segment.Start;
                using var xml = XmlReader.Create(new BoundedStream(_stream, segment.End - segment.Start), SegmentSettings);
                NoteParser.Parse(xml, note);
            }
            catch (Exception ex) when (ex is XmlException or FormatException or DecoderFallbackException or InvalidOperationException or ArgumentException)
            {
                note.ParseError = DescribeError(ex, segment);
            }

            if (note.ParseError is null && !segment.Terminated)
            {
                note.ParseError = "The note is incomplete (no closing </note> tag).";
            }
            if (note.ParseError is not null)
            {
                Recover(segment, note);
            }
            yield return note;
        }
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>
    /// Reads one note on demand (read-only), starting at its recorded byte offset, without scanning the file.
    /// Attachment data is hashed, not kept; use <see cref="ReadAttachmentData"/> to get it.
    /// If the note cannot be parsed, <see cref="EnexNote.ParseError"/> is set and the fields read so far are returned.
    /// </summary>
    public static EnexNote ReadNoteAt(string path, long noteOffset, int index)
    {
        var note = new EnexNote { Index = index, ByteOffset = noteOffset, Line = 0 };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = noteOffset;
        var settings = SegmentSettings.Clone();
        settings.ConformanceLevel = ConformanceLevel.Fragment; // the file continues after this note
        try
        {
            using var r = XmlReader.Create(stream, settings);
            NoteParser.Parse(r, note);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or DecoderFallbackException or InvalidOperationException or ArgumentException)
        {
            note.ParseError = ex.Message;
        }
        return note;
    }

    /// <summary>
    /// Decodes one attachment on demand. The file is opened read-only, parsing starts at the note's byte offset,
    /// other attachments are skipped without decoding, and the decoded data is checked against the expected MD5.
    /// </summary>
    /// <param name="resourceIndex">0-based position of the attachment within the note.</param>
    /// <exception cref="InvalidDataException">The attachment was not found, is larger than <paramref name="maxBytes"/>,
    /// or its data no longer matches (the file changed since it was inspected).</exception>
    public static byte[] ReadAttachmentData(string path, long noteOffset, int resourceIndex, string? expectedMd5, long maxBytes)
    {
        const string changed = "The export file may have changed since it was inspected; run the inspection again.";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = noteOffset;
        var settings = SegmentSettings.Clone();
        settings.ConformanceLevel = ConformanceLevel.Fragment; // the file continues after this note
        using var r = XmlReader.Create(stream, settings);

        r.MoveToContent();
        if (r.NodeType != XmlNodeType.Element || r.LocalName != "note" || r.IsEmptyElement)
        {
            throw new InvalidDataException("The note was not found at its recorded position. " + changed);
        }
        r.ReadStartElement();
        int position = 0;
        while (r.MoveToContent() == XmlNodeType.Element)
        {
            if (r.LocalName == "resource" && position++ == resourceIndex && !r.IsEmptyElement)
            {
                r.ReadStartElement();
                while (r.MoveToContent() == XmlNodeType.Element)
                {
                    if (r.LocalName == "data")
                    {
                        return DecodeData(r, expectedMd5, maxBytes, changed);
                    }
                    r.Skip();
                }
                break;
            }
            r.Skip();
        }
        throw new InvalidDataException("The attachment was not found in the note. " + changed);
    }

    private static byte[] DecodeData(XmlReader r, string? expectedMd5, long maxBytes, string changed)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var output = new MemoryStream();
        if (!r.IsEmptyElement)
        {
            var buffer = new byte[96 * 1024];
            int read;
            while ((read = r.ReadElementContentAsBase64(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length + read > maxBytes)
                {
                    throw new InvalidDataException($"The attachment is larger than {maxBytes:N0} bytes.");
                }
                output.Write(buffer, 0, read);
                md5.AppendData(buffer, 0, read);
            }
        }
        var actual = Convert.ToHexStringLower(md5.GetHashAndReset());
        if (expectedMd5 is not null && !actual.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The attachment data does not match what was inspected. " + changed);
        }
        return output.ToArray();
    }

    /// <summary>
    /// For a damaged note: recovers from the raw markup whatever the XML parser could not read
    /// (title, dates, tags, body text) and counts attachments that could not be inventoried.
    /// </summary>
    private void Recover(NoteSegment segment, EnexNote note)
    {
        long length = segment.End - segment.Start;
        if (length > MaxRecoveryBytes)
        {
            note.ParseError += $" The note is too large ({length:N0} bytes) for automatic recovery; " +
                               $"its original markup starts at line {segment.Line} of the source file.";
            return;
        }
        var bytes = ReadBytes(_stream, segment.Start, (int)length);
        var markup = RawText.DecodeWithoutBinaryData(bytes);
        note.Title ??= RawText.FindElement(markup, "title");
        note.Created ??= RawText.FindElement(markup, "created");
        note.Updated ??= RawText.FindElement(markup, "updated");
        if (note.Tags.Count == 0)
        {
            note.Tags.AddRange(RawText.FindElements(markup, "tag"));
        }
        note.UnreadAttachments = Math.Max(0, RawText.CountResources(markup) - note.Resources.Count);
        if (note.Content is null)
        {
            note.RecoveredText = RawText.FromNoteSegment(markup);
        }
    }

    private static string DescribeError(Exception ex, NoteSegment segment)
    {
        if (ex is XmlException xml && xml.LineNumber > 0)
        {
            var message = PositionSuffix().Replace(xml.Message, "").Trim();
            long line = segment.Line + xml.LineNumber - 1;
            return $"XML error near line {line.ToString("N0", CultureInfo.InvariantCulture)}: {message}";
        }
        return ex.Message;
    }

    /// <summary>
    /// Checks everything outside the notes: the XML declaration, the &lt;en-export&gt; root, and that
    /// nothing else (stray text, a stray &lt;/note&gt;, a missing end tag) is present.
    /// </summary>
    private static EnexHeader? CheckStructure(FileStream stream, List<NoteSegment> segments, List<FileProblem> problems)
    {
        EnexHeader? header = null;
        bool rootClosed = false;
        long length = stream.Length;
        bool lastTerminated = segments.Count == 0 || segments[^1].Terminated;

        int gapCount = segments.Count + (lastTerminated ? 1 : 0);
        for (int i = 0; i < gapCount; i++)
        {
            long start = i == 0 ? 0 : segments[i - 1].End;
            long end = i < segments.Count ? segments[i].Start : length;
            bool isFirst = i == 0;
            bool isLast = i == segments.Count;

            long gapLength = end - start;
            var text = new UTF8Encoding(false).GetString(ReadBytes(stream, start, (int)Math.Min(gapLength, MaxGapBytes)));
            var residue = CommentPattern().Replace(text.TrimStart('﻿'), "");

            if (isFirst)
            {
                var declaration = XmlDeclarationPattern().Match(residue);
                if (declaration.Success && EncodingPattern().Match(declaration.Value) is { Success: true } enc
                    && !enc.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(new FileProblem(IssueCodes.UnexpectedEncoding,
                        $"The file declares encoding '{enc.Groups[1].Value}'; it is read as UTF-8.", 0));
                }
                residue = XmlDeclarationPattern().Replace(residue, "", 1);
                residue = DoctypePattern().Replace(residue, "", 1);
                var root = RootStartPattern().Match(residue);
                if (root.Success)
                {
                    header = ParseHeader(root.Groups[1].Value);
                    rootClosed = root.Groups[2].Value == "/";
                    residue = residue.Remove(root.Index, root.Length);
                }
            }
            if (isLast)
            {
                rootClosed |= RootEndPattern().IsMatch(residue);
                residue = RootEndPattern().Replace(residue, "");
            }

            residue = residue.Trim();
            if (residue.Length > 0 || gapLength > MaxGapBytes)
            {
                string where = segments.Count == 0 ? "in a file without notes"
                    : isFirst ? "before the first note"
                    : isLast ? "after the last note"
                    : $"between note {i} and note {i + 1}";
                problems.Add(new FileProblem(IssueCodes.UnexpectedContent,
                    $"{gapLength:N0} bytes of unexpected data {where} (byte offset {start:N0}): \"{Snippet(residue)}\"", start));
            }
        }

        if (header is null)
        {
            problems.Add(new FileProblem(IssueCodes.NotEnex,
                "No <en-export> root element was found; this does not look like an Evernote export.", 0));
        }
        else if (!lastTerminated)
        {
            problems.Add(new FileProblem(IssueCodes.FileTruncated,
                $"The file ends inside note {segments.Count}; the export is incomplete and later notes may be missing.", length));
        }
        else if (!rootClosed)
        {
            problems.Add(new FileProblem(IssueCodes.FileTruncated,
                "The file ends without </en-export>; the export may be incomplete.", length));
        }
        return header;
    }

    private static EnexHeader ParseHeader(string attributes)
    {
        var values = AttributePattern().Matches(attributes)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
        return new EnexHeader(values.GetValueOrDefault("application"), values.GetValueOrDefault("version"), values.GetValueOrDefault("export-date"));
    }

    private static string Snippet(string text)
    {
        var collapsed = WhitespacePattern().Replace(text, " ");
        return collapsed.Length <= 80 ? collapsed : collapsed[..80] + "...";
    }

    private static byte[] ReadBytes(Stream stream, long offset, int count)
    {
        var bytes = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    [GeneratedRegex(@"\s*Line \d+, position \d+\.")]
    private static partial Regex PositionSuffix();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<\?xml\b[^?]*\?>")]
    private static partial Regex XmlDeclarationPattern();

    [GeneratedRegex(@"encoding\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex EncodingPattern();

    [GeneratedRegex(@"<!DOCTYPE[^\[>]*(\[.*?\])?\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DoctypePattern();

    [GeneratedRegex(@"<en-export\b([^>]*?)(/?)>")]
    private static partial Regex RootStartPattern();

    [GeneratedRegex(@"</en-export\s*>")]
    private static partial Regex RootEndPattern();

    [GeneratedRegex(@"([\w-]+)\s*=\s*""([^""]*)""")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    /// <summary>Parses a single &lt;note&gt; element. Attachment data is hashed while streaming and never kept in memory.</summary>
    private static class NoteParser
    {
        public static void Parse(XmlReader r, EnexNote note)
        {
            r.MoveToContent();
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "note")
            {
                throw new XmlException("Expected a <note> element.");
            }
            if (r.IsEmptyElement)
            {
                return;
            }
            r.ReadStartElement();
            while (true)
            {
                var type = r.MoveToContent();
                if (type == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "title": note.Title = r.ReadElementContentAsString(); break;
                        case "content": note.Content = r.ReadElementContentAsString(); break;
                        case "created": note.Created = r.ReadElementContentAsString(); break;
                        case "updated": note.Updated = r.ReadElementContentAsString(); break;
                        case "tag": note.Tags.Add(r.ReadElementContentAsString()); break;
                        case "note-attributes": note.Attributes.AddRange(ReadFields(r)); break;
                        case "task": note.Tasks.Add(ReadTask(r)); break;
                        case "resource": note.Resources.Add(ReadResource(r, note)); break;
                        default: note.UnknownElements.Add(r.ReadOuterXml()); break;
                    }
                }
                else if (type is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    note.UnknownElements.Add(r.Value);
                    r.Read();
                }
                else
                {
                    break;
                }
            }
            r.ReadEndElement();
        }

        private static EnexResource ReadResource(XmlReader r, EnexNote note)
        {
            var resource = new EnexResource();
            if (r.IsEmptyElement)
            {
                r.Read();
                return resource;
            }
            r.ReadStartElement();
            while (true)
            {
                var type = r.MoveToContent();
                if (type == XmlNodeType.Element)
                {
                    switch (r.LocalName)
                    {
                        case "data":
                            (resource.Size, resource.Md5) = ReadBinary(r);
                            break;
                        case "alternate-data":
                            resource.AlternateDataSize = ReadBinary(r).Size;
                            break;
                        case "mime": resource.Mime = r.ReadElementContentAsString().Trim(); break;
                        case "width": resource.Width = r.ReadElementContentAsString().Trim(); break;
                        case "height": resource.Height = r.ReadElementContentAsString().Trim(); break;
                        case "duration": resource.Attributes.Add(new("duration", r.ReadElementContentAsString())); break;
                        case "recognition":
                            resource.HasRecognition = true;
                            r.Skip();
                            break;
                        case "resource-attributes": resource.Attributes.AddRange(ReadFields(r)); break;
                        default: note.UnknownElements.Add(r.ReadOuterXml()); break;
                    }
                }
                else if (type is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    note.UnknownElements.Add(r.Value);
                    r.Read();
                }
                else
                {
                    break;
                }
            }
            r.ReadEndElement();
            return resource;
        }

        private static (long Size, string Md5) ReadBinary(XmlReader r)
        {
            var encoding = r.GetAttribute("encoding");
            if (encoding is not null && !encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new XmlException($"Unsupported attachment data encoding '{encoding}'.");
            }

            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            long size = 0;
            if (r.IsEmptyElement)
            {
                r.Read();
            }
            else
            {
                var buffer = new byte[96 * 1024];
                int read;
                while ((read = r.ReadElementContentAsBase64(buffer, 0, buffer.Length)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    size += read;
                }
            }
            return (size, Convert.ToHexStringLower(md5.GetHashAndReset()));
        }

        private static EnexTask ReadTask(XmlReader r)
        {
            var element = (XElement)XNode.ReadFrom(r);
            return new EnexTask(
                element.Element("title")?.Value,
                element.Element("taskStatus")?.Value,
                FieldsOf(element));
        }

        private static List<KeyValuePair<string, string>> ReadFields(XmlReader r) =>
            FieldsOf((XElement)XNode.ReadFrom(r));

        /// <summary>Every child element as name/value, so no metadata is silently dropped.</summary>
        private static List<KeyValuePair<string, string>> FieldsOf(XElement element)
        {
            var fields = new List<KeyValuePair<string, string>>();
            foreach (var node in element.Nodes())
            {
                if (node is XElement child)
                {
                    var key = child.Attribute("key") is { } k ? $"{child.Name.LocalName}[{k.Value}]" : child.Name.LocalName;
                    var value = child.HasElements ? string.Concat(child.Nodes()) : child.Value;
                    fields.Add(new(key, value));
                }
                else if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                {
                    fields.Add(new("#text", text.Value));
                }
            }
            return fields;
        }
    }

    /// <summary>Read-only view of the next <c>length</c> bytes of a stream.</summary>
    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }
            if (buffer.Length > _remaining)
            {
                buffer = buffer[..(int)_remaining];
            }
            int read = inner.Read(buffer);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
