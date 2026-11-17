using System.IO;
using System.Windows.Threading;

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
                                 window.AttachmentsGrid.Items.Count == result.Attachments.Count;
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
