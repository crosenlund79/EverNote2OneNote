using System.Text;
using System.Xml.Linq;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.Tests;

/// <summary>Stands in for the OneNote desktop API: an in-memory hierarchy; page XML is stored and returned like OneNote does.</summary>
internal sealed class FakeOneNote : IOneNoteDesktop
{
    private static readonly XNamespace One = PageXmlBuilder.Namespace;
    private readonly List<(string Id, string Name, string? Path, List<(string Id, string Name, List<(string Id, string Title)> Pages)> Sections)> _notebooks = [];
    private readonly Dictionary<string, string> _pages = [];
    private int _pageCount;

    public bool RejectRichContent { get; init; }
    public bool RejectTables { get; init; }
    public bool PublishWritesNothing { get; init; }

    /// <summary>Simulates OneNote closing: creating anything after this many pages fails with RPC_E_DISCONNECTED.</summary>
    public int? DisconnectAfterPages { get; init; }

    /// <summary>Every page creation fails with an ordinary (not connection) error.</summary>
    public bool FailPageCreation { get; init; }

    /// <summary>Called after each page is written (e.g. to stop a run).</summary>
    public Action<int>? PageWritten { get; init; }

    public List<string> Calls { get; } = [];
    public List<string> Deleted { get; } = [];

    /// <summary>Created notebooks (ID, folder) and sections (ID, notebook ID, name).</summary>
    public List<(string Id, string Folder)> CreatedNotebooks { get; } = [];
    public List<(string Id, string NotebookId, string Name)> CreatedSections { get; } = [];

    /// <summary>Pages created by the migration, with their section.</summary>
    public List<(string SectionId, string PageId)> CreatedPages { get; } = [];
    public List<string> Published { get; } = [];

    public void AddNotebook(string id, string name, string section, params string[] pageTitles) =>
        _notebooks.Add((id, name, null, [($"{id}-s", section, pageTitles.Select((t, i) => ($"existing-{i + 1}", t)).ToList())]));

    public void AddNotebookAt(string id, string name, string path) => _notebooks.Add((id, name, path, []));

    /// <summary>IDs of pre-existing pages that were never written.</summary>
    public List<string> UntouchedPages() => _notebooks.SelectMany(n => n.Sections).SelectMany(s => s.Pages)
        .Where(p => p.Id.StartsWith("existing", StringComparison.Ordinal) && !_pages.ContainsKey(p.Id)).Select(p => p.Id).ToList();

    private void CheckConnection()
    {
        if (_pageCount >= DisconnectAfterPages)
        {
            throw new OneNoteDesktopException("the connection to OneNote was lost (0x80010108)", unchecked((int)0x80010108));
        }
    }

    public string GetHierarchyXml(string startNodeId, HierarchyScope scope) => scope switch
    {
        HierarchyScope.Notebooks => new XElement(One + "Notebooks",
            _notebooks.Select(n => new XElement(One + "Notebook", new XAttribute("ID", n.Id), new XAttribute("name", n.Name),
                n.Path is null ? null : new XAttribute("path", n.Path)))).ToString(),
        HierarchyScope.Sections => new XElement(One + "Notebook",
            _notebooks.Single(n => n.Id == startNodeId).Sections.Select(s => new XElement(One + "Section", new XAttribute("ID", s.Id), new XAttribute("name", s.Name)))).ToString(),
        _ => new XElement(One + "Section",
            _notebooks.SelectMany(n => n.Sections).Single(s => s.Id == startNodeId).Pages.Select(p => new XElement(One + "Page", new XAttribute("ID", p.Id), new XAttribute("name", p.Title)))).ToString(),
    };

    public string CreateNotebook(string folderPath)
    {
        CheckConnection();
        Calls.Add("CreateNotebook");
        Directory.CreateDirectory(folderPath);
        var id = "nb" + (CreatedNotebooks.Count + 1);
        CreatedNotebooks.Add((id, folderPath));
        return id;
    }

    public string CreateSection(string notebookId, string sectionName)
    {
        CheckConnection();
        Calls.Add("CreateSection:" + sectionName);
        Assert.Contains(CreatedNotebooks, n => n.Id == notebookId); // sections only in notebooks the migration created
        var id = "s" + (CreatedSections.Count + 1);
        CreatedSections.Add((id, notebookId, sectionName));
        return id;
    }

    public string CreatePage(string sectionId)
    {
        CheckConnection();
        if (FailPageCreation)
        {
            throw new OneNoteDesktopException("CreateNewPage failed (0x80042010)", unchecked((int)0x80042010));
        }
        var id = "p" + ++_pageCount;
        _pages[id] = $"<one:Page xmlns:one=\"{PageXmlBuilder.Namespace}\" ID=\"{id}\" />";
        CreatedPages.Add((sectionId, id));
        return id;
    }

    public void UpdatePage(string pageXml)
    {
        if ((RejectRichContent && (pageXml.Contains("one:Image") || pageXml.Contains("one:InsertedFile") || pageXml.Contains("one:Table"))) ||
            (RejectTables && pageXml.Contains("one:Table")))
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
        PageWritten?.Invoke(_pageCount);
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
        Published.Add(notebookId);
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
