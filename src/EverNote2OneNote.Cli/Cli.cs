using System.Globalization;
using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.Cli;

public static class Cli
{
    public const int Ok = 0;
    public const int ErrorsFound = 1;
    public const int UsageError = 2;

    private const string Usage = """
        EverNote2OneNote - ENEX inspection (Pilot 1). Reads Evernote .enex exports without modifying them.

        Usage:
          e2o inspect <file-or-folder> [options]

        Options:
          --out <folder>              Where to write the detailed report.
                                      Default: %LOCALAPPDATA%\EverNote2OneNote\reports\inspect-<time>
          --dump-text                 Also write the full extracted text of every note (text\ folder).
          --expect-notes <n>          Reference figures from your Evernote account; the summary
          --expect-notebooks <n>      compares them with what the export contains.
          --expect-attachments <n>
          --expect-size <size>        e.g. 1.12GB (binary units: 1 GB = 1024 MB)

        Exit codes: 0 = no read errors, 1 = errors found (see report), 2 = usage or fatal error.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "/?")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? UsageError : Ok;
        }
        if (args[0] != "inspect")
        {
            error.WriteLine($"Unknown command '{args[0]}'.");
            error.WriteLine(Usage);
            return UsageError;
        }

        string? source = null, outDirectory = null;
        bool dumpText = false;
        int? notes = null, notebooks = null, attachments = null;
        long? size = null;
        try
        {
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--out": outDirectory = Value(args, ref i); break;
                    case "--dump-text": dumpText = true; break;
                    case "--expect-notes": notes = ParseCount(Value(args, ref i)); break;
                    case "--expect-notebooks": notebooks = ParseCount(Value(args, ref i)); break;
                    case "--expect-attachments": attachments = ParseCount(Value(args, ref i)); break;
                    case "--expect-size":
                        var text = Value(args, ref i);
                        size = Format.TryParseBytes(text, out var bytes) ? bytes : throw new ArgumentException($"Invalid size '{text}'.");
                        break;
                    default:
                        if (args[i].StartsWith("--", StringComparison.Ordinal) || source is not null)
                        {
                            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
                        }
                        source = args[i];
                        break;
                }
            }
            if (source is null)
            {
                throw new ArgumentException("Missing <file-or-folder>.");
            }
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            error.WriteLine(Usage);
            return UsageError;
        }

        try
        {
            var sourcePath = Path.GetFullPath(source);
            var files = Inspector.FindFiles(sourcePath);
            if (files.Count == 0)
            {
                error.WriteLine($"No .enex files found in {sourcePath}");
                return UsageError;
            }

            var reportDirectory = Path.GetFullPath(outDirectory ?? DefaultReportDirectory());
            if (Directory.Exists(sourcePath) && IsInside(reportDirectory, sourcePath))
            {
                error.WriteLine("The report folder must be outside the source folder, so the source folder is left untouched.");
                return UsageError;
            }

            output.WriteLine($"Inspecting {files.Count} ENEX file(s) in {sourcePath} ...");
            output.WriteLine();
            var result = Inspector.Run(sourcePath);
            var expectations = new Expectations(notes, notebooks, attachments, size);
            var summary = Summary.Build(result, expectations, reportDirectory);
            ReportWriter.Write(result, Summary.Build(result, expectations, null), reportDirectory, dumpText);
            output.Write(summary);
            return result.CountIssues(Severity.Error) > 0 ? ErrorsFound : Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Error: {ex.Message}");
            return UsageError;
        }
    }

    private static string DefaultReportDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EverNote2OneNote", "reports",
        "inspect-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    private static bool IsInside(string path, string folder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedFolder = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar).StartsWith(normalizedFolder, comparison);
    }

    private static string Value(string[] args, ref int i) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}.");

    private static int ParseCount(string text) =>
        int.TryParse(text.Replace(",", "").Replace("_", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ArgumentException($"Invalid number '{text}'.");
}
