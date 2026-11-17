using System.Text;
using System.Xml.Linq;
using EverNote2OneNote.Enex;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.Tests;

public class PageXmlBuilderTests
{
    private static (EnexNote Note, Dictionary<int, byte[]> Data) Load(NoteSummary summary)
    {
        var note = EnexReader.ReadNoteAt(summary.File.FullPath, summary.ByteOffset, summary.Index);
        var data = new Dictionary<int, byte[]>();
        for (int i = 0; i < note.Resources.Count; i++)
        {
            data[i] = EnexReader.ReadAttachmentData(summary.File.FullPath, summary.ByteOffset, i, note.Resources[i].Md5, 1_000_000);
        }
        return (note, data);
    }

    [Theory]
    [InlineData(PageMode.Full)]
    [InlineData(PageMode.TextOnly)]
    public void Every_fixture_note_keeps_all_of_its_text(PageMode mode)
    {
        foreach (var summary in Inspector.Run(TestFiles.Fixtures).Notes)
        {
            var (note, data) = Load(summary);
            var page = PageXmlBuilder.Build("{page}", note, summary.File.Notebook, data, i => $"C:\\temp\\{i}", mode, summary.Text);

            var content = PageXmlBuilder.Read(page.Xml);
            var verification = TextVerifier.Compare(summary.Text, content.Text, page.Added);

            Assert.True(verification.Identical, $"{summary.File.Notebook} #{summary.Index}: " +
                string.Join("; ", verification.Differences.Select(d => $"{d.Kind} '{d.Text}'")));
            Assert.Empty(verification.AddedTextNotFound);
            Assert.Equal(page.Title, content.Title);
        }
    }

    [Fact]
    public void Builds_lists_links_images_and_files()
    {
        var summary = Inspector.Run(TestFiles.Fixture("Basic/Recipes.enex")).Notes[0];
        var (note, data) = Load(summary);

        var page = PageXmlBuilder.Build("{page-1}", note, "Recipes", data, i => $"C:\\temp\\{i}", PageMode.Full, summary.Text);
        var xml = XElement.Parse(page.Xml);
        XNamespace one = PageXmlBuilder.Namespace;

        Assert.Equal("{page-1}", (string?)xml.Attribute("ID"));
        Assert.Equal("2012-03-15T08:15:00.000Z", (string?)xml.Attribute("dateTime"));
        Assert.Equal(2, xml.Descendants(one + "Bullet").Count());
        Assert.Contains(xml.Descendants(one + "T"), t => t.Value.Contains("<a href=\"https://example.com/pancakes?a=1&amp;b=2\">"));
        Assert.Single(xml.Descendants(one + "Image"));
        Assert.Equal(["pan.png"], page.Attachments.Select(a => a.FileName));
        var table = Assert.Single(xml.Descendants(one + "Table"));
        Assert.Equal(2, table.Element(one + "Columns")!.Elements(one + "Column").Count());
        Assert.Equal(2, table.Elements(one + "Row").Count());
        Assert.All(table.Elements(one + "Row"), row => Assert.Equal(2, row.Elements(one + "Cell").Count())); // first row padded

        var text = PageXmlBuilder.Build("{page-2}", note, "Recipes", data, i => "x", PageMode.TextOnly, summary.Text);
        Assert.Empty(XElement.Parse(text.Xml).Descendants(one + "Image"));
        Assert.DoesNotContain("<a ", text.Xml);
        Assert.Null(XElement.Parse(text.Xml).Attribute("dateTime"));
    }

    [Fact]
    public void Nested_lists_become_nested_outline_elements()
    {
        var note = new EnexNote { Index = 1, ByteOffset = 0, Line = 1, Title = "Nested",
            Content = Enex.Body("<en-note><ul><li>outer<ul><li>inner</li></ul></li><li>second</li></ul></en-note>") };

        var page = PageXmlBuilder.Build("{p}", note, "Nb", new Dictionary<int, byte[]>(), _ => "", PageMode.Full, "");
        XNamespace one = PageXmlBuilder.Namespace;
        var inner = XElement.Parse(page.Xml).Descendants(one + "T").Single(t => t.Value == "inner");

        Assert.Equal(one + "OEChildren", inner.Parent!.Parent!.Name);
        Assert.Equal("outer", inner.Parent.Parent.Parent!.Element(one + "T")!.Value);
    }

    [Fact]
    public void Simple_tables_keep_rows_and_cells_and_all_text()
    {
        var note = new EnexNote { Index = 1, ByteOffset = 0, Line = 1, Title = "Table",
            Content = Enex.Body("<en-note><table><tr><th>Item</th><th>Qty</th></tr><tr><td><div>Flour</div><div>(sifted)</div></td><td>200 g</td></tr>" +
                                "<tr><td>Inner <table><tr><td>nested</td><td>cells</td></tr></table></td></tr></table><div>After</div></en-note>") };
        var original = EverNote2OneNote.Text.EnmlText.Extract(note.Content).Text;

        var page = PageXmlBuilder.Build("{p}", note, "Nb", new Dictionary<int, byte[]>(), _ => "", PageMode.Full, original);
        XNamespace one = PageXmlBuilder.Namespace;
        var rows = XElement.Parse(page.Xml).Descendants(one + "Table").Single().Elements(one + "Row").ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["Flour", "(sifted)"], rows[1].Elements(one + "Cell").First().Descendants(one + "T").Select(t => t.Value));
        Assert.Contains("nested", rows[2].Elements(one + "Cell").First().Value); // nested table flattened into its cell
        Assert.True(TextVerifier.Compare(original, PageXmlBuilder.Read(page.Xml).Text, page.Added).Identical);
    }

    [Fact]
    public void Section_names_follow_onenote_rules()
    {
        Assert.Equal("Work_ Projects _ Ideas", LocalMigrator.SectionName("Work: Projects & Ideas"));
        Assert.Equal(50, LocalMigrator.SectionName(new string('x', 80)).Length);
        Assert.Equal("Evernote", LocalMigrator.SectionName("   "));
    }
}

public class LocalMigratorTests
{
    private static NoteSummary Note(int index) => Inspector.Run(TestFiles.Fixture("Basic/Recipes.enex")).Notes[index];

    [Fact]
    public void Creates_a_new_destination_notebook_and_section_and_verifies_the_page()
    {
        using var root = new TempDirectory();
        var shopping = Note(1); // PDF in body + unreferenced JPEG
        var oneNote = new FakeOneNote();

        var plan = LocalMigrator.PlanDestination(oneNote, "Evernote Import", shopping, root.Path, []);
        Assert.False(plan.NotebookExists);
        Assert.False(plan.SectionExists);
        Assert.Equal(Path.Combine(root.Path, "Notebooks", "Evernote Import"), plan.NotebookLocation);

        var result = LocalMigrator.Migrate(shopping, oneNote, plan, root.Path, exportBackup: true, exportTimeout: TimeSpan.FromSeconds(10));

        Assert.Null(result.Error);
        Assert.StartsWith("Full", result.Method);
        Assert.Equal(["CreateNotebook", "CreateSection:Recipes"], oneNote.Calls);
        Assert.True(result.TitleMatches);
        Assert.True(result.Text!.Identical, string.Join("; ", result.Text.Differences.Select(d => $"{d.Kind} '{d.Text}'")));
        Assert.All(result.Attachments, a => Assert.True(a.Ok, a.Status));
        Assert.Null(result.ExportProblem);
        Assert.True(File.Exists(result.ExportPath)); // published asynchronously by the fake; found by waiting
        var temp = Path.Combine(root.Path, "Temp");
        Assert.True(!Directory.Exists(temp) || !Directory.EnumerateFileSystemEntries(temp).Any(), "Temporary attachment copies must be removed.");
    }

    [Fact]
    public void Reuses_an_existing_notebook_and_section_and_reports_pages_with_the_same_title()
    {
        using var root = new TempDirectory();
        var pancakes = Note(0);
        var oneNote = new FakeOneNote();
        oneNote.AddNotebook("nb-existing", "Evernote Import", "Recipes", "Pancakes & Syrup", "Other page");

        var plan = LocalMigrator.PlanDestination(oneNote, "evernote import", pancakes, root.Path, []);
        Assert.True(plan.NotebookExists);
        Assert.Equal("nb-existing", plan.NotebookId);
        Assert.True(plan.SectionExists);
        Assert.Equal(2, plan.SectionPages);
        Assert.Equal(1, plan.PagesWithSameTitle);

        var result = LocalMigrator.Migrate(pancakes, oneNote, plan, root.Path, exportBackup: false);

        Assert.Null(result.Error);
        Assert.Empty(oneNote.Calls); // no notebook or section created
        Assert.Equal(["existing-1", "existing-2"], oneNote.UntouchedPages()); // existing pages never written
        Assert.Null(result.ExportPath);
        Assert.False(result.ExportRequested);
    }

    [Fact]
    public void Ambiguous_or_invalid_destination_names_are_refused()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();
        oneNote.AddNotebook("a", "Evernote Import", "S");
        oneNote.AddNotebook("b", "Evernote Import", "S");

        Assert.Contains("2 open notebooks", LocalMigrator.PlanDestination(oneNote, "Evernote Import", Note(0), root.Path, []).Problem);
        Assert.NotNull(LocalMigrator.PlanDestination(oneNote, "Bad:Name", Note(0), root.Path, []).Problem);
        Assert.NotNull(LocalMigrator.PlanDestination(oneNote, "  ", Note(0), root.Path, []).Problem);
    }

    [Fact]
    public void Rejected_pages_are_rewritten_as_text_only_on_a_fresh_page()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote { RejectRichContent = true };
        var plan = LocalMigrator.PlanDestination(oneNote, "Evernote Import", Note(0), root.Path, []);

        var result = LocalMigrator.Migrate(Note(0), oneNote, plan, root.Path, exportBackup: false);

        Assert.Null(result.Error);
        Assert.Equal("Text only (fallback)", result.Method);
        Assert.True(result.Text!.Identical);
        Assert.Equal(["p1"], oneNote.Deleted);
        Assert.All(result.Attachments, a => Assert.False(a.Ok));
    }

    [Fact]
    public void A_missing_export_is_reported_without_failing_the_migration()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote { PublishWritesNothing = true };
        var plan = LocalMigrator.PlanDestination(oneNote, "Evernote Import", Note(0), root.Path, []);

        var result = LocalMigrator.Migrate(Note(0), oneNote, plan, root.Path, exportBackup: true, exportTimeout: TimeSpan.FromSeconds(2));

        Assert.True(result.PageCreated);
        Assert.Null(result.Error);
        Assert.True(result.Text!.Identical);
        Assert.Contains("no .onepkg file appeared", result.ExportProblem);
        Assert.Null(result.ExportPath);
    }

    [Fact]
    public void Existing_exports_are_never_overwritten()
    {
        using var root = new TempDirectory();
        var existing = root.Write("Report.onepkg", "existing");
        var unique = LocalMigrator.UniquePath(Path.Combine(root.Path, "Report"), ".onepkg");

        Assert.Equal(Path.Combine(root.Path, "Report (2).onepkg"), unique);
        Assert.Equal("existing", File.ReadAllText(existing));
    }

    [Fact]
    public void Refuses_to_work_inside_a_onedrive_folder()
    {
        using var root = new TempDirectory();
        var oneNote = new FakeOneNote();

        var plan = LocalMigrator.PlanDestination(oneNote, "Evernote Import", Note(0), Path.Combine(root.Path, "sub"), [root.Path]);

        Assert.Contains("OneDrive", plan.Problem);
        Assert.Empty(oneNote.Calls);
    }

    /// <summary>Stands in for the OneNote desktop API: an in-memory hierarchy; page XML is stored and returned like OneNote does.</summary>
    private sealed class FakeOneNote : IOneNoteDesktop
    {
        private static readonly XNamespace One = PageXmlBuilder.Namespace;
        private readonly List<(string Id, string Name, List<(string Id, string Name, List<(string Id, string Title)> Pages)> Sections)> _notebooks = [];
        private readonly Dictionary<string, string> _pages = [];
        private int _pageCount;

        public bool RejectRichContent { get; init; }
        public bool PublishWritesNothing { get; init; }
        public List<string> Calls { get; } = [];
        public List<string> Deleted { get; } = [];

        public void AddNotebook(string id, string name, string section, params string[] pageTitles) =>
            _notebooks.Add((id, name, [($"{id}-s", section, pageTitles.Select((t, i) => ($"existing-{i + 1}", t)).ToList())]));

        /// <summary>IDs of pre-existing pages that were never written.</summary>
        public List<string> UntouchedPages() => _notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages)
            .Where(p => p.Id.StartsWith("existing", StringComparison.Ordinal) && !_pages.ContainsKey(p.Id)).Select(p => p.Id).ToList();

        public string GetHierarchyXml(string startNodeId, HierarchyScope scope) => scope switch
        {
            HierarchyScope.Notebooks => new XElement(One + "Notebooks",
                _notebooks.Select(n => new XElement(One + "Notebook", new XAttribute("ID", n.Id), new XAttribute("name", n.Name)))).ToString(),
            HierarchyScope.Sections => new XElement(One + "Notebook",
                _notebooks.Single(n => n.Id == startNodeId).Sections.Select(s => new XElement(One + "Section", new XAttribute("ID", s.Id), new XAttribute("name", s.Name)))).ToString(),
            _ => new XElement(One + "Section",
                _notebooks.SelectMany(n => n.Sections).Single(s => s.Id == startNodeId).Pages.Select(p => new XElement(One + "Page", new XAttribute("ID", p.Id), new XAttribute("name", p.Title)))).ToString(),
        };

        public string CreateNotebook(string folderPath)
        {
            Calls.Add("CreateNotebook");
            Directory.CreateDirectory(folderPath);
            return "nb";
        }

        public string CreateSection(string notebookId, string sectionName)
        {
            Calls.Add("CreateSection:" + sectionName);
            return "s";
        }

        public string CreatePage(string sectionId)
        {
            var id = "p" + ++_pageCount;
            _pages[id] = $"<one:Page xmlns:one=\"{PageXmlBuilder.Namespace}\" ID=\"{id}\" />";
            return id;
        }

        public void UpdatePage(string pageXml)
        {
            if (RejectRichContent && (pageXml.Contains("one:Image") || pageXml.Contains("one:InsertedFile") || pageXml.Contains("one:Table")))
            {
                throw new OneNoteDesktopException("Invalid XML (0x80042001)", unchecked((int)0x80042001));
            }
            var page = XElement.Parse(pageXml);
            var id = (string)page.Attribute("ID")!;
            Assert.StartsWith("p", id); // only pages created by the migration are ever written
            foreach (var file in page.Descendants(One + "InsertedFile"))
            {
                Assert.True(File.Exists((string)file.Attribute("pathSource")!), "Attachments must exist on disk while OneNote copies them.");
            }
            _pages[id] = pageXml;
        }

        public string GetPageXml(string pageId) => _pages[pageId];

        public void DeletePage(string pageId)
        {
            Deleted.Add(pageId);
            _pages.Remove(pageId);
        }

        public void SyncHierarchy(string objectId)
        {
        }

        public void PublishNotebook(string notebookId, string targetPath)
        {
            Assert.False(File.Exists(targetPath), "Exports must never overwrite a file.");
            if (!PublishWritesNothing)
            {
                // Like OneNote, return before the package is written.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    File.WriteAllBytes(targetPath, Encoding.ASCII.GetBytes("MSCF fake cabinet"));
                });
            }
        }
    }
}

public class TextVerifierTests
{
    private const string Original = "First paragraph here.\nSecond paragraph with more words.\nThird and final paragraph.";

    [Fact]
    public void Whitespace_and_line_breaks_are_ignored()
    {
        var result = TextVerifier.Compare(Original, "First   paragraph here. Second paragraph\twith more words.\n\nThird and final paragraph.", []);
        Assert.True(result.Identical);
    }

    [Fact]
    public void Added_text_is_recognised_at_its_position()
    {
        var page = "Migrated header line\n" + Original + "\nAttachments:";
        AddedText[] added = [new("Migrated header line", AddedTextPosition.Start), new("Attachments:", AddedTextPosition.End)];

        Assert.True(TextVerifier.Compare(Original, page, added).Identical);
        Assert.False(TextVerifier.Compare(Original, page, []).Identical);
    }

    [Fact]
    public void Missing_text_is_reported_with_context()
    {
        var result = TextVerifier.Compare(Original, Original.Replace(" with more words", ""), []);

        var difference = Assert.Single(result.Differences);
        Assert.Equal(DifferenceKind.Missing, difference.Kind);
        Assert.Equal("with more words", difference.Text);
        Assert.Contains("Second paragraph", difference.Context);
    }

    [Fact]
    public void Moved_text_is_reported_as_moved()
    {
        var page = "Second paragraph with more words.\nFirst paragraph here.\nThird and final paragraph.";
        var result = TextVerifier.Compare(Original, page, []);

        Assert.Contains(result.Differences, d => d.Kind == DifferenceKind.Moved);
        Assert.DoesNotContain(result.Differences, d => d.Kind == DifferenceKind.Missing);
    }

    [Fact]
    public void Duplicated_and_extra_text_are_reported()
    {
        var duplicated = TextVerifier.Compare(Original, Original + "\nThird and final paragraph.", []);
        Assert.Equal(DifferenceKind.Duplicated, Assert.Single(duplicated.Differences).Kind);

        var extra = TextVerifier.Compare(Original, Original + "\nSomething new", []);
        Assert.Equal(DifferenceKind.Extra, Assert.Single(extra.Differences).Kind);
    }
}
