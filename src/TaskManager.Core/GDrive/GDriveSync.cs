using System.Security.Cryptography;
using Dapper;
using TaskManager.Data;

namespace TaskManager.GDrive;

public sealed record SyncResult(int Uploaded, int Downloaded, int DeletedLocal, int DeletedRemote, int Conflicts, int Skipped)
{
    public bool NothingChanged => Uploaded + Downloaded + DeletedLocal + DeletedRemote + Conflicts == 0;

    public override string ToString()
    {
        if (NothingChanged) return "без изменений";
        var parts = new List<string>();
        if (Uploaded > 0) parts.Add($"↑ {Uploaded}");
        if (Downloaded > 0) parts.Add($"↓ {Downloaded}");
        if (DeletedLocal + DeletedRemote > 0) parts.Add($"удалено {DeletedLocal + DeletedRemote}");
        if (Conflicts > 0) parts.Add($"конфликтов {Conflicts}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Two-way sync of a knowledge base source folder with a Google Drive folder.
/// For every path it compares the local file, the Drive file and the state recorded after the last sync:
/// a side that differs from the recorded state has changed. Changes on one side are copied to the other,
/// deletions are propagated (local → .tm-trash, Drive → Drive trash), and when both sides changed
/// both versions are kept ("… (конфликт …)").
/// </summary>
public sealed class GDriveSync(Db db, IDriveApi drive)
{
    public const string LocalTrash = ".tm-trash";
    const long MaxFileSize = 100L * 1024 * 1024;

    sealed class State
    {
        public string Path { get; set; } = "";
        public bool IsFolder { get; set; }
        public string DriveId { get; set; } = "";
        public string? DriveMd5 { get; set; }
        public string? LocalMd5 { get; set; }
        public long? LocalSize { get; set; }
        public long? LocalMtime { get; set; }
    }

    sealed record Local(string Full, long Size, long Mtime)
    {
        public string? Md5;
    }

    long _sourceId;
    string _root = "";
    Dictionary<string, State> _state = null!;
    Dictionary<string, DriveItem> _remoteFolders = null!;
    int _up, _down, _delLocal, _delRemote, _conflicts, _skipped;

    public async Task<SyncResult> Run(long sourceId, CancellationToken ct)
    {
        _sourceId = sourceId;
        _up = _down = _delLocal = _delRemote = _conflicts = _skipped = 0;
        var folderId = db.Conn.ExecuteScalar<string?>("SELECT folder_id FROM gdrive_sources WHERE source_id=@sourceId", new { sourceId })
                       ?? throw new InvalidOperationException("Для источника не настроен Google Диск");
        _root = db.Conn.ExecuteScalar<string?>("SELECT path FROM kb_sources WHERE id=@sourceId", new { sourceId })
                ?? throw new InvalidOperationException("Источник не найден");
        if (!Directory.Exists(_root)) throw new InvalidOperationException($"Папка не найдена: {_root}");

        var rootItem = await drive.Get(folderId, ct)
                       ?? throw new InvalidOperationException("Папка на Google Диске не найдена — удалена, в корзине или нет доступа");

        _state = db.Conn.Query<State>("SELECT * FROM gdrive_state WHERE source_id=@sourceId", new { sourceId })
            .ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);

        // ---- Remote snapshot ----
        _remoteFolders = new(StringComparer.OrdinalIgnoreCase) { [""] = rootItem };
        var remoteFiles = new Dictionary<string, DriveItem>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, string Id)>([("", rootItem.Id)]);
        while (queue.Count > 0)
        {
            var (parent, id) = queue.Dequeue();
            foreach (var item in (await drive.ListChildren(id, ct)).OrderByDescending(i => i.Modified))
            {
                if (!IsSyncableName(item.Name)) { if (!item.Name.StartsWith('.')) _skipped++; continue; }
                var path = Join(parent, item.Name);
                if (item.IsFolder)
                {
                    if (_remoteFolders.TryAdd(path, item)) queue.Enqueue((path, item.Id));
                }
                else if (item.Md5 is null || item.Size > MaxFileSize) _skipped++; // Google Docs files have no content to sync
                else remoteFiles.TryAdd(path, item); // duplicates: keep the most recently modified
            }
        }

        // ---- Local snapshot ----
        var localFiles = new Dictionary<string, Local>(StringComparer.OrdinalIgnoreCase);
        var localFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Walk(_root, "", localFiles, localFolders);

        // ---- Files ----
        var paths = localFiles.Keys.Concat(remoteFiles.Keys)
            .Concat(_state.Values.Where(s => !s.IsFolder).Select(s => s.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            localFiles.TryGetValue(path, out var l);
            remoteFiles.TryGetValue(path, out var r);
            _state.TryGetValue(path, out var s);
            if (s is { IsFolder: true }) s = null;
            if (l is not null) l.Md5 = s is not null && s.LocalSize == l.Size && s.LocalMtime == l.Mtime ? s.LocalMd5 : Md5(l.Full);

            var localChanged = l is not null && (s is null || l.Md5 != s.LocalMd5);
            var remoteChanged = r is not null && (s is null || r.Md5 != s.DriveMd5 || r.Id != s.DriveId);

            if (l is not null && r is not null)
            {
                if (l.Md5 == r.Md5) { if (localChanged || remoteChanged || s is null) Record(path, r.Id, r.Md5, l); }
                else if (localChanged && remoteChanged) await Conflict(path, l, r, ct);
                else if (localChanged) await UpdateRemote(path, l, r.Id, ct);
                else if (remoteChanged) await Download(path, r, ct);
            }
            else if (l is not null) // missing on Drive
            {
                if (s is null || localChanged) { if (s is not null) Forget(path); await Upload(path, l, ct); }
                else { TrashLocal(path, l); Forget(path); localFiles.Remove(path); }
            }
            else if (r is not null) // missing locally
            {
                if (s is null || remoteChanged) await Download(path, r, ct);
                else { await TrashRemote(r.Id, ct); Forget(path); remoteFiles.Remove(path); }
            }
            else if (s is not null) Forget(path); // gone on both sides
        }

        // ---- Folders (including empty ones) ----
        bool LocalHasFiles(string folder) => Directory.Exists(FullPath(folder)) &&
            Directory.EnumerateFiles(FullPath(folder), "*", SearchOption.AllDirectories).Any(f => !Rel(f).Split('/').Any(p => p.StartsWith('.')));
        bool RemoteHasFiles(string folder) => remoteFiles.Keys.Any(p => p.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));

        foreach (var folder in localFolders.OrderBy(f => f.Length))
        {
            if (_remoteFolders.ContainsKey(folder)) { RecordFolder(folder, _remoteFolders[folder].Id); continue; }
            if (_state.TryGetValue(folder, out var s) && s.IsFolder && !LocalHasFiles(folder))
            {
                // Deleted on Drive and nothing left locally: remove the empty local folder.
                try { Directory.Delete(FullPath(folder), true); _delLocal++; } catch (IOException) { }
                Forget(folder);
            }
            else await EnsureRemoteFolder(folder, ct);
        }
        foreach (var (folder, item) in _remoteFolders.Where(f => f.Key.Length > 0).OrderBy(f => f.Key.Length))
        {
            if (Directory.Exists(FullPath(folder))) { RecordFolder(folder, item.Id); continue; }
            if (_state.TryGetValue(folder, out var s) && s.IsFolder && !RemoteHasFiles(folder))
            {
                await TrashRemote(item.Id, ct); // deleted locally and empty on Drive
                Forget(folder);
            }
            else
            {
                Directory.CreateDirectory(FullPath(folder));
                RecordFolder(folder, item.Id);
            }
        }
        foreach (var s in _state.Values.Where(s => s.IsFolder && !_remoteFolders.ContainsKey(s.Path) && !Directory.Exists(FullPath(s.Path))).ToList())
            Forget(s.Path);

        db.Conn.Execute("UPDATE gdrive_sources SET last_sync=@now, last_error=NULL WHERE source_id=@sourceId", new { now = Db.Now(), sourceId });
        return new SyncResult(_up, _down, _delLocal, _delRemote, _conflicts, _skipped);
    }

    // ---------------- Operations ----------------

    async Task Upload(string path, Local l, CancellationToken ct)
    {
        var parentId = await EnsureRemoteFolder(Parent(path), ct);
        DriveItem item;
        await using (var fs = File.OpenRead(l.Full)) item = await drive.Upload(parentId, Name(path), fs, ct);
        Record(path, item.Id, item.Md5 ?? l.Md5, l);
        _up++;
    }

    async Task UpdateRemote(string path, Local l, string driveId, CancellationToken ct)
    {
        DriveItem item;
        await using (var fs = File.OpenRead(l.Full)) item = await drive.Update(driveId, fs, ct);
        Record(path, item.Id, item.Md5 ?? l.Md5, l);
        _up++;
    }

    async Task Download(string path, DriveItem r, CancellationToken ct)
    {
        var full = FullPath(path);
        await DownloadTo(r, full, ct);
        var info = new FileInfo(full);
        Record(path, r.Id, r.Md5, new Local(full, info.Length, info.LastWriteTimeUtc.Ticks) { Md5 = r.Md5 });
        if (_remoteFolders.TryGetValue(Parent(path), out var parent)) RecordFolder(Parent(path), parent.Id);
        _down++;
    }

    async Task DownloadTo(DriveItem r, string full, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmpDir = Path.Combine(_root, LocalTrash, "tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N"));
        try
        {
            await using (var fs = File.Create(tmp)) await drive.Download(r.Id, fs, ct);
            File.Move(tmp, full, overwrite: true); // atomic replace: an editor never sees a half-written file
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Both sides changed: the Drive version is saved next to the file as a conflict copy, the local one wins.</summary>
    async Task Conflict(string path, Local l, DriveItem r, CancellationToken ct)
    {
        var stamp = r.Modified.ToLocalTime().ToString("yyyy-MM-dd HH-mm");
        var copy = Join(Parent(path), $"{Path.GetFileNameWithoutExtension(Name(path))} (конфликт Google Диск {stamp}){Path.GetExtension(path)}");
        var copyFull = FullPath(copy);
        for (var i = 2; File.Exists(copyFull); i++)
            copyFull = FullPath(copy = Join(Parent(path), $"{Path.GetFileNameWithoutExtension(Name(path))} (конфликт Google Диск {stamp} {i}){Path.GetExtension(path)}"));
        await DownloadTo(r, copyFull, ct);
        var info = new FileInfo(copyFull);
        await Upload(copy, new Local(copyFull, info.Length, info.LastWriteTimeUtc.Ticks) { Md5 = r.Md5 }, ct);
        await UpdateRemote(path, l, r.Id, ct);
        _up -= 2;
        _conflicts++;
    }

    void TrashLocal(string path, Local l)
    {
        var target = Path.Combine(_root, LocalTrash, DateTime.Now.ToString("yyyyMMdd-HHmmss"), path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(l.Full, target, overwrite: true);
        _delLocal++;
    }

    async Task TrashRemote(string id, CancellationToken ct)
    {
        try { await drive.Trash(id, ct); }
        catch (DriveException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { }
        _delRemote++;
    }

    async Task<string> EnsureRemoteFolder(string folder, CancellationToken ct)
    {
        if (_remoteFolders.TryGetValue(folder, out var existing)) return existing.Id;
        var parentId = await EnsureRemoteFolder(Parent(folder), ct);
        var created = await drive.CreateFolder(parentId, Name(folder), ct);
        _remoteFolders[folder] = created;
        RecordFolder(folder, created.Id);
        return created.Id;
    }

    // ---------------- State ----------------

    void Record(string path, string driveId, string? driveMd5, Local l)
    {
        var info = new FileInfo(l.Full);
        var s = new State
        {
            Path = path, DriveId = driveId, DriveMd5 = driveMd5, LocalMd5 = l.Md5,
            LocalSize = info.Exists ? info.Length : l.Size, LocalMtime = info.Exists ? info.LastWriteTimeUtc.Ticks : l.Mtime,
        };
        _state[path] = s;
        db.Conn.Execute("""
            INSERT OR REPLACE INTO gdrive_state(source_id, path, is_folder, drive_id, drive_md5, local_md5, local_size, local_mtime)
            VALUES (@sourceId, @Path, 0, @DriveId, @DriveMd5, @LocalMd5, @LocalSize, @LocalMtime)
            """, new { sourceId = _sourceId, s.Path, s.DriveId, s.DriveMd5, s.LocalMd5, s.LocalSize, s.LocalMtime });
    }

    void RecordFolder(string path, string driveId)
    {
        if (path.Length == 0 || _state.TryGetValue(path, out var s) && s.IsFolder && s.DriveId == driveId) return;
        _state[path] = new State { Path = path, IsFolder = true, DriveId = driveId };
        db.Conn.Execute("""
            INSERT OR REPLACE INTO gdrive_state(source_id, path, is_folder, drive_id) VALUES (@sourceId, @path, 1, @driveId)
            """, new { sourceId = _sourceId, path, driveId });
    }

    void Forget(string path)
    {
        _state.Remove(path);
        db.Conn.Execute("DELETE FROM gdrive_state WHERE source_id=@sourceId AND path=@path", new { sourceId = _sourceId, path });
    }

    // ---------------- Helpers ----------------

    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Hidden names (.obsidian, .git, .tm-trash) and names Windows can't store are not synced.</summary>
    static bool IsSyncableName(string name) =>
        name.Length > 0 && !name.StartsWith('.') && name.IndexOfAny(Invalid) < 0 && !name.EndsWith(' ') && !name.EndsWith('.');

    void Walk(string dir, string rel, Dictionary<string, Local> files, HashSet<string> folders)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (!IsSyncableName(name)) continue;
            var info = new FileInfo(f);
            if (info.Length > MaxFileSize) { _skipped++; continue; }
            files[Join(rel, name)] = new Local(f, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(d);
            if (!IsSyncableName(name) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Join(rel, name);
            folders.Add(path);
            Walk(d, path, files, folders);
        }
    }

    static string Md5(string full)
    {
        using var fs = File.OpenRead(full);
        return Convert.ToHexStringLower(MD5.HashData(fs));
    }

    string FullPath(string rel) => rel.Length == 0 ? _root : Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
    string Rel(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');
    static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;
    static string Parent(string p) => p.Contains('/') ? p[..p.LastIndexOf('/')] : "";
    static string Name(string p) => p[(p.LastIndexOf('/') + 1)..];
}
