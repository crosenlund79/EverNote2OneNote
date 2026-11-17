using System.Diagnostics;
using System.Windows;

namespace EverNote2OneNote.App;

/// <summary>Shows a plain-text report that can be copied; optionally asks for confirmation or offers its folder.</summary>
public partial class TextReportWindow : Window
{
    private readonly string? _folder;

    /// <param name="confirmLabel">If set, a confirm button with this label; the dialog result is true only if it is clicked.</param>
    /// <param name="folder">If set, an Open Report Folder button for this folder.</param>
    public TextReportWindow(string report, string? title = null, string? confirmLabel = null, string? folder = null)
    {
        InitializeComponent();
        ReportBox.Text = report;
        if (title is not null)
        {
            Title = title;
        }
        if (confirmLabel is not null)
        {
            ConfirmButton.Content = confirmLabel;
            ConfirmButton.Visibility = Visibility.Visible;
            CloseButton.Content = "Cancel";
            CloseButton.IsDefault = false; // Enter must not start anything
        }
        _folder = folder;
        OpenFolderButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(ReportBox.Text);

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_folder!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(this, "The report folder could not be opened.", ex);
        }
    }
}
