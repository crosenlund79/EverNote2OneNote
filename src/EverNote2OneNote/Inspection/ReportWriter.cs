using System.Globalization;
using System.Text;

namespace EverNote2OneNote.Inspection;

/// <summary>
/// Writes the detailed local report. It contains note titles and other personal details,
/// so it is written outside the repository and must never be committed.
/// </summary>
public static class ReportWriter
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly UTF8Encoding Utf8WithBom = new(true); // so Excel opens CSV files as UTF-8

    /// <summary>%LOCALAPPDATA%\EverNote2OneNote\reports\inspect-&lt;time&gt; (never inside the repository or the source folder).</summary>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EverNote2OneNote", "reports",
        "inspect-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>True if <paramref name="path"/> is <paramref name="folder"/> or lies inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar).StartsWith(normalizedFolder, comparison);
    }

    public static void Write(InspectionResult result, string summary, string directory, bool dumpText)
    {
        Directory.CreateDirectory(directory);
        var fileNumbers = result.Files.Select((f, i) => (f, i + 1)).ToDictionary(x => x.f, x => x.Item2);

        File.WriteAllText(Path.Combine(directory, "report.md"), BuildMarkdown(result, summary), Utf8);
        WriteNotesCsv(result, Path.Combine(directory, "notes.csv"));
        WriteAttachmentsCsv(result, Path.Combine(directory, "attachments.csv"));
        WriteRecovered(result, Path.Combine(directory, "recovered"), fileNumbers);
        if (dumpText)
        {
            WriteText(result, Path.Combine(directory, "text"), fileNumbers);
        }
    }

    private static string BuildMarkdown(InspectionResult result, string summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ENEX inspection report");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} from `{result.SourcePath}`.");
        sb.AppendLine();
        sb.AppendLine("> **Private:** this report contains note titles, notebook names and other personal details. Do not commit or share it.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("```text");
        sb.Append(summary);
        sb.AppendLine("```");
        sb.AppendLine();

        var issuesByFile = result.Issues.ToLookup(i => i.File);
        sb.AppendLine("## Notebooks");
        sb.AppendLine();
        sb.AppendLine("| # | Notebook | File | Notes | Attachments | Attachment size | Errors | Warnings | Exported by |");
        sb.AppendLine("|---:|---|---|---:|---:|---:|---:|---:|---|");
        int number = 0;
        foreach (var file in result.Files)
        {
            var notes = result.Notes.Where(n => n.File == file).ToList();
            var fileIssues = issuesByFile[file.RelativePath].ToList();
            sb.AppendLine($"| {++number} | {Cell(file.Notebook)} | {Cell(file.RelativePath)} | {file.NoteCount} | " +
                          $"{notes.Sum(n => n.AttachmentCount)} | {Format.Bytes(notes.Sum(n => n.AttachmentBytes))} | " +
                          $"{fileIssues.Count(i => i.Severity == Severity.Error)} | {fileIssues.Count(i => i.Severity == Severity.Warning)} | " +
                          $"{Cell($"{file.Header?.Application} {file.Header?.Version}".Trim())} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Findings");
        foreach (var severity in Enum.GetValues<Severity>())
        {
            var issues = result.Issues.Where(i => i.Severity == severity).ToList();
            sb.AppendLine();
            sb.AppendLine($"### {severity} ({issues.Count})");
            sb.AppendLine();
            if (issues.Count == 0)
            {
                sb.AppendLine("None.");
                continue;
            }
            sb.AppendLine("| Code | File | Note | Title | Details |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var issue in issues.OrderBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.NoteIndex))
            {
                sb.AppendLine($"| {issue.Code} | {Cell(issue.File)} | {issue.NoteIndex?.ToString(CultureInfo.InvariantCulture) ?? ""} | {Cell(issue.NoteTitle ?? "")} | {Cell(issue.Message)} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Attachment types");
        sb.AppendLine();
        sb.AppendLine("| Type | Count | Total size | Largest |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var group in result.Attachments.GroupBy(a => a.Mime ?? "(none)").OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"| {Cell(group.Key)} | {group.Count()} | {Format.Bytes(group.Sum(a => a.Size))} | {Format.Bytes(group.Max(a => a.Size))} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Finding codes");
        sb.AppendLine();
        sb.AppendLine("| Code | Severity | Meaning |");
        sb.AppendLine("|---|---|---|");
        foreach (var definition in IssueCodes.All)
        {
            sb.AppendLine($"| {definition.Code} | {definition.Severity} | {Cell(definition.Description)} |");
        }
        sb.AppendLine();
        sb.AppendLine("Files: `notes.csv` (one row per note), `attachments.csv` (one row per attachment), " +
                      "`recovered/` (notes whose text had to be recovered, with their original markup), " +
                      "`text/` (full extracted text, only with --dump-text).");
        return sb.ToString();
    }

    private static void WriteNotesCsv(InspectionResult result, string path)
    {
        var errorsByNote = result.Issues.Where(i => i.NoteIndex is not null)
            .GroupBy(i => (i.File, i.NoteIndex!.Value))
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = result.Notes.Select(n =>
        {
            var issues = errorsByNote.GetValueOrDefault((n.File.RelativePath, n.Index)) ?? [];
            return new[]
            {
                n.File.Notebook, n.File.RelativePath, Num(n.Index), n.Title ?? "", Format.Date(n.Created), Format.Date(n.Updated),
                string.Join("; ", n.Tags), n.TextStatus.ToString(), Num(n.Text.Length), Num(n.Words), Num(n.AttachmentCount),
                Num(n.AttachmentBytes), Num(n.UnreadAttachments), Num(n.Tasks.Count), Num(n.TodoCount), Num(n.EncryptedBlocks),
                string.Join(" ", issues.Where(i => i.Severity == Severity.Error).Select(i => i.Code).Distinct()),
                string.Join(" ", issues.Where(i => i.Severity == Severity.Warning).Select(i => i.Code).Distinct()),
                Num(n.Line),
            };
        });
        Csv.Write(path, Utf8WithBom,
            ["notebook", "file", "note", "title", "created", "updated", "tags", "text_status", "text_characters", "words",
             "attachments", "attachment_bytes", "unread_attachments", "tasks", "checkboxes", "encrypted_blocks", "errors",
             "warnings", "source_line"],
            rows);
    }

    private static void WriteAttachmentsCsv(InspectionResult result, string path)
    {
        var rows = result.Attachments.Select(a => new[]
        {
            a.Note.File.Notebook, Num(a.Note.Index), a.Note.Title ?? "", a.FileName ?? "", a.Mime ?? "", Num(a.Size),
            a.Md5 ?? "", a.Referenced ? "yes" : "no", a.Width ?? "", a.Height ?? "",
        });
        Csv.Write(path, Utf8WithBom,
            ["notebook", "note", "note_title", "file_name", "mime", "size_bytes", "md5", "shown_in_body", "width", "height"],
            rows);
    }

    private static void WriteRecovered(InspectionResult result, string directory, Dictionary<FileSummary, int> fileNumbers)
    {
        foreach (var note in result.Notes.Where(n => n.TextStatus != TextStatus.Extracted || n.Problem is not null || n.UnknownElements.Count > 0))
        {
            Directory.CreateDirectory(directory);
            var sb = new StringBuilder();
            sb.AppendLine($"Notebook:    {note.File.Notebook}");
            sb.AppendLine($"Source file: {note.File.RelativePath} (unchanged)");
            sb.AppendLine($"Note:        #{note.Index} \"{note.Title}\", starting at line {Format.Count(note.Line)} (byte offset {Format.Count(note.ByteOffset)})");
            sb.AppendLine($"Text status: {note.TextStatus}");
            if (note.Problem is not null)
            {
                sb.AppendLine($"Problem:     {note.Problem}");
            }
            sb.AppendLine();
            sb.AppendLine("===== Extracted text =====");
            sb.AppendLine(note.Text);
            if (note.OriginalContent is not null)
            {
                sb.AppendLine();
                sb.AppendLine("===== Original note body (ENML), exactly as exported =====");
                sb.AppendLine(note.OriginalContent);
            }
            if (note.UnknownElements.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("===== Unrecognised items, verbatim =====");
                foreach (var item in note.UnknownElements)
                {
                    sb.AppendLine(item);
                }
            }
            var name = $"{fileNumbers[note.File]:D3}-{note.Index:D4} {Format.FileName(note.File.Notebook)}.txt";
            File.WriteAllText(Path.Combine(directory, name), sb.ToString(), Utf8);
        }
    }

    private static void WriteText(InspectionResult result, string directory, Dictionary<FileSummary, int> fileNumbers)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in result.Files)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Notebook: {file.Notebook} ({file.RelativePath})");
            foreach (var note in result.Notes.Where(n => n.File == file))
            {
                sb.AppendLine();
                sb.AppendLine($"==================== #{note.Index} {note.Title}");
                sb.AppendLine($"Created: {Format.Date(note.Created)} | Updated: {Format.Date(note.Updated)} | Text: {note.TextStatus}");
                if (note.Tags.Count > 0)
                {
                    sb.AppendLine($"Tags: {string.Join(", ", note.Tags)}");
                }
                if (note.EncryptedBlocks > 0)
                {
                    sb.AppendLine($"Encrypted blocks (not readable): {note.EncryptedBlocks}");
                }
                sb.AppendLine("--------------------");
                sb.AppendLine(note.Text);
                if (note.Tasks.Count > 0)
                {
                    sb.AppendLine("Tasks:");
                    foreach (var task in note.Tasks)
                    {
                        sb.AppendLine($"  [{task.Status}] {task.Title}");
                    }
                }
            }
            var name = $"{fileNumbers[file]:D3} {Format.FileName(file.Notebook)}.txt";
            File.WriteAllText(Path.Combine(directory, name), sb.ToString(), Utf8);
        }
    }

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

internal static class Csv
{
    public static void Write(string path, Encoding encoding, string[] header, IEnumerable<string[]> rows)
    {
        using var writer = new StreamWriter(path, false, encoding);
        writer.Write(string.Join(",", header.Select(Escape)) + "\r\n");
        foreach (var row in rows)
        {
            writer.Write(string.Join(",", row.Select(Escape)) + "\r\n");
        }
    }

    private static string Escape(string value)
    {
        // Prevent spreadsheet apps from interpreting note titles as formulas.
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
