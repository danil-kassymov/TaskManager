using System.Security.Cryptography;
using Dapper;
using TaskManager.Data;
using TaskManager.GDrive;
using TaskManager.Kb;

namespace TaskManager.Tests;

/// <summary>In-memory Drive: folders and files with md5 like the real API.</summary>
sealed class FakeDrive : IDriveApi
{
    public sealed class Node
    {
        public string Id = "", Name = "", Parent = "";
        public bool IsFolder, Trashed;
        public byte[] Data = [];
        public DateTimeOffset Modified = DateTimeOffset.UtcNow;
    }

    public readonly Dictionary<string, Node> Nodes = [];
    int _next;
    public int Uploads, Updates, Downloads;

    public FakeDrive() => Nodes["root"] = new Node { Id = "root", Name = "root", IsFolder = true };

    static string Md5(byte[] d) => Convert.ToHexStringLower(MD5.HashData(d));

    DriveItem Item(Node n) => new(n.Id, n.Name, n.IsFolder, n.IsFolder ? null : Md5(n.Data), n.Modified, n.Data.Length);

    bool Alive(Node n) => !n.Trashed && (n.Parent.Length == 0 || Nodes.TryGetValue(n.Parent, out var p) && Alive(p));

    public Task<DriveItem?> Get(string id, CancellationToken ct) =>
        Task.FromResult(Nodes.TryGetValue(id, out var n) && Alive(n) ? Item(n) : null);

    public Task<List<DriveItem>> ListChildren(string folderId, CancellationToken ct) =>
        Task.FromResult(Nodes.Values.Where(n => n.Parent == folderId && !n.Trashed).Select(Item).ToList());

    public string Add(string parent, string name, bool folder = false, string? text = null)
    {
        var id = $"id{++_next}";
        Nodes[id] = new Node { Id = id, Name = name, Parent = parent, IsFolder = folder, Data = text is null ? [] : System.Text.Encoding.UTF8.GetBytes(text) };
        return id;
    }

    public Task<DriveItem> CreateFolder(string parentId, string name, CancellationToken ct) => Task.FromResult(Item(Nodes[Add(parentId, name, true)]));

    public async Task<DriveItem> Upload(string parentId, string name, Stream content, CancellationToken ct)
    {
        Uploads++;
        var id = Add(parentId, name);
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Nodes[id].Data = ms.ToArray();
        return Item(Nodes[id]);
    }

    public async Task<DriveItem> Update(string fileId, Stream content, CancellationToken ct)
    {
        Updates++;
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        Nodes[fileId].Data = ms.ToArray();
        Nodes[fileId].Modified = DateTimeOffset.UtcNow;
        return Item(Nodes[fileId]);
    }

    public async Task Download(string fileId, Stream target, CancellationToken ct)
    {
        Downloads++;
        await target.WriteAsync(Nodes[fileId].Data, ct);
    }

    public Task Trash(string id, CancellationToken ct)
    {
        Nodes[id].Trashed = true;
        return Task.CompletedTask;
    }

    /// <summary>"folder/file.md" → text of the live file, or null.</summary>
    public string? Read(string path)
    {
        var parent = "root";
        var parts = path.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var n = Nodes.Values.FirstOrDefault(x => x.Parent == parent && x.Name == parts[i] && !x.Trashed);
            if (n is null) return null;
            if (i == parts.Length - 1) return n.IsFolder ? "<folder>" : System.Text.Encoding.UTF8.GetString(n.Data);
            parent = n.Id;
        }
        return null;
    }

    public string Find(string path)
    {
        var parent = "root";
        foreach (var part in path.Split('/'))
            parent = Nodes.Values.First(x => x.Parent == parent && x.Name == part && !x.Trashed).Id;
        return parent;
    }
}

public sealed class GDriveSyncTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"tm-gd-{Guid.NewGuid():N}");
    readonly Db _db = new(":memory:");
    readonly FakeDrive _drive = new();
    readonly long _sourceId;

    public GDriveSyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "notes"));
        Directory.CreateDirectory(Path.Combine(_dir, ".obsidian"));
        Write("readme.md", "hello");
        Write("notes/a.md", "note a");
        Write(".obsidian/app.json", "{}");
        _sourceId = new KnowledgeBase(_db).AddSource(_dir).Id;
        _db.Conn.Execute("INSERT INTO gdrive_sources(source_id, folder_id) VALUES (@_sourceId, 'root')", new { _sourceId });
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }

    void Write(string rel, string text) => File.WriteAllText(Path.Combine(_dir, rel.Replace('/', '\\')), text);
    string? Read(string rel) => File.Exists(Path.Combine(_dir, rel)) ? File.ReadAllText(Path.Combine(_dir, rel)) : null;
    Task<SyncResult> Sync() => new GDriveSync(_db, _drive).Run(_sourceId, CancellationToken.None);

    [Fact]
    public async Task FirstSync_UploadsEverythingExceptHidden_ThenIsStable()
    {
        var r = await Sync();
        Assert.Equal(2, r.Uploaded);
        Assert.Equal("note a", _drive.Read("notes/a.md"));
        Assert.Null(_drive.Read(".obsidian/app.json"));

        var again = await Sync();
        Assert.True(again.NothingChanged);
        Assert.Equal(2, _drive.Uploads);
        Assert.Equal(0, _drive.Downloads);
    }

    [Fact]
    public async Task RemoteChanges_AreDownloaded()
    {
        await Sync();
        _drive.Add(_drive.Find("notes"), "new.md", text: "from phone");
        _drive.Nodes[_drive.Find("readme.md")].Data = "hello edited remotely"u8.ToArray();
        var folder = _drive.Add("root", "Ideas", folder: true);
        _drive.Add(folder, "idea.md", text: "idea");

        var r = await Sync();
        Assert.Equal(3, r.Downloaded);
        Assert.Equal("from phone", Read("notes/new.md"));
        Assert.Equal("hello edited remotely", Read("readme.md"));
        Assert.Equal("idea", Read("Ideas/idea.md"));
        Assert.True((await Sync()).NothingChanged);
    }

    [Fact]
    public async Task LocalChanges_AreUploaded()
    {
        await Sync();
        Write("readme.md", "hello edited locally!");
        Write("notes/b.md", "new b");
        var r = await Sync();
        Assert.Equal(2, r.Uploaded);
        Assert.Equal(1, _drive.Updates);
        Assert.Equal("hello edited locally!", _drive.Read("readme.md"));
        Assert.Equal("new b", _drive.Read("notes/b.md"));
    }

    [Fact]
    public async Task Deletions_PropagateToTrash()
    {
        await Sync();
        File.Delete(Path.Combine(_dir, "readme.md"));
        await _drive.Trash(_drive.Find("notes/a.md"), CancellationToken.None);

        var r = await Sync();
        Assert.Equal(1, r.DeletedRemote);
        Assert.Equal(1, r.DeletedLocal);
        Assert.Null(_drive.Read("readme.md"));
        Assert.Null(Read("notes/a.md"));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, GDriveSync.LocalTrash), "a.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EditedFileWins_OverDeletionOnTheOtherSide()
    {
        await Sync();
        Write("readme.md", "still needed, edited");
        await _drive.Trash(_drive.Find("readme.md"), CancellationToken.None);
        await Sync();
        Assert.Equal("still needed, edited", _drive.Read("readme.md"));
    }

    [Fact]
    public async Task Conflict_KeepsBothVersions()
    {
        await Sync();
        Write("notes/a.md", "local version!!");
        _drive.Nodes[_drive.Find("notes/a.md")].Data = "remote version"u8.ToArray();

        var r = await Sync();
        Assert.Equal(1, r.Conflicts);
        Assert.Equal("local version!!", Read("notes/a.md"));
        Assert.Equal("local version!!", _drive.Read("notes/a.md"));
        var copy = Assert.Single(Directory.GetFiles(Path.Combine(_dir, "notes"), "a (конфликт*"));
        Assert.Equal("remote version", File.ReadAllText(copy));
        Assert.Equal("remote version", _drive.Read("notes/" + Path.GetFileName(copy)));
        Assert.True((await Sync()).NothingChanged);
    }

    [Fact]
    public async Task SameContentOnBothSides_IsNotTransferred()
    {
        _drive.Add("root", "readme.md", text: "hello");
        var r = await Sync();
        Assert.Equal(1, r.Uploaded); // only notes/a.md
        Assert.Equal(0, _drive.Downloads);
    }

    [Fact]
    public async Task EmptyFolders_SyncBothWaysAndDeletionsPropagate()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty-local"));
        _drive.Add("root", "empty-remote", folder: true);
        await Sync();
        Assert.Equal("<folder>", _drive.Read("empty-local"));
        Assert.True(Directory.Exists(Path.Combine(_dir, "empty-remote")));

        Directory.Delete(Path.Combine(_dir, "empty-local"));
        await _drive.Trash(_drive.Find("empty-remote"), CancellationToken.None);
        await Sync();
        Assert.Null(_drive.Read("empty-local"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "empty-remote")));
    }

    [Fact]
    public async Task FilesOverTheLimit_AreIgnored_NotTreatedAsDeleted()
    {
        Write("big.md", new string('x', 5000));
        await Sync(); // synced while under the default limit

        // Another device with a small limit (or the file grew): it must be left alone on both sides.
        var small = new GDriveSync(_db, _drive, maxFileSize: 1000);
        var r = await small.Run(_sourceId, CancellationToken.None);
        Assert.Equal(0, r.DeletedLocal + r.DeletedRemote);
        Assert.NotNull(Read("big.md"));
        Assert.NotNull(_drive.Read("big.md"));

        _drive.Add("root", "huge-remote.pdf", text: new string('y', 5000));
        r = await small.Run(_sourceId, CancellationToken.None);
        Assert.Equal(0, r.Downloaded);
        Assert.Null(Read("huge-remote.pdf"));
        Assert.Equal(2, r.Skipped);
    }

    [Fact]
    public async Task MissingDriveFolder_FailsClearly()
    {
        await _drive.Trash("root", CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(Sync);
        Assert.Contains("не найдена", ex.Message);
    }

    [Theory]
    [InlineData("https://drive.google.com/drive/folders/1AbCdEfGhIjKlMnOp?usp=sharing", "1AbCdEfGhIjKlMnOp")]
    [InlineData("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("1AbCdEfGhIjKlMnOp", "1AbCdEfGhIjKlMnOp")]
    [InlineData("not a link", null)]
    public void ParseFolderId(string input, string? expected) => Assert.Equal(expected, GDriveClient.ParseFolderId(input));
}
