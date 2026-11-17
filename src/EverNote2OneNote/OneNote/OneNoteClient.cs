using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EverNote2OneNote.OneNote;

/// <summary>A OneNote request that failed.</summary>
public sealed class OneNoteException(HttpStatusCode? status, string message) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;

    /// <summary>True if OneNote definitely rejected the request (nothing was created), so retrying differently is safe.</summary>
    public bool Rejected => Status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType;
}

public sealed record CreatedPage(string Id, string? ClientUrl, string? WebUrl);

/// <summary>
/// Minimal Microsoft Graph OneNote client (delegated access, signed-in user only).
/// It only creates notebooks, sections and pages, appends to pages it created, and reads them back.
/// </summary>
public sealed class OneNoteClient(HttpClient http, Func<CancellationToken, Task<string>> getAccessToken)
{
    public const string BaseUrl = "https://graph.microsoft.com/v1.0/me/onenote/";

    /// <summary>Delays used when OneNote throttles (HTTP 429) or has not caught up yet (HTTP 404 right after creation).</summary>
    internal TimeSpan[] RetryDelays { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Finds a notebook by name, or creates it. Never changes an existing notebook.</summary>
    public async Task<string> GetOrCreateNotebookAsync(string name, CancellationToken ct)
    {
        var existing = await FindByNameAsync("notebooks?$select=id,displayName", name, ct);
        if (existing is not null)
        {
            return existing;
        }
        var json = await SendJsonAsync(() => JsonPost("notebooks", new JsonObject { ["displayName"] = name }), retryNotFound: false, ct);
        return json["id"]!.GetValue<string>();
    }

    /// <summary>Finds a section by name in the notebook, or creates it.</summary>
    public async Task<string> GetOrCreateSectionAsync(string notebookId, string name, CancellationToken ct)
    {
        var path = $"notebooks/{Uri.EscapeDataString(notebookId)}/sections";
        var existing = await FindByNameAsync(path + "?$select=id,displayName", name, ct);
        if (existing is not null)
        {
            return existing;
        }
        // A new notebook can take a moment to become available, so 404 is retried here.
        var json = await SendJsonAsync(() => JsonPost(path, new JsonObject { ["displayName"] = name }), retryNotFound: true, ct);
        return json["id"]!.GetValue<string>();
    }

    /// <summary>Creates a page from HTML, uploading the referenced parts in the same request.</summary>
    public async Task<CreatedPage> CreatePageAsync(string sectionId, string html, IReadOnlyList<PagePart> parts, CancellationToken ct)
    {
        var json = await SendJsonAsync(() =>
        {
            var content = new MultipartFormDataContent("e2o-" + Guid.NewGuid().ToString("N"));
            content.Add(Part(new StringContent(html, Encoding.UTF8, "text/html"), "Presentation"));
            AddParts(content, parts);
            return new HttpRequestMessage(HttpMethod.Post, $"sections/{Uri.EscapeDataString(sectionId)}/pages") { Content = content };
        }, retryNotFound: true, ct);

        return new CreatedPage(
            json["id"]!.GetValue<string>(),
            json["links"]?["oneNoteClientUrl"]?["href"]?.GetValue<string>(),
            json["links"]?["oneNoteWebUrl"]?["href"]?.GetValue<string>());
    }

    /// <summary>Appends HTML (and the parts it references) to the end of a page this tool created.</summary>
    public async Task AppendAsync(string pageId, string html, IReadOnlyList<PagePart> parts, CancellationToken ct)
    {
        var commands = new JsonArray { new JsonObject { ["target"] = "body", ["action"] = "append", ["content"] = html } };
        using var response = await SendAsync(() =>
        {
            var content = new MultipartFormDataContent("e2o-" + Guid.NewGuid().ToString("N"));
            content.Add(Part(new StringContent(commands.ToJsonString(), Encoding.UTF8, "application/json"), "Commands"));
            AddParts(content, parts);
            return new HttpRequestMessage(HttpMethod.Patch, $"pages/{Uri.EscapeDataString(pageId)}/content") { Content = content };
        }, retryNotFound: true, ct);
    }

    /// <summary>Returns the page's HTML as stored by OneNote.</summary>
    public async Task<string> GetPageContentAsync(string pageId, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/content"), retryNotFound: true, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string?> FindByNameAsync(string path, string name, CancellationToken ct)
    {
        string? url = path;
        while (url is not null)
        {
            var requestUrl = url;
            var json = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, requestUrl), retryNotFound: false, ct);
            foreach (var item in json["value"]?.AsArray() ?? [])
            {
                if (string.Equals(item?["displayName"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return item!["id"]!.GetValue<string>();
                }
            }
            url = json["@odata.nextLink"]?.GetValue<string>();
        }
        return null;
    }

    private static HttpRequestMessage JsonPost(string path, JsonObject body) =>
        new(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpContent Part(HttpContent content, string name)
    {
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
        return content;
    }

    private static void AddParts(MultipartFormDataContent content, IReadOnlyList<PagePart> parts)
    {
        foreach (var part in parts)
        {
            var bytes = new ByteArrayContent(part.Data);
            bytes.Headers.ContentType = new MediaTypeHeaderValue(part.Mime);
            content.Add(Part(bytes, part.Name));
        }
    }

    private async Task<JsonNode> SendJsonAsync(Func<HttpRequestMessage> request, bool retryNotFound, CancellationToken ct)
    {
        using var response = await SendAsync(request, retryNotFound, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    /// <summary>
    /// Sends a request. Retries only when OneNote did not process it: throttling (429), or 404 for something just created.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, bool retryNotFound, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = create();
            request.RequestUri = new Uri(new Uri(BaseUrl), request.RequestUri!.OriginalString);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getAccessToken(ct));
            var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            bool retry = response.StatusCode == HttpStatusCode.TooManyRequests ||
                         (retryNotFound && response.StatusCode == HttpStatusCode.NotFound);
            if (retry && attempt < RetryDelays.Length)
            {
                response.Dispose();
                await Task.Delay(RetryDelays[attempt], ct);
                continue;
            }
            var message = await ErrorMessageAsync(response, ct);
            var status = response.StatusCode;
            response.Dispose();
            throw new OneNoteException(status, message);
        }
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        string? detail = null;
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            detail = error is null ? null : $"{error["message"]?.GetValue<string>()} (code {error["code"]?.GetValue<string>()})";
        }
        catch (JsonException)
        {
        }
        var reason = response.StatusCode == HttpStatusCode.Unauthorized ? "Not signed in, or the sign-in has expired. " : "";
        return $"{reason}OneNote returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail ?? Truncate(body)}";
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";
}
