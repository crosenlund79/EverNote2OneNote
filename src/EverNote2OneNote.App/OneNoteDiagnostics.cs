using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace EverNote2OneNote.App;

/// <summary>
/// Read-only check of the OneNote desktop automation (COM) setup. It only reads the registry, loads the type
/// library without registering it, and makes one read-only OneNote call (list of open notebooks).
/// It never changes the registry, OneNote or any notebook.
/// </summary>
internal static class OneNoteDiagnostics
{
    private const string ProgId = "OneNote.Application";

    /// <summary>The OneNote object library; used only if the class registration does not name its type library.</summary>
    private const string KnownTypeLib = "{0EA692EE-BB50-4E3C-AEF0-356D91732725}";

    private const int TypeLibNotRegistered = unchecked((int)0x8002801D);
    private const uint UserDefaultLocale = 0x400;

    public const string RepairSteps =
        "How to repair the OneNote installation (Windows does this, not this app; your notebooks are not affected):\n" +
        "  1. Close OneNote and this app.\n" +
        "  2. Open Windows Settings > Apps > Installed apps, find \"Microsoft 365\" (or \"Microsoft Office\"),\n" +
        "     click ... > Modify, choose \"Quick Repair\" and click Repair.\n" +
        "  3. Open OneNote once normally, close it, then click \"Check OneNote Connection\" again.\n" +
        "  4. If it still fails, repeat step 2 but choose \"Online Repair\" (downloads and reinstalls Office; takes longer).\n" +
        "  5. Only if this check still lists a BROKEN type library entry under HKEY_CURRENT_USER: that per-user\n" +
        "     entry overrides Office's own registration. In Registry Editor, export (back up) exactly that key, then\n" +
        "     delete it, and run the check again. If you are unsure, send this report instead.";

    [ComImport, Guid("00020400-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDispatchProbe
    {
        [PreserveSig] int GetTypeInfoCount(out uint count);
        [PreserveSig] int GetTypeInfo(uint index, uint lcid, out IntPtr typeInfo);
        [PreserveSig] int GetIDsOfNames(ref Guid riid, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] names,
            uint count, uint lcid, [Out, MarshalAs(UnmanagedType.LPArray)] int[] dispIds);
    }

    [DllImport("oleaut32.dll")]
    private static extern int LoadRegTypeLib(ref Guid libId, ushort major, ushort minor, uint lcid, out IntPtr typeLib);

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

        // 1. Registration (read-only).
        var clsid = Default(Registry.ClassesRoot, $@"{ProgId}\CLSID");
        if (clsid is null)
        {
            r.Fail($"\"{ProgId}\" is not registered: the OneNote desktop automation API is not installed for this user.");
            return r.Conclude("The OneNote desktop app (Microsoft 365 / Office) is not installed or not registered.\n" + RepairSteps);
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
                r.Line(exists ? "OK  " : "FAIL", $"OneNote program ({(view == RegistryView.Registry64 ? "64-bit" : "32-bit")} registry): {exe}{(exists ? "" : "  (FILE MISSING)")}");
            }
        }
        if (!serverFound)
        {
            r.Fail("No existing OneNote program is registered for automation.");
        }

        var typeLib = Default(Registry.ClassesRoot, $@"CLSID\{clsid}\TypeLib") ?? KnownTypeLib;
        var versions = new List<(ushort Major, ushort Minor, string Name)>();
        bool brokenTypeLib = ListTypeLib(r, Registry.ClassesRoot, $@"TypeLib\{typeLib}", "HKEY_CLASSES_ROOT", versions);
        using (var perUser = Registry.CurrentUser.OpenSubKey($@"Software\Classes\TypeLib\{typeLib}"))
        {
            if (perUser is not null)
            {
                r.Warn($"A per-user type library entry exists and overrides Office's: HKEY_CURRENT_USER\\Software\\Classes\\TypeLib\\{typeLib}");
                brokenTypeLib |= ListTypeLib(r, Registry.CurrentUser, $@"Software\Classes\TypeLib\{typeLib}", "HKEY_CURRENT_USER", []);
            }
        }
        using (var office = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Office\ClickToRun\REGISTRY\MACHINE\Software\Classes\TypeLib\{typeLib}"))
        {
            if (office is not null)
            {
                r.Info($"Office's own (Click-to-Run) registry has type library version(s): {string.Join(", ", office.GetSubKeyNames())}");
            }
        }

        // 2. Can this app load the type library? (A typed COM interface would need this.)
        foreach (var (major, minor, name) in versions)
        {
            if (!Guid.TryParse(typeLib, out var libId))
            {
                r.Fail($"The class names an invalid type library ID: {typeLib}");
                break;
            }
            int hr = LoadRegTypeLib(ref libId, major, minor, 0, out var lib);
            if (hr == 0)
            {
                Marshal.Release(lib);
                r.Ok($"Type library {name} loads in this app");
            }
            else
            {
                r.Fail($"Type library {name} does not load in this app: {Hr(hr)}");
            }
        }

        // 3. Live, read-only checks against the running OneNote.
        object? app = null;
        var errors = new List<int>();
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
                                  "OneNote run as the same user (neither \"as administrator\").\n" + RepairSteps);
            }

            var dispatch = (IDispatchProbe)app!;
            int hrCount = dispatch.GetTypeInfoCount(out var count);
            r.Line(hrCount == 0 ? "OK  " : "FAIL", hrCount == 0 ? $"OneNote reports {count} type description(s)" : $"OneNote type description count: {Hr(hrCount)}");
            int hrInfo = dispatch.GetTypeInfo(0, UserDefaultLocale, out var info);
            if (hrInfo == 0)
            {
                Marshal.Release(info);
                r.Ok("OneNote can load its own type library");
            }
            else
            {
                errors.Add(hrInfo);
                r.Fail($"OneNote cannot load its own type library: {Hr(hrInfo)}");
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
                r.Fail($"OneNote cannot resolve method names (what this app's calls need): {Hr(hrNames)}");
            }

            // The only OneNote call made: list the open notebooks (read-only).
            try
            {
                object?[] args = ["", 2, ""]; // hsNotebooks
                var modifiers = new ParameterModifier(3);
                modifiers[2] = true;
                app!.GetType().InvokeMember("GetHierarchy", BindingFlags.InvokeMethod, null, app, args, [modifiers], null, null);
                var notebooks = XDocument.Parse((string)args[2]!).Root!.Elements().Count();
                r.Ok($"Read-only call works: OneNote lists {notebooks} open notebook(s)");
            }
            catch (Exception ex) when ((ex as TargetInvocationException)?.InnerException is COMException || ex is COMException)
            {
                var com = (ex as COMException) ?? (COMException)ex.InnerException!;
                errors.Add(com.HResult);
                r.Fail($"Read-only call GetHierarchy failed: {Hr(com.HResult)}");
            }
        }
        finally
        {
            if (app is not null)
            {
                Marshal.FinalReleaseComObject(app);
            }
        }

        if (errors.Count == 0 && !brokenTypeLib && serverFound)
        {
            return r.Conclude("The OneNote connection works. If a migration still fails, send this report together with the error message.");
        }
        if (errors.Contains(TypeLibNotRegistered) || brokenTypeLib)
        {
            return r.Conclude(
                "OneNote's type library (the registered description of its automation interface) is missing or registered incorrectly " +
                "on this PC. This is an installation problem, not a problem with this app: every program that automates OneNote, " +
                "late-bound or typed, depends on that registration. Repairing Office restores it.\n" + RepairSteps);
        }
        return r.Conclude("The OneNote automation API is not working correctly on this PC.\n" + RepairSteps);
    }

    /// <summary>Lists the versions of a type library registration and checks that the registered files exist.</summary>
    private static bool ListTypeLib(Report r, RegistryKey hive, string path, string hiveName, List<(ushort, ushort, string)> versions)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null)
        {
            r.Fail($"Type library is not registered: {hiveName}\\{path}");
            return true;
        }
        bool broken = false;
        foreach (var version in key.GetSubKeyNames())
        {
            var parts = version.Split('.');
            if (parts.Length == 2 &&
                ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var major) &&
                ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minor))
            {
                versions.Add((major, minor, version));
            }
            foreach (var platform in new[] { "win32", "win64" })
            {
                var file = Default(key, $@"{version}\0\{platform}");
                if (file is null)
                {
                    continue;
                }
                bool exists = File.Exists(file.Trim('"'));
                broken |= !exists;
                r.Line(exists ? "OK  " : "FAIL",
                    $"Type library {version} ({platform}): {file}{(exists ? "" : "  (BROKEN: file missing)")}  [{hiveName}\\{path}\\{version}]");
            }
        }
        return broken;
    }

    private static string? Default(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(null) as string;
    }

    private static string Hr(int hr) => unchecked((uint)hr) switch
    {
        0x8002801D => "0x8002801D TYPE_E_LIBNOTREGISTERED (type library not registered)",
        0x80029C4A => "0x80029C4A TYPE_E_CANTLOADLIBRARY (type library file cannot be loaded)",
        0x80040154 => "0x80040154 REGDB_E_CLASSNOTREG (class not registered)",
        0x80080005 => "0x80080005 CO_E_SERVER_EXEC_FAILURE (OneNote did not start)",
        0x80070005 => "0x80070005 E_ACCESSDENIED (access denied)",
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
