using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskManager.GDrive;

public sealed record DriveItem(string Id, string Name, bool IsFolder, string? Md5, DateTimeOffset Modified, long Size);

/// <summary>The handful of Google Drive v3 calls the sync needs (an interface so the engine can be tested offline).</summary>
public interface IDriveApi
{
    Task<DriveItem?> Get(string id, CancellationToken ct);
    Task<List<DriveItem>> ListChildren(string folderId, CancellationToken ct);
    Task<DriveItem> CreateFolder(string parentId, string name, CancellationToken ct);
    Task<DriveItem> Upload(string parentId, string name, Stream content, CancellationToken ct);
    Task<DriveItem> Update(string fileId, Stream content, CancellationToken ct);
    Task Download(string fileId, Stream target, CancellationToken ct);
    Task Trash(string id, CancellationToken ct);
}

public sealed class DriveException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Drive REST v3 over HttpClient; access tokens come from <see cref="GoogleAuth"/>.</summary>
public sealed class GDriveClient(GoogleAuth auth) : IDriveApi
{
    const string Api = "https://www.googleapis.com/drive/v3/files";
    const string UploadApi = "https://www.googleapis.com/upload/drive/v3/files";
    const string Fields = "id,name,mimeType,md5Checksum,modifiedTime,size,trashed";
    const string FolderMime = "application/vnd.google-apps.folder";

    async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.AccessToken(ct));
            var res = await GoogleAuth.Http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return res;
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            // Token revoked/expired early: refresh once. Rate limits / server errors: back off and retry.
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { auth.ForgetAccessToken(); continue; }
            if ((res.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError ||
                 res.StatusCode == HttpStatusCode.Forbidden && body.Contains("rateLimitExceeded")) && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }
            throw new DriveException($"Google Drive: {(int)res.StatusCode} {ErrorMessage(body)}", res.StatusCode);
        }
    }

    static string ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; }
        catch (JsonException) { return body; }
    }

    static DriveItem Parse(JsonNode n) => new(
        n["id"]!.GetValue<string>(),
        n["name"]?.GetValue<string>() ?? "",
        n["mimeType"]?.GetValue<string>() == FolderMime,
        n["md5Checksum"]?.GetValue<string>(),
        n["modifiedTime"] is { } m ? DateTimeOffset.Parse(m.GetValue<string>()) : DateTimeOffset.MinValue,
        n["size"] is { } s ? long.Parse(s.GetValue<string>()) : 0);

    static async Task<JsonNode> Json(HttpResponseMessage res, CancellationToken ct)
    {
        using (res) return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
    }

    public async Task<DriveItem?> Get(string id, CancellationToken ct)
    {
        try
        {
            var n = await Json(await Send(() => new HttpRequestMessage(HttpMethod.Get,
                $"{Api}/{Uri.EscapeDataString(id)}?fields={Fields}&supportsAllDrives=true"), ct), ct);
            return n["trashed"]?.GetValue<bool>() == true ? null : Parse(n);
        }
        catch (DriveException ex) when (ex.Status == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<List<DriveItem>> ListChildren(string folderId, CancellationToken ct)
    {
        var result = new List<DriveItem>();
        string? page = null;
        var q = Uri.EscapeDataString($"'{folderId.Replace("'", "\\'")}' in parents and trashed=false");
        do
        {
            var url = $"{Api}?q={q}&fields=nextPageToken,files({Fields})&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true"
                      + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var n = await Json(await Send(() => new HttpRequestMessage(HttpMethod.Get, url), ct), ct);
            foreach (var f in n["files"]!.AsArray()) result.Add(Parse(f!));
            page = n["nextPageToken"]?.GetValue<string>();
        } while (page is not null);
        return result;
    }

    public async Task<DriveItem> CreateFolder(string parentId, string name, CancellationToken ct)
    {
        var meta = new JsonObject { ["name"] = name, ["mimeType"] = FolderMime };
        if (parentId.Length > 0) meta["parents"] = new JsonArray(parentId);
        return Parse(await Json(await Send(() => new HttpRequestMessage(HttpMethod.Post, $"{Api}?fields={Fields}&supportsAllDrives=true")
        {
            Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json"),
        }, ct), ct));
    }

    public async Task<DriveItem> Upload(string parentId, string name, Stream content, CancellationToken ct)
    {
        var bytes = await ReadAll(content, ct);
        var meta = new JsonObject { ["name"] = name, ["parents"] = new JsonArray(parentId) }.ToJsonString();
        return Parse(await Json(await Send(() =>
        {
            var multipart = new MultipartContent("related")
            {
                new StringContent(meta, Encoding.UTF8, "application/json"),
                new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue(MimeOf(name)) } },
            };
            return new HttpRequestMessage(HttpMethod.Post, $"{UploadApi}?uploadType=multipart&fields={Fields}&supportsAllDrives=true") { Content = multipart };
        }, ct), ct));
    }

    public async Task<DriveItem> Update(string fileId, Stream content, CancellationToken ct)
    {
        var bytes = await ReadAll(content, ct);
        return Parse(await Json(await Send(() => new HttpRequestMessage(HttpMethod.Patch,
            $"{UploadApi}/{Uri.EscapeDataString(fileId)}?uploadType=media&fields={Fields}&supportsAllDrives=true")
        {
            Content = new ByteArrayContent(bytes),
        }, ct), ct));
    }

    public async Task Download(string fileId, Stream target, CancellationToken ct)
    {
        using var res = await Send(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Api}/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true"), ct);
        await res.Content.CopyToAsync(target, ct);
    }

    public async Task Trash(string id, CancellationToken ct)
    {
        using var _ = await Send(() => new HttpRequestMessage(HttpMethod.Patch, $"{Api}/{Uri.EscapeDataString(id)}?supportsAllDrives=true")
        {
            Content = new StringContent("{\"trashed\":true}", Encoding.UTF8, "application/json"),
        }, ct);
    }

    static async Task<byte[]> ReadAll(Stream s, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    static string MimeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".md" => "text/markdown",
        ".txt" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    /// <summary>Accepts a folder id or a drive.google.com folder link.</summary>
    public static string? ParseFolderId(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return null;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"/folders/([A-Za-z0-9_-]+)|[?&]id=([A-Za-z0-9_-]+)");
        if (m.Success) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9_-]{10,}$") ? s : null;
    }
}
