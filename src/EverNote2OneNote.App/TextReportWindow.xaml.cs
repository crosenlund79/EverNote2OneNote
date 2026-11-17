using System.Windows;

namespace EverNote2OneNote.App;

/// <summary>Shows a plain-text report that can be copied.</summary>
public partial class TextReportWindow : Window
{
    public TextReportWindow(string report)
    {
        InitializeComponent();
        ReportBox.Text = report;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(ReportBox.Text);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
