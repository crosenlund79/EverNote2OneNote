using System.Net;
using System.Text.RegularExpressions;
using EverNote2OneNote.Enex;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.Text;

namespace EverNote2OneNote.OneNote;

public sealed record AttachmentOutcome(string FileName, string Type, long Size, string Placement, string Status, bool Ok);

public sealed class NoteMigrationResult
{
    public required string NoteTitle { get; init; }
    public string Notebook { get; set; } = NoteMigrator.TargetNotebook;
    public string? Section { get; set; }
    public string? PageId { get; set; }
    public string? PageTitle { get; set; }
    public string? ClientUrl { get; set; }
    public string? WebUrl { get; set; }

    /// <summary>How the page was created.</summary>
    public string? Method { get; set; }

    /// <summary>Set if the migration stopped; the page may not exist.</summary>
    public string? Error { get; set; }

    public bool TitleMatches { get; set; }
    public TextVerification? Text { get; set; }
    public List<AttachmentOutcome> Attachments { get; } = [];
    public List<string> Notices { get; } = [];

    public bool PageCreated => PageId is not null;
}

/// <summary>Migrates one inspected note to a dedicated OneNote test notebook and verifies the result.</summary>
public static partial class NoteMigrator
{
    /// <summary>All pages go to this notebook; existing notebooks are never touched.</summary>
    public const string TargetNotebook = "EverNote2OneNote Test";

    /// <summary>Attachments uploaded together with the page; more than this are appended afterwards.</summary>
    internal const int MaxPartsPerRequest = 5;
    internal const long MaxBytesPerRequest = 50L * 1024 * 1024;

    public static async Task<NoteMigrationResult> MigrateAsync(NoteSummary summary, OneNoteClient client, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new NoteMigrationResult { NoteTitle = summary.Title ?? "" };

        // 1. Read the note and its attachments from the original ENEX file (read-only).
        progress?.Report("Reading the note from the ENEX file...");
        var note = EnexReader.ReadNoteAt(summary.File.FullPath, summary.ByteOffset, summary.Index);
        if (summary.TextStatus == TextStatus.Extracted && (note.Content is null || note.Title != summary.Title))
        {
            result.Error = "The note could not be read from the ENEX file as inspected; the file may have changed. Run the inspection again.";
            return result;
        }
        var data = new Dictionary<int, byte[]>();
        var unavailable = new Dictionary<int, string>();
        for (int i = 0; i < note.Resources.Count; i++)
        {
            var resource = note.Resources[i];
            if (resource.Size > PageBuilder.MaxPartBytes)
            {
                unavailable[i] = $"Not transferred: {Format.Bytes(resource.Size)} is over OneNote's 25 MB limit per file.";
                continue;
            }
            try
            {
                data[i] = EnexReader.ReadAttachmentData(summary.File.FullPath, summary.ByteOffset, i, resource.Md5, PageBuilder.MaxPartBytes);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
            {
                unavailable[i] = "Not transferred: the data could not be read from the export (" + ex.Message + ")";
            }
        }
        if (note.UnreadAttachments > 0)
        {
            result.Notices.Add($"{note.UnreadAttachments} attachment(s) in this damaged note could not be read from the export.");
        }

        bool embed = data.Count <= MaxPartsPerRequest && data.Values.Sum(d => (long)d.Length) <= MaxBytesPerRequest;
        var plan = PageBuilder.Build(note, summary.File.Notebook, data, embed, summary.Text);

        try
        {
            // 2. Dedicated notebook, one section per Evernote notebook.
            progress?.Report("Preparing the OneNote notebook...");
            var notebookId = await client.GetOrCreateNotebookAsync(TargetNotebook, ct);
            result.Section = SectionName(summary.File.Notebook);
            var sectionId = await client.GetOrCreateSectionAsync(notebookId, result.Section, ct);

            // 3. Create the page. If OneNote rejects it with attachments embedded, create it text-first instead.
            progress?.Report("Creating the OneNote page...");
            CreatedPage page;
            try
            {
                page = await client.CreatePageAsync(sectionId, plan.Html, plan.Parts, ct);
                result.Method = embed ? "Single request (attachments in place)" : "Text first, attachments appended";
            }
            catch (OneNoteException ex) when (ex.Rejected && embed)
            {
                result.Notices.Add($"OneNote rejected the page with attachments embedded ({ex.Message}); it was created text-first and the attachments were appended.");
                plan = PageBuilder.Build(note, summary.File.Notebook, data, embedMedia: false, summary.Text);
                page = await client.CreatePageAsync(sectionId, plan.Html, plan.Parts, ct);
                result.Method = "Text first, attachments appended (fallback)";
            }
            result.PageId = page.Id;
            result.ClientUrl = page.ClientUrl;
            result.WebUrl = page.WebUrl;
            result.Notices.AddRange(plan.Notices);

            var uploads = new Dictionary<int, (PagePart Part, AttachmentOutcome Outcome)>();
            foreach (var part in plan.Parts)
            {
                uploads[part.ResourceIndex] = (part, Outcome(part, "In place", "Uploaded", true));
            }
            for (int i = 0; i < plan.Deferred.Count; i++)
            {
                var part = plan.Deferred[i];
                progress?.Report($"Appending attachment {i + 1} of {plan.Deferred.Count}...");
                try
                {
                    await client.AppendAsync(page.Id, part.IsImage ? $"<p>{PageBuilder.MediaElement(part)}</p>" : PageBuilder.MediaElement(part), [part], ct);
                    uploads[part.ResourceIndex] = (part, Outcome(part, "End of page", "Uploaded", true));
                }
                catch (OneNoteException ex)
                {
                    uploads[part.ResourceIndex] = (part, Outcome(part, "-", "Failed: " + ex.Message, false));
                }
            }

            // 4. Read the page back and verify.
            progress?.Report("Reading the page back from OneNote...");
            var html = await client.GetPageContentAsync(page.Id, ct);
            result.PageTitle = PageTitle(html);
            result.TitleMatches = string.Equals(Collapse(result.PageTitle), Collapse(plan.Title), StringComparison.Ordinal);
            result.Text = TextVerifier.Compare(summary.Text, PageText(html), plan.Added);

            // Images are renamed by OneNote, so they are checked by count; files are checked by name.
            bool imagesPresent = ImagePattern().Count(html) >= uploads.Values.Count(u => u.Part.IsImage && u.Outcome.Ok);
            for (int i = 0; i < note.Resources.Count; i++)
            {
                if (unavailable.TryGetValue(i, out var reason))
                {
                    var r = note.Resources[i];
                    result.Attachments.Add(new AttachmentOutcome(r.FileName ?? $"attachment-{i + 1}", r.Mime ?? "", r.Size, "-", reason, false));
                }
                else if (uploads.TryGetValue(i, out var upload))
                {
                    result.Attachments.Add(upload.Outcome.Ok ? Verify(upload.Part, upload.Outcome, html, imagesPresent) : upload.Outcome);
                }
            }
        }
        catch (OneNoteException ex)
        {
            result.Error = ex.Status is null or >= HttpStatusCode.InternalServerError
                ? ex.Message + " The page may or may not have been created; check the OneNote section before trying again."
                : ex.Message;
        }
        catch (HttpRequestException ex)
        {
            result.Error = $"Could not reach OneNote: {ex.Message}. The page may or may not have been created; check OneNote before trying again.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            result.Error = "OneNote did not respond in time. The page may or may not have been created; check OneNote before trying again.";
        }
        return result;
    }

    /// <summary>OneNote section names: at most 50 characters, without ? * \ / : &lt; &gt; | &amp; # " ' % ~.</summary>
    public static string SectionName(string notebook)
    {
        var chars = notebook.Select(c => "?*\\/:<>|&#\"'%~".Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        if (name.Length > 50)
        {
            name = name[..50].TrimEnd();
        }
        return name.Length == 0 ? "Evernote" : name;
    }

    /// <summary>The visible text of a OneNote page body.</summary>
    public static string PageText(string html)
    {
        var body = BodyPattern().Match(html);
        return EnmlText.Extract("<div>" + (body.Success ? body.Groups[1].Value : html) + "</div>").Text;
    }

    private static string PageTitle(string html)
    {
        var m = TitlePattern().Match(html);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
    }

    private static AttachmentOutcome Outcome(PagePart part, string placement, string status, bool ok) =>
        new(part.FileName, part.Mime, part.Data.Length, placement, status, ok);

    /// <summary>Checks that an uploaded attachment is present on the page read back from OneNote.</summary>
    private static AttachmentOutcome Verify(PagePart part, AttachmentOutcome outcome, string html, bool imagesPresent)
    {
        bool found = part.IsImage
            ? imagesPresent
            : html.Contains($"data-attachment=\"{WebUtility.HtmlEncode(part.FileName)}\"", StringComparison.Ordinal) ||
              html.Contains($"data-attachment=\"{part.FileName}\"", StringComparison.Ordinal);
        return outcome with { Status = found ? "Uploaded and found on the page" : "Uploaded, but NOT found on the page", Ok = found };
    }

    private static string Collapse(string text) => WhitespacePattern().Replace(text, " ").Trim();

    [GeneratedRegex(@"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex BodyPattern();

    [GeneratedRegex(@"<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<img\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
