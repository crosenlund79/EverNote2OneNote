using System.IO;
using System.Windows.Threading;
using EverNote2OneNote.OneNote;

namespace EverNote2OneNote.App;

/// <summary>
/// Hidden check used by the Windows build pipeline: <c>EverNote2OneNote.exe --self-test &lt;enex-folder&gt; &lt;report-folder&gt;</c>.
/// Opens the real window, runs the same inspection as the Inspect Archive button, and writes
/// <c>self-test.txt</c> to the report folder. Exit code 0 means the app works end to end.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string folder, string reportDirectory)
    {
        reportDirectory = Path.GetFullPath(reportDirectory);
        string log;
        int exitCode;
        try
        {
            var window = new MainWindow(quiet: true) { ShowActivated = false };
            window.Show();
            var result = await window.InspectAsync(Path.GetFullPath(folder), reportDirectory);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); // let the results render

            if (result is null)
            {
                log = "FAILED: " + window.LastError;
                exitCode = 1;
            }
            else
            {
                bool reportWritten = File.Exists(Path.Combine(reportDirectory, "report.md"));
                bool displayed = window.FindingsGrid.Items.Count == result.Issues.Count &&
                                 window.NotebooksGrid.Items.Count == result.Files.Count &&
                                 window.AttachmentsGrid.Items.Count == result.Attachments.Count &&
                                 window.NotesGrid.Items.Count == result.Notes.Count;

                // The migration result window must load and render (no OneNote connection in the pipeline).
                var sample = new NoteMigrationResult { NoteTitle = "Sample", Section = "Recipes", PageId = "p1", Method = "test", TitleMatches = true };
                sample.Text = TextVerifier.Compare("one two", "one", []);
                sample.Attachments.Add(new AttachmentOutcome("a.png", "image/png", 10, "In place", "Uploaded and found on the page", true));
                var resultWindow = new MigrationResultWindow(sample) { ShowActivated = false };
                resultWindow.Show();
                await resultWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                displayed &= resultWindow.DifferencesGrid.Items.Count == 1;
                resultWindow.Close();
                // Preview every image attachment: valid images must show, damaged ones must be reported, not crash.
                var previews = new List<string>();
                foreach (var row in ((IEnumerable<AttachmentRow>)window.AttachmentsGrid.ItemsSource)
                         .Where(r => PreviewWindow.CanPreview(r.Attachment.Mime)))
                {
                    var preview = new PreviewWindow(row) { ShowActivated = false };
                    preview.Show();
                    await preview.LoadAsync();
                    previews.Add($"{row.FileName}:{(preview.ImageShown ? "shown" : "error")}");
                    preview.Close();
                }

                log = $"notes={result.Notes.Count} notebooks={result.Files.Count} attachments={result.Attachments.Count} " +
                      $"findings={result.Issues.Count} errors={result.CountIssues(Severity.Error)} " +
                      $"report={reportWritten} displayed={displayed} previews={string.Join(",", previews)}";
                exitCode = reportWritten && displayed ? 0 : 1;
            }
            window.Close();
        }
        catch (Exception ex)
        {
            log = "FAILED: " + ex;
            exitCode = 1;
        }

        Directory.CreateDirectory(reportDirectory);
        await File.WriteAllTextAsync(Path.Combine(reportDirectory, "self-test.txt"), log + Environment.NewLine);
        return exitCode;
    }
}
