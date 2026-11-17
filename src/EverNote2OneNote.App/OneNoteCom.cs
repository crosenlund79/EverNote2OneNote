using System.Reflection;
using System.Runtime.InteropServices;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.App;

/// <summary>
/// The OneNote desktop automation API (COM, ProgID "OneNote.Application"), called by late binding so no Office
/// interop assembly is needed. Everything stays on this PC: no cloud service and no sign-in.
/// </summary>
internal sealed class OneNoteCom : IOneNoteDesktop, IDisposable
{
    // Values of the OneNote API enumerations used here.
    private const int CreateFileTypeNotebook = 1; // cftNotebook
    private const int CreateFileTypeSection = 3;  // cftSection
    private const int PublishFormatPackage = 1;   // pfOneNotePackage (.onepkg)

    private readonly object _app;

    private OneNoteCom(object app) => _app = app;

    /// <summary>Connects to (and if needed starts) the OneNote desktop application.</summary>
    /// <exception cref="OneNoteDesktopException">OneNote's automation API is not available.</exception>
    public static OneNoteCom Connect()
    {
        var type = Type.GetTypeFromProgID("OneNote.Application");
        if (type is null)
        {
            throw new OneNoteDesktopException(
                "The OneNote desktop automation API is not available on this PC (\"OneNote.Application\" is not registered). " +
                "It is provided by the OneNote desktop app from Microsoft 365 / Office, not by \"OneNote for Windows 10\". " +
                "Nothing was created.");
        }
        try
        {
            return new OneNoteCom(Activator.CreateInstance(type)!);
        }
        catch (COMException ex)
        {
            throw new OneNoteDesktopException(
                $"OneNote could not be started for automation (error 0x{ex.HResult:X8}: {ex.Message}). " +
                "Make sure the OneNote desktop app opens normally, and that this tool and OneNote both run as the same user " +
                "(not one of them \"as administrator\"). Nothing was created.", ex.HResult, ex);
        }
    }

    public string CreateNotebook(string folderPath) =>
        (string)Invoke("OpenHierarchy", [folderPath, "", "", CreateFileTypeNotebook], outIndex: 2)!;

    public string CreateSection(string notebookId, string sectionName) =>
        (string)Invoke("OpenHierarchy", [sectionName + ".one", notebookId, "", CreateFileTypeSection], outIndex: 2)!;

    public string CreatePage(string sectionId) => (string)Invoke("CreateNewPage", [sectionId, ""], outIndex: 1)!;

    public void UpdatePage(string pageXml) => Invoke("UpdatePageContent", [pageXml]);

    public string GetPageXml(string pageId) => (string)Invoke("GetPageContent", [pageId, ""], outIndex: 1)!;

    public void DeletePage(string pageId) => Invoke("DeleteHierarchy", [pageId]);

    public void PublishNotebook(string notebookId, string targetPath) => Invoke("Publish", [notebookId, targetPath, PublishFormatPackage]);

    /// <summary>Shows the page in the OneNote window.</summary>
    public void ShowPage(string pageId) => Invoke("NavigateTo", [pageId]);

    public void Dispose() => Marshal.FinalReleaseComObject(_app);

    /// <summary>Calls an IApplication method through IDispatch; optional trailing parameters use OneNote's defaults.</summary>
    private object? Invoke(string method, object?[] args, int outIndex = -1)
    {
        var modifiers = new ParameterModifier(args.Length);
        if (outIndex >= 0)
        {
            modifiers[outIndex] = true;
        }
        try
        {
            _app.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, _app, args, [modifiers], null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException com)
        {
            throw Failure(method, com);
        }
        catch (COMException com)
        {
            throw Failure(method, com);
        }
        return outIndex >= 0 ? args[outIndex] : null;
    }

    private static OneNoteDesktopException Failure(string method, COMException com) =>
        new($"{method} failed: {Describe(com.HResult)} (0x{com.HResult:X8})", com.HResult, com);

    /// <summary>Names of the OneNote API errors most likely to occur here.</summary>
    private static string Describe(int hresult) => unchecked((uint)hresult) switch
    {
        0x80042000 => "the page XML is not well-formed",
        0x80042001 => "the page XML is not valid for OneNote",
        0x80042002 => "the section could not be created",
        0x80042007 => "an image could not be inserted",
        0x80042016 => "a file could not be inserted",
        0x80042017 => "the name is not valid",
        0x80042018 => "the folder does not exist",
        0x8004201A => "the file already exists",
        0x8004201C => "this action is disabled by policy",
        0x8002801D => "OneNote could not load its type library (TYPE_E_LIBNOTREGISTERED). " +
                      "Click \"Check OneNote Connection\" for a read-only analysis and send the report",
        0x80080005 => "OneNote did not start for automation (if this app or OneNote runs \"as administrator\", run both normally)",
        _ => "OneNote reported an error",
    };
}
