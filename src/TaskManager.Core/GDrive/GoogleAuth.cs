using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskManager.Services;

namespace TaskManager.GDrive;

/// <summary>Client credentials (from the user's Google Cloud project) and the refresh token.</summary>
public sealed class GoogleAccount
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string? RefreshToken { get; set; }
    public string? Email { get; set; }
    public bool FullAccess { get; set; }
}

/// <summary>
/// OAuth 2.0 for installed apps (loopback redirect + PKCE). The account file is encrypted with
/// Windows DPAPI for the current user, so the desktop app and the MCP server (same user) share it.
/// </summary>
public sealed class GoogleAuth(AppPaths paths)
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(100) };

    const string ScopeFile = "https://www.googleapis.com/auth/drive.file";
    const string ScopeFull = "https://www.googleapis.com/auth/drive";
    static readonly byte[] Entropy = "TaskManager.GoogleDrive"u8.ToArray();

    readonly SemaphoreSlim _tokenLock = new(1, 1);
    string? _accessToken;
    DateTimeOffset _expires;

    string FilePath => Path.Combine(paths.Root, "google-account.bin");

    public GoogleAccount? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<GoogleAccount>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    void Save(GoogleAccount a) =>
        File.WriteAllBytes(FilePath, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(a), Entropy, DataProtectionScope.CurrentUser));

    public bool IsSignedIn => Load()?.RefreshToken is { Length: > 0 };

    public void SignOut()
    {
        var a = Load();
        _accessToken = null;
        if (a is null) return;
        if (a.RefreshToken is { } rt)
            _ = Http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = rt }));
        a.RefreshToken = null;
        a.Email = null;
        Save(a); // keep client id/secret so signing in again is one click
    }

    public void ForgetAccessToken() => _accessToken = null;

    /// <summary>Opens the browser for consent and waits for the redirect to a local port.</summary>
    public async Task<GoogleAccount> SignIn(string clientId, string clientSecret, bool fullAccess, Action<string> openBrowser, CancellationToken ct)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (clientId.Length == 0) throw new ArgumentException("Нужен Client ID");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var scope = fullAccess ? ScopeFull : ScopeFile;
            openBrowser("https://accounts.google.com/o/oauth2/v2/auth?" + Query(new()
            {
                ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = scope,
                ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state,
                ["access_type"] = "offline", ["prompt"] = "consent",
            }));

            var query = await WaitForRedirect(listener, ct);
            if (query.GetValueOrDefault("error") is { } error) throw new InvalidOperationException($"Вход отменён: {error}");
            if (query.GetValueOrDefault("state") != state) throw new InvalidOperationException("Неверный ответ авторизации (state)");
            var code = query.GetValueOrDefault("code") ?? throw new InvalidOperationException("Google не вернул код авторизации");

            var token = await TokenRequest(new()
            {
                ["code"] = code, ["client_id"] = clientId, ["client_secret"] = clientSecret, ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
            }, ct);
            var refresh = token["refresh_token"]?.GetValue<string>() ?? throw new InvalidOperationException("Google не выдал refresh token");
            var account = new GoogleAccount { ClientId = clientId, ClientSecret = clientSecret, RefreshToken = refresh, FullAccess = fullAccess };
            _accessToken = token["access_token"]!.GetValue<string>();
            _expires = DateTimeOffset.UtcNow.AddSeconds(token["expires_in"]?.GetValue<int>() ?? 3000) - TimeSpan.FromMinutes(2);
            Save(account);
            account.Email = await FetchEmail(ct);
            Save(account);
            return account;
        }
        finally
        {
            listener.Stop();
        }
    }

    static async Task<Dictionary<string, string>> WaitForRedirect(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, ct);
            var requestLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0]; // GET /?code=...&state=... HTTP/1.1
            var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
            if (!target.Contains('?')) { await Respond(stream, 404, "Not found"); continue; } // e.g. /favicon.ico
            var query = target[(target.IndexOf('?') + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
            var ok = query.ContainsKey("code");
            await Respond(stream, 200, ok
                ? "<h2>TaskManager подключён к Google Диску</h2><p>Можно закрыть эту вкладку.</p>"
                : "<h2>Вход не выполнен</h2><p>Вернитесь в TaskManager.</p>");
            return query;
        }
    }

    static async Task Respond(NetworkStream stream, int status, string html)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><body style=\"font-family:Segoe UI;background:#0f1012;color:#e6e6e8;padding:40px\">{html}</body>");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    /// <summary>Valid access token, refreshed when needed.</summary>
    public async Task<string> AccessToken(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expires) return _accessToken;
            var a = Load();
            if (a?.RefreshToken is not { Length: > 0 } rt) throw new InvalidOperationException("Не выполнен вход в Google Диск");
            JsonNode token;
            try
            {
                token = await TokenRequest(new()
                {
                    ["client_id"] = a.ClientId, ["client_secret"] = a.ClientSecret, ["refresh_token"] = rt, ["grant_type"] = "refresh_token",
                }, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("invalid_grant"))
            {
                throw new InvalidOperationException("Вход в Google истёк или был отозван — войдите заново (⋯ → Google Диск)");
            }
            _accessToken = token["access_token"]!.GetValue<string>();
            _expires = DateTimeOffset.UtcNow.AddSeconds(token["expires_in"]?.GetValue<int>() ?? 3000) - TimeSpan.FromMinutes(2);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    async Task<string?> FetchEmail(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
        req.Headers.Authorization = new("Bearer", await AccessToken(ct));
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["user"]?["emailAddress"]?.GetValue<string>();
    }

    static async Task<JsonNode> TokenRequest(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = JsonNode.Parse(body);
            throw new InvalidOperationException($"Google OAuth: {err?["error"]?.GetValue<string>()} {err?["error_description"]?.GetValue<string>()}".Trim());
        }
        return JsonNode.Parse(body)!;
    }

    static string Query(Dictionary<string, string> p) =>
        string.Join('&', p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
