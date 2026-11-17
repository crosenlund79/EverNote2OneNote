using System.Text;

namespace EverNote2OneNote.Tests;

internal static class TestFiles
{
    public static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Fixture(string relative) =>
        Path.Combine(Fixtures, relative.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>A temporary folder that is deleted after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("e2o-tests-").FullName;

    public string Write(string relative, string content)
    {
        var path = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Builds small synthetic ENEX documents for edge cases.</summary>
internal static class Enex
{
    public const string Header =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE en-export SYSTEM \"http://xml.evernote.com/pub/evernote-export4.dtd\">\n" +
        "<en-export export-date=\"20261117T090000Z\" application=\"Evernote\" version=\"10.105.4\">\n";

    public const string Footer = "</en-export>\n";

    public static string Body(string enml) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n" +
        "<!DOCTYPE en-note SYSTEM \"http://xml.evernote.com/pub/enml2.dtd\">\n" + enml;

    public static string Note(string title, string enml, string extra = "") =>
        $"<note><title>{title}</title><content><![CDATA[{Body(enml)}]]></content>" +
        $"<created>20200101T000000Z</created><updated>20200102T000000Z</updated>{extra}</note>\n";

    public static string File(params string[] notes) => Header + string.Concat(notes) + Footer;
}
