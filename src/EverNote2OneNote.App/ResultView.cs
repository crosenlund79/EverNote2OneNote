using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.App;

public sealed record Figure(string Value, string Label);

public sealed record NotebookRow(
    string Notebook, string File, int Notes, int TextExtracted, int TextRecovered, int TextNotRecovered,
    int Attachments, string AttachmentSize, int Errors, int Warnings);

public sealed record FindingRow(string Severity, string Code, string Meaning, string File, string Note, string Title, string Details);

/// <summary>The inspection result shaped for display. No logic beyond grouping and counting.</summary>
public sealed record ResultView(IReadOnlyList<Figure> Figures, IReadOnlyList<NotebookRow> Notebooks, IReadOnlyList<FindingRow> Findings)
{
    public static ResultView From(InspectionResult result)
    {
        int Count(TextStatus status) => result.Notes.Count(n => n.TextStatus == status);

        var figures = new List<Figure>
        {
            new(Format.Count(result.Files.Count), "Notebooks"),
            new(Format.Count(result.Notes.Count), "Notes"),
            new(Format.Count(result.Attachments.Count), $"Attachments ({Format.Bytes(result.Attachments.Sum(a => a.Size))})"),
            new(Format.Count(Count(TextStatus.Extracted)), "Text extracted"),
            new(Format.Count(Count(TextStatus.Recovered)), "Text recovered from damage"),
            new(Format.Count(Count(TextStatus.NotRecovered)), "Text NOT recovered"),
            new(Format.Count(result.CountIssues(Severity.Error)), "Errors"),
            new(Format.Count(result.CountIssues(Severity.Warning)), "Warnings"),
        };

        var issuesByFile = result.Issues.ToLookup(i => i.File);
        var notebooks = result.Files.Select(file =>
        {
            var notes = result.Notes.Where(n => n.File == file).ToList();
            var issues = issuesByFile[file.RelativePath].ToList();
            return new NotebookRow(
                file.Notebook,
                file.RelativePath,
                notes.Count,
                notes.Count(n => n.TextStatus == TextStatus.Extracted),
                notes.Count(n => n.TextStatus == TextStatus.Recovered),
                notes.Count(n => n.TextStatus == TextStatus.NotRecovered),
                notes.Sum(n => n.AttachmentCount),
                Format.Bytes(notes.Sum(n => n.AttachmentBytes)),
                issues.Count(i => i.Severity == Severity.Error),
                issues.Count(i => i.Severity == Severity.Warning));
        }).ToList();

        var findings = result.Issues
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.NoteIndex ?? 0)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .Select(i => new FindingRow(
                i.Severity.ToString(),
                i.Code,
                IssueCodes.Get(i.Code).Description,
                i.File,
                i.NoteIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                i.NoteTitle ?? "",
                i.Message))
            .ToList();

        return new ResultView(figures, notebooks, findings);
    }
}
