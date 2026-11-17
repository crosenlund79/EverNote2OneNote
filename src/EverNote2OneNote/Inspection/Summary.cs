using System.Text;
using EverNote2OneNote.Enex;

namespace EverNote2OneNote.Inspection;

/// <summary>Reference figures from the Evernote account, to compare the export against.</summary>
public sealed record Expectations(int? Notes = null, int? Notebooks = null, int? Attachments = null, long? DataBytes = null)
{
    public bool Any => Notes is not null || Notebooks is not null || Attachments is not null || DataBytes is not null;
}

/// <summary>
/// The console summary. It contains counts only: no note titles, notebook names, tags or text,
/// so it can be shared safely.
/// </summary>
public static class Summary
{
    public static string Build(InspectionResult result, Expectations expectations, string? reportDirectory)
    {
        var notes = result.Notes;
        var attachments = result.Attachments;
        long attachmentBytes = attachments.Sum(a => a.Size);
        long dataBytes = attachmentBytes + notes.Sum(n => n.ContentBytes);
        int extracted = notes.Count(n => n.TextStatus == TextStatus.Extracted);
        int recovered = notes.Count(n => n.TextStatus == TextStatus.Recovered);
        int notRecovered = notes.Count(n => n.TextStatus == TextStatus.NotRecovered);

        var sb = new StringBuilder();
        sb.AppendLine("EverNote2OneNote - ENEX inspection");
        sb.AppendLine("==================================");
        Line(sb, "ENEX files", $"{Format.Count(result.Files.Count)} ({Format.Bytes(result.Files.Sum(f => f.Bytes))} on disk)");
        Line(sb, "Notebooks", Format.Count(result.Files.Count));
        Line(sb, "Notes", Format.Count(notes.Count));
        Line(sb, "  text extracted", Format.Count(extracted));
        if (recovered > 0)
        {
            Line(sb, "  text recovered", $"{Format.Count(recovered)} (from damaged markup, see report)");
        }
        if (notRecovered > 0)
        {
            Line(sb, "  text NOT recovered", $"{Format.Count(notRecovered)} (see report)");
        }
        Line(sb, "Attachments", $"{Format.Count(attachments.Count)} ({Format.Bytes(attachmentBytes)})" +
            (attachments.Any(a => !a.Referenced) ? $", {Format.Count(attachments.Count(a => !a.Referenced))} not shown in note bodies" : ""));
        int unreadAttachments = notes.Sum(n => n.UnreadAttachments);
        if (unreadAttachments > 0)
        {
            Line(sb, "  NOT inventoried", $"{Format.Count(unreadAttachments)} (in damaged notes, see report)");
        }
        Line(sb, "Note text",$"{Format.Count(notes.Sum(n => (long)n.Words))} words");
        Line(sb, "Tasks", Format.Count(notes.Sum(n => n.Tasks.Count)));
        Line(sb, "Encrypted", $"{Format.Count(notes.Count(n => n.EncryptedBlocks > 0))} note(s)");
        if (DateRange(notes) is { } range)
        {
            Line(sb, "Date range", range);
        }
        var exporters = result.Files.Where(f => f.Header is not null)
            .GroupBy(f => $"{f.Header!.Application} {f.Header.Version}".Trim())
            .Select(g => $"{(g.Key.Length > 0 ? g.Key : "unknown")} ({g.Count()})");
        Line(sb, "Exported by", string.Join(", ", exporters));

        if (expectations.Any)
        {
            sb.AppendLine();
            sb.AppendLine($"{"Reference check",-22}{"Expected",12}{"Found",12}  Status");
            Compare(sb, "Notebooks", expectations.Notebooks, result.Files.Count);
            Compare(sb, "Notes", expectations.Notes, notes.Count);
            Compare(sb, "Attachments", expectations.Attachments, attachments.Count);
            if (expectations.DataBytes is long expected)
            {
                double difference = expected == 0 ? 0 : (dataBytes - expected) * 100.0 / expected;
                var status = Math.Abs(difference) <= 5 ? $"close ({difference:+0.0;-0.0;0}%)" : $"DIFFERS ({difference:+0.0;-0.0;0}%)";
                sb.AppendLine($"{"Data size",-22}{Format.Bytes(expected),12}{Format.Bytes(dataBytes),12}  {status}");
            }
        }

        sb.AppendLine();
        if (result.Issues.Count == 0)
        {
            sb.AppendLine("Findings: none.");
        }
        else
        {
            sb.AppendLine($"{"Findings",-24}{"Severity",-9}{"Count",6}{"Notes",7}  Meaning");
            foreach (var group in result.Issues.GroupBy(i => i.Code)
                         .OrderBy(g => IssueCodes.Get(g.Key).Severity).ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                var definition = IssueCodes.Get(group.Key);
                int affectedNotes = group.Where(i => i.NoteIndex is not null).Select(i => (i.File, i.NoteIndex)).Distinct().Count();
                sb.AppendLine($"{group.Key,-24}{definition.Severity,-9}{group.Count(),6}{affectedNotes,7}  {definition.Description}");
            }
        }

        sb.AppendLine();
        int errors = result.CountIssues(Severity.Error);
        int warnings = result.CountIssues(Severity.Warning);
        sb.AppendLine(errors > 0
            ? $"RESULT: {errors} error(s) need attention; {warnings} warning(s) to review."
            : $"RESULT: no read errors; all note text was extracted. {warnings} warning(s) to review.");
        if (reportDirectory is not null)
        {
            sb.AppendLine($"Detailed report (private, contains note titles): {reportDirectory}");
        }
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string label, string value) => sb.AppendLine($"{label,-22}{value}");

    private static void Compare(StringBuilder sb, string label, int? expected, int found)
    {
        if (expected is not int e)
        {
            return;
        }
        var status = e == found ? "OK" : $"DIFFERS ({found - e:+#;-#})";
        sb.AppendLine($"{label,-22}{Format.Count(e),12}{Format.Count(found),12}  {status}");
    }

    private static string? DateRange(List<NoteSummary> notes)
    {
        var dates = notes.SelectMany(n => new[] { n.Created, n.Updated })
            .Select(d => EnexTimestamp.TryParse(d, out var utc) ? utc : (DateTime?)null)
            .OfType<DateTime>()
            .ToList();
        return dates.Count == 0 ? null : $"{dates.Min():yyyy-MM-dd} to {dates.Max():yyyy-MM-dd}";
    }
}
