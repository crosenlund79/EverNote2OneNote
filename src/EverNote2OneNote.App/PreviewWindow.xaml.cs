using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using EverNote2OneNote.Enex;

namespace EverNote2OneNote.App;

/// <summary>
/// Shows one image attachment. Its data is decoded on demand from the original .enex file (read-only);
/// nothing is extracted or saved.
/// </summary>
public partial class PreviewWindow : Window
{
    private const long MaxPreviewBytes = 100L * 1024 * 1024;

    private static readonly HashSet<string> PreviewableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/bmp", "image/x-ms-bmp", "image/tiff",
    };

    private readonly AttachmentRow _row;
    private Task? _loading;

    internal PreviewWindow(AttachmentRow row)
    {
        _row = row;
        InitializeComponent();
        DataContext = row;
        Title = $"Preview - {row.FileName}";
        Loaded += async (_, _) => await LoadAsync();
    }

    public static bool CanPreview(string? mime) => mime is not null && PreviewableTypes.Contains(mime);

    /// <summary>True once the image has been decoded and is displayed.</summary>
    internal bool ImageShown => PreviewImage.Source is not null;

    internal Task LoadAsync() => _loading ??= LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        var attachment = _row.Attachment;
        try
        {
            PreviewImage.Source = await Task.Run(() =>
            {
                var data = EnexReader.ReadAttachmentData(
                    attachment.Note.File.FullPath, attachment.Note.ByteOffset, attachment.ResourceIndex, attachment.Md5, MaxPreviewBytes);
                return Decode(data);
            });
            MessageText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Unsupported, damaged or changed data: report it in the window instead of failing.
            MessageText.Text = $"This image could not be displayed.\n\n{ex.Message}";
        }
    }

    private static BitmapSource Decode(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
