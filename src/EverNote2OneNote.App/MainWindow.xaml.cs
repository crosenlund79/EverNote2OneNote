using System.ComponentModel;
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
    private const string DestinationHint = "Checked in OneNote when you migrate. Migrate All Notes always creates new notebooks.";
    private readonly HashSet<string> _confirmedNotebooks = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private string? _reportDirectory;
    private InspectionResult? _inspection;

    /// <summary>Set while Migrate All Notes runs; cancelling it stops the run after the current note.</summary>
    private CancellationTokenSource? _bulkCancel;

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
        var destination = DestinationBox.Text.Trim();
        bool exportBackup = ExportBackupBox.IsChecked == true;

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

            // 1. Look at the destination first (read-only) and confirm before writing into anything that exists.
            StatusText.Text = "Checking the destination notebook in OneNote...";
            var root = LocalMigrator.DefaultRoot();
            var plan = await Task.Run(() =>
            {
                using var oneNote = OneNoteCom.Connect();
                return LocalMigrator.PlanDestination(oneNote, destination, row.Summary, root, LocalMigrator.OneDriveFolders());
            });
            DestinationStatus.Text = plan.Describe();
            if (plan.Problem is not null)
            {
                ShowError(plan.Problem, null);
                return;
            }
            if (!ConfirmDestination(plan, row.Title))
            {
                StatusText.Text = "Migration cancelled; nothing was written.";
                return;
            }

            // 2. Migrate into the confirmed destination.
            var result = await Task.Run(() =>
            {
                ((IProgress<string>)progress).Report("Connecting to the OneNote desktop app...");
                using var oneNote = OneNoteCom.Connect();
                return LocalMigrator.Migrate(row.Summary, oneNote, plan, root, exportBackup, progress);
            });
            if (result.PageCreated)
            {
                DestinationStatus.Text = $"Notebook exists; section \"{result.Section}\" exists.";
            }
            StatusText.Text = result.PageCreated
                ? $"Migrated \"{row.Title}\" to OneNote: {result.NotebookName} > {result.Section}."
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

    /// <summary>
    /// Asks before writing into an existing notebook (once per notebook per session) and, always, before adding a page
    /// whose title already exists in the section. A new notebook needs no confirmation.
    /// </summary>
    private bool ConfirmDestination(DestinationPlan plan, string title)
    {
        bool askNotebook = plan.NotebookExists && !_confirmedNotebooks.Contains(plan.NotebookName);
        if (!askNotebook && plan.PagesWithSameTitle == 0)
        {
            return true;
        }
        var text = new System.Text.StringBuilder();
        text.Append($"Destination notebook: \"{plan.NotebookName}\" - ALREADY EXISTS");
        text.Append(plan.NotebookId is null ? " (created earlier by this app; it will be reopened)" : " (open in OneNote)");
        if (plan.NotebookLocation is not null)
        {
            text.Append($"\nLocation: {plan.NotebookLocation}");
        }
        text.Append(plan.SectionExists
            ? $"\nSection: \"{plan.SectionName}\" - already exists ({plan.SectionPages} page(s))"
            : $"\nSection: \"{plan.SectionName}\" - will be created");
        if (plan.PagesWithSameTitle > 0)
        {
            text.Append($"\n\n{plan.PagesWithSameTitle} page(s) in this section already have the title \"{title}\". " +
                        "Continuing adds another page with the same title.");
        }
        text.Append("\n\nThe note will be added as a NEW page. Existing pages are not changed.\n\nContinue?");
        if (MessageBox.Show(this, text.ToString(), "EverNote2OneNote - confirm destination",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return false;
        }
        _confirmedNotebooks.Add(plan.NotebookName);
        return true;
    }

    private void Destination_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DestinationStatus is not null)
        {
            DestinationStatus.Text = DestinationHint;
        }
    }

    /// <summary>
    /// Migrates every inspected note into new notebooks (one per folder, one section per ENEX file), one note at a time.
    /// Shows the plan first and starts only after confirmation; can be stopped after the current note.
    /// </summary>
    private async void MigrateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_inspection is not { } inspection)
        {
            return;
        }
        bool exportBackup = ExportBackupBox.IsChecked == true;
        SetBusy(true);
        try
        {
            // 1. Plan (read-only: lists OneNote's open notebooks) and confirm.
            StatusText.Text = "Planning the migration (read-only): checking the notebooks that are open in OneNote...";
            var root = LocalMigrator.DefaultRoot();
            var plan = await Task.Run(() =>
            {
                using var oneNote = OneNoteCom.Connect();
                return BulkMigrator.Plan(inspection, oneNote, root, LocalMigrator.OneDriveFolders(), DateTime.Now);
            });
            if (plan.Problem is not null)
            {
                ShowError(plan.Problem, null);
                return;
            }
            var confirm = new TextReportWindow(plan.Describe(exportBackup), "Migrate All Notes - check the plan", "Start Migration") { Owner = this };
            if (confirm.ShowDialog() != true)
            {
                StatusText.Text = "Migration cancelled; nothing was written.";
                return;
            }

            // 2. Migrate, one note at a time, in the background.
            _bulkCancel = new CancellationTokenSource();
            var token = _bulkCancel.Token;
            StopButton.Content = "Stop After Current Note";
            StopButton.IsEnabled = true;
            StopButton.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            StatusText.Text = "Connecting to the OneNote desktop app...";
            var progress = new Progress<BulkProgress>(ShowBulkProgress);
            var result = await Task.Run(() =>
            {
                using var oneNote = OneNoteCom.Connect();
                return BulkMigrator.Run(plan, oneNote, exportBackup, progress, token);
            });

            // 3. Report (late progress updates are ignored from here on).
            _bulkCancel.Dispose();
            _bulkCancel = null;
            StopButton.Visibility = Visibility.Collapsed;
            StatusText.Text = result.Headline + $" Report: {result.RunFolder}";
            new TextReportWindow(result.ReportText(), "Migration report", folder: result.RunFolder) { Owner = this }.ShowDialog();
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
            _bulkCancel?.Dispose();
            _bulkCancel = null;
            StopButton.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void ShowBulkProgress(BulkProgress progress)
    {
        if (_bulkCancel is null)
        {
            return; // a late update after the run has finished
        }
        ProgressBar.Value = progress.Total == 0 ? 0 : (double)progress.Completed / progress.Total;
        var where = progress.Section is null ? progress.Notebook : $"{progress.Notebook} > {progress.Section}";
        var note = progress.Note is null ? "" : $" > \"{progress.Note}\"";
        var step = progress.Step is null ? "" : $"  ({progress.Step})";
        var stopping = _bulkCancel.IsCancellationRequested ? "  Stopping after the current note..." : "";
        StatusText.Text = $"{where}{note}{step}\n{progress.Completed} of {progress.Total} notes done: {progress.Migrated} migrated " +
                          $"({progress.Review} to review), {progress.Failed} failed.{stopping}";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _bulkCancel?.Cancel();
        StopButton.IsEnabled = false;
        StopButton.Content = "Stopping after the current note...";
    }

    /// <summary>Closing during Migrate All Notes could interrupt a page; the run must be stopped first.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_bulkCancel is not null)
        {
            e.Cancel = true;
            MessageBox.Show(this, "A migration is running. Click \"Stop After Current Note\", wait for the report, then close the app.",
                "EverNote2OneNote", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        base.OnClosing(e);
    }

    private static void ShowInOneNote(string pageId)
    {
        using var oneNote = OneNoteCom.Connect();
        oneNote.ShowPage(pageId);
    }

    /// <summary>Read-only check of the OneNote automation setup; changes nothing.</summary>
    private async void CheckOneNote_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "Checking the OneNote connection (read-only)...";
        try
        {
            var report = await Task.Run(OneNoteDiagnostics.Run);
            StatusText.Text = "OneNote connection check finished.";
            new TextReportWindow(report) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowError("The OneNote connection check could not be completed.", ex);
        }
        finally
        {
            SetBusy(false);
        }
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
            _inspection = result;
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
        _inspection = null;
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
        MigrateAllButton.IsEnabled = !busy && _inspection is { Notes.Count: > 0 };
        CheckOneNoteButton.IsEnabled = !busy;
        DestinationBox.IsEnabled = !busy;
        ExportBackupBox.IsEnabled = !busy;
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
