using System.Windows;

namespace EverNote2OneNote.App;

/// <summary>Asks once for the Application (client) ID of the user's own app registration.</summary>
public partial class ClientIdWindow : Window
{
    public ClientIdWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ClientIdBox.Focus();
    }

    public string ClientId { get; private set; } = "";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Guid.TryParse(ClientIdBox.Text.Trim(), out var id))
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ClientId = id.ToString();
        DialogResult = true;
    }
}
