namespace EverNote2OneNote.OneNote;

/// <summary>
/// The few OneNote desktop (COM) operations the migration uses. Implemented in the Windows app;
/// faked in tests. All IDs are OneNote object IDs.
/// </summary>
public interface IOneNoteDesktop
{
    /// <summary>Creates and opens a new local notebook in an empty folder (OpenHierarchy, cftNotebook).</summary>
    string CreateNotebook(string folderPath);

    /// <summary>Creates a section in the notebook (OpenHierarchy, cftSection).</summary>
    string CreateSection(string notebookId, string sectionName);

    /// <summary>Creates an empty page (CreateNewPage).</summary>
    string CreatePage(string sectionId);

    /// <summary>Writes page content (UpdatePageContent).</summary>
    void UpdatePage(string pageXml);

    /// <summary>Returns the page content as XML (GetPageContent).</summary>
    string GetPageXml(string pageId);

    /// <summary>Deletes a page this migration created (DeleteHierarchy).</summary>
    void DeletePage(string pageId);

    /// <summary>Exports the notebook as a OneNote package (.onepkg) (Publish, pfOneNotePackage).</summary>
    void PublishNotebook(string notebookId, string targetPath);
}

/// <summary>A OneNote desktop operation failed.</summary>
public sealed class OneNoteDesktopException(string message, int errorCode = 0, Exception? inner = null) : Exception(message, inner)
{
    public int ErrorCode { get; } = errorCode;
}
