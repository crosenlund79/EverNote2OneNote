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
            : result.ExportProblem is not null ? ("#FFF4CE", "#7A4D00", "All text verified, but the .onepkg export did not succeed. See below.")
            : !attachmentsOk ? ("#FFF4CE", "#7A4D00", "All text verified and exported. Some attachments were not transferred or could not be confirmed.")
            : result.Notices.Count > 0 ? ("#FFF4CE", "#7A4D00", "All text and attachments verified and exported. Some content needs attention - see Notes.")
            : ("#DFF6DD", "#0E700E", "All text verified: the OneNote page contains the complete note text in the original order, all attachments were found, and the notebook was exported.");
        Banner.Background = Brush(background);
        BannerText.Foreground = Brush(foreground);
        BannerText.Text = message;

        var details = new List<string>();
        if (result.NotebookName is not null)
        {
            details.Add($"OneNote notebook: {result.NotebookName} > {result.Section ?? "-"}");
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

        bool exported = result.PageCreated && result.ExportProblem is null && result.ExportPath is not null && File.Exists(result.ExportPath);
        ExportPathBox.Text = exported ? result.ExportPath : "(not exported)";
        ExportStatus.Text = exported
            ? $"Exported with OneNote's own publish function ({Format.Bytes(new FileInfo(result.ExportPath!).Length)}). It is stored locally and not uploaded anywhere."
            : result.ExportProblem ?? "The notebook was not exported.";
        ExportStatus.Foreground = Brush(exported ? "#555555" : "#A4262C");

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
