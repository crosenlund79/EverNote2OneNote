using System.Security.Cryptography;
using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.Tests;

public class InspectorTests
{
    [Fact]
    public void Inspection_never_modifies_or_adds_source_files()
    {
        using var source = new TempDirectory();
        using var output = new TempDirectory();
        CopyDirectory(TestFiles.Fixtures, source.Path);
        var before = Snapshot(source.Path);

        var result = Inspector.Run(source.Path);
        ReportWriter.Write(result, Summary.Build(result, new Expectations(), null), Path.Combine(output.Path, "report"), dumpText: true);

        Assert.Equal(before, Snapshot(source.Path));
    }

    [Fact]
    public void Clean_exports_are_fully_extracted_and_findings_are_reported()
    {
        var result = Inspector.Run(TestFiles.Fixture("Basic"));

        Assert.Equal(["Empty", "Recipes", "Trips"], result.Files.Select(f => f.Notebook));
        Assert.Equal(4, result.Notes.Count);
        Assert.All(result.Notes, n => Assert.Equal(TextStatus.Extracted, n.TextStatus));
        Assert.Equal(4, result.Attachments.Count);
        Assert.Equal(0, result.CountIssues(Severity.Error));
        Assert.Equal(
            [IssueCodes.EmptyExport, IssueCodes.EmptyNote, IssueCodes.EncryptedText, IssueCodes.ExternalImage, IssueCodes.MergedTableCells,
             IssueCodes.Tasks, IssueCodes.UnreferencedAttachment, IssueCodes.UnsupportedImage, IssueCodes.UntitledNote],
            result.Issues.Select(i => i.Code).Distinct().Order(StringComparer.Ordinal));

        var photo = Assert.Single(result.Attachments, a => a.FileName == "photo.jpg");
        Assert.False(photo.Referenced);
    }

    [Fact]
    public void Damaged_exports_keep_all_recoverable_text_and_report_every_problem()
    {
        var result = Inspector.Run(TestFiles.Fixture("Problems"));

        Assert.Equal(5, result.Notes.Count);
        Assert.Equal(
            [IssueCodes.ContentMalformed, IssueCodes.InvalidDate, IssueCodes.MissingAttachment, IssueCodes.NoteMalformed,
             IssueCodes.UnexpectedContent, IssueCodes.UnexpectedMarkup, IssueCodes.UnknownElement, IssueCodes.UnknownEntity],
            result.Issues.Select(i => i.Code).Distinct().Order(StringComparer.Ordinal));

        string[] expectedText =
        [
            "First note is fine.",
            "Batter recipe: flour, beer & salt.\nFry at 180 degrees.",
            "Unclosed bold\nStill here\na < b and c > d\nKeep x <y z> w",
            "Price &bogus; 10\nSectioned text",
            "Last note is fine.",
        ];
        Assert.Equal(expectedText, result.Notes.Select(n => n.Text));
        Assert.Equal(
            [TextStatus.Extracted, TextStatus.Recovered, TextStatus.Recovered, TextStatus.Extracted, TextStatus.Extracted],
            result.Notes.Select(n => n.TextStatus));
    }

    [Fact]
    public void Summary_contains_counts_but_no_personal_details()
    {
        var result = Inspector.Run(TestFiles.Fixtures);
        var summary = Summary.Build(result, new Expectations(Notes: 9, Notebooks: 4, Attachments: 5), null);

        Assert.Contains("Notes                 9", summary);
        Assert.Contains("DIFFERS (-1)", summary);
        foreach (var personal in new[] { "Pancakes", "Shopping", "breakfast", "Fish", "Batter", "Recipes", "Trips", "pan.png", "pet name", "bakery" })
        {
            Assert.DoesNotContain(personal, summary);
        }
    }

    [Fact]
    public void Writes_detailed_report_files()
    {
        using var output = new TempDirectory();
        var result = Inspector.Run(TestFiles.Fixtures);
        var directory = Path.Combine(output.Path, "report");

        ReportWriter.Write(result, Summary.Build(result, new Expectations(), null), directory, dumpText: false);

        Assert.True(File.Exists(Path.Combine(directory, "report.md")));
        Assert.Equal(10, File.ReadAllLines(Path.Combine(directory, "notes.csv")).Length); // header + 9 notes
        Assert.Equal(5, File.ReadAllLines(Path.Combine(directory, "attachments.csv")).Length); // header + 4
        Assert.False(Directory.Exists(Path.Combine(directory, "text")));

        var recovered = Directory.GetFiles(Path.Combine(directory, "recovered")).Order().ToList();
        Assert.Equal(3, recovered.Count);
        var brokenBody = File.ReadAllText(recovered[1]);
        Assert.Contains("Original note body (ENML), exactly as exported", brokenBody);
        Assert.Contains("<p>Keep x <y z> w</p>", brokenBody);
    }

    [Fact]
    public void Indented_note_bodies_are_parsed_normally_and_attachments_are_matched()
    {
        // Real Evernote exports put the body on its own indented line, so whitespace precedes the XML declaration.
        using var temp = new TempDirectory();
        var image = new byte[] { 1, 2, 3, 4 };
        var md5 = Convert.ToHexStringLower(MD5.HashData(image));
        var note =
            "<note>\n  <title>Indented</title>\n  <content>\n    <![CDATA[" +
            Enex.Body($"<en-note><div>Line one</div><div>Line two</div><en-media hash=\"{md5}\" type=\"image/png\"/></en-note>") +
            "]]>\n  </content>\n" +
            $"  <resource><data encoding=\"base64\">{Convert.ToBase64String(image)}</data><mime>image/png</mime></resource>\n</note>\n";
        var path = temp.Write("Indented.enex", Enex.File(note));

        var result = Inspector.Run(path);

        var summary = Assert.Single(result.Notes);
        Assert.Equal(TextStatus.Extracted, summary.TextStatus);
        Assert.Null(summary.Problem);
        Assert.Equal("Line one\nLine two", summary.Text);
        Assert.True(Assert.Single(result.Attachments).Referenced);
        Assert.DoesNotContain(result.Issues, i => i.Code is IssueCodes.ContentMalformed or IssueCodes.UnreferencedAttachment);
    }

    [Fact]
    public void Duplicate_notebook_names_are_reported()
    {
        using var temp = new TempDirectory();
        temp.Write("A/Work.enex", Enex.File(Enex.Note("One", "<en-note>1</en-note>")));
        temp.Write("B/work.enex", Enex.File(Enex.Note("Two", "<en-note>2</en-note>")));

        var result = Inspector.Run(temp.Path);

        Assert.Contains(result.Issues, i => i.Code == IssueCodes.DuplicateNotebook);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static List<string> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => File.Exists(path)
                ? $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"
                : path)
            .ToList();
}
