using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskManager.Data;
using TaskManager.Models;

namespace TaskManager.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public AppServices S { get; }
    readonly DispatcherTimer _filterTimer;
    bool _suppressReload;

    public ObservableCollection<NavItem> Views { get; } = [];
    public ObservableCollection<NavItem> ProjectNav { get; } = [];
    public ObservableCollection<NavItem> LabelNav { get; } = [];
    public ObservableCollection<NavItem> KbNav { get; } = [];

    public List<Project> Projects { get; private set; } = [];
    public List<Label> Labels { get; private set; } = [];
    public Dictionary<long, Label> LabelsById { get; private set; } = [];

    public BulkObservableCollection<IssueRow> Issues { get; } = [];
    public ICollectionView IssuesView { get; }
    public BulkObservableCollection<BoardColumn> Columns { get; } = [];

    public IReadOnlyList<SortMode> SortModes { get; } = [SortMode.Priority, SortMode.Manual, SortMode.Updated, SortMode.Created];

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private bool _isBoard;
    [ObservableProperty] private SortMode _sortMode;
    [ObservableProperty] private bool _hideClosed;
    [ObservableProperty] private Priority? _priorityFilter;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _header = "";
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private IssueRow? _selectedRow;
    [ObservableProperty] private IssueDetailViewModel? _detail;
    [ObservableProperty] private string? _status;

    public CommandPaletteViewModel Palette { get; }
    public QuickCreateViewModel Create { get; }
    public KbViewModel Kb { get; }

    /// <summary>True when a knowledge base source is selected in the sidebar.</summary>
    public bool IsKbMode => SelectedNav?.Kind == NavKind.KbSource;
    NavItem? _lastIssueNav;

    /// <summary>Raised when the view should move keyboard focus (e.g. "focus-filter").</summary>
    public event Action<string>? FocusRequested;
    public event Action<string, string>? ErrorRaised;

    public MainViewModel(AppServices services)
    {
        S = services;
        IssuesView = CollectionViewSource.GetDefaultView(Issues);
        IssuesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(IssueRow.Status)));
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); Reload(); };

        Palette = new CommandPaletteViewModel(this);
        Create = new QuickCreateViewModel(this);
        Kb = new KbViewModel(this);

        _suppressReload = true;
        _isBoard = S.Settings.BoardView;
        _sortMode = S.Settings.Sort;
        _hideClosed = S.Settings.HideClosed;
        Views.Add(new NavItem(NavKind.Active, "Активные"));
        Views.Add(new NavItem(NavKind.All, "Все задачи"));
        Views.Add(new NavItem(NavKind.Backlog, "Бэклог"));
        Seed.EnsureInitialData(S);
        LoadReferenceData();
        SelectedNav = AllNav().FirstOrDefault(n => n.Token == S.Settings.Nav) ?? Views[0];
        _suppressReload = false;
        Reload();

        _dataVersion = S.Db.DataVersion();
        _externalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _externalTimer.Tick += (_, _) => CheckExternalChanges();
        _externalTimer.Start();

        S.GDrive.Status += (id, msg) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!msg.EndsWith('…') && !msg.EndsWith("без изменений")) Status = msg; // routine periodic checks stay quiet
            if (IsKbMode && Kb.SourceId == id) Kb.UpdateGDriveStatus();
        });
        S.GDrive.Synced += (id, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!IsKbMode || Kb.SourceId != id) return;
            Kb.Refresh();
            Kb.Entry?.ReloadIfChanged();
        });    }

    readonly DispatcherTimer _externalTimer;
    long _dataVersion;

    /// <summary>Picks up commits made by other processes (the MCP server used by Claude).</summary>
    void CheckExternalChanges()
    {
        var version = S.Db.DataVersion();
        if (version == _dataVersion) return;
        _dataVersion = version;
        LoadReferenceData();
        Reload();
        if (Detail is { } d)
        {
            if (S.Issues.Get(d.Id) is null) Detail = null; // deleted elsewhere
            else d.Refresh();
        }
        if (IsKbMode)
        {
            Kb.Refresh(); // tag colors may have changed
            Kb.Entry?.RefreshIssues();
        }
        Status = "Обновлено: изменения из другого процесса";
    }

    IEnumerable<NavItem> AllNav() => Views.Concat(ProjectNav).Concat(LabelNav).Concat(KbNav);

    public void LoadReferenceData()
    {
        Projects = S.Projects.All();
        Labels = S.Labels.All();
        LabelsById = Labels.ToDictionary(l => l.Id);
        var selected = SelectedNav?.Token;
        Sync(ProjectNav, Projects.Select(p => new NavItem(NavKind.Project, p.Name, p.Id, p.Color)));
        Sync(LabelNav, Labels.Select(l => new NavItem(NavKind.Label, l.Name, l.Id, l.Color)));
        var gdrive = TaskManager.GDrive.GDriveService.EnabledSources(S.Db).ToHashSet();
        Sync(KbNav, S.Kb.Sources().Select(s => new NavItem(NavKind.KbSource, s.Name, s.Id) { IsGDrive = gdrive.Contains(s.Id) }));
        if (selected is not null && SelectedNav is { IsEntity: true })
            SelectedNav = AllNav().FirstOrDefault(n => n.Token == selected) ?? Views[0];
    }

    static void Sync(ObservableCollection<NavItem> target, IEnumerable<NavItem> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    partial void OnSelectedNavChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is null) return;
        var value = newValue;
        value.IsSelected = true;
        S.Settings.Nav = value.Token;
        OnPropertyChanged(nameof(IsKbMode));
        if (value.Kind == NavKind.KbSource)
        {
            CloseDetail();
            Header = value.Title;
            try { Kb.Open(value.Id); }
            catch (ArgumentException) { SelectedNav = Views[0]; }
            return;
        }
        _lastIssueNav = value;
        if (oldValue?.Kind == NavKind.KbSource) Kb.Close();
        Reload();
    }

    partial void OnIsBoardChanged(bool value) { S.Settings.BoardView = value; Reload(); }
    partial void OnSortModeChanged(SortMode value) { S.Settings.Sort = value; Reload(); }
    partial void OnHideClosedChanged(bool value) { S.Settings.HideClosed = value; Reload(); }
    partial void OnPriorityFilterChanged(Priority? value) => Reload();

    partial void OnFilterTextChanged(string value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    public Project? CurrentProject =>
        SelectedNav is { Kind: NavKind.Project } n ? Projects.FirstOrDefault(p => p.Id == n.Id) : null;

    IReadOnlyList<IssueStatus> VisibleStatuses()
    {
        IssueStatus[] statuses = SelectedNav?.Kind switch
        {
            NavKind.Active => [IssueStatus.Todo, IssueStatus.InProgress],
            NavKind.Backlog => [IssueStatus.Backlog],
            _ => HideClosed
                ? [IssueStatus.Backlog, IssueStatus.Todo, IssueStatus.InProgress]
                : [IssueStatus.Backlog, IssueStatus.Todo, IssueStatus.InProgress, IssueStatus.Done, IssueStatus.Canceled],
        };
        return statuses;
    }

    public void Reload()
    {
        if (_suppressReload || SelectedNav is null || IsKbMode) return;
        var statuses = VisibleStatuses();
        var q = new IssueQuery
        {
            ProjectId = SelectedNav.Kind == NavKind.Project ? SelectedNav.Id : null,
            LabelId = SelectedNav.Kind == NavKind.Label ? SelectedNav.Id : null,
            Statuses = statuses,
            Priority = PriorityFilter,
            Text = FilterText,
            Sort = IsBoard ? SortMode.Manual : SortMode,
        };
        var rows = S.Issues.Query(q).Select(i => new IssueRow(i, LabelsById)).ToList();
        var selectedId = SelectedRow?.Id ?? Detail?.Id;

        _suppressReload = true;
        Issues.ReplaceAll(rows);
        if (IsBoard)
        {
            var byStatus = rows.ToLookup(r => r.Status);
            var columns = Names.Statuses.Where(statuses.Contains)
                .Where(s => s != IssueStatus.Canceled || byStatus[s].Any());
            Columns.ReplaceAll(columns.Select(s => new BoardColumn(s, byStatus[s])));
        }
        SelectedRow = selectedId is { } id ? rows.FirstOrDefault(r => r.Id == id) : null;
        _suppressReload = false;

        IssueCount = rows.Count;
        Header = SelectedNav.Title;
    }

    partial void OnSelectedRowChanged(IssueRow? value)
    {
        if (_suppressReload || value is null || value.Id == Detail?.Id) return;
        OpenIssue(value.Id);
    }

    public void OpenIssue(long id)
    {
        Detail?.Flush();
        var issue = S.Issues.Get(id);
        if (issue is null) { Detail = null; return; }
        Detail = new IssueDetailViewModel(this, issue);
        if (SelectedRow?.Id != id)
        {
            _suppressReload = true;
            SelectedRow = Issues.FirstOrDefault(r => r.Id == id);
            _suppressReload = false;
        }
    }

    public void OpenIssueByKey(string key)
    {
        if (S.Issues.GetByKey(key) is { } issue) OpenIssue(issue.Id);
        else Status = $"Задача {key} не найдена";
    }

    [RelayCommand]
    public void CloseDetail()
    {
        Detail?.Flush();
        Detail = null;
        _suppressReload = true;
        SelectedRow = null;
        _suppressReload = false;
    }

    [RelayCommand]
    void ToggleView() => IsBoard = !IsBoard;

    [RelayCommand]
    void SelectNav(NavItem item) => SelectedNav = item;

    [RelayCommand]
    void SetSort(SortMode mode) => SortMode = mode;

    [RelayCommand]
    void SetPriorityFilter(Priority? p) => PriorityFilter = PriorityFilter == p ? null : p;

    [RelayCommand]
    public void NewIssue(IssueStatus? status) => Create.Open(status ?? DefaultNewStatus());

    IssueStatus DefaultNewStatus() => SelectedNav?.Kind == NavKind.Backlog ? IssueStatus.Backlog : IssueStatus.Todo;

    /// <summary>J/K and arrow navigation over the visible list.</summary>
    public void MoveSelection(int delta)
    {
        if (Issues.Count == 0) return;
        var ordered = IsBoard ? Columns.SelectMany(c => c.Items).ToList() : Issues.ToList();
        var index = SelectedRow is null ? -1 : ordered.FindIndex(r => r.Id == SelectedRow.Id);
        var next = Math.Clamp(index < 0 ? (delta > 0 ? 0 : ordered.Count - 1) : index + delta, 0, ordered.Count - 1);
        SelectedRow = ordered[next];
    }

    // ---- Issue actions shared by hotkeys, palette and the detail panel ----

    public IssueDetailViewModel? Target => Detail;

    public void SetStatus(long id, IssueStatus status)
    {
        S.Issues.SetStatus(id, status);
        AfterIssueChanged(id);
    }

    public void SetPriority(long id, Priority priority)
    {
        S.Issues.SetPriority(id, priority);
        AfterIssueChanged(id);
    }

    void AfterIssueChanged(long id)
    {
        if (Detail?.Id == id) Detail.Refresh();
        Reload();
    }

    /// <summary>Board drag&amp;drop: drop <paramref name="row"/> before <paramref name="before"/> (null = end).</summary>
    public void MoveOnBoard(IssueRow row, IssueStatus status, IssueRow? before)
    {
        var column = Columns.FirstOrDefault(c => c.Status == status)?.Items.Where(r => r.Id != row.Id).ToList() ?? [];
        double sort;
        var index = before is null ? column.Count : column.FindIndex(r => r.Id == before.Id);
        if (index < 0) index = column.Count;
        if (column.Count == 0) sort = row.Model.Sort;
        else if (index == 0) sort = column[0].Model.Sort - 1;
        else if (index >= column.Count) sort = column[^1].Model.Sort + 1;
        else sort = (column[index - 1].Model.Sort + column[index].Model.Sort) / 2;
        S.Issues.Move(row.Id, status, sort);
        AfterIssueChanged(row.Id);
    }

    public void Archive(long id)
    {
        if (Detail?.Id == id) Detail = null;
        S.Issues.Archive(id);
        Reload();
        Status = "Задача удалена";
    }

    public void ShowError(string title, string message) => ErrorRaised?.Invoke(title, message);

    public void RequestFocus(string target) => FocusRequested?.Invoke(target);

    // ---- Projects and labels ----

    public Project? AddProject(string name, string key)
    {
        try
        {
            var color = Controls.Palette.LabelColors[Projects.Count % Controls.Palette.LabelColors.Length];
            var p = S.Projects.Create(name, key, color);
            LoadReferenceData();
            SelectedNav = ProjectNav.FirstOrDefault(n => n.Id == p.Id);
            return p;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ShowError("Новый проект", ex.Message);
            return null;
        }
    }

    public void UpdateProject(long id, string name, string color)
    {
        S.Projects.Update(id, name, color);
        LoadReferenceData();
        Reload();
    }

    public void ArchiveProject(long id)
    {
        S.Projects.Archive(id);
        if (SelectedNav is { Kind: NavKind.Project } n && n.Id == id) SelectedNav = Views[0];
        LoadReferenceData();
        Reload();
    }

    public Label? AddLabel(string name, string? color = null)
    {
        try
        {
            color ??= Controls.Palette.LabelColors[Labels.Count % Controls.Palette.LabelColors.Length];
            var l = S.Labels.Create(name, color);
            LoadReferenceData();
            Detail?.Refresh();
            return l;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ShowError("Новая метка", ex.Message);
            return null;
        }
    }

    public void UpdateLabel(long id, string name, string color)
    {
        S.Labels.Update(id, name, color);
        LoadReferenceData();
        Detail?.Refresh();
        Reload();
    }

    public void DeleteLabel(long id)
    {
        S.Labels.Delete(id);
        if (SelectedNav is { Kind: NavKind.Label } n && n.Id == id) SelectedNav = Views[0];
        LoadReferenceData();
        Detail?.Refresh();
        Reload();
    }

    // ---- Knowledge base ----

    public void AddKbSource(string path)
    {
        try
        {
            var s = S.Kb.AddSource(path);
            LoadReferenceData();
            SelectedNav = KbNav.FirstOrDefault(n => n.Id == s.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowError("База данных", ex.Message);
        }
    }

    public void RenameKbSource(long id, string name)
    {
        S.Kb.RenameSource(id, name);
        LoadReferenceData();
        if (IsKbMode && Kb.SourceId == id) { Kb.SourceName = name; Header = name; }
    }

    public void RemoveKbSource(long id)
    {
        if (SelectedNav is { Kind: NavKind.KbSource } n && n.Id == id) SelectedNav = _lastIssueNav ?? Views[0];
        S.Kb.RemoveSource(id);
        LoadReferenceData();
        Detail?.Refresh();
    }

    // ---- Google Drive ----

    public async Task<bool> EnableGDrive(long sourceId, string? folder)
    {
        Status = "Подключаю Google Диск…";
        try
        {
            var info = await S.GDrive.Enable(sourceId, folder);
            LoadReferenceData();
            if (IsKbMode && Kb.SourceId == sourceId) Kb.UpdateGDriveStatus();
            Status = $"Google Диск подключён: папка «{info.FolderName}»";
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or TaskManager.GDrive.DriveException or HttpRequestException)
        {
            ShowError("Google Диск", ex is HttpRequestException ? "Нет связи с Google. Проверьте интернет." : ex.Message);
            Status = null;
            return false;
        }
    }

    public void DisableGDrive(long sourceId)
    {
        S.GDrive.Disable(sourceId);
        LoadReferenceData();
        if (IsKbMode && Kb.SourceId == sourceId) Kb.UpdateGDriveStatus();
        Status = "Google Диск отключён. Файлы остались и на компьютере, и на Диске.";
    }

    public void SyncGDriveNow(long sourceId) => _ = S.GDrive.SyncNow(sourceId);

    public void OpenKbEntry(long sourceId, string path)
    {
        Detail?.Flush();
        var nav = KbNav.FirstOrDefault(n => n.Id == sourceId);
        if (nav is null) { Status = "Источник базы не найден"; return; }
        SelectedNav = nav;
        if (S.Kb.Entry(sourceId, path) is null) { Status = $"Запись не найдена: {path}"; return; }
        Kb.OpenEntry(path);
    }

    /// <summary>From a knowledge base entry back to the issues view.</summary>
    public void OpenIssueFromKb(long issueId)
    {
        if (IsKbMode) SelectedNav = _lastIssueNav ?? Views[1];
        if (!Issues.Any(r => r.Id == issueId)) SelectedNav = Views[1]; // "Все задачи", so the row is visible
        OpenIssue(issueId);
    }

    /// <summary>Opens any link from rendered markdown: issue:KEY, kb:…, tag:…, urls and files.</summary>
    public void OpenLink(string url)
    {
        if (url.StartsWith("issue:"))
        {
            if (S.Issues.GetByKey(url["issue:".Length..]) is { } issue) OpenIssueFromKb(issue.Id);
            else Status = $"Задача {url["issue:".Length..]} не найдена";
            return;
        }
        if (TaskManager.Kb.KnowledgeBase.TryParseLink(url, out var sourceId, out var rel))
        {
            OpenKbEntry(sourceId, rel);
            return;
        }
        if (url.StartsWith("tag:"))
        {
            if (IsKbMode) Kb.TagFilter = url["tag:".Length..];
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status = $"Не удалось открыть: {url}";
        }
    }

    public void AfterImport()
    {
        _dataVersion = S.Db.DataVersion();
        Detail = null;
        LoadReferenceData();
        SelectedNav = Views[0];
        Reload();
    }
}
