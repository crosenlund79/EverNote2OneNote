using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Identity.Client;

namespace EverNote2OneNote.App;

/// <summary>
/// Microsoft account sign-in for OneNote (delegated, scope Notes.ReadWrite) using the system web browser.
/// Tokens are kept in memory only and are gone when the app closes. The only thing stored on disk is the
/// app registration's Application (client) ID, in %LOCALAPPDATA%\EverNote2OneNote\settings.json.
/// </summary>
internal sealed class OneNoteSignIn
{
    private static readonly string[] Scopes = ["Notes.ReadWrite"];

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EverNote2OneNote", "settings.json");

    private IPublicClientApplication? _app;
    private IAccount? _account;

    public bool IsSignedIn => _account is not null;
    public string? UserName => _account?.Username;

    public static string? SavedClientId()
    {
        try
        {
            return File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath))?["clientId"]?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void SaveClientId(string? clientId)
    {
        if (clientId is null)
        {
            File.Delete(SettingsPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, new JsonObject { ["clientId"] = clientId }.ToJsonString());
    }

    /// <summary>Opens the web browser for the Microsoft sign-in (personal Microsoft accounts).</summary>
    public async Task SignInAsync(string clientId, CancellationToken ct)
    {
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority("https://login.microsoftonline.com/consumers")
            .WithRedirectUri("http://localhost")
            .Build();
        var result = await _app.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(ct);
        _account = result.Account;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_app is null || _account is null)
        {
            throw new InvalidOperationException("Not connected to OneNote.");
        }
        try
        {
            return (await _app.AcquireTokenSilent(Scopes, _account).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            var result = await _app.AcquireTokenInteractive(Scopes).WithAccount(_account).WithUseEmbeddedWebView(false).ExecuteAsync(ct);
            _account = result.Account;
            return result.AccessToken;
        }
    }

    /// <summary>A plain explanation of a sign-in failure, and whether the saved Application ID is probably wrong.</summary>
    public static (string Message, bool ClientIdSuspect) Explain(Exception ex)
    {
        var text = ex.Message;
        if (ex is OperationCanceledException || (ex is MsalClientException client && client.ErrorCode == MsalError.AuthenticationCanceledError))
        {
            return ("Sign-in was cancelled or did not finish within 5 minutes.", false);
        }
        if (text.Contains("AADSTS700016", StringComparison.Ordinal) || text.Contains("unauthorized_client", StringComparison.Ordinal) ||
            text.Contains("AADSTS90002", StringComparison.Ordinal) || text.Contains("AADSTS700054", StringComparison.Ordinal))
        {
            return ("Microsoft did not accept the Application (client) ID. Check that it was copied correctly and that the app registration's " +
                    "\"Supported account types\" include personal Microsoft accounts. You will be asked for the ID again next time.\n\n" + text, true);
        }
        if (text.Contains("AADSTS50011", StringComparison.Ordinal))
        {
            return ("The app registration is missing the redirect URI. Add the platform \"Mobile and desktop applications\" " +
                    "with the redirect URI http://localhost, then try again.\n\n" + text, false);
        }
        return ("Sign-in failed.\n\n" + text, false);
    }
}
