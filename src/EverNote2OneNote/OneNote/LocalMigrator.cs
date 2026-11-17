using EverNote2OneNote.Enex;
using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.OneNote;

public sealed record AttachmentOutcome(string FileName, string Type, long Size, string Placement, string Status, bool Ok);

public sealed class LocalMigrationResult
{
    public required string NoteTitle { get; init; }
    public string? NotebookName { get; set; }
    public string? NotebookFolder { get; set; }
    public string? Section { get; set; }
    public string? PageId { get; set; }
    public string? PageTitle { get; set; }

    /// <summary>How the page content was written.</summary>
    public string? Method { get; set; }

    /// <summary>Set if the migration stopped early.</summary>
    public string? Error { get; set; }

    public bool TitleMatches { get; set; }
    public TextVerification? Text { get; set; }
    public List<AttachmentOutcome> Attachments { get; } = [];
    public List<string> Notices { get; } = [];

    public string? ExportPath { get; set; }

    /// <summary>Null if the export succeeded and looks like a OneNote package; otherwise what went wrong.</summary>
    public string? ExportProblem { get; set; }

    public bool PageCreated => PageId is not null;
}

/// <summary>
/// Migrates one inspected note into a new, uniquely named local OneNote notebook through the OneNote desktop API,
/// reads the page back, verifies it, and exports the notebook as a .onepkg file.
/// </summary>
public static class LocalMigrator
{
    public const string NotebookPrefix = "EverNote2OneNote Test";
    private const long MaxAttachmentBytes = 100L * 1024 * 1024;

    /// <summary>Default location for test notebooks and exports: %LOCALAPPDATA%\EverNote2OneNote\OneNote (never synced by OneDrive).</summary>
    public static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EverNote2OneNote", "OneNote");

    /// <summary>Folders synced by OneDrive (from the OneDrive client's environment variables).</summary>
    public static IReadOnlyList<string> OneDriveFolders() =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static LocalMigrationResult Migrate(NoteSummary summary, IOneNoteDesktop oneNote, string root,
        IReadOnlyList<string> oneDriveFolders, IProgress<string>? progress = null)
    {
        var result = new LocalMigrationResult { NoteTitle = summary.Title ?? "" };

        // Never write into a OneDrive-synced folder: nothing may be uploaded automatically.
        var syncedBy = oneDriveFolders.FirstOrDefault(f => ReportWriter.IsInside(root, f));
        if (syncedBy is not null)
        {
            result.Error = $"The working folder {root} is inside the OneDrive folder {syncedBy}; nothing was created, to avoid uploading anything.";
            return result;
        }

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
            if (resource.Size > MaxAttachmentBytes)
            {
                unavailable[i] = $"Not transferred: {Format.Bytes(resource.Size)} is over this pilot's {Format.Bytes(MaxAttachmentBytes)} limit per file.";
                continue;
            }
            try
            {
                data[i] = EnexReader.ReadAttachmentData(summary.File.FullPath, summary.ByteOffset, i, resource.Md5, MaxAttachmentBytes);
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

        // 2. A new, uniquely named notebook folder and export file; nothing existing is reused or overwritten.
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        result.NotebookFolder = UniquePath(Path.Combine(root, "Notebooks", $"{NotebookPrefix} {stamp}"), "");
        result.NotebookName = Path.GetFileName(result.NotebookFolder);
        result.ExportPath = UniquePath(Path.Combine(root, "Exports", result.NotebookName), ".onepkg");
        Directory.CreateDirectory(Path.Combine(root, "Notebooks"));
        Directory.CreateDirectory(Path.Combine(root, "Exports"));
        var tempFolder = Path.Combine(root, "Temp", Guid.NewGuid().ToString("N"));

        try
        {
            progress?.Report("Creating the test notebook in OneNote...");
            var notebookId = oneNote.CreateNotebook(result.NotebookFolder);
            result.Section = SectionName(summary.File.Notebook);
            var sectionId = oneNote.CreateSection(notebookId, result.Section);
            var pageId = oneNote.CreatePage(sectionId);

            // 3. Write the page. If OneNote rejects the full page, use a fresh page with plain text only.
            progress?.Report("Writing the page...");
            string SaveFile(int index)
            {
                Directory.CreateDirectory(tempFolder);
                var name = note.Resources[index].FileName is { Length: > 0 } fn ? Format.FileName(fn, 100) : $"attachment-{index + 1}";
                var path = Path.Combine(tempFolder, $"{index + 1}-{name}");
                File.WriteAllBytes(path, data[index]);
                return path;
            }
            var page = PageXmlBuilder.Build(pageId, note, summary.File.Notebook, data, SaveFile, PageMode.Full, summary.Text);
            try
            {
                oneNote.UpdatePage(page.Xml);
                result.Method = "Full: text, lists, links, images and attachments";
            }
            catch (OneNoteDesktopException ex)
            {
                result.Notices.Add($"OneNote rejected the full page ({ex.Message}). It was written again as plain text only; attachments were not transferred.");
                TryDelete(oneNote, pageId, result);
                pageId = oneNote.CreatePage(sectionId);
                page = PageXmlBuilder.Build(pageId, note, summary.File.Notebook, data, SaveFile, PageMode.TextOnly, summary.Text);
                oneNote.UpdatePage(page.Xml);
                result.Method = "Text only (fallback)";
            }
            result.PageId = pageId;
            result.Notices.AddRange(page.Notices);

            // 4. Read the page back and verify title, text and attachments.
            progress?.Report("Reading the page back from OneNote...");
            var content = PageXmlBuilder.Read(oneNote.GetPageXml(pageId));
            result.PageTitle = content.Title;
            result.TitleMatches = Collapse(content.Title) == Collapse(page.Title);
            result.Text = TextVerifier.Compare(summary.Text, content.Text, page.Added);

            bool imagesPresent = content.Images >= page.Attachments.Count(a => a.IsImage);
            for (int i = 0; i < note.Resources.Count; i++)
            {
                var resource = note.Resources[i];
                var placed = page.Attachments.FirstOrDefault(a => a.ResourceIndex == i);
                if (placed is not null)
                {
                    bool found = placed.IsImage ? imagesPresent : content.Files.Contains(placed.FileName, StringComparer.OrdinalIgnoreCase);
                    result.Attachments.Add(new AttachmentOutcome(placed.FileName, placed.Mime, placed.Size, placed.Placement,
                        found ? "Transferred and found on the page" : "Sent, but NOT found on the page", found));
                }
                else
                {
                    var reason = unavailable.GetValueOrDefault(i) ??
                                 (result.Method!.StartsWith("Text only", StringComparison.Ordinal) ? "Not transferred: text-only fallback" : "Not transferred");
                    result.Attachments.Add(new AttachmentOutcome(resource.FileName ?? $"attachment-{i + 1}", resource.Mime ?? "", resource.Size, "-", reason, false));
                }
            }

            // 5. Export the notebook as a .onepkg file with OneNote's own publishing function.
            progress?.Report("Exporting the notebook as a .onepkg file...");
            try
            {
                oneNote.PublishNotebook(notebookId, result.ExportPath);
                result.ExportProblem = CheckPackage(result.ExportPath);
            }
            catch (OneNoteDesktopException ex)
            {
                result.ExportProblem = "OneNote could not export the notebook: " + ex.Message;
            }
        }
        catch (OneNoteDesktopException ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempFolder))
                {
                    Directory.Delete(tempFolder, recursive: true);
                }
            }
            catch (IOException)
            {
                // Temporary copies of attachments; a later run can remove them.
            }
        }
        return result;
    }

    /// <summary>OneNote section names: at most 50 characters, without characters that are invalid in names or file names.</summary>
    public static string SectionName(string notebook)
    {
        var chars = notebook.Select(c => "?*\\/:<>|&#\"'%~".Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.');
        if (name.Length > 50)
        {
            name = name[..50].TrimEnd();
        }
        return name.Length == 0 ? "Evernote" : name;
    }

    /// <summary>The path if nothing exists there yet, otherwise the first free "name (2)", "name (3)", ...</summary>
    internal static string UniquePath(string pathWithoutExtension, string extension)
    {
        var candidate = pathWithoutExtension + extension;
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = $"{pathWithoutExtension} ({n}){extension}";
        }
        return candidate;
    }

    /// <summary>A .onepkg file is a Microsoft Cabinet archive; checks that the export exists and starts like one.</summary>
    private static string? CheckPackage(string path)
    {
        if (!File.Exists(path))
        {
            return "OneNote reported success, but no .onepkg file was found.";
        }
        var header = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, 4) < 4)
            {
                return "The .onepkg file is empty.";
            }
        }
        return header is [(byte)'M', (byte)'S', (byte)'C', (byte)'F'] ? null : "The exported file does not look like a OneNote package (not a cabinet file).";
    }

    private static void TryDelete(IOneNoteDesktop oneNote, string pageId, LocalMigrationResult result)
    {
        try
        {
            oneNote.DeletePage(pageId);
        }
        catch (OneNoteDesktopException ex)
        {
            result.Notices.Add($"The rejected page could not be removed from the test notebook ({ex.Message}).");
        }
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
