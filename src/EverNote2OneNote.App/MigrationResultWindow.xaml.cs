using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.App;

public sealed record DifferenceRow(string Kind, string Text, string Context);

public sealed record AttachmentResultRow(string FileName, string Type, string Size, string Placement, string Status, bool Ok);

/// <summary>Shows what was migrated, how it compares with the original note, and where the .onepkg export is.</summary>
public partial class MigrationResultWindow : Window
{
    private readonly LocalMigrationResult _result;
    private readonly Action<string>? _showPage;

    public MigrationResultWindow(LocalMigrationResult result, Action<string>? showPage)
    {
        _result = result;
        _showPage = showPage;
        InitializeComponent();
        Title = $"Migration result - {result.NoteTitle}";

        var text = result.Text;
        bool textOk = text is not null && text.Identical && result.TitleMatches;
        bool attachmentsOk = result.Attachments.All(a => a.Ok);
        var (background, foreground, message) =
            !result.PageCreated ? ("#FDE7E9", "#A4262C", "The note was NOT migrated. " + result.Error)
            : result.Error is not null ? ("#FDE7E9", "#A4262C", "The page was created, but the migration did not finish. " + result.Error)
            : !textOk ? ("#FDE7E9", "#A4262C", "Differences found: the OneNote page does not match the original note text. See below.")
            : !attachmentsOk ? ("#FFF4CE", "#7A4D00", "All text verified. Some attachments were not transferred or could not be confirmed - see Attachments.")
            : result.Notices.Count > 0 ? ("#FFF4CE", "#7A4D00", "All text and attachments verified. Some content needs attention - see Notes.")
            : ("#DFF6DD", "#0E700E", "All text verified: the OneNote page contains the complete note text in the original order, and all attachments were found.");
        Banner.Background = Brush(background);
        BannerText.Foreground = Brush(foreground);
        BannerText.Text = message;

        var details = new List<string>();
        if (result.NotebookName is not null)
        {
            details.Add($"OneNote: {result.NotebookName} ({(result.NotebookExisted ? "existing" : "new")} notebook) > " +
                        $"{result.Section ?? "-"} ({(result.SectionExisted ? "existing" : "new")} section)");
        }
        if (result.NotebookLocation is not null)
        {
            details.Add($"Notebook location: {result.NotebookLocation}");
        }
        if (result.Method is not null)
        {
            details.Add($"Written as: {result.Method}");
        }
        if (result.PageTitle is not null)
        {
            details.Add(result.TitleMatches ? $"Title: \"{result.PageTitle}\" (matches)" : $"Title on page: \"{result.PageTitle}\" (DIFFERS from \"{result.NoteTitle}\")");
        }
        DetailsText.Text = string.Join("    |    ", details);

        bool exported = result.ExportPath is not null && File.Exists(result.ExportPath);
        ExportPathBox.Text = exported ? result.ExportPath : "(no backup)";
        ExportStatus.Text = exported
            ? $"Optional backup: exported with OneNote's own publish function and checked ({Format.Bytes(new FileInfo(result.ExportPath!).Length)}). Stored locally, not uploaded."
            : !result.ExportRequested ? "Optional backup: not requested."
            : !result.PageCreated ? "Optional backup: not attempted, because the page was not created."
            : "Optional backup FAILED (the migrated page is not affected): " + (result.ExportProblem ?? "unknown reason");
        ExportStatus.Foreground = Brush(exported || !result.ExportRequested ? "#555555" : "#A4262C");

        TextSummary.Text = text is null ? "The page was not read back, so the text was not verified."
            : text.TooManyDifferences ? $"The texts differ too much to list individual differences ({Format.Count(text.OriginalCharacters)} characters in the original, {Format.Count(text.PageCharacters)} on the page)."
            : text.Identical ? $"No differences. All {Format.Count(text.OriginalCharacters)} characters of the original text (ignoring spaces and line breaks) were read back from OneNote, in the same order, exactly once."
            : $"{text.Differences.Count} difference(s) between the original text ({Format.Count(text.OriginalCharacters)} characters) and the page read back from OneNote ({Format.Count(text.PageCharacters)} characters), ignoring spaces and line breaks:";
        DifferencesGrid.ItemsSource = text?.Differences.Select(d => new DifferenceRow(d.Kind.ToString(), d.Text, d.Context)).ToList();
        TextTab.Header = text is null || text.Identical ? "Text verification" : $"Text differences ({text.Differences.Count})";

        AttachmentsGrid.ItemsSource = result.Attachments
            .Select(a => new AttachmentResultRow(a.FileName, a.Type, Format.Bytes(a.Size), a.Placement, a.Status, a.Ok))
            .ToList();
        AttachmentsTab.Header = $"Attachments ({result.Attachments.Count(a => a.Ok)} of {result.Attachments.Count} transferred)";

        var notices = result.Notices.ToList();
        if (text is not null)
        {
            notices.AddRange(text.AddedTextNotFound.Select(t => $"Text added by the migration was not found on the page: \"{t}\""));
        }
        NoticesList.ItemsSource = notices;
        NoticesTab.Header = $"Notes ({notices.Count})";

        ShowPageButton.IsEnabled = result.PageId is not null && showPage is not null;
        OpenExportButton.IsEnabled = exported;
    }

    private void ShowPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _showPage?.Invoke(_result.PageId!);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "OneNote could not show the page.", ex);
        }
    }

    private void OpenExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_result.ExportPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "The export folder could not be opened.", ex);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(hex)!;
}
