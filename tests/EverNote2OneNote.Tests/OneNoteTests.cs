using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EverNote2OneNote.Enex;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.Tests;

public class PageBuilderTests
{
    private static (NoteSummary Summary, EnexNote Note, Dictionary<int, byte[]> Data) Load(NoteSummary summary)
    {
        var note = EnexReader.ReadNoteAt(summary.File.FullPath, summary.ByteOffset, summary.Index);
        var data = new Dictionary<int, byte[]>();
        for (int i = 0; i < note.Resources.Count; i++)
        {
            data[i] = EnexReader.ReadAttachmentData(summary.File.FullPath, summary.ByteOffset, i, note.Resources[i].Md5, 1_000_000);
        }
        return (summary, note, data);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_fixture_note_keeps_all_of_its_text(bool embedMedia)
    {
        foreach (var summary in Inspector.Run(TestFiles.Fixtures).Notes)
        {
            var (_, note, data) = Load(summary);
            var plan = PageBuilder.Build(note, summary.File.Notebook, data, embedMedia, summary.Text);

            var verification = TextVerifier.Compare(summary.Text, NoteMigrator.PageText(plan.Html), plan.Added);

            Assert.True(verification.Identical, $"{summary.File.Notebook} #{summary.Index}: " +
                string.Join("; ", verification.Differences.Select(d => $"{d.Kind} '{d.Text}'")));
            Assert.Empty(verification.AddedTextNotFound);
        }
    }

    [Fact]
    public void Converts_structure_links_and_attachments()
    {
        var summary = Inspector.Run(TestFiles.Fixture("Basic/Recipes.enex")).Notes[0];
        var (_, note, data) = Load(summary);

        var plan = PageBuilder.Build(note, "Recipes", data, embedMedia: true, summary.Text);

        Assert.Contains("<title>Pancakes &amp; Syrup</title>", plan.Html);
        Assert.Contains("<meta name=\"created\" content=\"2012-03-15T08:15:00+00:00\" />", plan.Html);
        Assert.Contains("<li>[x] Buy eggs</li>", plan.Html);
        Assert.Contains("<a href=\"https://example.com/pancakes?a=1&amp;b=2\">the recipe </a><a href=\"https://example.com/pancakes?a=1&amp;b=2\"><b>site</b></a>", plan.Html);
        Assert.Contains("<td>Flour</td><td>200 g</td>", plan.Html);
        Assert.Contains("<img src=\"name:part1\" alt=\"pan.png\" />", plan.Html);
        Assert.Equal("part1", Assert.Single(plan.Parts).Name);
        Assert.Contains(plan.Notices, n => n.Contains("merged table cell"));
    }

    [Fact]
    public void Links_to_other_notes_keep_their_text_and_are_reported()
    {
        var note = new EnexNote { Index = 1, ByteOffset = 0, Line = 1, Title = "Links",
            Content = Enex.Body("<en-note><div>See <a href=\"evernote:///view/1/s1/abc/abc/\">my other note</a>.</div></en-note>") };

        var plan = PageBuilder.Build(note, "Nb", new Dictionary<int, byte[]>(), embedMedia: true, "");

        Assert.DoesNotContain("<a ", plan.Html);
        Assert.Contains("See my other note.", NoteMigrator.PageText(plan.Html));
        Assert.Contains(plan.Notices, n => n.Contains("link(s)"));
    }

    [Fact]
    public void Section_names_follow_onenote_rules()
    {
        Assert.Equal("Work_ Projects _ Ideas", NoteMigrator.SectionName("Work: Projects & Ideas"));
        Assert.Equal(50, NoteMigrator.SectionName(new string('x', 80)).Length);
        Assert.Equal("Evernote", NoteMigrator.SectionName("   "));
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

public class NoteMigratorTests
{
    [Theory]
    [InlineData(0, "Single request")]
    [InlineData(1, "fallback")]
    public async Task Migrates_verifies_and_reports_attachments(int rejectFirstCreate, string method)
    {
        var shopping = Inspector.Run(TestFiles.Fixture("Basic/Recipes.enex")).Notes[1]; // PDF in body + unreferenced JPEG
        var fake = new FakeOneNote { RejectPageCreations = rejectFirstCreate };
        var client = new OneNoteClient(new HttpClient(fake), _ => Task.FromResult("token")) { RetryDelays = [] };

        var result = await NoteMigrator.MigrateAsync(shopping, client, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Contains(method, result.Method);
        Assert.Equal("Recipes", result.Section);
        Assert.Equal("https://onenote.example/page", result.WebUrl);
        Assert.True(result.TitleMatches);
        Assert.True(result.Text!.Identical, string.Join("; ", result.Text.Differences.Select(d => $"{d.Kind} '{d.Text}'")));
        Assert.Equal(2, result.Attachments.Count);
        Assert.All(result.Attachments, a => Assert.True(a.Ok, a.Status));
        Assert.Equal(1, fake.Count("POST notebooks"));
        Assert.Equal(1 + rejectFirstCreate, fake.Count("POST sections/s1/pages"));
    }

    [Fact]
    public async Task Existing_notebook_and_section_are_reused_not_recreated()
    {
        var note = Inspector.Run(TestFiles.Fixture("Basic/Recipes.enex")).Notes[0];
        var fake = new FakeOneNote { NotebookExists = true, SectionExists = true };
        var client = new OneNoteClient(new HttpClient(fake), _ => Task.FromResult("token")) { RetryDelays = [] };

        var result = await NoteMigrator.MigrateAsync(note, client, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(0, fake.Count("POST notebooks"));
        Assert.Equal(0, fake.Count("POST notebooks/nb1/sections"));
    }

    [Fact]
    public async Task Server_errors_are_reported_without_retrying_page_creation()
    {
        var note = Inspector.Run(TestFiles.Fixture("Basic/Recipes.enex")).Notes[0];
        var fake = new FakeOneNote { FailPageCreation = HttpStatusCode.BadGateway };
        var client = new OneNoteClient(new HttpClient(fake), _ => Task.FromResult("token")) { RetryDelays = [] };

        var result = await NoteMigrator.MigrateAsync(note, client, null, CancellationToken.None);

        Assert.False(result.PageCreated);
        Assert.Contains("may or may not have been created", result.Error);
        Assert.Equal(1, fake.Count("POST sections/s1/pages"));
    }

    /// <summary>Simulates the OneNote API: echoes created pages back with images and files rewritten as OneNote does.</summary>
    private sealed class FakeOneNote : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        private string _page = "";

        public int RejectPageCreations { get; set; }
        public HttpStatusCode? FailPageCreation { get; init; }
        public bool NotebookExists { get; init; }
        public bool SectionExists { get; init; }

        public int Count(string request) => _requests.Count(r => r == request);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer token", request.Headers.Authorization?.ToString());
            var path = request.RequestUri!.AbsolutePath.Replace("/v1.0/me/onenote/", "");
            _requests.Add($"{request.Method} {path}");
            switch ($"{request.Method} {path}")
            {
                case "GET notebooks":
                    return Json(NotebookExists ? """{"value":[{"id":"nb1","displayName":"evernote2onenote test"}]}""" : """{"value":[]}""");
                case "POST notebooks":
                    return Json("""{"id":"nb1"}""", HttpStatusCode.Created);
                case "GET notebooks/nb1/sections":
                    return Json(SectionExists ? """{"value":[{"id":"s1","displayName":"Recipes"}]}""" : """{"value":[]}""");
                case "POST notebooks/nb1/sections":
                    return Json("""{"id":"s1"}""", HttpStatusCode.Created);
                case "POST sections/s1/pages":
                    if (FailPageCreation is { } failure)
                    {
                        return new HttpResponseMessage(failure);
                    }
                    var parts = await Parts(request.Content!);
                    if (RejectPageCreations-- > 0)
                    {
                        return Json("""{"error":{"code":"20016","message":"Request too large"}}""", HttpStatusCode.RequestEntityTooLarge);
                    }
                    _page = parts["Presentation"];
                    return Json("""{"id":"p1","links":{"oneNoteClientUrl":{"href":"onenote:page"},"oneNoteWebUrl":{"href":"https://onenote.example/page"}}}""", HttpStatusCode.Created);
                case "PATCH pages/p1/content":
                    var commands = JsonNode.Parse((await Parts(request.Content!))["Commands"])!.AsArray();
                    _page = _page.Replace("</body>", commands[0]!["content"]!.GetValue<string>() + "\n</body>");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "GET pages/p1/content":
                    var stored = Regex.Replace(_page, "src=\"name:[^\"]+\"", "src=\"https://graph.example/resource/$value\"");
                    stored = Regex.Replace(stored, "data=\"name:[^\"]+\"", "data=\"https://graph.example/resource/$value\"");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stored, Encoding.UTF8, "text/html") };
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static async Task<Dictionary<string, string>> Parts(HttpContent content)
        {
            var result = new Dictionary<string, string>();
            foreach (var part in (MultipartFormDataContent)content)
            {
                result[part.Headers.ContentDisposition!.Name!.Trim('"')] = await part.ReadAsStringAsync();
            }
            return result;
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
