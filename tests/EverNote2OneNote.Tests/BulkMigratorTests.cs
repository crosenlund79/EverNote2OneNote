using System.Text.Json;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.Tests;

public class BulkMigratorTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 20, 15, 0);

    private static BulkPlan PlanBasic(FakeOneNote oneNote, string root) =>
        BulkMigrator.Plan(Inspector.Run(TestFiles.Fixture("Basic")), oneNote, root, [], Now);

    [Theory]
    [InlineData("", "EverNote")]
    [InlineData("Gaming", "EverNote - Gaming")]
    [InlineData("Gaming/Campaigns", "EverNote - Gaming - Campaigns")]
    [InlineData("Gaming\\Campaigns\\2024", "EverNote - Gaming - Campaigns - 2024")]
    [InlineData("Work & Life", "EverNote - Work _ Life")]
    public void Folders_map_to_notebook_names(string folder, string expected) =>
        Assert.Equal(expected, BulkMigrator.NotebookName(folder));

    [Fact]
    public void Plan_maps_folders_to_new_notebooks_and_files_to_sections_without_writing_anything()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();

        var plan = PlanBasic(oneNote, root.Path);

        Assert.Null(plan.Problem);
        Assert.Equal(["EverNote", "EverNote - Travel"], plan.Notebooks.Select(n => n.Name));
        Assert.Equal(["Empty", "Recipes"], plan.Notebooks[0].Sections.Select(s => s.Name));
        Assert.Equal(["Trips"], plan.Notebooks[1].Sections.Select(s => s.Name));
        Assert.Equal(4, plan.NotesToMigrate);
        Assert.All(plan.Notebooks, n => Assert.Null(n.RenameReason));
        Assert.Equal(Path.Combine(root.Path, "Notebooks", "EverNote - Travel"), plan.Notebooks[1].Location);
        Assert.Empty(oneNote.Calls);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "Notebooks")));
        Assert.Contains("Travel  ->  notebook \"EverNote - Travel\"", plan.Describe(exportBackup: true));
    }

    [Fact]
    public void Existing_notebooks_are_never_reused_but_get_a_marked_unique_name()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();
        oneNote.AddNotebook("open", "EverNote", "Old section", "Old page");
        Directory.CreateDirectory(Path.Combine(root.Path, "Notebooks", "EverNote - Travel")); // from an earlier import
        Directory.CreateDirectory(Path.Combine(root.Path, "Notebooks", "EverNote - Travel (Import 2026-11-17 2015)"));

        var plan = PlanBasic(oneNote, root.Path);

        Assert.Equal(["EverNote (Import 2026-11-17 2015)", "EverNote - Travel (Import 2026-11-17 2015) (2)"], plan.Notebooks.Select(n => n.Name));
        Assert.Equal(["EverNote", "EverNote - Travel"], plan.Notebooks.Select(n => n.MappedName));
        Assert.Contains("already open in OneNote", plan.Notebooks[0].RenameReason);
        Assert.Contains("already exists", plan.Notebooks[1].RenameReason);
        Assert.Contains("RENAMED", plan.Describe(exportBackup: false));
    }

    [Fact]
    public void An_open_notebook_in_the_planned_folder_counts_as_existing()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();
        oneNote.AddNotebookAt("open", "My renamed notebook", Path.Combine(root.Path, "Notebooks", "EverNote"));

        Assert.Equal("EverNote (Import 2026-11-17 2015)", PlanBasic(oneNote, root.Path).Notebooks[0].Name);
    }

    [Fact]
    public void Colliding_folder_and_file_names_within_the_export_are_made_unique()
    {
        using var source = new TempDirectory();
        var file = Enex.File(Enex.Note("N", "<en-note><div>x</div></en-note>"));
        source.Write("A - B/One.enex", file);
        source.Write(Path.Combine("A", "B", "Two.enex"), file);
        source.Write("C/Tom & Jerry.enex", file);
        source.Write("C/Tom _ Jerry.enex", file);
        using var root = new TempDirectory();

        var plan = BulkMigrator.Plan(Inspector.Run(source.Path), new FakeOneNote(), root.Path, [], Now);

        Assert.Equal(["EverNote - A - B", "EverNote - A - B (2)", "EverNote - C"], plan.Notebooks.Select(n => n.Name));
        Assert.Contains("also maps to", plan.Notebooks[1].RenameReason);
        Assert.Equal(["Tom _ Jerry", "Tom _ Jerry (2)"], plan.Notebooks[2].Sections.Select(s => s.Name));
    }

    [Fact]
    public void Unreadable_notes_are_excluded_and_file_problems_are_listed()
    {
        using var root = new TempDirectory();
        var inspection = Inspector.Run(TestFiles.Fixture("Problems"));
        var damaged = inspection.Notes[1];
        inspection.Notes[1] = new NoteSummary
        {
            File = damaged.File, Index = damaged.Index, ByteOffset = damaged.ByteOffset, Line = damaged.Line, Title = damaged.Title,
            Text = "", TextStatus = TextStatus.NotRecovered,
        };

        var plan = BulkMigrator.Plan(inspection, new FakeOneNote(), root.Path, [], Now);

        Assert.Same(inspection.Notes[1], Assert.Single(plan.Excluded).Note);
        Assert.Equal(4, plan.NotesToMigrate);
        Assert.Contains(plan.FileProblems, p => p.StartsWith("Damaged.enex", StringComparison.Ordinal));
        Assert.Contains("EXCLUDED: 1 NOTE(S) WILL NOT BE MIGRATED", plan.Describe(exportBackup: false));
    }

    [Fact]
    public void A_file_changed_since_the_inspection_stops_the_plan()
    {
        using var source = new TempDirectory();
        var path = source.Write("One.enex", Enex.File(Enex.Note("N", "<en-note><div>x</div></en-note>")));
        var inspection = Inspector.Run(source.Path);
        File.AppendAllText(path, " ");
        using var root = new TempDirectory();

        Assert.Contains("changed", BulkMigrator.Plan(inspection, new FakeOneNote(), root.Path, [], Now).Problem);
    }

    [Fact]
    public void Runs_every_note_into_new_notebooks_with_a_journal_a_report_and_one_backup_per_notebook()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();
        oneNote.AddNotebook("open", "Personal", "Section", "Existing page");
        var plan = PlanBasic(oneNote, root.Path);
        var updates = new List<BulkProgress>();

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: true, new Collect(updates), exportTimeout: TimeSpan.FromSeconds(10));

        Assert.Equal(4, result.Notes.Count);
        Assert.All(result.Notes, n => Assert.True(n.Migrated && n.TextVerified && !n.TitleDiffers, n.Details));
        Assert.Equal([BulkNoteStatus.Verified, BulkNoteStatus.Verified, BulkNoteStatus.Review, BulkNoteStatus.Verified], result.Notes.Select(n => n.Status));
        Assert.Contains("encrypted", result.Notes[2].Details); // encrypted text cannot be migrated, so the run is not complete
        Assert.False(result.Complete);
        Assert.Equal(0, result.AttachmentsMissing);
        Assert.Equal(["CreateNotebook", "CreateSection:Empty", "CreateSection:Recipes", "CreateNotebook", "CreateSection:Trips"], oneNote.Calls);
        Assert.Equal(["existing-1"], oneNote.UntouchedPages());

        // Each note became a page in the section of its ENEX file, in order.
        var sections = oneNote.CreatedSections.ToDictionary(s => s.Id, s => s.Name);
        Assert.Equal(result.Notes.Select(n => n.Section.Name), oneNote.CreatedPages.Select(p => sections[p.SectionId]));

        // One backup per new notebook, after the notes.
        Assert.Equal(["nb1", "nb2"], oneNote.Published);
        Assert.All(result.Notebooks, n => Assert.True(File.Exists(n.Export?.Path), n.Export?.Problem));

        // The journal: valid JSON lines, a start and a result per note, with source path and note identifiers.
        var journal = File.ReadAllLines(result.JournalPath);
        var events = journal.Select(line => JsonDocument.Parse(line).RootElement).ToList();
        Assert.Equal("run-start", events[0].GetProperty("event").GetString());
        Assert.Equal("run-end", events[^1].GetProperty("event").GetString());
        Assert.False(events[^1].GetProperty("complete").GetBoolean());
        var notes = events.Where(e => e.GetProperty("event").GetString() == "note").ToList();
        Assert.Equal(4, notes.Count);
        Assert.Equal(4, events.Count(e => e.GetProperty("event").GetString() == "note-start"));
        Assert.All(notes, n => Assert.True(File.Exists(n.GetProperty("sourcePath").GetString())));
        Assert.Equal(Path.Combine("Travel", "Trips.enex"), notes[^1].GetProperty("file").GetString());
        Assert.Equal(2, notes[^1].GetProperty("note").GetInt32()); // note numbers start at 1, as in the inspection
        Assert.Equal("Verified", notes[^1].GetProperty("status").GetString());
        Assert.Equal("Review", notes[2].GetProperty("status").GetString());
        Assert.True(notes[2].GetProperty("textVerified").GetBoolean());

        // The report and the CSV with every note and its destination.
        var report = File.ReadAllText(result.ReportPath);
        Assert.Contains(result.Headline, report);
        Assert.Contains("Section \"Trips\" <- " + Path.Combine("Travel", "Trips.enex"), report);
        var csv = File.ReadAllLines(result.CsvPath);
        Assert.Equal(5, csv.Length);
        Assert.StartsWith("Verified,Recipes.enex,1,", csv[1]);
        Assert.Contains(",EverNote - Travel,Trips,", csv[4]);

        Assert.Equal(4, updates.Max(u => u.Completed));
        Assert.Contains(updates, u => u.Notebook == "EverNote - Travel" && u.Section == "Trips");
    }

    [Fact]
    public void The_result_is_complete_only_when_every_note_and_attachment_is_verified()
    {
        using var source = new TempDirectory();
        source.Write("Clean.enex", Enex.File(Enex.Note("One", "<en-note><div>First</div></en-note>"), Enex.Note("Two", "<en-note><ul><li>Second</li></ul></en-note>")));
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();
        var plan = BulkMigrator.Plan(Inspector.Run(source.Path), oneNote, root.Path, [], Now);

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: false);

        Assert.True(result.Complete, string.Join("; ", result.Shortfalls()));
        Assert.Equal("RESULT: COMPLETE - all 2 notes were migrated and verified (text, titles and all 0 attachments).", result.Headline);
        Assert.Empty(result.Shortfalls());
        Assert.Empty(oneNote.Published);
    }

    [Fact]
    public void Stopping_finishes_the_current_note_and_leaves_the_rest_not_attempted()
    {
        using var root = new TempDirectory();
        using var cancel = new CancellationTokenSource();
        var oneNote = new FakeOneNote { PageWritten = _ => cancel.Cancel() };
        var plan = PlanBasic(oneNote, root.Path);

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: true, null, cancel.Token);

        Assert.Equal(BulkNoteStatus.Verified, result.Notes[0].Status); // the current note is finished and verified
        Assert.Single(oneNote.CreatedPages);
        Assert.All(result.Notes.Skip(1), n => Assert.Equal(BulkNoteStatus.NotAttempted, n.Status));
        Assert.Equal(["CreateNotebook", "CreateSection:Empty", "CreateSection:Recipes"], oneNote.Calls); // nothing created after the stop
        Assert.Empty(oneNote.Published); // no backup of a stopped run
        Assert.True(result.Cancelled);
        Assert.False(result.Complete);
        Assert.StartsWith("RESULT: INCOMPLETE - 1 of 4", result.Headline);
        Assert.Contains("3 note(s) were not attempted", string.Join("\n", result.Shortfalls()));
    }

    [Fact]
    public void Stopping_after_the_last_note_skips_the_backups_without_making_the_migration_incomplete()
    {
        using var source = new TempDirectory();
        source.Write("Clean.enex", Enex.File(Enex.Note("One", "<en-note><div>First</div></en-note>")));
        using var root = new TempDirectory();
        using var cancel = new CancellationTokenSource();
        var oneNote = new FakeOneNote { PageWritten = _ => cancel.Cancel() };
        var plan = BulkMigrator.Plan(Inspector.Run(source.Path), oneNote, root.Path, [], Now);

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: true, null, cancel.Token);

        Assert.True(result.Complete, string.Join("; ", result.Shortfalls()));
        Assert.Empty(oneNote.Published);
        Assert.Contains("Backup not made: you stopped the migration.", result.ReportText());
    }

    [Fact]
    public void A_lost_connection_stops_the_run_and_the_page_creation_is_not_retried()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote { DisconnectAfterPages = 1 };
        var plan = PlanBasic(oneNote, root.Path);

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: true);

        Assert.Equal(BulkNoteStatus.Verified, result.Notes[0].Status);
        Assert.Equal(BulkNoteStatus.Failed, result.Notes[1].Status);
        Assert.Contains(result.Notes[1].Migration!.Notices, n => n.Contains("not retried", StringComparison.Ordinal));
        Assert.Single(oneNote.CreatedPages);
        Assert.Contains("connection to OneNote was lost", result.StoppedReason);
        Assert.All(result.Notes.Skip(2), n => Assert.Equal(BulkNoteStatus.NotAttempted, n.Status));
        Assert.Empty(oneNote.Published);
        Assert.Contains("## Failed notes (1)", result.ReportText());
    }

    [Fact]
    public void Repeated_failures_stop_the_run()
    {
        using var source = new TempDirectory();
        source.Write("Many.enex", Enex.File(Enumerable.Range(1, 7).Select(i => Enex.Note($"Note {i}", $"<en-note><div>Text {i}</div></en-note>")).ToArray()));
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote { FailPageCreation = true };
        var plan = BulkMigrator.Plan(Inspector.Run(source.Path), oneNote, root.Path, [], Now);

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: false);

        Assert.Equal(5, result.Count(BulkNoteStatus.Failed));
        Assert.Equal(2, result.Count(BulkNoteStatus.NotAttempted));
        Assert.Contains("5 notes in a row failed", result.StoppedReason);
        Assert.Contains(result.Notes[0].Migration!.Notices, n => n.Contains("not retried", StringComparison.Ordinal));
    }

    [Fact]
    public void Damaged_source_notes_are_migrated_but_never_reported_as_complete()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();
        var plan = BulkMigrator.Plan(Inspector.Run(TestFiles.Fixture("Problems")), oneNote, root.Path, [], Now);

        var result = BulkMigrator.Run(plan, oneNote, exportBackup: false);

        Assert.Equal(5, result.Notes.Count(n => n.Migrated));
        Assert.All(result.Notes, n => Assert.True(n.TextVerified, n.Details));
        Assert.Contains(result.Notes, n => n.Status == BulkNoteStatus.Review && n.Details!.Contains("damaged in the export", StringComparison.Ordinal));
        Assert.False(result.Complete);
        Assert.Contains(result.Shortfalls(), s => s.Contains("problem(s) in the ENEX files", StringComparison.Ordinal));
    }

    private sealed class Collect(List<BulkProgress> updates) : IProgress<BulkProgress>
    {
        public void Report(BulkProgress value) => updates.Add(value);
    }
}
