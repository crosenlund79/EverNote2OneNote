using System.Globalization;
using System.Runtime.InteropServices;
using EverNote2OneNote.OneNote;
using Microsoft.Win32;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace EverNote2OneNote.App;

/// <summary>
/// The OneNote desktop automation API (COM, ProgID "OneNote.Application"), called through the typed IApplication
/// interface. Late-bound (by-name) calls are not used: on some PCs OneNote cannot resolve names because of a stale
/// type library registration, while typed calls work.
/// The interface is bound at run time from OneNote's own registered type library: each method is located by name,
/// and its exact parameter list is checked before it is called, so no method order or ID is assumed.
/// Everything stays on this PC: no cloud service and no sign-in.
/// </summary>
internal sealed class OneNoteCom : IOneNoteDesktop, IDisposable
{
    private const string ProgId = "OneNote.Application";

    /// <summary>The OneNote object library; used only if the class registration does not name its type library.</summary>
    private const string KnownTypeLib = "{0EA692EE-BB50-4E3C-AEF0-356D91732725}";

    // IApplication methods as vtable calls. Parameter kinds (checked against the type library before every call):
    // S = BSTR in, O = BSTR* out, I = 32-bit integer or enumeration, D = DATE, B = VARIANT_BOOL.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenHierarchyFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string path,
        [MarshalAs(UnmanagedType.BStr)] string relativeToObjectId, [MarshalAs(UnmanagedType.BStr)] out string objectId, int createFileType); // SSOI

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateNewPageFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string sectionId,
        [MarshalAs(UnmanagedType.BStr)] out string pageId, int newPageStyle); // SOI

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UpdatePageContentFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string pageXml,
        double expectedLastModified, int schema, short force); // SDIB

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UpdatePageContentNoForceFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string pageXml,
        double expectedLastModified, int schema); // SDI

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPageContentFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string pageId,
        [MarshalAs(UnmanagedType.BStr)] out string pageXml, int pageInfo, int schema); // SOII

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeleteHierarchyFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string objectId,
        double expectedLastModified, short deletePermanently); // SDB

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DeleteHierarchyNoFlagFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string objectId,
        double expectedLastModified); // SD

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PublishFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string hierarchyId,
        [MarshalAs(UnmanagedType.BStr)] string targetFilePath, int format, [MarshalAs(UnmanagedType.BStr)] string exporterClsid); // SSIS

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NavigateToFn(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string hierarchyObjectId,
        [MarshalAs(UnmanagedType.BStr)] string objectId, short newWindow); // SSB

    [DllImport("oleaut32.dll")]
    private static extern int LoadRegTypeLib(ref Guid libId, ushort major, ushort minor, uint lcid, out IntPtr typeLib);

    private readonly IntPtr _application; // typed IApplication pointer (holds OneNote alive)
    private readonly Dictionary<string, (int Slot, string Signature)> _methods;
    private readonly Dictionary<string, int> _constants;

    private OneNoteCom(IntPtr application, Dictionary<string, (int, string)> methods, Dictionary<string, int> constants)
    {
        _application = application;
        _methods = methods;
        _constants = constants;
    }

    /// <summary>Connects to (and if needed starts) the OneNote desktop application through its typed interface.</summary>
    /// <exception cref="OneNoteDesktopException">OneNote's automation API is not available or not usable.</exception>
    public static OneNoteCom Connect()
    {
        var type = Type.GetTypeFromProgID(ProgId);
        var clsid = Default($@"{ProgId}\CLSID");
        if (type is null || clsid is null || !Guid.TryParse(clsid, out var classId))
        {
            throw new OneNoteDesktopException(
                "The OneNote desktop automation API is not available on this PC (\"OneNote.Application\" is not registered). " +
                "It is provided by the OneNote desktop app from Microsoft 365 / Office, not by \"OneNote for Windows 10\". " +
                "Nothing was created.");
        }

        // Describe IApplication from OneNote's type library (read-only; nothing is registered).
        var (iid, methods, constants) = Describe(classId);

        object app;
        try
        {
            app = Activator.CreateInstance(type)!;
        }
        catch (COMException ex)
        {
            throw new OneNoteDesktopException(
                $"OneNote could not be started for automation (error 0x{ex.HResult:X8}: {ex.Message}). " +
                "Make sure the OneNote desktop app opens normally, and that this tool and OneNote both run as the same user " +
                "(not one of them \"as administrator\"). Nothing was created.", ex.HResult, ex);
        }

        var unknown = Marshal.GetIUnknownForObject(app);
        try
        {
            int hr = Marshal.QueryInterface(unknown, in iid, out var application);
            if (hr != 0)
            {
                throw new OneNoteDesktopException(
                    $"OneNote does not provide its typed automation interface to this app: {Message(hr)} (0x{hr:X8}). " +
                    "Click \"Check OneNote Connection\" for a read-only analysis. Nothing was created.", hr);
            }
            return new OneNoteCom(application, methods, constants);
        }
        finally
        {
            Marshal.Release(unknown);
            Marshal.FinalReleaseComObject(app);
        }
    }

    public string CreateNotebook(string folderPath)
    {
        Check("OpenHierarchy", Method<OpenHierarchyFn>("OpenHierarchy", "SSOI")(_application, folderPath, "", out var id, Constant("cftNotebook")));
        return id;
    }

    public string CreateSection(string notebookId, string sectionName)
    {
        Check("OpenHierarchy", Method<OpenHierarchyFn>("OpenHierarchy", "SSOI")(_application, sectionName + ".one", notebookId, out var id, Constant("cftSection")));
        return id;
    }

    public string CreatePage(string sectionId)
    {
        Check("CreateNewPage", Method<CreateNewPageFn>("CreateNewPage", "SOI")(_application, sectionId, out var id, Constant("npsDefault")));
        return id;
    }

    public void UpdatePage(string pageXml)
    {
        // A last-modified date of 0 means "do not check"; force = false.
        int hr = Signature("UpdatePageContent") == "SDI"
            ? Method<UpdatePageContentNoForceFn>("UpdatePageContent", "SDI")(_application, pageXml, 0, Constant("xsCurrent"))
            : Method<UpdatePageContentFn>("UpdatePageContent", "SDIB")(_application, pageXml, 0, Constant("xsCurrent"), 0);
        Check("UpdatePageContent", hr);
    }

    public string GetPageXml(string pageId)
    {
        Check("GetPageContent", Method<GetPageContentFn>("GetPageContent", "SOII")(_application, pageId, out var xml, Constant("piBasic"), Constant("xsCurrent")));
        return xml;
    }

    public void DeletePage(string pageId)
    {
        // Only used for a page this migration just created in its own new notebook; moves it to the notebook's recycle bin.
        int hr = Signature("DeleteHierarchy") == "SD"
            ? Method<DeleteHierarchyNoFlagFn>("DeleteHierarchy", "SD")(_application, pageId, 0)
            : Method<DeleteHierarchyFn>("DeleteHierarchy", "SDB")(_application, pageId, 0, 0);
        Check("DeleteHierarchy", hr);
    }

    public void PublishNotebook(string notebookId, string targetPath) =>
        Check("Publish", Method<PublishFn>("Publish", "SSIS")(_application, notebookId, targetPath, Constant("pfOneNotePackage"), ""));

    /// <summary>Shows the page in the OneNote window.</summary>
    public void ShowPage(string pageId) =>
        Check("NavigateTo", Method<NavigateToFn>("NavigateTo", "SSB")(_application, pageId, "", 0));

    public void Dispose() => Marshal.Release(_application);

    /// <summary>The vtable function for a method, after checking its parameter list against the expected one.</summary>
    private T Method<T>(string name, string expectedSignature) where T : Delegate
    {
        if (!_methods.TryGetValue(name, out var method))
        {
            throw new OneNoteDesktopException($"OneNote's type library has no method {name}; it was not called.");
        }
        if (method.Signature != expectedSignature)
        {
            throw new OneNoteDesktopException(
                $"{name} has an unexpected parameter list in OneNote's type library ({method.Signature}, expected {expectedSignature}); it was not called.");
        }
        var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(_application), method.Slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    private string Signature(string name) => _methods.TryGetValue(name, out var method) ? method.Signature : "";

    private int Constant(string name) => _constants.TryGetValue(name, out var value)
        ? value
        : throw new OneNoteDesktopException($"OneNote's type library does not define {name}; nothing was called.");

    private static void Check(string method, int hr)
    {
        if (hr != 0)
        {
            throw new OneNoteDesktopException($"{method} failed: {Message(hr)} (0x{hr:X8})", hr);
        }
    }

    /// <summary>
    /// Loads OneNote's registered type library (newest version that loads) and returns the IID of the application class's
    /// default interface, every method's vtable slot and parameter kinds, and the enumeration constants used here.
    /// </summary>
    private static (Guid Iid, Dictionary<string, (int, string)> Methods, Dictionary<string, int> Constants) Describe(Guid classId)
    {
        var typeLib = Default($@"CLSID\{classId:B}\TypeLib") ?? KnownTypeLib;
        using var versionsKey = Registry.ClassesRoot.OpenSubKey($@"TypeLib\{typeLib}");
        var versions = (versionsKey?.GetSubKeyNames() ?? [])
            .Select(v => v.Split('.'))
            .Where(p => p.Length == 2)
            .Select(p => (Ok: ushort.TryParse(p[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var major) &
                              ushort.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minor),
                          Major: major, Minor: minor))
            .Where(v => v.Ok)
            .OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor);

        if (Guid.TryParse(typeLib, out var libId))
        {
            foreach (var (_, major, minor) in versions)
            {
                if (LoadRegTypeLib(ref libId, major, minor, 0, out var libPtr) != 0)
                {
                    continue;
                }
                var lib = (ComTypes.ITypeLib)Marshal.GetObjectForIUnknown(libPtr);
                Marshal.Release(libPtr);
                try
                {
                    var described = DescribeInterface(lib, classId);
                    if (described is not null)
                    {
                        return described.Value;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(lib);
                }
            }
        }
        throw new OneNoteDesktopException(
            "OneNote's type library could not be loaded or does not describe its automation interface. " +
            "Click \"Check OneNote Connection\" for a read-only analysis. Nothing was created.");
    }

    private static (Guid, Dictionary<string, (int, string)>, Dictionary<string, int>)? DescribeInterface(ComTypes.ITypeLib lib, Guid classId)
    {
        // The default (non-event) interface of OneNote's application class.
        ComTypes.ITypeInfo? itf = null;
        for (int i = 0; i < lib.GetTypeInfoCount() && itf is null; i++)
        {
            lib.GetTypeInfoType(i, out var kind);
            if (kind != ComTypes.TYPEKIND.TKIND_COCLASS)
            {
                continue;
            }
            lib.GetTypeInfo(i, out var coclass);
            var attr = Attr(coclass);
            if (attr.guid != classId)
            {
                continue;
            }
            for (int j = 0; j < attr.cImplTypes; j++)
            {
                coclass.GetImplTypeFlags(j, out var flags);
                if (flags.HasFlag(ComTypes.IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) && !flags.HasFlag(ComTypes.IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE))
                {
                    coclass.GetRefTypeOfImplType(j, out var href);
                    coclass.GetRefTypeInfo(href, out itf);
                    break;
                }
            }
        }
        if (itf is null)
        {
            return null;
        }

        var itfAttr = Attr(itf);
        var vtable = itf;
        if (itfAttr.typekind == ComTypes.TYPEKIND.TKIND_DISPATCH)
        {
            if (!itfAttr.wTypeFlags.HasFlag(ComTypes.TYPEFLAGS.TYPEFLAG_FDUAL))
            {
                return null; // dispatch-only: no typed calls possible
            }
            itf.GetRefTypeOfImplType(-1, out var vtHref);
            itf.GetRefTypeInfo(vtHref, out vtable);
        }

        var methods = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        var vtAttr = Attr(vtable);
        for (int k = 0; k < vtAttr.cFuncs; k++)
        {
            vtable.GetFuncDesc(k, out var pfd);
            try
            {
                var fd = Marshal.PtrToStructure<ComTypes.FUNCDESC>(pfd);
                if (fd.invkind != ComTypes.INVOKEKIND.INVOKE_FUNC || (VarEnum)fd.elemdescFunc.tdesc.vt != VarEnum.VT_HRESULT)
                {
                    continue;
                }
                var names = new string[1];
                vtable.GetNames(fd.memid, names, 1, out _);
                var size = Marshal.SizeOf<ComTypes.ELEMDESC>();
                var signature = string.Concat(Enumerable.Range(0, fd.cParams)
                    .Select(p => Kind(Marshal.PtrToStructure<ComTypes.ELEMDESC>(fd.lprgelemdescParam + p * size).tdesc, vtable)));
                methods[names[0]] = (fd.oVft / IntPtr.Size, signature);
            }
            finally
            {
                vtable.ReleaseFuncDesc(pfd);
            }
        }

        var constants = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < lib.GetTypeInfoCount(); i++)
        {
            lib.GetTypeInfoType(i, out var kind);
            if (kind != ComTypes.TYPEKIND.TKIND_ENUM)
            {
                continue;
            }
            lib.GetTypeInfo(i, out var info);
            var attr = Attr(info);
            for (int v = 0; v < attr.cVars; v++)
            {
                info.GetVarDesc(v, out var pvd);
                try
                {
                    var vd = Marshal.PtrToStructure<ComTypes.VARDESC>(pvd);
                    if (vd.varkind != ComTypes.VARKIND.VAR_CONST)
                    {
                        continue;
                    }
                    var names = new string[1];
                    info.GetNames(vd.memid, names, 1, out _);
                    constants[names[0]] = Convert.ToInt32(Marshal.GetObjectForNativeVariant(vd.desc.lpvarValue), CultureInfo.InvariantCulture);
                }
                finally
                {
                    info.ReleaseVarDesc(pvd);
                }
            }
        }
        return (itfAttr.guid, methods, constants);
    }

    /// <summary>S = BSTR, O = BSTR* (out), I = 32-bit integer or enumeration, D = DATE, B = VARIANT_BOOL, ? = anything else.</summary>
    private static char Kind(ComTypes.TYPEDESC type, ComTypes.ITypeInfo context)
    {
        switch ((VarEnum)type.vt)
        {
            case VarEnum.VT_BSTR:
                return 'S';
            case VarEnum.VT_PTR:
                return (VarEnum)Marshal.PtrToStructure<ComTypes.TYPEDESC>(type.lpValue).vt == VarEnum.VT_BSTR ? 'O' : '?';
            case VarEnum.VT_I4 or VarEnum.VT_INT:
                return 'I';
            case VarEnum.VT_DATE:
                return 'D';
            case VarEnum.VT_BOOL:
                return 'B';
            case VarEnum.VT_USERDEFINED:
                context.GetRefTypeInfo(unchecked((int)type.lpValue.ToInt64()), out var referenced);
                var attr = Attr(referenced);
                return attr.typekind == ComTypes.TYPEKIND.TKIND_ENUM ||
                       (attr.typekind == ComTypes.TYPEKIND.TKIND_ALIAS && (VarEnum)attr.tdescAlias.vt is VarEnum.VT_I4 or VarEnum.VT_INT)
                    ? 'I'
                    : '?';
            default:
                return '?';
        }
    }

    private static ComTypes.TYPEATTR Attr(ComTypes.ITypeInfo info)
    {
        info.GetTypeAttr(out var p);
        try
        {
            return Marshal.PtrToStructure<ComTypes.TYPEATTR>(p);
        }
        finally
        {
            info.ReleaseTypeAttr(p);
        }
    }

    private static string? Default(string path)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    /// <summary>Names of the OneNote API errors most likely to occur here.</summary>
    private static string Message(int hresult) => unchecked((uint)hresult) switch
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
        0x8002801D => "type library not registered (TYPE_E_LIBNOTREGISTERED); click \"Check OneNote Connection\" for a read-only analysis",
        0x80004002 => "interface not supported (E_NOINTERFACE)",
        0x80080005 => "OneNote did not start for automation (if this app or OneNote runs \"as administrator\", run both normally)",
        0x80010108 or 0x800706BA or 0x800706BE => "the connection to OneNote was lost (was OneNote closed?)",
        _ => "OneNote reported an error",
    };
}
