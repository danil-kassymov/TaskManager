using TaskManager.Data;
using TaskManager.Kb;

namespace TaskManager.Tests;

public sealed class ObsidianSyncTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"tm-obs-{Guid.NewGuid():N}");
    readonly string _vault;
    readonly string _config;
    readonly List<string> _launched = [];
    bool _running;

    public ObsidianSyncTests()
    {
        _vault = Path.Combine(_root, "My Vault");
        Directory.CreateDirectory(Path.Combine(_vault, ".obsidian"));
        Directory.CreateDirectory(Path.Combine(_vault, "Projects"));
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        _config = Path.Combine(_root, "obsidian.json");
    }

    public void Dispose() => Directory.Delete(_root, true);

    void WriteConfig(bool open)
    {
        var path = System.Text.Json.JsonSerializer.Serialize(_vault);
        File.WriteAllText(_config, "{\"vaults\":{\"abc123\":{\"path\":" + path + ",\"ts\":1,\"open\":" + (open ? "true" : "false") + "}}}");
    }

    ObsidianSync Sync(TimeSpan? delay = null) => new(_config, () => _running, _launched.Add, delay ?? TimeSpan.FromMilliseconds(50));

    [Fact]
    public void FindVault_WalksUpAndReadsConfig()
    {
        WriteConfig(open: true);
        var v = Sync().FindVault(Path.Combine(_vault, "Projects"));
        Assert.NotNull(v);
        Assert.Equal("My Vault", v!.Name);
        Assert.Equal("abc123", v.Id);
        Assert.True(v.IsOpen);
        Assert.Null(Sync().FindVault(Path.Combine(_root, "plain")));
    }

    [Fact]
    public void SyncNow_SkipsWhenVaultIsOpenInRunningObsidian()
    {
        WriteConfig(open: true);
        _running = true;
        Assert.False(Sync().SyncNow(_vault));
        Assert.Empty(_launched);
    }

    [Theory]
    [InlineData(false, true)]  // Obsidian runs, but another vault is open
    [InlineData(true, false)]  // config says open, but Obsidian is closed
    public void SyncNow_OpensVaultById(bool open, bool running)
    {
        WriteConfig(open);
        _running = running;
        Assert.True(Sync().SyncNow(Path.Combine(_vault, "Projects")));
        Assert.Equal(["obsidian://open?vault=abc123"], _launched);
    }

    [Fact]
    public void SyncNow_UnregisteredVaultOpensByName()
    {
        Sync().SyncNow(_vault);
        Assert.Equal(["obsidian://open?vault=My%20Vault"], _launched);
    }

    [Fact]
    public void Schedule_DebouncesAndRespectsFlag()
    {
        using var db = new Db(":memory:");
        var kb = new KnowledgeBase(db);
        var source = kb.AddSource(Path.Combine(_vault, "Projects"));
        Assert.True(source.ObsidianSync); // inside a vault → on by default
        Assert.False(kb.AddSource(Path.Combine(_root, "plain")).ObsidianSync);

        using var sync = Sync(TimeSpan.FromMilliseconds(150));
        kb.Changed = sync.Schedule;
        kb.Create(source.Id, "", "a");
        kb.Create(source.Id, "", "b");
        kb.Write(source.Id, "a.md", "edited");
        Thread.Sleep(600);
        Assert.Single(_launched);

        kb.SetObsidianSync(source.Id, false);
        kb.Write(source.Id, "a.md", "again");
        Thread.Sleep(400);
        Assert.Single(_launched);
    }

    [Fact]
    public void Dispose_FlushesPending()
    {
        var sync = Sync(TimeSpan.FromMinutes(1));
        sync.Schedule(new KbSource { Path = _vault, ObsidianSync = true });
        sync.Dispose();
        Assert.Single(_launched);
    }
}
