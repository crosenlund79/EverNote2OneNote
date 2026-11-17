namespace EverNote2OneNote.Enex;

/// <summary>Byte range of one &lt;note&gt; element in an ENEX file.</summary>
internal readonly record struct NoteSegment(long Start, long End, long Line, bool Terminated);

/// <summary>
/// Finds the byte ranges of &lt;note&gt; elements without parsing the XML, so that every note can
/// be parsed on its own and one damaged note cannot hide the notes after it.
/// CDATA sections and comments are skipped, so markup inside a note body is never mistaken
/// for a note boundary. Works for any ASCII-compatible encoding (ENEX files are UTF-8).
/// </summary>
internal sealed class EnexScanner
{
    private const int Lookahead = 9; // length of "<![CDATA["

    private static ReadOnlySpan<byte> NoteOpen => "<note>"u8;
    private static ReadOnlySpan<byte> NoteClose => "</note>"u8;
    private static ReadOnlySpan<byte> CDataOpen => "<![CDATA["u8;
    private static ReadOnlySpan<byte> CDataClose => "]]>"u8;
    private static ReadOnlySpan<byte> CommentOpen => "<!--"u8;
    private static ReadOnlySpan<byte> CommentClose => "-->"u8;

    private enum Mode { Markup, CData, Comment }

    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private readonly List<NoteSegment> _segments = [];
    private int _count;          // valid bytes in _buffer
    private int _pos;            // scan position in _buffer
    private int _lineMark;       // _buffer index up to which newlines have been counted
    private long _bufferStart;   // absolute file offset of _buffer[0]
    private long _line = 1;      // line number at _lineMark
    private bool _eof;
    private long? _noteStart;
    private long _noteLine;

    private EnexScanner(Stream stream, int bufferSize)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(bufferSize, 4 * Lookahead)];
    }

    public static List<NoteSegment> Scan(Stream stream, int bufferSize)
    {
        var scanner = new EnexScanner(stream, bufferSize);
        scanner.Run();
        return scanner._segments;
    }

    private void Run()
    {
        var mode = Mode.Markup;
        while (true)
        {
            int available = _count - _pos;
            int needed = mode == Mode.Markup ? Lookahead : 3;
            if (available < needed && !_eof)
            {
                Refill();
                continue;
            }
            if (available == 0)
            {
                break;
            }

            var span = _buffer.AsSpan(_pos, available);
            if (mode == Mode.Markup)
            {
                int lt = span.IndexOf((byte)'<');
                if (lt < 0)
                {
                    _pos = _count;
                    continue;
                }
                _pos += lt;
                span = span[lt..];
                if (span.Length < Lookahead && !_eof)
                {
                    continue; // refill to see the whole tag
                }

                if (span.StartsWith(CDataOpen))
                {
                    mode = Mode.CData;
                    _pos += CDataOpen.Length;
                }
                else if (span.StartsWith(CommentOpen))
                {
                    mode = Mode.Comment;
                    _pos += CommentOpen.Length;
                }
                else if (span.StartsWith(NoteOpen))
                {
                    OnNoteOpen();
                    _pos += NoteOpen.Length;
                }
                else if (span.StartsWith(NoteClose))
                {
                    _pos += NoteClose.Length;
                    OnNoteClose(_bufferStart + _pos);
                }
                else
                {
                    _pos++;
                }
            }
            else
            {
                var terminator = mode == Mode.CData ? CDataClose : CommentClose;
                int idx = span.IndexOf(terminator);
                if (idx < 0)
                {
                    // Keep the last bytes in case the terminator straddles the buffer boundary.
                    _pos = _eof ? _count : Math.Max(_pos, _count - (terminator.Length - 1));
                    continue;
                }
                _pos += idx + terminator.Length;
                mode = Mode.Markup;
            }
        }

        long fileLength = _bufferStart + _count;
        if (_noteStart is long start)
        {
            _segments.Add(new NoteSegment(start, fileLength, _noteLine, Terminated: false));
        }
    }

    private void OnNoteOpen()
    {
        long offset = _bufferStart + _pos;
        _line += _buffer.AsSpan(_lineMark, _pos - _lineMark).Count((byte)'\n');
        _lineMark = _pos;

        if (_noteStart is long previous)
        {
            // A new note starts before the previous one was closed.
            _segments.Add(new NoteSegment(previous, offset, _noteLine, Terminated: false));
        }
        _noteStart = offset;
        _noteLine = _line;
    }

    private void OnNoteClose(long end)
    {
        if (_noteStart is long start)
        {
            _segments.Add(new NoteSegment(start, end, _noteLine, Terminated: true));
            _noteStart = null;
        }
        // A stray </note> outside a note is reported by the gap check in EnexReader.
    }

    private void Refill()
    {
        _line += _buffer.AsSpan(_lineMark, _pos - _lineMark).Count((byte)'\n');
        int remaining = _count - _pos;
        Buffer.BlockCopy(_buffer, _pos, _buffer, 0, remaining);
        _bufferStart += _pos;
        _count = remaining;
        _pos = 0;
        _lineMark = 0;

        int read = _stream.Read(_buffer, _count, _buffer.Length - _count);
        if (read == 0)
        {
            _eof = true;
        }
        else
        {
            _count += read;
        }
    }
}
