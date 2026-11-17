using System.Xml.Linq;

namespace EverNote2OneNote.OneNote;

/// <summary>What GetHierarchy should return.</summary>
public enum HierarchyScope { Notebooks, Sections, Pages }

/// <summary>A notebook, section or page as listed by OneNote.</summary>
public sealed record HierarchyItem(string Id, string Name, string? Nickname, string? Path);

/// <summary>
/// The few OneNote desktop (COM) operations the migration uses. Implemented in the Windows app;
/// faked in tests. All IDs are OneNote object IDs.
/// </summary>
public interface IOneNoteDesktop
{
    /// <summary>Lists the hierarchy below a node ("" = all open notebooks) as OneNote XML (GetHierarchy).</summary>
    string GetHierarchyXml(string startNodeId, HierarchyScope scope);

    /// <summary>Opens the local notebook in the folder, or creates it there if it does not exist (OpenHierarchy, cftNotebook).</summary>
    string CreateNotebook(string folderPath);

    /// <summary>Opens the section with that name in the notebook, or creates it (OpenHierarchy, cftSection).</summary>
    string CreateSection(string notebookId, string sectionName);

    /// <summary>Creates an empty page (CreateNewPage).</summary>
    string CreatePage(string sectionId);

    /// <summary>Writes page content (UpdatePageContent). Only used on pages this migration created.</summary>
    void UpdatePage(string pageXml);

    /// <summary>Returns the page content as XML (GetPageContent).</summary>
    string GetPageXml(string pageId);

    /// <summary>Deletes a page this migration just created (DeleteHierarchy).</summary>
    void DeletePage(string pageId);

    /// <summary>Asks OneNote to save the notebook's pending changes (SyncHierarchy).</summary>
    void SyncHierarchy(string objectId);

    /// <summary>Starts exporting the notebook as a OneNote package (.onepkg) (Publish, pfOneNotePackage).</summary>
    void PublishNotebook(string notebookId, string targetPath);
}

/// <summary>A OneNote desktop operation failed.</summary>
public sealed class OneNoteDesktopException(string message, int errorCode = 0, Exception? inner = null) : Exception(message, inner)
{
    public int ErrorCode { get; } = errorCode;
}

public static class OneNoteHierarchy
{
    private static readonly XNamespace One = PageXmlBuilder.Namespace;

    /// <summary>The direct children of the hierarchy XML's root element with the given name (Notebook, Section or Page).</summary>
    public static List<HierarchyItem> Children(string hierarchyXml, string elementName)
    {
        var root = XDocument.Parse(hierarchyXml).Root!;
        return root.Elements(One + elementName)
            .Select(e => new HierarchyItem(
                (string?)e.Attribute("ID") ?? "",
                (string?)e.Attribute("name") ?? "",
                (string?)e.Attribute("nickname"),
                (string?)e.Attribute("path")))
            .Where(i => i.Id.Length > 0)
            .ToList();
    }
}
