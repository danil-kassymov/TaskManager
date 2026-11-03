using System.Net.Http;
using TaskManager.GDrive;
using TaskManager.Services;

namespace TaskManager.Tests;

public sealed class GoogleAuthTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"tm-auth-{Guid.NewGuid():N}");

    public void Dispose() => Directory.Delete(_root, true);

    static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public async Task SignIn_BuildsPkceRequest_AndHandlesBrowserRedirect()
    {
        var auth = new GoogleAuth(new AppPaths(_root));
        string? authUrl = null;
        var task = auth.SignIn("client-id.apps.googleusercontent.com", "secret", fullAccess: false, url => authUrl = url, CancellationToken.None);

        Assert.NotNull(authUrl);
        var q = Query(authUrl!);
        Assert.Equal("https://www.googleapis.com/auth/drive.file", q["scope"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.StartsWith("http://127.0.0.1:", q["redirect_uri"]);

        // The browser comes back with the user's refusal: no network call to Google is made.
        using var http = new HttpClient();
        var page = await http.GetStringAsync($"{q["redirect_uri"]}/?error=access_denied&state={q["state"]}");
        Assert.Contains("Вход не выполнен", page);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Contains("access_denied", ex.Message);
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignIn_RejectsForgedState()
    {
        var auth = new GoogleAuth(new AppPaths(_root));
        string? authUrl = null;
        var task = auth.SignIn("id", "secret", false, url => authUrl = url, CancellationToken.None);
        var q = Query(authUrl!);
        using var http = new HttpClient();
        await http.GetStringAsync($"{q["redirect_uri"]}/?code=abc&state=forged");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Contains("state", ex.Message);
    }

    [Fact]
    public async Task AccessToken_WithoutAccount_Explains()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new GoogleAuth(new AppPaths(_root)).AccessToken(CancellationToken.None));
        Assert.Contains("вход", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
