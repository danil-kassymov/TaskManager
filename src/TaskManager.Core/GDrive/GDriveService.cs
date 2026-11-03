using Dapper;
using TaskManager.Data;
using TaskManager.Kb;
using TaskManager.Services;

namespace TaskManager.GDrive;

public sealed record GDriveInfo(long SourceId, string FolderId, string FolderName, DateTimeOffset? LastSync, string? LastError)
{
    public string Link => $"https://drive.google.com/drive/folders/{FolderId}";
}

/// <summary>
/// Runs Google Drive sync for knowledge base sources: debounced after local changes, periodically,
/// or on demand. Every run opens its own database connection (runs happen off the UI thread) and
/// takes a per-source lock file, so the app and the MCP server never sync the same source at once.
/// </summary>
public sealed class GDriveService : IDisposable
{
    readonly AppPaths _paths;
    readonly Func<IDriveApi> _api;
    readonly TimeSpan _delay;
    readonly Dictionary<long, Timer> _pending = [];
    readonly Dictionary<long, SemaphoreSlim> _running = [];
    readonly CancellationTokenSource _cts = new();
    Timer? _periodic;

    public GoogleAuth Auth { get; }

    /// <summary>(sourceId, message) — progress and errors, raised on a worker thread.</summary>
    public event Action<long, string>? Status;
    /// <summary>Raised after a sync that changed local files.</summary>
    public event Action<long, SyncResult>? Synced;

    public GDriveService(AppPaths paths, Func<IDriveApi>? api = null, TimeSpan? delay = null)
    {
        _paths = paths;
        Auth = new GoogleAuth(paths);
        _api = api ?? (() => new GDriveClient(Auth));
        _delay = delay ?? TimeSpan.FromSeconds(5);
    }

    Db OpenDb() => new(_paths.Database);

    public static GDriveInfo? Info(Db db, long sourceId) =>
        db.Conn.Query<(long SourceId, string FolderId, string FolderName, long? LastSync, string? LastError)>(
                "SELECT source_id, folder_id, folder_name, last_sync, last_error FROM gdrive_sources WHERE source_id=@sourceId", new { sourceId })
            .Select(r => new GDriveInfo(r.SourceId, r.FolderId, r.FolderName,
                r.LastSync is { } t ? DateTimeOffset.FromUnixTimeMilliseconds(t) : null, r.LastError))
            .FirstOrDefault();

    public static List<long> EnabledSources(Db db) => db.Conn.Query<long>("SELECT source_id FROM gdrive_sources").ToList();

    /// <summary>Connects a source to a Drive folder (an existing one by id/link, or a new folder named like the source) and syncs it.</summary>
    public async Task<GDriveInfo> Enable(long sourceId, string? folder, CancellationToken ct = default)
    {
        if (!Auth.IsSignedIn) throw new InvalidOperationException("Сначала войдите в Google (⋯ → Google Диск)");
        var api = _api();
        DriveItem item;
        string sourceName;
        using (var db = OpenDb())
            sourceName = db.Conn.ExecuteScalar<string?>("SELECT name FROM kb_sources WHERE id=@sourceId", new { sourceId })
                         ?? throw new ArgumentException("Источник не найден");
        if (string.IsNullOrWhiteSpace(folder))
            item = await api.CreateFolder("", sourceName, ct);
        else
        {
            var id = GDriveClient.ParseFolderId(folder) ?? throw new ArgumentException("Не похоже на ссылку на папку Google Диска или её ID");
            item = await api.Get(id, ct) ?? throw new InvalidOperationException(
                "Папка не найдена или нет доступа. С ограниченным доступом (drive.file) видны только папки, созданные TaskManager — " +
                "оставьте поле пустым, чтобы создать новую, или включите полный доступ при входе.");
            if (!item.IsFolder) throw new ArgumentException("По ссылке не папка");
        }
        using (var db = OpenDb())
        {
            using var tx = db.Conn.BeginTransaction();
            db.Conn.Execute("DELETE FROM gdrive_state WHERE source_id=@sourceId", new { sourceId }, tx);
            db.Conn.Execute("INSERT OR REPLACE INTO gdrive_sources(source_id, folder_id, folder_name) VALUES (@sourceId, @id, @name)",
                new { sourceId, id = item.Id, name = item.Name }, tx);
            tx.Commit();
        }
        await SyncNow(sourceId, ct);
        using (var db = OpenDb()) return Info(db, sourceId)!;
    }

    public void Disable(long sourceId)
    {
        lock (_pending) if (_pending.Remove(sourceId, out var t)) t.Dispose();
        using var db = OpenDb();
        db.Conn.Execute("DELETE FROM gdrive_sources WHERE source_id=@sourceId; DELETE FROM gdrive_state WHERE source_id=@sourceId", new { sourceId });
    }

    /// <summary>Call after local changes in a source; syncs once things are quiet for a few seconds.</summary>
    public void Schedule(KbSource source)
    {
        lock (_pending)
        {
            if (_cts.IsCancellationRequested) return;
            if (_pending.TryGetValue(source.Id, out var t)) t.Change(_delay, Timeout.InfiniteTimeSpan);
            else _pending[source.Id] = new Timer(_ => _ = Fire(source.Id), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    async Task Fire(long sourceId)
    {
        lock (_pending) if (_pending.Remove(sourceId, out var t)) t.Dispose();
        await SyncNow(sourceId, _cts.Token);
    }

    /// <summary>Syncs every connected source now and then every <paramref name="interval"/> (picks up changes from other devices).</summary>
    public void StartPeriodic(TimeSpan interval) =>
        _periodic = new Timer(_ => _ = SyncAll(), null, TimeSpan.FromSeconds(2), interval);

    public async Task SyncAll()
    {
        if (!Auth.IsSignedIn) return;
        List<long> ids;
        using (var db = OpenDb()) ids = EnabledSources(db);
        foreach (var id in ids) await SyncNow(id, _cts.Token);
    }

    /// <summary>Runs one sync; errors are stored and reported through <see cref="Status"/> instead of thrown.</summary>
    public async Task<SyncResult?> SyncNow(long sourceId, CancellationToken ct = default)
    {
        SemaphoreSlim gate;
        lock (_running) gate = _running.TryGetValue(sourceId, out var g) ? g : _running[sourceId] = new SemaphoreSlim(1, 1);
        await gate.WaitAsync(ct);
        try
        {
            using var db = OpenDb();
            if (Info(db, sourceId) is null) return null;
            if (!Auth.IsSignedIn)
            {
                SetError(db, sourceId, "Не выполнен вход в Google");
                return null;
            }
            using var fileLock = await AcquireLock(sourceId, ct);
            Status?.Invoke(sourceId, "Синхронизация с Google Диском…");
            var result = await new GDriveSync(db, _api()).Run(sourceId, ct);
            Status?.Invoke(sourceId, $"Google Диск: {result}");
            if (!result.NothingChanged) Synced?.Invoke(sourceId, result);
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) when (ex is DriveException or InvalidOperationException or ArgumentException or IOException
                                       or UnauthorizedAccessException or HttpRequestException)
        {
            var message = ex is HttpRequestException ? "нет связи с Google (оффлайн?) — повторю позже" : ex.Message;
            try { using var db = OpenDb(); SetError(db, sourceId, message); } catch (Exception) { }
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    void SetError(Db db, long sourceId, string message)
    {
        db.Conn.Execute("UPDATE gdrive_sources SET last_error=@message WHERE source_id=@sourceId", new { message, sourceId });
        Status?.Invoke(sourceId, $"Google Диск: {message}");
    }

    /// <summary>Exclusive lock file shared with other TaskManager processes (app + MCP server).</summary>
    async Task<FileStream> AcquireLock(long sourceId, CancellationToken ct)
    {
        var path = Path.Combine(_paths.Root, $"gdrive-{sourceId}.lock");
        for (var i = 0; ; i++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (i < 120) { await Task.Delay(500, ct); }
        }
    }

    /// <summary>Stops timers and runs syncs that were still waiting (so edits made just before exit are not left behind).</summary>
    public void Dispose()
    {
        _periodic?.Dispose();
        List<long> pending;
        lock (_pending)
        {
            pending = _pending.Keys.ToList();
            foreach (var t in _pending.Values) t.Dispose();
            _pending.Clear();
        }
        if (pending.Count > 0)
            Task.Run(async () => { foreach (var id in pending) await SyncNow(id); }).Wait(TimeSpan.FromSeconds(30));
        _cts.Cancel();
    }
}
