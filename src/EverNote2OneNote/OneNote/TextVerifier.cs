using System.Text;

namespace EverNote2OneNote.OneNote;

/// <summary>Where text added by the migration (not part of the original note) appears on the page.</summary>
public enum AddedTextPosition { Start, Inline, End }

public sealed record AddedText(string Text, AddedTextPosition Position);

public enum DifferenceKind
{
    /// <summary>Original text that is not on the page.</summary>
    Missing,

    /// <summary>Text on the page that is not in the original.</summary>
    Extra,

    /// <summary>Original text that is on the page, but in a different place.</summary>
    Moved,

    /// <summary>Original text that appears on the page more than once.</summary>
    Duplicated,
}

public sealed record TextDifference(DifferenceKind Kind, string Text, string Context);

public sealed class TextVerification
{
    /// <summary>Non-whitespace characters in the original text.</summary>
    public int OriginalCharacters { get; init; }

    /// <summary>Non-whitespace characters on the page, excluding text the migration added.</summary>
    public int PageCharacters { get; init; }

    public List<TextDifference> Differences { get; } = [];

    /// <summary>Text added by the migration (header, markers) that was not found on the page.</summary>
    public List<string> AddedTextNotFound { get; } = [];

    /// <summary>True if the texts differ too much to list individual differences.</summary>
    public bool TooManyDifferences { get; init; }

    public bool Identical => Differences.Count == 0 && !TooManyDifferences;
}

/// <summary>
/// Compares the original note text with the text read back from OneNote. Whitespace and line breaks are ignored
/// (formatting may change); every other character must be present, in the same order, exactly once.
/// </summary>
public static class TextVerifier
{
    private const int MaxEdits = 3000;
    private const int MinMatchLength = 3;

    public static TextVerification Compare(string original, string page, IReadOnlyList<AddedText> added)
    {
        var (a, aMap) = Normalize(original);
        var b = Normalize(page).Text;

        var notFound = new List<string>();
        b = RemoveAdded(b, added, notFound);

        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)])
        {
            suffix++;
        }
        var midA = a.Substring(prefix, a.Length - prefix - suffix);
        var midB = b.Substring(prefix, b.Length - prefix - suffix);

        var ops = midA.Length + midB.Length == 0 ? [] : Diff(midA, midB, MaxEdits);
        var result = new TextVerification
        {
            OriginalCharacters = a.Length,
            PageCharacters = b.Length,
            TooManyDifferences = ops is null,
        };
        result.AddedTextNotFound.AddRange(notFound);
        if (ops is null)
        {
            return result;
        }

        // Group edits into regions of consecutive deletions (missing) and insertions (extra).
        var missing = new List<(int Start, int End)>();
        var extra = new List<(int At, string Text)>();
        int ai = 0, bi = 0, i = 0;
        while (i < ops.Count)
        {
            if (ops[i] == Op.Equal)
            {
                ai++;
                bi++;
                i++;
                continue;
            }
            int deleteStart = ai;
            var inserted = new StringBuilder();
            while (i < ops.Count && ops[i] != Op.Equal)
            {
                if (ops[i] == Op.Delete)
                {
                    ai++;
                }
                else
                {
                    inserted.Append(midB[bi++]);
                }
                i++;
            }
            if (ai > deleteStart)
            {
                missing.Add((prefix + deleteStart, prefix + ai));
            }
            if (inserted.Length > 0)
            {
                extra.Add((prefix + deleteStart, inserted.ToString()));
            }
        }

        // Classify: a deletion and an insertion of the same text is a move; inserted original text is a duplicate.
        var missingTexts = missing.Select(m => a[m.Start..m.End]).ToList();
        var moved = new HashSet<int>();
        foreach (var (at, text) in extra)
        {
            int match = -1;
            for (int m = 0; m < missingTexts.Count && text.Length >= MinMatchLength; m++)
            {
                if (!moved.Contains(m) && SameText(missingTexts[m], text))
                {
                    match = m;
                    break;
                }
            }
            if (match >= 0 && moved.Add(match))
            {
                result.Differences.Add(new TextDifference(DifferenceKind.Moved, Original(original, aMap, missing[match]), Context(original, aMap, missing[match].Start)));
            }
            else if (text.Length >= MinMatchLength && a.Contains(text, StringComparison.Ordinal))
            {
                result.Differences.Add(new TextDifference(DifferenceKind.Duplicated, text, Context(original, aMap, at)));
            }
            else
            {
                result.Differences.Add(new TextDifference(DifferenceKind.Extra, text, Context(original, aMap, at)));
            }
        }
        for (int m = 0; m < missing.Count; m++)
        {
            if (!moved.Contains(m))
            {
                result.Differences.Add(new TextDifference(DifferenceKind.Missing, Original(original, aMap, missing[m]), Context(original, aMap, missing[m].Start)));
            }
        }
        return result;
    }

    /// <summary>
    /// True if two runs are the same text, allowing for the diff aligning a moved block a few characters differently
    /// (e.g. "abc." deleted and ".abc" inserted): they share a common substring of at least 80% of the longer run.
    /// </summary>
    private static bool SameText(string x, string y)
    {
        if (x == y)
        {
            return true;
        }
        if (x.Length > 2000 || y.Length > 2000)
        {
            return false;
        }
        int longest = 0;
        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (int i = 1; i <= x.Length; i++)
        {
            for (int j = 1; j <= y.Length; j++)
            {
                current[j] = x[i - 1] == y[j - 1] ? previous[j - 1] + 1 : 0;
                longest = Math.Max(longest, current[j]);
            }
            (previous, current) = (current, previous);
        }
        return longest >= 0.8 * Math.Max(x.Length, y.Length);
    }

    /// <summary>Removes text the migration added, at the position where it was placed.</summary>
    private static string RemoveAdded(string page, IReadOnlyList<AddedText> added, List<string> notFound)
    {
        int inlineFrom = 0;
        foreach (var item in added)
        {
            var text = Normalize(item.Text).Text;
            if (text.Length == 0)
            {
                continue;
            }
            int index = item.Position switch
            {
                AddedTextPosition.Start => page.StartsWith(text, StringComparison.Ordinal) ? 0 : -1,
                AddedTextPosition.End => page.LastIndexOf(text, StringComparison.Ordinal),
                _ => page.IndexOf(text, inlineFrom, StringComparison.Ordinal),
            };
            if (index < 0)
            {
                notFound.Add(item.Text);
                continue;
            }
            page = page.Remove(index, text.Length);
            if (item.Position == AddedTextPosition.Inline)
            {
                inlineFrom = index;
            }
        }
        return page;
    }

    /// <summary>The text without whitespace, plus the position of each kept character in the original.</summary>
    private static (string Text, int[] Map) Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                sb.Append(text[i]);
                map.Add(i);
            }
        }
        return (sb.ToString(), map.ToArray());
    }

    private static string Original(string original, int[] map, (int Start, int End) range) =>
        original[map[range.Start]..(map[range.End - 1] + 1)];

    private static string Context(string original, int[] map, int normalizedIndex)
    {
        int at = map.Length == 0 ? 0 : normalizedIndex < map.Length ? map[normalizedIndex] : original.Length;
        int start = Math.Max(0, at - 30);
        int end = Math.Min(original.Length, at + 30);
        var snippet = original[start..end].Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        return (start > 0 ? "..." : "") + snippet + (end < original.Length ? "..." : "");
    }

    private enum Op : byte { Equal, Delete, Insert }

    /// <summary>Myers' O((N+M)D) difference algorithm. Returns null if more than <paramref name="maxEdits"/> edits are needed.</summary>
    private static List<Op>? Diff(string a, string b, int maxEdits)
    {
        int n = a.Length, m = b.Length;
        int limit = Math.Min(maxEdits, n + m);
        int offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();

        int found = -1;
        for (int d = 0; d <= limit && found < 0; d++)
        {
            for (int k = -d; k <= d; k += 2)
            {
                bool down = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]);
                int x = down ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = d;
                    break;
                }
            }
            var snapshot = new int[2 * d + 1];
            Array.Copy(v, offset - d, snapshot, 0, 2 * d + 1);
            trace.Add(snapshot);
        }
        if (found < 0)
        {
            return null;
        }

        var ops = new List<Op>(n + m);
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var previous = trace[d - 1];
            int Get(int k) => previous[k + d - 1];
            int k = cx - cy;
            bool down = k == -d || (k != d && Get(k - 1) < Get(k + 1));
            int prevK = down ? k + 1 : k - 1;
            int prevX = Get(prevK);
            int prevY = prevX - prevK;
            int startX = down ? prevX : prevX + 1;
            int startY = down ? prevY + 1 : prevY;
            while (cx > startX && cy > startY)
            {
                ops.Add(Op.Equal);
                cx--;
                cy--;
            }
            ops.Add(down ? Op.Insert : Op.Delete);
            cx = prevX;
            cy = prevY;
        }
        while (cx > 0 && cy > 0)
        {
            ops.Add(Op.Equal);
            cx--;
            cy--;
        }
        ops.Reverse();
        return ops;
    }
}
