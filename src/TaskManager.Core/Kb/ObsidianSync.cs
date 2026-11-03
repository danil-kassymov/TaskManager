using System.Diagnostics;
using System.Text.Json;

namespace TaskManager.Kb;

/// <summary>An Obsidian vault: the nearest folder with a ".obsidian" directory, as registered in obsidian.json.</summary>
public sealed record ObsidianVault(string Root, string Name, string? Id, bool IsOpen);

/// <summary>
/// Makes Obsidian Sync pick up changes written by TaskManager or the MCP server.
/// A running Obsidian with the vault open already watches the files and syncs them, so nothing is done then.
/// Otherwise the vault is opened via obsidian://open, and Obsidian Sync syncs it on start.
/// Calls are debounced so a burst of edits opens Obsidian once.
/// </summary>
public sealed class ObsidianSync : IDisposable
{
    readonly string _configPath;
    readonly Func<bool> _isRunning;
    readonly Action<string> _launch;
    readonly TimeSpan _delay;
    readonly Dictionary<string, Timer> _timers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Human-readable messages ("Obsidian запущен для синхронизации …").</summary>
    public event Action<string>? Log;
    /// <summary>Raised right after Obsidian was launched (the app uses it to take focus back).</summary>
    public event Action? Launched;

    public ObsidianSync(string? configPath = null, Func<bool>? isRunning = null, Action<string>? launch = null, TimeSpan? delay = null)
    {
        _configPath = configPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
        _isRunning = isRunning ?? (() => Process.GetProcessesByName("Obsidian").Length > 0);
        _launch = launch ?? (uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }));
        _delay = delay ?? TimeSpan.FromSeconds(4);
    }

    /// <summary>Vault containing <paramref name="path"/>, or null if the folder is not inside an Obsidian vault.</summary>
    public ObsidianVault? FindVault(string path)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(path));
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".obsidian"))) dir = dir.Parent;
        if (dir is null) return null;
        var root = Path.TrimEndingDirectorySeparator(dir.FullName);
        string? id = null;
        var open = false;
        try
        {
            if (File.Exists(_configPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
                if (doc.RootElement.TryGetProperty("vaults", out var vaults))
                    foreach (var v in vaults.EnumerateObject())
                    {
                        if (!v.Value.TryGetProperty("path", out var p) || p.GetString() is not { } vp) continue;
                        if (!Path.TrimEndingDirectorySeparator(vp).Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
                        id = v.Name;
                        open = v.Value.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True;
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable config: fall back to opening the vault by name.
        }
        return new ObsidianVault(root, dir.Name, id, open);
    }

    /// <summary>Call after anything in the source changed. Does nothing for sources with sync turned off.</summary>
    public void Schedule(KbSource source)
    {
        if (!source.ObsidianSync) return;
        var root = source.Path;
        lock (_timers)
        {
            if (_timers.TryGetValue(root, out var t)) t.Change(_delay, Timeout.InfiniteTimeSpan);
            else _timers[root] = new Timer(_ => Fire(root), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    void Fire(string root)
    {
        lock (_timers)
        {
            if (_timers.Remove(root, out var t)) t.Dispose();
        }
        try { SyncNow(root); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log?.Invoke($"Не удалось запустить Obsidian: {ex.Message}");
        }
    }

    /// <summary>Returns true if Obsidian had to be launched.</summary>
    public bool SyncNow(string sourcePath)
    {
        if (FindVault(sourcePath) is not { } vault) return false;
        if (vault.IsOpen && _isRunning()) return false; // Obsidian is watching the files already
        _launch($"obsidian://open?vault={Uri.EscapeDataString(vault.Id ?? vault.Name)}");
        Launched?.Invoke();
        Log?.Invoke($"Obsidian открыт для синхронизации хранилища «{vault.Name}»");
        return true;
    }

    /// <summary>True if the folder is (inside) an Obsidian vault.</summary>
    public static bool IsInVault(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".obsidian"))) return true;
        return false;
    }

    /// <summary>Runs pending syncs right away (the process is exiting).</summary>
    public void Dispose()
    {
        List<string> pending;
        lock (_timers)
        {
            pending = _timers.Keys.ToList();
            foreach (var t in _timers.Values) t.Dispose();
            _timers.Clear();
        }
        foreach (var root in pending)
        {
            try { SyncNow(root); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        }
    }
}
