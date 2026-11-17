using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.OneNote;

/// <summary>One ENEX file, migrated as one section.</summary>
public sealed class PlannedSection
{
    public required string Name { get; init; }
    public required FileSummary File { get; init; }

    /// <summary>Notes to migrate, in file order.</summary>
    public List<NoteSummary> Notes { get; } = [];

    /// <summary>Notes left out before migration because they could not be read reliably.</summary>
    public List<ExcludedNote> Excluded { get; } = [];
}

public sealed record ExcludedNote(NoteSummary Note, string Reason);

/// <summary>One folder of ENEX files, migrated as one new notebook.</summary>
public sealed class PlannedNotebook
{
    /// <summary>The name the new notebook gets (unique).</summary>
    public required string Name { get; init; }

    /// <summary>The name derived from the folder, before it was made unique.</summary>
    public required string MappedName { get; init; }

    /// <summary>Folder of the ENEX files relative to the export folder ("" for the export folder itself).</summary>
    public required string RelativeFolder { get; init; }

    /// <summary>Folder where the new local notebook is created.</summary>
    public required string Location { get; init; }

    /// <summary>Why <see cref="Name"/> differs from <see cref="MappedName"/>, if it does.</summary>
    public string? RenameReason { get; init; }

    public List<PlannedSection> Sections { get; } = [];

    public int NoteCount => Sections.Sum(s => s.Notes.Count);
}

/// <summary>What a bulk migration will create, determined (read-only) before anything is written.</summary>
public sealed class BulkPlan
{
    public required InspectionResult Inspection { get; init; }

    /// <summary>This app's OneNote working folder (new notebooks, exports, run reports).</summary>
    public required string Root { get; init; }

    public List<PlannedNotebook> Notebooks { get; } = [];

    /// <summary>File-level read errors from the inspection: content of these files may be missing from the export.</summary>
    public List<string> FileProblems { get; } = [];

    /// <summary>Why the migration cannot start, if it cannot.</summary>
    public string? Problem { get; set; }

    public int NotesToMigrate => Notebooks.Sum(n => n.NoteCount);

    public IEnumerable<ExcludedNote> Excluded => Notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Excluded);

    /// <summary>The plan in words, for confirmation before the migration starts.</summary>
    public string Describe(bool exportBackup)
    {
        var sb = new StringBuilder();
        var inspection = Inspection;
        sb.AppendLine("MIGRATE ALL NOTES INTO NEW ONENOTE NOTEBOOKS");
        sb.AppendLine();
        sb.AppendLine($"Source:          {inspection.SourcePath}");
        sb.AppendLine($"                 {inspection.Files.Count} ENEX file(s), {Format.Count(inspection.Notes.Count)} note(s), " +
                      $"{Format.Count(inspection.Attachments.Count)} attachment(s)");
        sb.AppendLine($"Notes to migrate: {Format.Count(NotesToMigrate)}" +
                      (Excluded.Any() ? $" ({Excluded.Count()} excluded, see below)" : ""));
        sb.AppendLine($"New notebooks:   {Notebooks.Count}, created in {Path.Combine(Root, "Notebooks")} (local, not synced by OneDrive)");
        sb.AppendLine($".onepkg backup:  {(exportBackup ? "yes, once per new notebook after all notes are migrated" : "no")}");
        sb.AppendLine();
        sb.AppendLine("Only NEW notebooks are created. Existing notebooks and pages are not changed. Your .enex files are only read.");
        sb.AppendLine("Each note becomes a new page and is verified. You can stop after the current note at any time.");
        sb.AppendLine();
        sb.AppendLine("FOLDER -> NOTEBOOK, ENEX FILE -> SECTION");
        foreach (var notebook in Notebooks)
        {
            var folder = notebook.RelativeFolder.Length == 0 ? "(export folder)" : notebook.RelativeFolder;
            sb.AppendLine();
            sb.AppendLine($"{folder}  ->  notebook \"{notebook.Name}\" ({notebook.NoteCount} note(s))");
            if (notebook.RenameReason is not null)
            {
                sb.AppendLine($"    RENAMED: {notebook.RenameReason}, so a new notebook \"{notebook.Name}\" is created instead.");
            }
            foreach (var section in notebook.Sections)
            {
                var excluded = section.Excluded.Count > 0 ? $", {section.Excluded.Count} excluded" : "";
                sb.AppendLine($"    {Path.GetFileName(section.File.RelativePath)}  ->  section \"{section.Name}\" ({section.Notes.Count} note(s){excluded})");
            }
        }
        if (Excluded.Any())
        {
            sb.AppendLine();
            sb.AppendLine($"EXCLUDED: {Excluded.Count()} NOTE(S) WILL NOT BE MIGRATED");
            foreach (var excluded in Excluded)
            {
                sb.AppendLine($"  - {BulkMigrator.Describe(excluded.Note)}: {excluded.Reason}");
            }
        }
        if (FileProblems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"PROBLEMS IN THE ENEX FILES ({FileProblems.Count}): content of these files may be missing from the export");
            foreach (var problem in FileProblems)
            {
                sb.AppendLine("  - " + problem);
            }
        }
        return sb.ToString();
    }
}

public enum BulkNoteStatus
{
    /// <summary>Migrated; text, title and every attachment verified.</summary>
    Verified,

    /// <summary>Migrated, but something differs, is missing, or the source note was damaged.</summary>
    Review,

    Failed,

    /// <summary>Left out before migration because it could not be read reliably.</summary>
    Excluded,

    /// <summary>Not attempted because the run was stopped.</summary>
    NotAttempted,
}

public sealed class BulkNoteResult
{
    public required NoteSummary Note { get; init; }
    public required PlannedNotebook Notebook { get; init; }
    public required PlannedSection Section { get; init; }
    public BulkNoteStatus Status { get; set; }
    public LocalMigrationResult? Migration { get; set; }

    /// <summary>Why the note was excluded, failed or not attempted, or what needs review.</summary>
    public string? Details { get; set; }

    public bool Migrated => Migration is { Error: null, PageCreated: true };
    public bool TextVerified => Migrated && Migration!.Text?.Identical == true;
    public bool TextChanged => Migrated && Migration!.Text?.Identical != true;
    public bool TitleDiffers => Migrated && !Migration!.TitleMatches;

    /// <summary>Attachments in the export, including those in a damaged note that could not be read.</summary>
    public int AttachmentsTotal => Note.AttachmentCount + Note.UnreadAttachments;

    public int AttachmentsTransferred => Migration?.Attachments.Count(a => a.Ok) ?? 0;
}

public sealed class BulkNotebookResult
{
    public required PlannedNotebook Plan { get; init; }
    public string? NotebookId { get; set; }

    /// <summary>Why the notebook could not be created, if it could not.</summary>
    public string? Error { get; set; }

    public NotebookExport? Export { get; set; }

    /// <summary>Why the optional export was not attempted, if it was requested but skipped.</summary>
    public string? ExportSkipped { get; set; }
}

/// <summary>Progress of a bulk migration: notes completed so far and what is being done now.</summary>
public sealed record BulkProgress(int Completed, int Total, int Migrated, int Review, int Failed, string Notebook, string? Section, string? Note, string? Step);

public sealed class BulkResult
{
    public required BulkPlan Plan { get; init; }
    public required string RunFolder { get; init; }
    public required DateTime Started { get; init; }
    public DateTime Finished { get; set; }
    public bool ExportRequested { get; init; }
    public List<BulkNotebookResult> Notebooks { get; } = [];

    /// <summary>Every discovered note, in plan order: migrated, failed, excluded or not attempted.</summary>
    public List<BulkNoteResult> Notes { get; } = [];

    /// <summary>True if you stopped the run and something was left undone.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Why the run stopped by itself, if it did.</summary>
    public string? StoppedReason { get; set; }

    /// <summary>Why the report files could not be written, if they could not.</summary>
    public string? ReportProblem { get; set; }

    public string JournalPath => Path.Combine(RunFolder, "journal.jsonl");
    public string ReportPath => Path.Combine(RunFolder, "migration-report.md");
    public string CsvPath => Path.Combine(RunFolder, "migration-notes.csv");

    public int Count(BulkNoteStatus status) => Notes.Count(n => n.Status == status);
    public int AttachmentsDiscovered => Notes.Sum(n => n.AttachmentsTotal);
    public int AttachmentsTransferred => Notes.Sum(n => n.AttachmentsTransferred);
    public int AttachmentsMissing => AttachmentsDiscovered - AttachmentsTransferred;

    /// <summary>Complete only if every discovered note was migrated and fully verified and nothing was skipped.</summary>
    public bool Complete => !Cancelled && StoppedReason is null && Plan.FileProblems.Count == 0 && Notes.Count > 0 &&
                            Notes.All(n => n.Status == BulkNoteStatus.Verified) && AttachmentsMissing == 0;

    public string Headline => Complete
        ? $"RESULT: COMPLETE - all {Format.Count(Notes.Count)} notes were migrated and verified (text, titles and all {Format.Count(AttachmentsDiscovered)} attachments)."
        : $"RESULT: INCOMPLETE - {Format.Count(Count(BulkNoteStatus.Verified))} of {Format.Count(Notes.Count)} notes were migrated and fully verified.";

    /// <summary>Everything that keeps the result from being complete.</summary>
    public List<string> Shortfalls()
    {
        var list = new List<string>();
        if (StoppedReason is not null)
        {
            list.Add("The run stopped: " + StoppedReason);
        }
        if (Cancelled)
        {
            list.Add("You stopped the run after the current note.");
        }
        void Add(int count, string text)
        {
            if (count > 0)
            {
                list.Add($"{Format.Count(count)} {text}");
            }
        }
        Add(Count(BulkNoteStatus.Failed), "note(s) failed.");
        Add(Notes.Count(n => n.TextChanged), "migrated note(s) have missing or changed text.");
        Add(Notes.Count(n => n.TitleDiffers), "migrated note(s) have a different title.");
        Add(AttachmentsMissing, "attachment(s) are missing in OneNote.");
        Add(Count(BulkNoteStatus.Excluded), "note(s) were excluded before migration because they could not be read reliably.");
        Add(Count(BulkNoteStatus.NotAttempted), "note(s) were not attempted because the run stopped.");
        Add(Notes.Count(n => n.Migrated && n.Note.Problem is not null), "migrated note(s) were damaged in the export; their text was recovered - compare them with Evernote.");
        Add(Notes.Count(n => n.Migrated && n.Note.EncryptedBlocks > 0), "migrated note(s) contain encrypted text that could not be migrated (a marker shows where).");
        Add(Plan.FileProblems.Count, "problem(s) in the ENEX files: content may be missing from the export (see below).");
        return list;
    }

    /// <summary>The full report (plain text with Markdown headings).</summary>
    public string ReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EverNote2OneNote - migration report");
        sb.AppendLine();
        sb.AppendLine(Headline);
        foreach (var shortfall in Shortfalls())
        {
            sb.AppendLine("  - " + shortfall);
        }
        var exported = Notebooks.Count(n => n.Export?.Path is not null);
        var exportFailed = Notebooks.Count(n => n.Export is { Path: null });
        if (ExportRequested && exportFailed + Notebooks.Count(n => n.ExportSkipped is not null) > 0)
        {
            sb.AppendLine($"  (Optional .onepkg backup: {exported} of {Notebooks.Count(n => n.NotebookId is not null)} notebook(s) exported. " +
                          "This does not affect the migrated pages.)");
        }
        sb.AppendLine();
        sb.AppendLine($"Source folder:   {Plan.Inspection.SourcePath}");
        sb.AppendLine($"Started:         {Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Finished:        {Finished.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} " +
                      $"({(Finished - Started).TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture)} minutes)");
        sb.AppendLine($"Notebooks in:    {Path.Combine(Plan.Root, "Notebooks")}");
        sb.AppendLine($"This report:     {RunFolder}");
        sb.AppendLine("                 migration-report.md, migration-notes.csv (every note), journal.jsonl (progress log)");

        sb.AppendLine();
        sb.AppendLine("## Totals");
        void Line(string label, long value) => sb.AppendLine($"{label,-48} {Format.Count(value),8}");
        Line("Notes discovered", Notes.Count);
        Line("Notes migrated (new page written)", Notes.Count(n => n.Migrated));
        Line("Notes with verified text", Notes.Count(n => n.TextVerified));
        Line("Notes with missing or changed text", Notes.Count(n => n.TextChanged));
        Line("Notes with a different title", Notes.Count(n => n.TitleDiffers));
        Line("Notes fully verified (text, title, attachments)", Count(BulkNoteStatus.Verified));
        Line("Notes migrated that need review", Count(BulkNoteStatus.Review));
        Line("Notes failed", Count(BulkNoteStatus.Failed));
        Line("Notes excluded before migration", Count(BulkNoteStatus.Excluded));
        Line("Notes not attempted (run stopped)", Count(BulkNoteStatus.NotAttempted));
        Line("Attachments discovered", AttachmentsDiscovered);
        Line("Attachments transferred and found on the page", AttachmentsTransferred);
        Line("Attachments missing", AttachmentsMissing);
        Line("New notebooks created", Notebooks.Count(n => n.NotebookId is not null));
        if (ExportRequested)
        {
            Line("Notebook backups (.onepkg) exported", exported);
        }

        if (Plan.FileProblems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Problems in the ENEX files ({Plan.FileProblems.Count})");
            sb.AppendLine("Found during inspection. Content of these files may be missing from the export itself.");
            foreach (var problem in Plan.FileProblems)
            {
                sb.AppendLine("- " + problem);
            }
        }

        var failed = Notes.Where(n => n.Status == BulkNoteStatus.Failed).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Failed notes ({failed.Count})");
            foreach (var note in failed)
            {
                sb.AppendLine($"- {BulkMigrator.Describe(note.Note)} -> {note.Notebook.Name} > {note.Section.Name}: {note.Details}");
                foreach (var notice in note.Migration?.Notices.Where(n => n.Contains("page", StringComparison.OrdinalIgnoreCase) && n.Contains("not retried", StringComparison.Ordinal)) ?? [])
                {
                    sb.AppendLine("    " + notice);
                }
            }
        }

        var review = Notes.Where(n => n.Status == BulkNoteStatus.Review).ToList();
        if (review.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Migrated notes that need review ({review.Count})");
            foreach (var note in review)
            {
                sb.AppendLine($"- {BulkMigrator.Describe(note.Note)} -> {note.Notebook.Name} > {note.Section.Name}: {note.Details}");
            }
        }

        var excluded = Notes.Where(n => n.Status == BulkNoteStatus.Excluded).ToList();
        if (excluded.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Notes excluded before migration ({excluded.Count})");
            foreach (var note in excluded)
            {
                sb.AppendLine($"- {BulkMigrator.Describe(note.Note)}: {note.Details}");
            }
        }

        var notAttempted = Notes.Where(n => n.Status == BulkNoteStatus.NotAttempted).ToList();
        if (notAttempted.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Notes not attempted ({notAttempted.Count})");
            sb.AppendLine("Each is listed in migration-notes.csv with the status \"Not attempted\".");
            foreach (var file in notAttempted.GroupBy(n => n.Section))
            {
                sb.AppendLine($"- {file.Key.File.RelativePath}: {file.Count()} note(s) -> {file.First().Notebook.Name} > {file.Key.Name}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"## Notebooks ({Plan.Notebooks.Count})");
        foreach (var notebook in Notebooks)
        {
            var notes = Notes.Where(n => n.Notebook == notebook.Plan).ToList();
            var folder = notebook.Plan.RelativeFolder.Length == 0 ? "(export folder)" : notebook.Plan.RelativeFolder;
            sb.AppendLine();
            sb.AppendLine($"### {notebook.Plan.Name}");
            sb.AppendLine($"From folder: {folder}");
            if (notebook.Plan.RenameReason is not null)
            {
                sb.AppendLine($"Renamed: {notebook.Plan.RenameReason} (wanted name: \"{notebook.Plan.MappedName}\").");
            }
            sb.AppendLine(notebook.Error is not null
                ? $"NOT CREATED: {notebook.Error}"
                : notebook.NotebookId is null ? "Not created (the run stopped before this notebook)." : $"Location: {notebook.Plan.Location}");
            sb.AppendLine($"Notes: {notes.Count} discovered, {notes.Count(n => n.Migrated)} migrated, {notes.Count(n => n.Status == BulkNoteStatus.Verified)} fully verified");
            foreach (var section in notebook.Plan.Sections)
            {
                var sectionNotes = notes.Where(n => n.Section == section).ToList();
                sb.AppendLine($"  - Section \"{section.Name}\" <- {section.File.RelativePath}: " +
                              $"{sectionNotes.Count(n => n.Migrated)} of {sectionNotes.Count} note(s) migrated");
            }
            if (ExportRequested && notebook.NotebookId is not null)
            {
                sb.AppendLine(notebook.Export?.Path is { } path ? $"Backup: {path}"
                    : notebook.Export?.Problem is { } problem ? $"Backup failed (the migrated pages are not affected): {problem}"
                    : $"Backup not made: {notebook.ExportSkipped}");
                foreach (var notice in notebook.Export?.Notices ?? [])
                {
                    sb.AppendLine("  " + notice);
                }
            }
        }
        if (ReportProblem is not null)
        {
            sb.AppendLine();
            sb.AppendLine("The report files could not be saved: " + ReportProblem);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Migrates every inspected note into new OneNote notebooks: each folder of ENEX files becomes a new notebook
/// ("EverNote", "EverNote - Folder", "EverNote - Folder - Subfolder"), each ENEX file a section, each note a new page.
/// Existing notebooks are never used: a name that is taken gets a clearly marked unique name. Notes are migrated one at a
/// time with the single-note engine; each is verified and recorded in a journal. Nothing is retried automatically.
/// </summary>
public static class BulkMigrator
{
    public const string RootNotebookName = "EverNote";
    private const int MaxMappedNameLength = 80;
    private const int MaxConsecutiveFailures = 5;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Plans the migration (read-only): maps folders to new notebook names, files to sections, and lists the notes that
    /// must be excluded and the file-level problems found during inspection.
    /// </summary>
    public static BulkPlan Plan(InspectionResult inspection, IOneNoteDesktop oneNote, string root, IReadOnlyList<string> oneDriveFolders, DateTime now)
    {
        var plan = new BulkPlan { Inspection = inspection, Root = root };
        plan.Problem = LocalMigrator.OneDriveProblem(root, oneDriveFolders);
        if (plan.Problem is not null)
        {
            return plan;
        }
        if (inspection.Notes.Count == 0)
        {
            plan.Problem = "The inspection found no notes to migrate.";
            return plan;
        }
        foreach (var file in inspection.Files)
        {
            var info = new FileInfo(file.FullPath);
            if (!info.Exists || info.Length != file.Bytes)
            {
                plan.Problem = $"{file.RelativePath} has changed or was removed since the inspection. Run Inspect Archive again, then migrate.";
                return plan;
            }
        }
        plan.FileProblems.AddRange(inspection.Issues
            .Where(i => i.NoteIndex is null && i.Severity == Severity.Error)
            .Select(i => $"{i.File}: {i.Message}"));

        // Names that are taken: notebooks open in OneNote, and folders that already exist in this app's notebook folder.
        var open = OneNoteHierarchy.Children(oneNote.GetHierarchyXml("", HierarchyScope.Notebooks), "Notebook");
        var openNames = new HashSet<string>(open.SelectMany(n => new[] { n.Name, n.Nickname }).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        var openPaths = new HashSet<string>(open.Select(n => n.Path).OfType<string>().Select(p => p.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
        string Location(string name) => Path.Combine(root, "Notebooks", name);
        bool Taken(string name) => openNames.Contains(name) || openPaths.Contains(Location(name)) ||
                                   Directory.Exists(Location(name)) || File.Exists(Location(name));
        var stamp = now.ToString("yyyy-MM-dd HHmm", Inv);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var folders = inspection.Files
            .GroupBy(f => Path.GetDirectoryName(f.RelativePath) ?? "", StringComparer.Ordinal)
            .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var mapped = NotebookName(folder.Key);
            var name = mapped;
            for (int n = 2; used.Contains(name); n++)
            {
                name = $"{mapped} ({n})";
            }
            string? reason = name == mapped ? null : $"another folder in this export also maps to \"{mapped}\"";
            if (Taken(name))
            {
                reason = openNames.Contains(name) || openPaths.Contains(Location(name))
                    ? $"a notebook named \"{name}\" is already open in OneNote"
                    : $"a notebook folder \"{name}\" already exists in {Path.Combine(root, "Notebooks")} (from an earlier import)";
                var marked = $"{name} (Import {stamp})";
                name = marked;
                for (int n = 2; Taken(name) || used.Contains(name); n++)
                {
                    name = $"{marked} ({n})";
                }
            }
            used.Add(name);

            var notebook = new PlannedNotebook
            {
                Name = name, MappedName = mapped, RelativeFolder = folder.Key, Location = Location(name), RenameReason = reason,
            };
            var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in folder.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var baseName = LocalMigrator.SectionName(file.Notebook);
                var sectionName = baseName;
                for (int n = 2; sections.Contains(sectionName); n++)
                {
                    var suffix = $" ({n})";
                    sectionName = baseName[..Math.Min(baseName.Length, 50 - suffix.Length)].TrimEnd() + suffix;
                }
                sections.Add(sectionName);

                var section = new PlannedSection { Name = sectionName, File = file };
                foreach (var note in inspection.Notes.Where(n => n.File == file).OrderBy(n => n.Index))
                {
                    if (note.TextStatus == TextStatus.NotRecovered)
                    {
                        section.Excluded.Add(new ExcludedNote(note,
                            "the note is damaged in the export and its text could not be read (see the inspection report). " +
                            "It was not migrated, so that nothing is migrated incompletely; check it in Evernote."));
                    }
                    else
                    {
                        section.Notes.Add(note);
                    }
                }
                notebook.Sections.Add(section);
            }
            plan.Notebooks.Add(notebook);
        }
        if (plan.NotesToMigrate == 0)
        {
            plan.Problem = "None of the notes can be migrated reliably; see the excluded notes.";
        }
        return plan;
    }

    /// <summary>"EverNote" for the export folder itself, "EverNote - Folder - Subfolder" for subfolders.</summary>
    public static string NotebookName(string relativeFolder)
    {
        var segments = relativeFolder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(CleanSegment);
        var name = string.Join(" - ", new[] { RootNotebookName }.Concat(segments));
        if (name.Length > MaxMappedNameLength)
        {
            name = name[..MaxMappedNameLength].TrimEnd(' ', '-', '.');
        }
        return name;
    }

    private static string CleanSegment(string segment)
    {
        var name = LocalMigrator.CleanName(segment).Trim().TrimEnd('.').Trim();
        return name.Length == 0 ? "_" : name;
    }

    /// <summary>
    /// Runs the planned migration: creates each new notebook and its sections, then migrates the notes one at a time
    /// (stopping safely between notes when cancelled), and optionally exports each new notebook once at the end.
    /// Writes a journal while running and the report files at the end.
    /// </summary>
    public static BulkResult Run(BulkPlan plan, IOneNoteDesktop oneNote, bool exportBackup, IProgress<BulkProgress>? progress = null,
        CancellationToken cancel = default, TimeSpan? exportTimeout = null)
    {
        if (plan.Problem is not null)
        {
            throw new InvalidOperationException(plan.Problem);
        }
        var started = DateTime.Now;
        var runFolder = LocalMigrator.UniquePath(Path.Combine(plan.Root, "Runs", "Migration " + started.ToString("yyyy-MM-dd HHmmss", Inv)), "");
        Directory.CreateDirectory(runFolder);
        var result = new BulkResult { Plan = plan, RunFolder = runFolder, Started = started, ExportRequested = exportBackup };
        var journal = new Journal(result.JournalPath);
        int total = plan.NotesToMigrate, done = 0, migrated = 0, review = 0, failed = 0, consecutiveFailures = 0;
        var current = new BulkProgress(0, total, 0, 0, 0, "", null, null, "Starting...");
        var step = new Relay(s => progress?.Report(current with { Step = s }));

        void Log(string kind, params (string Name, object? Value)[] fields)
        {
            journal.Write(kind, fields);
            if (journal.Problem is not null && result.StoppedReason is null)
            {
                result.StoppedReason = $"the progress journal could not be written ({journal.Problem}), so the run was stopped to keep it traceable.";
            }
        }
        bool Halted() => result.StoppedReason is not null || cancel.IsCancellationRequested;
        string HaltReason() => result.StoppedReason is { } reason ? "Not attempted: " + reason : "Not attempted: you stopped the migration.";
        void Failure()
        {
            if (++consecutiveFailures >= MaxConsecutiveFailures && result.StoppedReason is null)
            {
                result.StoppedReason = $"{MaxConsecutiveFailures} notes in a row failed, so the run was stopped to avoid further failures. Check the errors below.";
            }
        }
        void LostConnection(int errorCode)
        {
            if (OneNoteDesktopException.IsConnectionLost(errorCode) && result.StoppedReason is null)
            {
                result.StoppedReason = "the connection to OneNote was lost (was OneNote closed?). The remaining notes were not attempted.";
            }
        }

        Log("run-start", ("source", plan.Inspection.SourcePath), ("notesDiscovered", plan.Inspection.Notes.Count), ("notesToMigrate", total),
            ("notebooks", plan.Notebooks.Count), ("exportBackup", exportBackup), ("root", plan.Root));

        foreach (var notebook in plan.Notebooks)
        {
            var notebookResult = new BulkNotebookResult { Plan = notebook };
            result.Notebooks.Add(notebookResult);

            // 1. The new notebook (never an existing one).
            if (!Halted())
            {
                current = current with { Notebook = notebook.Name, Section = null, Note = null, Step = "Creating the notebook..." };
                progress?.Report(current);
                if (Directory.Exists(notebook.Location) || File.Exists(notebook.Location))
                {
                    notebookResult.Error = $"{notebook.Location} appeared after the migration was planned; nothing was written into it.";
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(notebook.Location)!);
                        notebookResult.NotebookId = oneNote.CreateNotebook(notebook.Location);
                    }
                    catch (OneNoteDesktopException ex)
                    {
                        notebookResult.Error = ex.Message;
                        LostConnection(ex.ErrorCode);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        notebookResult.Error = ex.Message;
                    }
                }
                if (notebookResult.Error is not null)
                {
                    Failure();
                }
                Log("notebook", ("name", notebook.Name), ("mappedName", notebook.MappedName), ("folder", notebook.RelativeFolder),
                    ("location", notebook.Location), ("notebookId", notebookResult.NotebookId), ("error", notebookResult.Error));
            }

            foreach (var section in notebook.Sections)
            {
                foreach (var excluded in section.Excluded)
                {
                    result.Notes.Add(new BulkNoteResult
                    {
                        Note = excluded.Note, Notebook = notebook, Section = section, Status = BulkNoteStatus.Excluded, Details = excluded.Reason,
                    });
                    Log("note-excluded", NoteFields(excluded.Note, notebook, section).Append(("reason", excluded.Reason)).ToArray());
                }

                // 2. The section for the ENEX file.
                string? sectionId = null;
                string? sectionError = notebookResult.Error is null ? null : "The notebook could not be created: " + notebookResult.Error;
                if (notebookResult.NotebookId is not null && !Halted())
                {
                    try
                    {
                        sectionId = oneNote.CreateSection(notebookResult.NotebookId, section.Name);
                    }
                    catch (OneNoteDesktopException ex)
                    {
                        sectionError = "The section could not be created: " + ex.Message;
                        LostConnection(ex.ErrorCode);
                        Failure();
                    }
                    Log("section", ("notebook", notebook.Name), ("section", section.Name), ("file", section.File.RelativePath),
                        ("sourcePath", section.File.FullPath), ("sectionId", sectionId), ("error", sectionError));
                }

                // 3. The notes, one at a time.
                foreach (var note in section.Notes)
                {
                    var entry = new BulkNoteResult { Note = note, Notebook = notebook, Section = section };
                    result.Notes.Add(entry);
                    if (Halted() || (sectionId is null && sectionError is null))
                    {
                        entry.Status = BulkNoteStatus.NotAttempted;
                        entry.Details = HaltReason();
                        result.Cancelled |= result.StoppedReason is null && cancel.IsCancellationRequested;
                        Log("note-not-attempted", NoteFields(note, notebook, section).Append(("reason", entry.Details)).ToArray());
                        continue;
                    }
                    if (sectionId is null)
                    {
                        entry.Status = BulkNoteStatus.Failed;
                        entry.Details = sectionError;
                        done++;
                        failed++;
                        Log("note", NoteFields(note, notebook, section).Concat([("status", "Failed"), ("error", sectionError)]).ToArray());
                        continue;
                    }

                    current = new BulkProgress(done, total, migrated, review, failed, notebook.Name, section.Name, Title(note), null);
                    progress?.Report(current);
                    Log("note-start", NoteFields(note, notebook, section).ToArray());
                    LocalMigrationResult migration;
                    try
                    {
                        migration = LocalMigrator.MigrateNote(note, oneNote, notebook.Name, section.Name, sectionId, plan.Root, step);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        migration = new LocalMigrationResult
                        {
                            NoteTitle = note.Title ?? "", NotebookName = notebook.Name, Section = section.Name, Error = "Unexpected error: " + ex.Message,
                        };
                    }
                    entry.Migration = migration;
                    (entry.Status, entry.Details) = Classify(note, migration);
                    done++;
                    switch (entry.Status)
                    {
                        case BulkNoteStatus.Failed:
                            failed++;
                            Failure();
                            break;
                        case BulkNoteStatus.Review:
                            migrated++;
                            review++;
                            consecutiveFailures = 0;
                            break;
                        default:
                            migrated++;
                            consecutiveFailures = 0;
                            break;
                    }
                    LostConnection(migration.ErrorCode);
                    Log("note", NoteFields(note, notebook, section).Concat(ResultFields(entry)).ToArray());
                    current = new BulkProgress(done, total, migrated, review, failed, notebook.Name, section.Name, Title(note), null);
                    progress?.Report(current);
                }
            }
        }

        // 4. Optional backup: each new notebook once, after all notes (never repeated per note).
        if (exportBackup)
        {
            foreach (var notebook in result.Notebooks.Where(n => n.NotebookId is not null))
            {
                if (Halted())
                {
                    // Backups are optional: skipping them does not make the migration itself incomplete.
                    notebook.ExportSkipped = result.StoppedReason is not null ? "the run stopped: " + result.StoppedReason : "you stopped the migration.";
                    Log("export-skipped", ("notebook", notebook.Plan.Name), ("reason", notebook.ExportSkipped));
                    continue;
                }
                current = current with { Notebook = notebook.Plan.Name, Section = null, Note = null, Step = "Exporting a .onepkg backup of the notebook..." };
                progress?.Report(current);
                var megabytes = notebook.Plan.Sections.Sum(s => s.Notes.Sum(n => n.AttachmentBytes)) / (1024.0 * 1024);
                var timeout = exportTimeout ?? LocalMigrator.DefaultExportTimeout + TimeSpan.FromMinutes(megabytes / 100);
                notebook.Export = LocalMigrator.ExportNotebook(oneNote, notebook.NotebookId!, notebook.Plan.Name, plan.Root, timeout, step);
                Log("export", ("notebook", notebook.Plan.Name), ("path", notebook.Export.Path), ("problem", notebook.Export.Problem));
            }
        }

        result.Finished = DateTime.Now;
        Log("run-end", ("complete", result.Complete), ("verified", result.Count(BulkNoteStatus.Verified)), ("review", result.Count(BulkNoteStatus.Review)),
            ("failed", result.Count(BulkNoteStatus.Failed)), ("excluded", result.Count(BulkNoteStatus.Excluded)),
            ("notAttempted", result.Count(BulkNoteStatus.NotAttempted)), ("attachmentsTransferred", result.AttachmentsTransferred),
            ("attachmentsMissing", result.AttachmentsMissing), ("cancelled", result.Cancelled), ("stoppedReason", result.StoppedReason));
        try
        {
            WriteCsv(result);
            File.WriteAllText(result.ReportPath, result.ReportText(), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.ReportProblem = ex.Message;
        }
        progress?.Report(current with { Section = null, Note = null, Step = "Finished." });
        return result;
    }

    /// <summary>Verified only if the text, the title and every attachment are confirmed and the source note was intact.</summary>
    private static (BulkNoteStatus Status, string? Details) Classify(NoteSummary note, LocalMigrationResult migration)
    {
        if (migration.Error is not null)
        {
            return (BulkNoteStatus.Failed, migration.Error);
        }
        var issues = new List<string>();
        if (migration.Text is not { } text)
        {
            issues.Add("the text was not verified");
        }
        else if (!text.Identical)
        {
            var kinds = text.Differences.GroupBy(d => d.Kind).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}");
            issues.Add(text.TooManyDifferences ? "the text differs too much to list the differences" : "text differs: " + string.Join(", ", kinds) + " passage(s)");
        }
        if (!migration.TitleMatches)
        {
            issues.Add($"the page title is \"{migration.PageTitle}\"");
        }
        int missing = migration.Attachments.Count(a => !a.Ok) + note.UnreadAttachments;
        if (missing > 0)
        {
            issues.Add($"{missing} of {note.AttachmentCount + note.UnreadAttachments} attachment(s) missing");
        }
        if (note.Problem is not null)
        {
            issues.Add("the note was damaged in the export and its text was recovered; compare it with Evernote");
        }
        if (note.EncryptedBlocks > 0)
        {
            issues.Add($"{note.EncryptedBlocks} encrypted block(s) could not be migrated (a marker shows where)");
        }
        return issues.Count == 0 ? (BulkNoteStatus.Verified, null) : (BulkNoteStatus.Review, string.Join("; ", issues));
    }

    /// <summary>"Folder\File.enex, note 3 "Title"" - identifies a note in the source.</summary>
    public static string Describe(NoteSummary note) => $"{note.File.RelativePath}, note {note.Index} \"{Title(note)}\"";

    private static string Title(NoteSummary note) => string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : note.Title.Trim();

    private static IEnumerable<(string, object?)> NoteFields(NoteSummary note, PlannedNotebook notebook, PlannedSection section) =>
    [
        ("file", note.File.RelativePath), ("sourcePath", note.File.FullPath), ("note", note.Index), ("byteOffset", note.ByteOffset),
        ("title", note.Title), ("created", note.Created), ("notebook", notebook.Name), ("section", section.Name),
    ];

    private static IEnumerable<(string, object?)> ResultFields(BulkNoteResult entry)
    {
        var m = entry.Migration!;
        return
        [
            ("status", entry.Status.ToString()), ("pageId", m.PageId), ("method", m.Method), ("textVerified", entry.TextVerified),
            ("titleMatches", m.TitleMatches), ("differences", m.Text?.Differences.Count), ("attachmentsTransferred", entry.AttachmentsTransferred),
            ("attachments", entry.AttachmentsTotal), ("error", m.Error), ("details", entry.Details), ("notices", m.Notices),
        ];
    }

    private static void WriteCsv(BulkResult result)
    {
        string[] header =
        [
            "Status", "ENEX file", "Note #", "Title", "Created", "Notebook", "Section", "Text verified", "Title matches",
            "Attachments transferred", "Attachments", "Method", "Details", "Notices", "Page ID",
        ];
        var rows = result.Notes.Select(n => new[]
        {
            n.Status == BulkNoteStatus.NotAttempted ? "Not attempted" : n.Status.ToString(),
            n.Note.File.RelativePath,
            n.Note.Index.ToString(Inv),
            n.Note.Title ?? "",
            Format.Date(n.Note.Created),
            n.Notebook.Name,
            n.Section.Name,
            n.Migrated ? (n.TextVerified ? "Yes" : "NO") : "-",
            n.Migrated ? (n.TitleDiffers ? "NO" : "Yes") : "-",
            n.AttachmentsTransferred.ToString(Inv),
            n.AttachmentsTotal.ToString(Inv),
            n.Migration?.Method ?? "",
            n.Details ?? "",
            string.Join(" | ", n.Migration?.Notices ?? []),
            n.Migration?.PageId ?? "",
        });
        Csv.Write(result.CsvPath, new UTF8Encoding(true), header, rows);
    }

    /// <summary>Reports synchronously on the calling thread (unlike <see cref="Progress{T}"/>), so steps arrive in order.</summary>
    private sealed class Relay(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>
    /// The progress journal: one JSON object per line, appended and flushed to disk immediately, so it shows what was
    /// done (and which note was being migrated) even if the app or PC stops unexpectedly.
    /// </summary>
    private sealed class Journal(string path)
    {
        private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public string? Problem { get; private set; }

        public void Write(string kind, IEnumerable<(string Name, object? Value)> fields)
        {
            try
            {
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer, Options))
                {
                    json.WriteStartObject();
                    json.WriteString("time", DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", Inv));
                    json.WriteString("event", kind);
                    foreach (var (name, value) in fields)
                    {
                        switch (value)
                        {
                            case null:
                                json.WriteNull(name);
                                break;
                            case string s:
                                json.WriteString(name, s);
                                break;
                            case bool b:
                                json.WriteBoolean(name, b);
                                break;
                            case int i:
                                json.WriteNumber(name, i);
                                break;
                            case long l:
                                json.WriteNumber(name, l);
                                break;
                            case IEnumerable<string> list:
                                json.WriteStartArray(name);
                                foreach (var item in list)
                                {
                                    json.WriteStringValue(item);
                                }
                                json.WriteEndArray();
                                break;
                            default:
                                json.WriteString(name, Convert.ToString(value, Inv));
                                break;
                        }
                    }
                    json.WriteEndObject();
                }
                buffer.WriteByte((byte)'\n');
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                file.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Problem ??= ex.Message;
            }
        }
    }
}
