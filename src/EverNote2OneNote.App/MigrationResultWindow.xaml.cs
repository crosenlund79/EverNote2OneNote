using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.App;

public sealed record DifferenceRow(string Kind, string Text, string Context);

public sealed record AttachmentResultRow(string FileName, string Type, string Size, string Placement, string Status, bool Ok);

/// <summary>Shows what was migrated and how it compares with the original note. Nothing here changes the page.</summary>
public partial class MigrationResultWindow : Window
{
    private readonly NoteMigrationResult _result;

    public MigrationResultWindow(NoteMigrationResult result)
    {
        _result = result;
        InitializeComponent();
        Title = $"Migration result - {result.NoteTitle}";

        var text = result.Text;
        bool textOk = text is not null && text.Identical && result.TitleMatches;
        bool attachmentsOk = result.Attachments.All(a => a.Ok);
        var (background, foreground, message) =
            !result.PageCreated ? ("#FDE7E9", "#A4262C", "The note was NOT migrated: " + result.Error)
            : result.Error is not null ? ("#FDE7E9", "#A4262C", "The page was created, but the migration did not finish: " + result.Error)
            : !textOk ? ("#FDE7E9", "#A4262C", "Differences found: the OneNote page does not match the original note text. See below.")
            : !attachmentsOk ? ("#FFF4CE", "#7A4D00", "All text verified. Some attachments were not transferred or could not be confirmed - see Attachments.")
            : result.Notices.Count > 0 ? ("#FFF4CE", "#7A4D00", "All text and attachments verified. Some content needs attention - see Notes.")
            : ("#DFF6DD", "#0E700E", "All text verified: the page contains the complete note text, in the original order, and all attachments.");
        Banner.Background = Brush(background);
        BannerText.Foreground = Brush(foreground);
        BannerText.Text = message;

        var details = new List<string> { $"OneNote: {result.Notebook} > {result.Section ?? "-"}" };
        if (result.Method is not null)
        {
            details.Add($"Method: {result.Method}");
        }
        if (result.PageTitle is not null)
        {
            details.Add(result.TitleMatches ? $"Title: \"{result.PageTitle}\" (matches)" : $"Title on page: \"{result.PageTitle}\" (DIFFERS from \"{result.NoteTitle}\")");
        }
        DetailsText.Text = string.Join("    |    ", details);

        TextSummary.Text = text is null ? "The page was not read back, so the text was not verified."
            : text.TooManyDifferences ? $"The texts differ too much to list individual differences ({Format.Count(text.OriginalCharacters)} characters in the original, {Format.Count(text.PageCharacters)} on the page)."
            : text.Identical ? $"No differences. All {Format.Count(text.OriginalCharacters)} characters of the original text (ignoring spaces and line breaks) are on the page, in the same order, exactly once."
            : $"{text.Differences.Count} difference(s) between the original text ({Format.Count(text.OriginalCharacters)} characters) and the page ({Format.Count(text.PageCharacters)} characters), ignoring spaces and line breaks:";
        DifferencesGrid.ItemsSource = text?.Differences.Select(d => new DifferenceRow(d.Kind.ToString(), d.Text, d.Context)).ToList();
        TextTab.Header = text is null || text.Identical ? "Text verification" : $"Text differences ({text.Differences.Count})";

        AttachmentsGrid.ItemsSource = result.Attachments
            .Select(a => new AttachmentResultRow(a.FileName, a.Type, Format.Bytes(a.Size), a.Placement, a.Status, a.Ok))
            .ToList();
        AttachmentsTab.Header = $"Attachments ({result.Attachments.Count(a => a.Ok)} of {result.Attachments.Count} verified)";

        var notices = result.Notices.ToList();
        if (text is not null)
        {
            notices.AddRange(text.AddedTextNotFound.Select(t => $"Text added by the migration was not found on the page: \"{t}\""));
        }
        NoticesList.ItemsSource = notices;
        NoticesTab.Header = $"Notes ({notices.Count})";

        OpenOneNoteButton.IsEnabled = result.ClientUrl is not null;
        OpenWebButton.IsEnabled = result.WebUrl is not null;
    }

    private void OpenOneNote_Click(object sender, RoutedEventArgs e) => Open(_result.ClientUrl);

    private void OpenWeb_Click(object sender, RoutedEventArgs e) => Open(_result.WebUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Open(string? url)
    {
        if (url is null)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "The link could not be opened. Is OneNote installed? You can also use \"Open in browser\".", ex);
        }
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(hex)!;
}
