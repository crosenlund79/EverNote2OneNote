using System.Text;

namespace EverNote2OneNote.Text;

/// <summary>
/// Assembles extracted text line by line, collapsing whitespace the way a browser would
/// (except inside &lt;pre&gt;), while keeping every character of visible text in order.
/// </summary>
internal sealed class TextBuilder
{
    private readonly List<string> _lines = [];
    private readonly StringBuilder _line = new();
    private bool _pendingSpace;

    public void Append(string value, bool preserveWhitespace)
    {
        if (preserveWhitespace)
        {
            var parts = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    HardBreak();
                }
                FlushPendingSpace();
                _line.Append(parts[i]);
            }
            return;
        }

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c)) // includes non-breaking space
            {
                _pendingSpace = true;
                continue;
            }
            FlushPendingSpace();
            _line.Append(c);
        }
    }

    /// <summary>Inline marker such as a checkbox, kept with the text that follows it.</summary>
    public void AppendMarker(string marker)
    {
        FlushPendingSpace();
        _line.Append(marker);
    }

    /// <summary>End of a table cell.</summary>
    public void CellBreak()
    {
        _pendingSpace = false;
        _line.Append('\t');
    }

    /// <summary>Block boundary: starts a new line unless the current one is empty.</summary>
    public void SoftBreak()
    {
        if (_line.Length > 0)
        {
            Flush();
        }
        _pendingSpace = false;
    }

    /// <summary>Explicit line break (&lt;br/&gt;): always ends the current line, even an empty one.</summary>
    public void HardBreak() => Flush();

    public override string ToString()
    {
        if (_line.Length > 0)
        {
            Flush();
        }

        var result = new StringBuilder();
        bool blankPending = false;
        foreach (var line in _lines)
        {
            if (line.Length == 0)
            {
                blankPending = result.Length > 0;
                continue;
            }
            if (result.Length > 0)
            {
                result.Append(blankPending ? "\n\n" : "\n");
            }
            result.Append(line);
            blankPending = false;
        }
        return result.ToString();
    }

    private void FlushPendingSpace()
    {
        if (_pendingSpace && _line.Length > 0 && !char.IsWhiteSpace(_line[^1]))
        {
            _line.Append(' ');
        }
        _pendingSpace = false;
    }

    private void Flush()
    {
        _lines.Add(_line.ToString().TrimEnd());
        _line.Clear();
        _pendingSpace = false;
    }
}
