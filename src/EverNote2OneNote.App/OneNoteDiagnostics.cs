using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace EverNote2OneNote.App;

/// <summary>
/// Read-only check of the OneNote desktop automation (COM) setup. It only reads the registry, loads type libraries
/// without registering them, and makes read-only OneNote calls (list of open notebooks), late-bound and typed.
/// It never changes the registry, OneNote or any notebook.
/// </summary>
internal static class OneNoteDiagnostics
{
    private const string ProgId = "OneNote.Application";

    /// <summary>The OneNote object library; used only if the class registration does not name its type library.</summary>
    private const string KnownTypeLib = "{0EA692EE-BB50-4E3C-AEF0-356D91732725}";

    private const int TypeLibNotRegistered = unchecked((int)0x8002801D);
    private const uint UserDefaultLocale = 0x400;

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchProbe
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
            uint count, uint lcid, [Out, MarshalAs(UnmanagedType.LPArray)] int[] dispIds);
    }

    /// <summary>IApplication::GetHierarchy as a vtable call (signature checked against the type library before use).</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetHierarchyCall(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string startNodeId, int scope,
        [MarshalAs(UnmanagedType.BStr)] out string xml, int schema);

    [DllImport("oleaut32.dll")]
    private static extern int LoadRegTypeLib(ref Guid libId, ushort major, ushort minor, uint lcid, out IntPtr typeLib);

    /// <summary>What the working type library says about the interface OneNote's application object implements.</summary>
    private sealed record TypedInterface(Guid Iid, string Name, string Version, int GetHierarchySlot, int NotebooksScope, int Schema);

    public static string Run()
    {
        var r = new Report();
        r.Info($"This app: {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} on Windows {Environment.OSVersion.Version}");
        using (var c2r = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration"))
        {
            if (c2r is not null)
            {
                r.Info($"Office (Click-to-Run): {c2r.GetValue("Platform")} version {c2r.GetValue("VersionToReport")}");
            }
        }

        // 1. Class registration.
        var clsid = Default(Registry.ClassesRoot, $@"{ProgId}\CLSID");
        if (clsid is null)
        {
            r.Fail($"\"{ProgId}\" is not registered: the OneNote desktop automation API is not installed for this user.");
            return r.Conclude("The OneNote desktop app (Microsoft 365 / Office) is not installed or not registered for automation.");
        }
        r.Ok($"{ProgId} is registered (class {clsid}, current version {Default(Registry.ClassesRoot, $@"{ProgId}\CurVer") ?? "-"})");

        bool serverFound = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            var server = Default(root, $@"CLSID\{clsid}\LocalServer32");
            if (server is not null)
            {
                var exe = server.Trim().Trim('"').Split('"')[0].Split(" /", 2)[0].Trim();
                bool exists = File.Exists(exe);
                serverFound |= exists;
                r.Line(exists ? "OK  " : "FAIL", $"OneNote program ({ViewName(view)} registry): {exe}{(exists ? "" : "  (FILE MISSING)")}");
            }
        }
        if (!serverFound)
        {
            r.Fail("No existing OneNote program is registered for automation.");
        }

        // 2. Type library registrations: every version, its files, and where each entry is stored.
        var typeLib = Default(Registry.ClassesRoot, $@"CLSID\{clsid}\TypeLib") ?? KnownTypeLib;
        var versions = new List<(ushort Major, ushort Minor, string Name)>();
        var incomplete = new List<string>();
        ListTypeLib(r, typeLib, versions, incomplete);
        foreach (var (_, _, name) in versions)
        {
            DescribeStorage(r, typeLib, name);
        }
        using (var office = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Office\ClickToRun\REGISTRY\MACHINE\Software\Classes\TypeLib\{typeLib}"))
        {
            if (office is not null)
            {
                r.Info($"Office's own (Click-to-Run) registry has type library version(s): {string.Join(", ", office.GetSubKeyNames())}");
            }
        }

        // 3. Which versions load in this app; inspect the typed interface in the newest one that loads.
        TypedInterface? typed = null;
        if (Guid.TryParse(typeLib, out var libId) && Guid.TryParse(clsid, out var classId))
        {
            foreach (var (major, minor, name) in versions.OrderByDescending(v => v.Major).ThenByDescending(v => v.Minor))
            {
                int hr = LoadRegTypeLib(ref libId, major, minor, 0, out var libPtr);
                if (hr != 0)
                {
                    r.Fail($"Type library {name} does not load in this app: {Hr(hr)}");
                    continue;
                }
                var lib = (ComTypes.ITypeLib)Marshal.GetObjectForIUnknown(libPtr);
                Marshal.Release(libPtr);
                r.Ok($"Type library {name} loads in this app");
                if (typed is null)
                {
                    typed = InspectTypedInterface(r, lib, classId, name);
                }
                Marshal.ReleaseComObject(lib);
            }
        }
        else
        {
            r.Fail($"Invalid class or type library ID in the registry: {clsid} / {typeLib}");
        }
        if (typed is not null)
        {
            DescribeInterfaceRegistration(r, typed);
        }

        // 4. Live, read-only checks against the running OneNote.
        object? app = null;
        var errors = new List<int>();
        bool lateBoundWorks = false, typedWorks = false;
        int typedQueryResult = 0;
        try
        {
            try
            {
                app = Activator.CreateInstance(Type.GetTypeFromProgID(ProgId)!);
                r.Ok("OneNote started (or was already running) for automation");
            }
            catch (COMException ex)
            {
                r.Fail($"OneNote could not be started for automation: {Hr(ex.HResult)}");
                return r.Conclude("OneNote's automation server did not start. Make sure OneNote opens normally, and that this app and " +
                                  "OneNote run as the same user (neither \"as administrator\").");
            }

            // 4a. Late-bound path (what the migration uses today): OneNote resolves names from its own type library.
            var dispatch = (IDispatchProbe)app!;
            int hrCount = dispatch.GetTypeInfoCount(out var count);
            r.Line(hrCount == 0 ? "OK  " : "FAIL", hrCount == 0 ? $"OneNote reports {count} type description(s)" : $"OneNote type description count: {Hr(hrCount)}");
            int hrInfo = dispatch.GetTypeInfo(0, UserDefaultLocale, out var info);
            if (hrInfo == 0)
            {
                Marshal.Release(info);
                r.Ok("OneNote can load its own type library (for late-bound calls)");
            }
            else
            {
                errors.Add(hrInfo);
                r.Fail($"OneNote cannot load its own type library (for late-bound calls): {Hr(hrInfo)}");
            }
            var none = Guid.Empty;
            var ids = new int[1];
            int hrNames = dispatch.GetIDsOfNames(ref none, ["GetHierarchy"], 1, UserDefaultLocale, ids);
            if (hrNames == 0)
            {
                r.Ok($"OneNote resolves method names (GetHierarchy = {ids[0]})");
            }
            else
            {
                errors.Add(hrNames);
                r.Fail($"OneNote cannot resolve method names (needed for late-bound calls): {Hr(hrNames)}");
            }
            try
            {
                object?[] args = ["", 2, ""]; // hsNotebooks
                var modifiers = new ParameterModifier(3);
                modifiers[2] = true;
                app!.GetType().InvokeMember("GetHierarchy", BindingFlags.InvokeMethod, null, app, args, [modifiers], null, null);
                r.Ok($"Late-bound read-only call works: OneNote lists {CountNotebooks((string)args[2]!)} open notebook(s)");
                lateBoundWorks = true;
            }
            catch (Exception ex) when ((ex as TargetInvocationException)?.InnerException is COMException || ex is COMException)
            {
                var com = (ex as COMException) ?? (COMException)ex.InnerException!;
                errors.Add(com.HResult);
                r.Fail($"Late-bound read-only call GetHierarchy failed: {Hr(com.HResult)}");
            }

            // 4b. Typed (vtable) path: no name lookup in OneNote; calls are marshaled using the interface's registered type library.
            if (typed is not null)
            {
                (typedWorks, typedQueryResult) = TryTypedCall(r, app!, typed);
            }
        }
        finally
        {
            if (app is not null)
            {
                Marshal.FinalReleaseComObject(app);
            }
        }

        // 5. Conclusion, from the evidence above.
        if (lateBoundWorks)
        {
            return r.Conclude("The OneNote connection works. If a migration still fails, send this report together with the error message.");
        }
        if (typedWorks)
        {
            return r.Conclude(
                $"Late-bound calls fail, but typed calls through {typed!.Name} work on this PC (type library {typed.Version}). " +
                "This can be fixed in this app alone, by calling OneNote through the typed interface instead of by name. " +
                "No change to Office or the registry is needed. Nothing has been changed.");
        }
        var evidence = new StringBuilder("Neither late-bound nor typed calls work on this PC. Evidence:\n");
        if (incomplete.Count > 0)
        {
            evidence.Append($"  - Type library entry/entries {string.Join(", ", incomplete)} have no registered file (incomplete), see above.\n");
        }
        if (errors.Contains(TypeLibNotRegistered))
        {
            evidence.Append("  - OneNote's own type library lookup fails with TYPE_E_LIBNOTREGISTERED.\n");
        }
        if (typedQueryResult != 0)
        {
            evidence.Append($"  - OneNote cannot provide the typed interface: {Hr(typedQueryResult)}.\n");
        }
        evidence.Append("Nothing has been changed. Send this report before deciding on any fix.");
        return r.Conclude(evidence.ToString());
    }

    /// <summary>Lists the registered versions, checking each file (a trailing \N is a resource number inside the file).</summary>
    private static void ListTypeLib(Report r, string typeLib, List<(ushort, ushort, string)> versions, List<string> incomplete)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"TypeLib\{typeLib}");
        if (key is null)
        {
            r.Fail($@"Type library is not registered: HKEY_CLASSES_ROOT\TypeLib\{typeLib}");
            return;
        }
        foreach (var version in key.GetSubKeyNames())
        {
            var parts = version.Split('.');
            if (parts.Length == 2 &&
                ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var major) &&
                ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minor))
            {
                versions.Add((major, minor, version));
            }
            bool anyFile = false;
            foreach (var platform in new[] { "win32", "win64" })
            {
                var file = Default(key, $@"{version}\0\{platform}");
                if (file is null)
                {
                    continue;
                }
                anyFile = true;
                var (exists, note) = CheckTypeLibFile(file);
                r.Line(exists ? "OK  " : "FAIL", $"Type library {version} file for {platform}: {file}{note}");
            }
            if (!anyFile)
            {
                incomplete.Add(version);
                r.Warn($@"Type library {version} has no registered file (no 0\win32 or 0\win64 entry): the entry is incomplete");
            }
        }
    }

    /// <summary>A type library path is either a file, or a file followed by \N (resource N inside that file).</summary>
    private static (bool Exists, string Note) CheckTypeLibFile(string path)
    {
        var file = path.Trim().Trim('"');
        if (File.Exists(file))
        {
            return (true, "");
        }
        var resource = Regex.Match(file, @"^(.*)\\(\d+)$");
        if (resource.Success && File.Exists(resource.Groups[1].Value))
        {
            return (true, $"  (type library resource {resource.Groups[2].Value} inside the file; the file exists)");
        }
        return (false, "  (BROKEN: file missing)");
    }

    /// <summary>Shows where a version entry is stored (machine 64-bit view, machine 32-bit view, current user) and its full contents.</summary>
    private static void DescribeStorage(Report r, string typeLib, string version)
    {
        var locations = new (string Name, RegistryHive Hive, RegistryView View, string Path)[]
        {
            ("HKLM\\SOFTWARE\\Classes (64-bit view)", RegistryHive.LocalMachine, RegistryView.Registry64, $@"SOFTWARE\Classes\TypeLib\{typeLib}\{version}"),
            ("HKLM\\SOFTWARE\\Classes (32-bit view, WOW6432Node)", RegistryHive.LocalMachine, RegistryView.Registry32, $@"SOFTWARE\Classes\TypeLib\{typeLib}\{version}"),
            ("HKCU\\Software\\Classes", RegistryHive.CurrentUser, RegistryView.Default, $@"Software\Classes\TypeLib\{typeLib}\{version}"),
        };
        bool found = false;
        foreach (var (name, hive, view, path) in locations)
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                continue;
            }
            found = true;
            var contents = new StringBuilder();
            Dump(key, "", contents, depth: 0);
            r.Info($"Type library {version} entry is stored in {name}:\n{contents.ToString().TrimEnd()}");
        }
        if (!found)
        {
            r.Info($"Type library {version} entry was not found in HKLM (64/32-bit) or HKCU; it may come from another source (e.g. Office's virtual registry).");
        }
    }

    private static void Dump(RegistryKey key, string prefix, StringBuilder output, int depth)
    {
        foreach (var valueName in key.GetValueNames().OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
        {
            output.Append("         ").Append(prefix).Append(valueName.Length == 0 ? "(Default)" : valueName)
                  .Append(" = ").Append(key.GetValue(valueName)).Append('\n');
        }
        if (depth >= 4)
        {
            return;
        }
        foreach (var subKeyName in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(subKeyName);
            if (sub is null)
            {
                continue;
            }
            output.Append("         ").Append(prefix).Append(subKeyName).Append("\\\n");
            Dump(sub, prefix + subKeyName + "\\", output, depth + 1);
        }
    }

    /// <summary>
    /// Finds the default interface of OneNote's application class in the type library, lists its methods in vtable order,
    /// and verifies the GetHierarchy signature so it can be called safely as a typed call.
    /// </summary>
    private static TypedInterface? InspectTypedInterface(Report r, ComTypes.ITypeLib lib, Guid classId, string version)
    {
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
            r.Fail($"Type library {version} does not describe OneNote's application class {classId:B}.");
            return null;
        }

        itf.GetDocumentation(-1, out var name, out _, out _, out _);
        var itfAttr = Attr(itf);
        bool dual = itfAttr.wTypeFlags.HasFlag(ComTypes.TYPEFLAGS.TYPEFLAG_FDUAL);
        var vtable = itf;
        if (itfAttr.typekind == ComTypes.TYPEKIND.TKIND_DISPATCH)
        {
            if (!dual)
            {
                r.Fail($"{name} ({itfAttr.guid:B}) is dispatch-only: typed (vtable) calls are not possible.");
                return null;
            }
            itf.GetRefTypeOfImplType(-1, out var vtHref);
            itf.GetRefTypeInfo(vtHref, out vtable);
        }
        r.Ok($"Type library {version}: OneNote's application class implements {name} {itfAttr.guid:B} " +
             $"({(dual ? "dual: typed and late-bound" : itfAttr.typekind.ToString())})");

        var methods = new List<(int Slot, string Text)>();
        int getHierarchySlot = -1;
        var vtAttr = Attr(vtable);
        for (int k = 0; k < vtAttr.cFuncs; k++)
        {
            vtable.GetFuncDesc(k, out var pfd);
            try
            {
                var fd = Marshal.PtrToStructure<ComTypes.FUNCDESC>(pfd);
                var names = new string[1];
                vtable.GetNames(fd.memid, names, 1, out _);
                int slot = fd.oVft / IntPtr.Size;
                var kind = fd.invkind switch
                {
                    ComTypes.INVOKEKIND.INVOKE_PROPERTYGET => "get_",
                    ComTypes.INVOKEKIND.INVOKE_PROPERTYPUT or ComTypes.INVOKEKIND.INVOKE_PROPERTYPUTREF => "put_",
                    _ => "",
                };
                methods.Add((slot, $"{slot}:{kind}{names[0]}({fd.cParams})"));
                if (names[0] == "GetHierarchy" && fd.invkind == ComTypes.INVOKEKIND.INVOKE_FUNC && GetHierarchySignatureOk(fd, vtable))
                {
                    getHierarchySlot = slot;
                }
            }
            finally
            {
                vtable.ReleaseFuncDesc(pfd);
            }
        }
        r.Info($"{name} methods in vtable order (slot:name(parameters)): " +
               string.Join(", ", methods.OrderBy(m => m.Slot).Select(m => m.Text)));

        var scope = EnumValue(lib, "HierarchyScope", "hsNotebooks");
        var schema = EnumValue(lib, "XMLSchema", "xsCurrent");
        if (getHierarchySlot < 0 || scope is null || schema is null)
        {
            r.Fail("GetHierarchy's signature or its enumeration values are not as expected; the typed test call is skipped.");
            return new TypedInterface(itfAttr.guid, name, version, -1, 0, 0);
        }
        r.Ok($"GetHierarchy signature verified (vtable slot {getHierarchySlot}; hsNotebooks = {scope}, xsCurrent = {schema})");
        return new TypedInterface(itfAttr.guid, name, version, getHierarchySlot, scope.Value, schema.Value);
    }

    /// <summary>GetHierarchy(BSTR, HierarchyScope, BSTR*, XMLSchema) returning HRESULT.</summary>
    private static bool GetHierarchySignatureOk(ComTypes.FUNCDESC fd, ComTypes.ITypeInfo info)
    {
        if (fd.cParams != 4 || (VarEnum)fd.elemdescFunc.tdesc.vt != VarEnum.VT_HRESULT)
        {
            return false;
        }
        var size = Marshal.SizeOf<ComTypes.ELEMDESC>();
        var p = Enumerable.Range(0, 4)
            .Select(i => Marshal.PtrToStructure<ComTypes.ELEMDESC>(fd.lprgelemdescParam + i * size).tdesc)
            .ToArray();
        bool IsInt32Like(ComTypes.TYPEDESC t) => (VarEnum)t.vt is VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_USERDEFINED;
        bool outBstr = (VarEnum)p[2].vt == VarEnum.VT_PTR &&
                       (VarEnum)Marshal.PtrToStructure<ComTypes.TYPEDESC>(p[2].lpValue).vt == VarEnum.VT_BSTR;
        return (VarEnum)p[0].vt == VarEnum.VT_BSTR && IsInt32Like(p[1]) && outBstr && IsInt32Like(p[3]);
    }

    /// <summary>The value of an enumeration member, read from the type library.</summary>
    private static int? EnumValue(ComTypes.ITypeLib lib, string enumName, string memberName)
    {
        for (int i = 0; i < lib.GetTypeInfoCount(); i++)
        {
            lib.GetTypeInfoType(i, out var kind);
            if (kind != ComTypes.TYPEKIND.TKIND_ENUM)
            {
                continue;
            }
            lib.GetDocumentation(i, out var name, out _, out _, out _);
            if (name != enumName)
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
                    var names = new string[1];
                    info.GetNames(vd.memid, names, 1, out _);
                    if (names[0] == memberName && vd.varkind == ComTypes.VARKIND.VAR_CONST)
                    {
                        return Convert.ToInt32(Marshal.GetObjectForNativeVariant(vd.desc.lpvarValue), CultureInfo.InvariantCulture);
                    }
                }
                finally
                {
                    info.ReleaseVarDesc(pvd);
                }
            }
        }
        return null;
    }

    /// <summary>Shows how the interface is registered for cross-process calls (which type library version marshaling uses).</summary>
    private static void DescribeInterfaceRegistration(Report r, TypedInterface typed)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var key = root.OpenSubKey($@"Interface\{typed.Iid:B}");
            if (key is null)
            {
                r.Warn($"Interface {typed.Iid:B} is not registered in the {ViewName(view)} registry");
                continue;
            }
            using var lib = key.OpenSubKey("TypeLib");
            r.Info($"Interface {typed.Name} ({ViewName(view)} registry): proxy/stub {Default(key, "ProxyStubClsid32") ?? "-"}, " +
                   $"type library {lib?.GetValue(null) ?? "-"} version {lib?.GetValue("Version") ?? "-"}");
        }
    }

    /// <summary>Asks OneNote for the typed interface (no method call), then makes one read-only typed call if that worked.</summary>
    private static (bool Works, int QueryResult) TryTypedCall(Report r, object app, TypedInterface typed)
    {
        var unknown = Marshal.GetIUnknownForObject(app);
        try
        {
            var iid = typed.Iid;
            int hr = Marshal.QueryInterface(unknown, in iid, out var itf);
            if (hr != 0)
            {
                r.Fail($"OneNote cannot provide the typed interface {typed.Name} to this app: {Hr(hr)}");
                return (false, hr);
            }
            try
            {
                r.Ok($"OneNote provides the typed interface {typed.Name} to this app (typed calls can be marshaled)");
                if (typed.GetHierarchySlot < 0)
                {
                    return (false, 0);
                }
                var function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), typed.GetHierarchySlot * IntPtr.Size);
                var getHierarchy = Marshal.GetDelegateForFunctionPointer<GetHierarchyCall>(function);
                int callResult = getHierarchy(itf, "", typed.NotebooksScope, out var xml, typed.Schema);
                if (callResult != 0)
                {
                    r.Fail($"Typed read-only call GetHierarchy failed: {Hr(callResult)}");
                    return (false, 0);
                }
                r.Ok($"Typed read-only call works: OneNote lists {CountNotebooks(xml)} open notebook(s)");
                return (true, 0);
            }
            finally
            {
                Marshal.Release(itf);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static int CountNotebooks(string xml) => XDocument.Parse(xml).Root!.Elements().Count();

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

    private static string? Default(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    private static string ViewName(RegistryView view) => view == RegistryView.Registry64 ? "64-bit" : "32-bit";

    private static string Hr(int hr) => unchecked((uint)hr) switch
    {
        0x8002801D => "0x8002801D TYPE_E_LIBNOTREGISTERED (type library not registered)",
        0x80029C4A => "0x80029C4A TYPE_E_CANTLOADLIBRARY (type library file cannot be loaded)",
        0x80004002 => "0x80004002 E_NOINTERFACE (interface not supported)",
        0x80040154 => "0x80040154 REGDB_E_CLASSNOTREG (class not registered)",
        0x80080005 => "0x80080005 CO_E_SERVER_EXEC_FAILURE (OneNote did not start)",
        0x80070005 => "0x80070005 E_ACCESSDENIED (access denied)",
        0x80004005 => "0x80004005 E_FAIL (unspecified failure)",
        _ => $"0x{hr:X8}",
    };

    private sealed class Report
    {
        private readonly StringBuilder _text = new("EverNote2OneNote - OneNote connection check (read-only)\n\n");

        public void Line(string status, string text) => _text.Append('[').Append(status).Append("] ").Append(text).Append('\n');
        public void Ok(string text) => Line("OK  ", text);
        public void Fail(string text) => Line("FAIL", text);
        public void Warn(string text) => Line("WARN", text);
        public void Info(string text) => Line("INFO", text);

        public string Conclude(string conclusion) => _text.Append("\nConclusion:\n").Append(conclusion).Append('\n').ToString();
    }
}
