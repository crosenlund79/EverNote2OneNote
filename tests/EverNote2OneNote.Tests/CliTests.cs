using EverNote2OneNote.Cli;
using EverNote2OneNote.Inspection;

namespace EverNote2OneNote.Tests;

public class CliTests
{
    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = Cli.Cli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Clean_export_exits_with_zero_and_prints_the_summary()
    {
        using var temp = new TempDirectory();
        var (code, output, _) = Run("inspect", TestFiles.Fixture("Basic"), "--out", temp.Path,
            "--expect-notes", "4", "--expect-notebooks", "3", "--expect-attachments", "4", "--expect-size", "1KB");

        Assert.Equal(Cli.Cli.Ok, code);
        Assert.Contains("RESULT: no read errors", output);
        Assert.Contains("Reference check", output);
        Assert.True(File.Exists(Path.Combine(temp.Path, "report.md")));
    }

    [Fact]
    public void Read_errors_exit_with_one()
    {
        using var temp = new TempDirectory();
        var (code, output, _) = Run("inspect", TestFiles.Fixture("Problems/Damaged.enex"), "--out", temp.Path);

        Assert.Equal(Cli.Cli.ErrorsFound, code);
        Assert.Contains("RESULT: 3 error(s)", output);
    }

    [Fact]
    public void Report_folder_inside_the_source_folder_is_refused()
    {
        using var temp = new TempDirectory();
        temp.Write("Notes.enex", Enex.File(Enex.Note("A", "<en-note>a</en-note>")));

        var (code, _, error) = Run("inspect", temp.Path, "--out", Path.Combine(temp.Path, "report"));

        Assert.Equal(Cli.Cli.UsageError, code);
        Assert.Contains("outside the source folder", error);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "report")));
    }

    [Theory]
    [InlineData]
    [InlineData("migrate")]
    [InlineData("inspect")]
    [InlineData("inspect", "a", "b")]
    [InlineData("inspect", "a", "--out")]
    [InlineData("inspect", "a", "--expect-notes", "many")]
    [InlineData("inspect", "a", "--expect-size", "big")]
    [InlineData("inspect", "a", "--unknown")]
    public void Invalid_arguments_exit_with_two(params string[] args)
    {
        Assert.Equal(Cli.Cli.UsageError, Run(args).Code);
    }

    [Fact]
    public void Folders_without_enex_files_exit_with_two()
    {
        using var temp = new TempDirectory();
        Assert.Equal(Cli.Cli.UsageError, Run("inspect", temp.Path).Code);
    }

    [Theory]
    [InlineData("1.12GB", 1202590843L)]
    [InlineData("1,12 GB", 1202590843L)]
    [InlineData("950MB", 996147200L)]
    [InlineData("123", 123L)]
    public void Parses_sizes(string text, long expected)
    {
        Assert.True(Format.TryParseBytes(text, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Rejects_invalid_sizes()
    {
        Assert.False(Format.TryParseBytes("lots", out _));
    }
}
