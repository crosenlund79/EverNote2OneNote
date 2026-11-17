using EverNote2OneNote.Enex;
using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.OneNote;

public sealed record AttachmentOutcome(string FileName, string Type, long Size, string Placement, string Status, bool Ok);

/// <summary>Where a note will go, determined before anything is written.</summary>
public sealed class DestinationPlan
{
    public required string NotebookName { get; init; }

    /// <summary>ID of the matching notebook that is open in OneNote, if any.</summary>
    public string? NotebookId { get; init; }

    /// <summary>Location of the existing notebook, or the folder where a new one will be created.</summary>
    public string? NotebookLocation { get; init; }

    /// <summary>True if the notebook is open in OneNote, or was created earlier in this app's folder (it will be reopened).</summary>
    public bool NotebookExists { get; init; }

    public required string SectionName { get; init; }

    /// <summary>ID of the existing section with the same name, if any.</summary>
    public string? SectionId { get; init; }

    public int SectionPages { get; init; }

    /// <summary>Pages in the section that already have the note's title.</summary>
    public int PagesWithSameTitle { get; init; }

    /// <summary>Why the destination cannot be used, if it cannot.</summary>
    public string? Problem { get; init; }

    public bool SectionExists => SectionId is not null;

    /// <summary>Short status for the interface.</summary>
    public string Describe() => Problem ?? (NotebookExists
        ? $"Notebook exists; section \"{SectionName}\" {(SectionExists ? $"exists ({SectionPages} page(s))" : "will be created")}."
        : $"Notebook will be created; section \"{SectionName}\" will be created.");
}

public sealed class LocalMigrationResult
{
    public required string NoteTitle { get; init; }
    public string? NotebookName { get; set; }
    public string? NotebookLocation { get; set; }
    public bool NotebookExisted { get; set; }
    public string? Section { get; set; }
    public bool SectionExisted { get; set; }
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

    /// <summary>Whether an optional .onepkg backup was requested.</summary>
    public bool ExportRequested { get; set; }

    /// <summary>The verified .onepkg file, if the export succeeded.</summary>
    public string? ExportPath { get; set; }

    /// <summary>Why the optional export did not produce a valid package, if it did not.</summary>
    public string? ExportProblem { get; set; }

    public bool PageCreated => PageId is not null;
}

/// <summary>
/// Migrates one inspected note into a OneNote notebook through the OneNote desktop API: the chosen destination
/// notebook (existing or new), one section per Evernote notebook, one new page per note. Existing pages are never
/// changed. The page is read back and verified; optionally the notebook is exported as a .onepkg backup.
/// </summary>
public static class LocalMigrator
{
    public const string DefaultNotebookName = "Evernote Import";
    private const long MaxAttachmentBytes = 100L * 1024 * 1024;
    private static readonly TimeSpan DefaultExportTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Default location for new notebooks and exports: %LOCALAPPDATA%\EverNote2OneNote\OneNote (never synced by OneDrive).</summary>
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

    /// <summary>
    /// Finds the destination notebook and section (read-only): an open notebook with that name, or else a notebook this
    /// app created earlier in its folder, or else a new one; and a section named after the Evernote notebook.
    /// </summary>
    public static DestinationPlan PlanDestination(IOneNoteDesktop oneNote, string notebookName, NoteSummary note, string root,
        IReadOnlyList<string> oneDriveFolders)
    {
        var name = notebookName.Trim();
        var section = SectionName(note.File.Notebook);
        var problem = NotebookNameProblem(name);
        if (problem is not null)
        {
            return new DestinationPlan { NotebookName = name, SectionName = section, Problem = problem };
        }

        // New notebooks, exports and temporary files go under this app's folder; it must not be synced by OneDrive.
        var syncedBy = oneDriveFolders.FirstOrDefault(f => ReportWriter.IsInside(root, f));
        if (syncedBy is not null)
        {
            return new DestinationPlan
            {
                NotebookName = name, SectionName = section,
                Problem = $"This app's working folder ({root}) is inside the OneDrive folder {syncedBy}; nothing was created, to avoid uploading anything.",
            };
        }

        var matches = OneNoteHierarchy.Children(oneNote.GetHierarchyXml("", HierarchyScope.Notebooks), "Notebook")
            .Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(n.Nickname, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
        {
            return new DestinationPlan
            {
                NotebookName = name, SectionName = section,
                Problem = $"{matches.Count} open notebooks are named \"{name}\". Close or rename one of them, or choose another name.",
            };
        }
        if (matches.Count == 1)
        {
            var notebook = matches[0];
            var sectionItem = OneNoteHierarchy.Children(oneNote.GetHierarchyXml(notebook.Id, HierarchyScope.Sections), "Section")
                .FirstOrDefault(s => string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase));
            var pages = sectionItem is null ? [] : OneNoteHierarchy.Children(oneNote.GetHierarchyXml(sectionItem.Id, HierarchyScope.Pages), "Page");
            var title = string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : note.Title.Trim();
            return new DestinationPlan
            {
                NotebookName = notebook.Nickname ?? notebook.Name,
                NotebookId = notebook.Id,
                NotebookLocation = notebook.Path,
                NotebookExists = true,
                SectionName = sectionItem?.Name ?? section,
                SectionId = sectionItem?.Id,
                SectionPages = pages.Count,
                PagesWithSameTitle = pages.Count(p => string.Equals(p.Name.Trim(), title, StringComparison.OrdinalIgnoreCase)),
            };
        }

        // Not open in OneNote: a new local notebook in this app's folder (or the one created there earlier, reopened).
        var folder = Path.Combine(root, "Notebooks", name);
        return new DestinationPlan
        {
            NotebookName = name,
            NotebookLocation = folder,
            NotebookExists = Directory.Exists(folder),
            SectionName = section,
        };
    }

    public static LocalMigrationResult Migrate(NoteSummary summary, IOneNoteDesktop oneNote, DestinationPlan destination, string root,
        bool exportBackup, IProgress<string>? progress = null, TimeSpan? exportTimeout = null)
    {
        var result = new LocalMigrationResult
        {
            NoteTitle = summary.Title ?? "",
            NotebookName = destination.NotebookName,
            NotebookLocation = destination.NotebookLocation,
            NotebookExisted = destination.NotebookExists,
            Section = destination.SectionName,
            SectionExisted = destination.SectionExists,
            ExportRequested = exportBackup,
        };
        if (destination.Problem is not null)
        {
            result.Error = destination.Problem;
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

        var tempFolder = Path.Combine(root, "Temp", Guid.NewGuid().ToString("N"));
        try
        {
            // 2. Destination: the existing notebook and section, or open-or-create (never a duplicate of the same folder or section file).
            progress?.Report(destination.NotebookExists ? "Opening the destination notebook..." : "Creating the destination notebook...");
            string notebookId;
            if (destination.NotebookId is not null)
            {
                notebookId = destination.NotebookId;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination.NotebookLocation!)!);
                notebookId = oneNote.CreateNotebook(destination.NotebookLocation!);
            }
            var sectionId = destination.SectionId ?? oneNote.CreateSection(notebookId, destination.SectionName);
            var pageId = oneNote.CreatePage(sectionId);

            // 3. Write the new page. If OneNote rejects the full page, use a fresh page with plain text only.
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
                result.Method = "Full: text, lists, tables, links, images and attachments";
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

            // 5. Optional backup: export the notebook as a .onepkg file with OneNote's own publish function.
            if (exportBackup)
            {
                progress?.Report("Exporting a .onepkg backup of the notebook (optional)...");
                Export(oneNote, notebookId, result, root, exportTimeout ?? DefaultExportTimeout, progress);
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

    /// <summary>
    /// Publishes the notebook as a .onepkg to a new file and waits for OneNote to finish writing it: OneNote may return
    /// before the package exists. Never overwrites a file; reports exactly what happened.
    /// </summary>
    private static void Export(IOneNoteDesktop oneNote, string notebookId, LocalMigrationResult result, string root, TimeSpan timeout,
        IProgress<string>? progress)
    {
        var folder = Path.Combine(root, "Exports");
        Directory.CreateDirectory(folder);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var target = UniquePath(Path.Combine(folder, $"{Format.FileName(result.NotebookName ?? "Notebook")} {stamp}"), ".onepkg");
        var before = Directory.GetFiles(folder, "*.onepkg").ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            oneNote.SyncHierarchy(notebookId); // save pending changes first (best effort)
        }
        catch (OneNoteDesktopException ex)
        {
            result.Notices.Add($"OneNote could not save the notebook before exporting ({ex.Message}); the export was still attempted.");
        }
        try
        {
            oneNote.PublishNotebook(notebookId, target);
        }
        catch (OneNoteDesktopException ex)
        {
            result.ExportProblem = $"OneNote could not export the notebook to {target}: {ex.Message}";
            return;
        }

        progress?.Report("Waiting for OneNote to finish writing the .onepkg file...");
        var deadline = DateTime.UtcNow + timeout;
        long lastSize = -1;
        string? candidate = null;
        while (DateTime.UtcNow < deadline)
        {
            // The requested file, or else a new package OneNote wrote under a slightly different name.
            candidate = File.Exists(target)
                ? target
                : Directory.GetFiles(folder, "*.onepkg").Where(f => !before.Contains(f)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (candidate is not null)
            {
                long size = new FileInfo(candidate).Length;
                if (size > 0 && size == lastSize && CanRead(candidate))
                {
                    break; // finished: size unchanged since the last check and no longer locked by OneNote
                }
                lastSize = size;
            }
            Thread.Sleep(1000);
        }

        if (candidate is null)
        {
            result.ExportProblem = $"OneNote accepted the export request, but no .onepkg file appeared in {folder} within {timeout.TotalSeconds:0} seconds.";
            return;
        }
        var problem = CheckPackage(candidate);
        if (problem is not null)
        {
            result.ExportProblem = problem + $" ({candidate})";
            return;
        }
        result.ExportPath = candidate;
        if (!string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
        {
            result.Notices.Add($"OneNote saved the package as \"{Path.GetFileName(candidate)}\" instead of \"{Path.GetFileName(target)}\".");
        }
    }

    /// <summary>OneNote notebook names: 1 to 128 characters, without characters that are invalid in folder names.</summary>
    public static string? NotebookNameProblem(string name)
    {
        if (name.Length == 0)
        {
            return "Enter a name for the destination OneNote notebook.";
        }
        if (name.Length > 128)
        {
            return "The notebook name is longer than 128 characters.";
        }
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl) || name.EndsWith('.'))
        {
            return "The notebook name cannot contain \\ / : * ? \" < > | or end with a dot.";
        }
        return null;
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

    private static bool CanRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A .onepkg file is a Microsoft Cabinet archive; checks that the export starts like one.</summary>
    private static string? CheckPackage(string path)
    {
        var header = new byte[4];
        using (var stream = File.OpenRead(path))
        {
            if (stream.Read(header, 0, 4) < 4)
            {
                return "The .onepkg file is empty or incomplete.";
            }
        }
        return header is [(byte)'M', (byte)'S', (byte)'C', (byte)'F'] ? null : "The exported file is not a valid OneNote package (not a cabinet file).";
    }

    private static void TryDelete(IOneNoteDesktop oneNote, string pageId, LocalMigrationResult result)
    {
        try
        {
            oneNote.DeletePage(pageId);
        }
        catch (OneNoteDesktopException ex)
        {
            result.Notices.Add($"The rejected new page could not be removed ({ex.Message}).");
        }
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
