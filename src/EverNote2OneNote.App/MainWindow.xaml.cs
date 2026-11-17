using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EverNote2OneNote.Inspection;
using EverNote2OneNote.OneNote;
using Microsoft.Win32;

namespace EverNote2OneNote.App;

public partial class MainWindow : Window
{
    private readonly bool _quiet;
    private readonly HashSet<(string File, int Note)> _migrated = [];
    private bool _busy;
    private string? _reportDirectory;

    public MainWindow() : this(quiet: false)
    {
    }

    /// <param name="quiet">True for the automated self-test: errors are recorded instead of shown in a dialog.</param>
    internal MainWindow(bool quiet)
    {
        _quiet = quiet;
        InitializeComponent();
    }

    /// <summary>The last error reported to the user.</summary>
    internal string? LastError { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder that contains your Evernote .enex files" };
        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        FolderBox.Text = dialog.FolderName;
        InspectButton.IsEnabled = true;
        StatusText.Text = "Click Inspect Archive to check every note. Your files are only read, never changed.";
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e) =>
        await InspectAsync(FolderBox.Text, ReportWriter.DefaultDirectory());

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_reportDirectory is null)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_reportDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("The report folder could not be opened.", ex);
        }
    }

    private async void Migrate_Click(object sender, RoutedEventArgs e)
    {
        if (NotesGrid.SelectedItem is not NoteRow row)
        {
            return;
        }
        var key = (row.Summary.File.FullPath, row.Summary.Index);
        if (_migrated.Contains(key) &&
            MessageBox.Show(this,
                $"\"{row.Title}\" has already been migrated in this session.\n\nMigrate it again? This creates another new test notebook.",
                "EverNote2OneNote", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true); // also disables the button, so repeated clicks cannot start a second migration
        try
        {
            var progress = new Progress<string>(status =>
            {
                if (_busy)
                {
                    StatusText.Text = status;
                }
            });
            var result = await Task.Run(() =>
            {
                ((IProgress<string>)progress).Report("Connecting to the OneNote desktop app...");
                using var oneNote = OneNoteCom.Connect();
                return LocalMigrator.Migrate(row.Summary, oneNote, LocalMigrator.DefaultRoot(), LocalMigrator.OneDriveFolders(), progress);
            });
            if (result.PageCreated)
            {
                _migrated.Add(key);
            }
            StatusText.Text = result.PageCreated
                ? $"Migrated \"{row.Title}\" to the OneNote notebook \"{result.NotebookName}\"."
                : $"\"{row.Title}\" was not migrated.";
            new MigrationResultWindow(result, ShowInOneNote) { Owner = this }.ShowDialog();
        }
        catch (OneNoteDesktopException ex)
        {
            ShowError("Migration stopped. " + ex.Message, null);
        }
        catch (Exception ex)
        {
            ShowError("The migration could not be completed.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void ShowInOneNote(string pageId)
    {
        using var oneNote = OneNoteCom.Connect();
        oneNote.ShowPage(pageId);
    }

    private void NotesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetBusy(_busy);

    private void AttachmentRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { Item: AttachmentRow row })
        {
            return;
        }
        if (!PreviewWindow.CanPreview(row.Attachment.Mime))
        {
            var type = string.IsNullOrEmpty(row.Type) ? "of unknown type" : row.Type;
            MessageBox.Show(this,
                $"Preview is available for images only (PNG, JPEG, GIF, BMP, TIFF).\n\n\"{row.FileName}\" is {type}.",
                "EverNote2OneNote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new PreviewWindow(row) { Owner = this }.ShowDialog();
    }

    /// <summary>Runs the inspection in the background and shows the result. Returns null if it failed.</summary>
    internal async Task<InspectionResult?> InspectAsync(string folder, string reportDirectory)
    {
        SetBusy(true);
        ClearResults();
        try
        {
            if (!Directory.Exists(folder))
            {
                ShowError("The selected folder does not exist or cannot be opened.", null);
                return null;
            }
            var files = await Task.Run(() => Inspector.FindFiles(folder));
            if (files.Count == 0)
            {
                ShowError("No .enex files were found in this folder or its subfolders.\n\n" +
                          "In Evernote, export your notebooks as ENEX files first, then choose the folder you saved them in.", null);
                return null;
            }
            if (ReportWriter.IsInside(reportDirectory, folder))
            {
                ShowError("The report would be saved inside the selected folder. Choose a folder that contains only your Evernote exports.", null);
                return null;
            }

            ProgressBar.IsIndeterminate = files.Count == 1;
            var progress = new Progress<InspectionProgress>(ShowProgress);
            var watch = Stopwatch.StartNew();
            var (result, summary) = await Task.Run(() =>
            {
                var inspection = Inspector.Run(folder, progress);
                var text = Summary.Build(inspection, new Expectations(), null);
                ReportWriter.Write(inspection, text, reportDirectory, dumpText: true);
                return (inspection, text);
            });

            _reportDirectory = reportDirectory;
            ShowResults(result, summary);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 1;
            StatusText.Text = $"Finished in {watch.Elapsed.TotalSeconds:0.0} seconds. The detailed report was saved in {reportDirectory}";
            return result;
        }
        catch (Exception ex)
        {
            ShowError("The inspection could not be completed.", ex);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowProgress(InspectionProgress progress)
    {
        if (!_busy)
        {
            return; // a late update after the inspection has finished
        }
        ProgressBar.Value = progress.FileCount == 0 ? 0 : (double)progress.FilesCompleted / progress.FileCount;
        StatusText.Text = progress.CurrentFile is null
            ? "Writing the report..."
            : $"Reading file {progress.FilesCompleted + 1} of {progress.FileCount}: {progress.CurrentFile}";
    }

    private void ShowResults(InspectionResult result, string summary)
    {
        var view = ResultView.From(result);
        int errors = result.CountIssues(Severity.Error);
        int warnings = result.CountIssues(Severity.Warning);

        (ResultBanner.Background, ResultText.Foreground, ResultText.Text) = errors > 0
            ? (Brush("#FDE7E9"), Brush("#A4262C"), $"{errors} error(s): some data could not be read reliably. See the findings below and the report.")
            : warnings > 0
                ? (Brush("#FFF4CE"), Brush("#7A4D00"), $"All note text was read. {warnings} warning(s) to review before migration.")
                : (Brush("#DFF6DD"), Brush("#0E700E"), "All note text was read. No problems were found.");

        FiguresList.ItemsSource = view.Figures;
        FindingsGrid.ItemsSource = view.Findings;
        NotebooksGrid.ItemsSource = view.Notebooks;
        AttachmentsGrid.ItemsSource = view.Attachments;
        NotesGrid.ItemsSource = view.Notes;
        NotesTab.Header = $"Notes ({view.Notes.Count})";
        FindingsTab.Header = $"Findings ({view.Findings.Count})";
        NotebooksTab.Header = $"Notebooks ({view.Notebooks.Count})";
        int notInBody = view.Attachments.Count(a => !a.InBody);
        AttachmentsTab.Header = notInBody > 0
            ? $"Attachments ({view.Attachments.Count}, {notInBody} not in body)"
            : $"Attachments ({view.Attachments.Count})";
        SummaryBox.Text = summary;
        ResultTabs.SelectedItem = errors + warnings > 0 ? FindingsTab : NotebooksTab;
        ResultBanner.Visibility = FiguresList.Visibility = ResultTabs.Visibility = Visibility.Visible;
    }

    private void ClearResults()
    {
        _reportDirectory = null;
        ResultBanner.Visibility = FiguresList.Visibility = ResultTabs.Visibility = Visibility.Collapsed;
        FiguresList.ItemsSource = null;
        FindingsGrid.ItemsSource = null;
        NotebooksGrid.ItemsSource = null;
        AttachmentsGrid.ItemsSource = null;
        NotesGrid.ItemsSource = null;
        SummaryBox.Text = "";
        ProgressBar.Value = 0;
        StatusText.Text = "Looking for .enex files...";
        LastError = null;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BrowseButton.IsEnabled = !busy;
        InspectButton.IsEnabled = !busy && FolderBox.Text.Length > 0;
        OpenReportButton.IsEnabled = !busy && _reportDirectory is not null;
        MigrateButton.IsEnabled = !busy && NotesGrid.SelectedItem is NoteRow;
        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
        if (!busy)
        {
            ProgressBar.IsIndeterminate = false;
        }
    }

    private void ShowError(string message, Exception? exception)
    {
        LastError = exception is null ? message : $"{message} {exception}";
        StatusText.Text = message;
        if (!_quiet)
        {
            ErrorDialog.Show(this, message, exception);
        }
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(hex)!;
}

internal static class ErrorDialog
{
    public static void Show(Window? owner, string message, Exception? exception)
    {
        var text = exception switch
        {
            null => message,
            IOException or UnauthorizedAccessException => $"{message}\n\n{exception.Message}",
            _ => $"{message}\n\n{exception.Message}\n\nTechnical details (press Ctrl+C to copy this message):\n{Truncate(exception.ToString(), 2500)}",
        };
        if (owner is { IsLoaded: true })
        {
            MessageBox.Show(owner, text, "EverNote2OneNote", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(text, "EverNote2OneNote", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
