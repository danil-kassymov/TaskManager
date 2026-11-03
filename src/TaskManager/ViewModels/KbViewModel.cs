using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskManager.Kb;
using TaskManager.Markdown;
using TaskManager.Services;

namespace TaskManager.ViewModels;

public sealed partial class TagChip(string name, string color, int count = 0) : ObservableObject
{
    public string Name { get; } = name;
    [ObservableProperty] private string _color = color;
    public int Count { get; } = count;
    [ObservableProperty] private bool _isActive;
    public string Display => "#" + Name;
}

public sealed partial class KbNode(KbViewModel owner, bool isFolder, string path) : ObservableObject
{
    public bool IsFolder { get; } = isFolder;
    public string Path { get; } = path;
    public string Name => IsFolder ? Path.Split('/')[^1] : KnowledgeBase.TitleOf(Path);
    public string Folder => KnowledgeBase.FolderOf(Path);
    public ObservableCollection<KbNode> Children { get; } = [];
    [ObservableProperty] private IReadOnlyList<TagChip> _tags = [];
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _modified = "";

    partial void OnIsExpandedChanged(bool value) => owner.RememberExpanded(this, value);
}

/// <summary>"База данных" tab: a tree of one source's folders and .md files plus the open entry.</summary>
public sealed partial class KbViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _refreshTimer;
    readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _watcher;
    string _signature = "";
    Dictionary<string, string> _colors = new(StringComparer.CurrentCultureIgnoreCase);

    public KnowledgeBase Kb => _main.S.Kb;
    public MainViewModel Main => _main;

    [ObservableProperty] private long _sourceId;
    [ObservableProperty] private string _sourceName = "";
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private bool _sourceMissing;
    [ObservableProperty] private bool _sourceSync;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string? _tagFilter;
    [ObservableProperty] private KbEntryViewModel? _entry;
    [ObservableProperty] private int _entryCount;

    public BulkObservableCollection<KbNode> Tree { get; } = [];
    public BulkObservableCollection<KbNode> Results { get; } = [];
    public BulkObservableCollection<TagChip> Tags { get; } = [];
    public bool IsFiltering => Filter.Trim().Length > 0 || TagFilter is not null;

    public KbViewModel(MainViewModel main)
    {
        _main = main;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
            Entry?.ReloadIfChanged();
        };
    }

    public void Open(long sourceId)
    {
        if (sourceId == SourceId && _watcher is not null) { Refresh(); return; }
        Close();
        var s = Kb.Source(sourceId);
        SourceId = s.Id;
        SourceName = s.Name;
        SourcePath = s.Path;
        SourceSync = s.ObsidianSync;
        _signature = "";
        _expanded.Clear();
        Filter = "";
        TagFilter = null;
        SourceMissing = !Directory.Exists(s.Path);
        if (!SourceMissing)
        {
            _watcher = new FileSystemWatcher(s.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler changed = (_, _) => Application.Current?.Dispatcher.BeginInvoke(Schedule);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(Schedule);
            _watcher.EnableRaisingEvents = true;
        }
        Refresh();
    }

    void Schedule()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void Close()
    {
        Entry?.Flush();
        Entry = null;
        _watcher?.Dispose();
        _watcher = null;
        SourceId = 0;
        Tree.ReplaceAll([]);
        Results.ReplaceAll([]);
        Tags.ReplaceAll([]);
    }

    partial void OnFilterChanged(string value) { OnPropertyChanged(nameof(IsFiltering)); Refresh(); }
    partial void OnTagFilterChanged(string? value) { OnPropertyChanged(nameof(IsFiltering)); Refresh(); }

    public void RememberExpanded(KbNode node, bool expanded)
    {
        if (expanded) _expanded.Add(node.Path);
        else _expanded.Remove(node.Path);
    }

    public string ColorOf(string tag) => _colors.TryGetValue(tag, out var c) ? c : "#9A9CA5";

    IReadOnlyList<TagChip> Chips(IEnumerable<string> tags) => tags.Select(t => new TagChip(t, ColorOf(t))).ToList();

    /// <summary>Re-reads the source. Rebuilds the tree only when files/folders were added, removed or renamed.</summary>
    public void Refresh()
    {
        if (SourceId == 0) return;
        if (!Directory.Exists(SourcePath))
        {
            SourceMissing = true;
            Tree.ReplaceAll([]);
            return;
        }
        SourceMissing = false;
        var entries = Kb.Entries(SourceId);
        var folders = Kb.Folders(SourceId);
        _colors = Kb.TagColors(entries.SelectMany(e => e.Tags));
        EntryCount = entries.Count;

        var tagCounts = entries.SelectMany(e => e.Tags).GroupBy(t => t, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new TagChip(g.First(), ColorOf(g.First()), g.Count()) { IsActive = string.Equals(g.First(), TagFilter, StringComparison.CurrentCultureIgnoreCase) })
            .OrderByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Tags.ReplaceAll(tagCounts);

        var byPath = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var signature = string.Join("|", folders) + "||" + string.Join("|", entries.Select(e => e.Path).Order(StringComparer.OrdinalIgnoreCase));
        if (signature != _signature)
        {
            _signature = signature;
            Tree.ReplaceAll(BuildTree(folders, entries));
        }
        else
        {
            foreach (var node in AllNodes(Tree).Where(n => !n.IsFolder))
                if (byPath.TryGetValue(node.Path, out var e)) Update(node, e);
        }

        if (IsFiltering)
        {
            var found = Kb.Search(SourceId, Filter, TagFilter).OrderByDescending(e => e.Modified);
            Results.ReplaceAll(found.Select(e => Update(new KbNode(this, false, e.Path), e)));
        }
        else Results.ReplaceAll([]);

        if (Entry is not null)
        {
            Entry.RefreshTags();
            foreach (var n in AllNodes(Tree).Concat(Results)) n.IsSelected = n.Path.Equals(Entry.Path, StringComparison.OrdinalIgnoreCase);
        }
    }

    KbNode Update(KbNode node, KbEntry e)
    {
        node.Tags = Chips(e.Tags);
        node.Modified = IssueRow.Ago(new DateTimeOffset(e.Modified).ToUnixTimeMilliseconds());
        return node;
    }

    List<KbNode> BuildTree(List<string> folders, List<KbEntry> entries)
    {
        var roots = new List<KbNode>();
        var map = new Dictionary<string, KbNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in folders) // sorted: parents come before children
        {
            var node = new KbNode(this, true, f) { IsExpanded = _expanded.Contains(f) };
            map[f] = node;
            var parent = KnowledgeBase.FolderOf(f);
            if (parent.Length > 0 && map.TryGetValue(parent, out var p)) p.Children.Add(node);
            else roots.Add(node);
        }
        foreach (var e in entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = Update(new KbNode(this, false, e.Path), e);
            if (e.Folder.Length > 0 && map.TryGetValue(e.Folder, out var p)) p.Children.Add(node);
            else roots.Add(node);
        }
        // Folders first, then files — inside every level.
        static void Order(IList<KbNode> list)
        {
            var sorted = list.OrderBy(n => n.IsFolder ? 0 : 1).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            list.Clear();
            foreach (var n in sorted) { list.Add(n); Order(n.Children); }
        }
        Order(roots);
        return roots;
    }

    static IEnumerable<KbNode> AllNodes(IEnumerable<KbNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(AllNodes(n.Children)));

    public void OpenEntry(string path)
    {
        if (Entry?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true) return;
        Entry?.Flush();
        try
        {
            Entry = new KbEntryViewModel(this, SourceId, path);
        }
        catch (ArgumentException ex)
        {
            _main.Status = ex.Message;
            Entry = null;
            return;
        }
        // Reveal it in the tree.
        var folder = KnowledgeBase.FolderOf(path);
        while (folder.Length > 0)
        {
            _expanded.Add(folder);
            folder = KnowledgeBase.FolderOf(folder);
        }
        foreach (var n in AllNodes(Tree))
        {
            if (n.IsFolder && _expanded.Contains(n.Path)) n.IsExpanded = true;
            n.IsSelected = !n.IsFolder && n.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    public void CloseEntry()
    {
        Entry?.Flush();
        Entry = null;
    }

    /// <summary>Creates "Без названия.md" in the folder (or next to the open entry) and opens it with the title focused.</summary>
    [RelayCommand]
    public void NewEntry(KbNode? folder)
    {
        if (SourceId == 0 || SourceMissing) return;
        var dir = folder is null ? Entry?.Folder ?? "" : folder.IsFolder ? folder.Path : folder.Folder;
        var e = Kb.Create(SourceId, dir, "Без названия", TagFilter is { } t ? $"#{t}\n\n" : "");
        Refresh();
        OpenEntry(e.Path);
        if (Entry is not null) Entry.IsEditing = true;
        _main.RequestFocus("kb-title");
    }

    public void CreateFolder(KbNode? parent, string name)
    {
        var dir = parent is null ? "" : parent.IsFolder ? parent.Path : parent.Folder;
        try
        {
            var rel = Kb.CreateFolder(SourceId, dir, name);
            if (dir.Length > 0) _expanded.Add(dir);
            _expanded.Add(rel);
            Refresh();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _main.ShowError("Новая папка", ex.Message);
        }
    }

    public void Rename(KbNode node, string newName)
    {
        try
        {
            var wasOpen = Entry is not null && (Entry.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase) ||
                                                 Entry.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase));
            Entry?.Flush();
            var newRel = Kb.Rename(SourceId, node.Path, newName);
            if (node.IsFolder && _expanded.Remove(node.Path)) _expanded.Add(newRel);
            if (wasOpen && Entry is not null)
            {
                var openPath = node.IsFolder ? newRel + Entry.Path[node.Path.Length..] : newRel;
                Entry = null;
                Refresh();
                OpenEntry(openPath);
            }
            else Refresh();
            _main.Reload();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _main.ShowError("Переименование", ex.Message);
        }
    }

    public void Move(KbNode node, KbNode? targetFolder)
    {
        try
        {
            Entry?.Flush();
            var openPath = Entry?.Path;
            var newRel = Kb.Move(SourceId, node.Path, targetFolder?.Path ?? "");
            if (targetFolder is not null) _expanded.Add(targetFolder.Path);
            Refresh();
            if (openPath is not null && openPath.Equals(node.Path, StringComparison.OrdinalIgnoreCase))
            {
                Entry = null;
                OpenEntry(newRel);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _main.ShowError("Перемещение", ex.Message);
        }
    }

    public void Delete(KbNode node)
    {
        try
        {
            if (Entry is not null && (Entry.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase) ||
                                      Entry.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase)))
                Entry = null;
            RecycleBin.Delete(Kb.Resolve(SourceId, node.Path));
            Kb.ForgetDeleted(SourceId, node.Path);
            Refresh();
            _main.Status = $"«{node.Name}» перемещено в корзину";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _main.ShowError("Удаление", ex.Message);
        }
    }

    public void Reveal(KbNode? node)
    {
        var path = node is null ? SourcePath : Kb.Resolve(SourceId, node.Path);
        var args = node is null ? $"\"{path}\"" : $"/select,\"{path}\"";
        System.Diagnostics.Process.Start("explorer.exe", args);
    }

    [RelayCommand]
    void ToggleTag(TagChip chip) =>
        TagFilter = string.Equals(TagFilter, chip.Name, StringComparison.CurrentCultureIgnoreCase) ? null : chip.Name;

    [RelayCommand]
    void ClearFilters()
    {
        Filter = "";
        TagFilter = null;
    }

    public void SetTagColor(string tag, string color)
    {
        Kb.SetTagColor(tag, color);
        _signature = ""; // rebuild so every chip picks up the new color
        Refresh();
    }
}

/// <summary>An open .md file: title = file name, content saved back to disk with a debounce.</summary>
public sealed partial class KbEntryViewModel : ObservableObject
{
    readonly KbViewModel _owner;
    readonly DispatcherTimer _saveTimer;
    bool _loading, _dirty;
    DateTime _lastWrite;

    public long SourceId { get; }
    [ObservableProperty] private string _path;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _meta = "";
    public string Folder => KnowledgeBase.FolderOf(Path);
    public string Breadcrumb => _owner.SourceName + (Folder.Length > 0 ? " / " + Folder.Replace("/", " / ") : "");
    public ObservableCollection<TagChip> Tags { get; } = [];
    public ObservableCollection<IssueRow> LinkedIssues { get; } = [];
    public KbViewModel Owner => _owner;

    public KbEntryViewModel(KbViewModel owner, long sourceId, string path)
    {
        _owner = owner;
        SourceId = sourceId;
        _path = path;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => Flush();
        Load();
        IsEditing = string.IsNullOrWhiteSpace(Content);
    }

    string FullPath => _owner.Kb.Resolve(SourceId, Path);

    void Load()
    {
        _loading = true;
        Content = _owner.Kb.Read(SourceId, Path);
        Title = KnowledgeBase.TitleOf(Path);
        _lastWrite = File.GetLastWriteTimeUtc(FullPath);
        _loading = false;
        RefreshTags();
        RefreshIssues();
        OnPropertyChanged(nameof(Breadcrumb));
    }

    public void RefreshTags()
    {
        var tags = KnowledgeBase.ParseTags(Content).ToList();
        var colors = _owner.Kb.TagColors(tags);
        Tags.Clear();
        foreach (var t in tags) Tags.Add(new TagChip(t, colors[t]));
        Meta = $"{System.IO.Path.GetFileName(Path)} · изменён {File.GetLastWriteTime(FullPath):d MMM yyyy, HH:mm}";
    }

    public void RefreshIssues()
    {
        LinkedIssues.Clear();
        foreach (var id in _owner.Kb.IssuesLinking(SourceId, Path))
            if (_owner.Main.S.Issues.Get(id) is { } issue) LinkedIssues.Add(new IssueRow(issue, _owner.Main.LabelsById));
    }

    /// <summary>Picks up edits made by another program, unless there are unsaved local changes.</summary>
    public void ReloadIfChanged()
    {
        if (_dirty) return;
        try
        {
            if (!File.Exists(FullPath)) return;
            if (File.GetLastWriteTimeUtc(FullPath) != _lastWrite) Load();
        }
        catch (IOException) { }
    }

    partial void OnContentChanged(string value)
    {
        if (_loading) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        try
        {
            _owner.Kb.Write(SourceId, Path, Content);
            _lastWrite = File.GetLastWriteTimeUtc(FullPath);
            _dirty = false;
            RefreshTags();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.Main.Status = $"Не удалось сохранить: {ex.Message}";
        }
    }

    /// <summary>Title edits rename the file.</summary>
    public void CommitTitle()
    {
        var name = Title.Trim();
        if (name.Length == 0 || name == KnowledgeBase.TitleOf(Path)) { Title = KnowledgeBase.TitleOf(Path); return; }
        Flush();
        _owner.Rename(new KbNode(_owner, false, Path), name);
    }

    [RelayCommand]
    void Edit() => IsEditing = true;

    [RelayCommand]
    void Preview()
    {
        Flush();
        if (!string.IsNullOrWhiteSpace(Content)) IsEditing = false;
    }

    public void ToggleTask(int offset)
    {
        Content = MarkdownRenderer.ToggleTaskAt(Content, offset);
        Flush();
    }

    [RelayCommand]
    void OpenIssue(IssueRow row) => _owner.Main.OpenIssueFromKb(row.Id);

    [RelayCommand]
    void LinkIssue() => _owner.Main.Palette.OpenIssuePicker(issueId =>
    {
        _owner.Kb.Link(issueId, SourceId, Path);
        RefreshIssues();
        _owner.Main.Reload();
    });

    [RelayCommand]
    void UnlinkIssue(IssueRow row)
    {
        _owner.Kb.Unlink(row.Id, SourceId, Path);
        RefreshIssues();
    }

    [RelayCommand]
    void CopyLink()
    {
        try
        {
            Clipboard.SetText(KnowledgeBase.LinkMarkdown(SourceId, Path));
            _owner.Main.Status = "Ссылка на запись скопирована — вставь её в описание задачи";
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
