using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskManager.Kb;
using TaskManager.Markdown;
using TaskManager.Models;

namespace TaskManager.ViewModels;

public sealed partial class IssueDetailViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _saveTimer;
    bool _loading;
    bool _titleDirty, _descriptionDirty;

    public long Id { get; }
    public IReadOnlyList<IssueStatus> Statuses => Names.Statuses;
    public IReadOnlyList<Priority> Priorities => Names.Priorities;
    public IReadOnlyList<Project> Projects => _main.Projects;

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private IssueStatus _status;
    [ObservableProperty] private Priority _priority;
    [ObservableProperty] private Project? _project;
    [ObservableProperty] private IssueRow? _parent;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _newSubtask = "";
    [ObservableProperty] private string _meta = "";

    public ObservableCollection<LabelToggle> LabelChoices { get; } = [];
    public ObservableCollection<Label> SelectedLabels { get; } = [];
    public ObservableCollection<IssueRow> Subtasks { get; } = [];
    public ObservableCollection<KbLink> KbLinks { get; } = [];
    public bool HasSubtasks => Subtasks.Count > 0;
    public string SubtaskProgress => Subtasks.Count == 0 ? "" : $"{Subtasks.Count(s => s.IsClosed)}/{Subtasks.Count}";

    public IssueDetailViewModel(MainViewModel main, Issue issue)
    {
        _main = main;
        Id = issue.Id;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => Flush();
        Load(issue);
        IsEditing = string.IsNullOrWhiteSpace(issue.Description);
    }

    void Load(Issue issue)
    {
        _loading = true;
        Key = issue.Key;
        if (!_titleDirty) Title = issue.Title;
        if (!_descriptionDirty) Description = issue.Description;
        Status = issue.Status;
        Priority = issue.Priority;
        Project = _main.Projects.FirstOrDefault(p => p.Id == issue.ProjectId);
        Parent = issue.ParentId is { } pid && _main.S.Issues.Get(pid) is { } parent ? new IssueRow(parent, _main.LabelsById) : null;

        var ids = issue.GetLabelIds().ToHashSet();
        LabelChoices.Clear();
        SelectedLabels.Clear();
        foreach (var l in _main.Labels)
        {
            LabelChoices.Add(new LabelToggle(l, ids.Contains(l.Id)));
            if (ids.Contains(l.Id)) SelectedLabels.Add(l);
        }

        Subtasks.Clear();
        foreach (var c in _main.S.Issues.Children(issue.Id)) Subtasks.Add(new IssueRow(c, _main.LabelsById));
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtaskProgress));

        KbLinks.Clear();
        foreach (var l in _main.S.Kb.LinksOf(issue.Id)) KbLinks.Add(l);

        var created = DateTimeOffset.FromUnixTimeMilliseconds(issue.CreatedAt).LocalDateTime;
        Meta = $"Создана {created:d MMM yyyy, HH:mm} · обновлена {IssueRow.Ago(issue.UpdatedAt)}";
        _loading = false;
    }

    /// <summary>Re-read from the database (after changes made elsewhere: palette, board, hotkeys).</summary>
    public void Refresh()
    {
        if (_main.S.Issues.Get(Id) is { } issue) Load(issue);
    }

    partial void OnTitleChanged(string value)
    {
        if (_loading) return;
        _titleDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    partial void OnDescriptionChanged(string value)
    {
        if (_loading) return;
        _descriptionDirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes pending text edits. Called on a debounce timer and before switching issues.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        var reload = false;
        if (_titleDirty && !string.IsNullOrWhiteSpace(Title))
        {
            _main.S.Issues.SetTitle(Id, Title);
            _titleDirty = false;
            reload = true;
        }
        if (_descriptionDirty)
        {
            _main.S.Issues.SetDescription(Id, Description);
            _descriptionDirty = false;
        }
        if (reload) _main.Reload();
    }

    [RelayCommand]
    void SetStatus(IssueStatus s) => _main.SetStatus(Id, s);

    [RelayCommand]
    void SetPriority(Priority p) => _main.SetPriority(Id, p);

    [RelayCommand]
    void MoveToProject(Project p)
    {
        _main.S.Issues.MoveToProject(Id, p.Id);
        Refresh();
        _main.Reload();
    }

    [RelayCommand]
    public void ToggleLabel(Label label)
    {
        var ids = SelectedLabels.Select(l => l.Id).ToHashSet();
        if (!ids.Remove(label.Id)) ids.Add(label.Id);
        _main.S.Issues.SetLabels(Id, ids);
        Refresh();
        _main.Reload();
    }

    [RelayCommand]
    void AddSubtask()
    {
        var title = NewSubtask.Trim();
        if (title.Length == 0 || Project is null) return;
        _main.S.Issues.Create(Project.Id, title, IssueStatus.Todo, parentId: Id);
        NewSubtask = "";
        Refresh();
        _main.Reload();
    }

    [RelayCommand]
    void ToggleSubtask(IssueRow row)
    {
        _main.S.Issues.SetStatus(row.Id, row.IsClosed ? IssueStatus.Todo : IssueStatus.Done);
        Refresh();
        _main.Reload();
    }

    [RelayCommand]
    void OpenSubtask(IssueRow row) => _main.OpenIssue(row.Id);

    [RelayCommand]
    void OpenParent()
    {
        if (Parent is not null) _main.OpenIssue(Parent.Id);
    }

    [RelayCommand]
    void Delete() => _main.Archive(Id);

    [RelayCommand]
    void CopyKey() => CopyToClipboard($"{Key} {Title}", "Скопировано: ID и заголовок");

    [RelayCommand]
    void CopyBranch() => CopyToClipboard(BranchName(Key, Title), "Скопировано имя ветки");

    void CopyToClipboard(string text, string status)
    {
        try
        {
            Clipboard.SetText(text);
            _main.Status = status;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            _main.Status = "Буфер обмена занят, попробуй ещё раз";
        }
    }

    /// <summary>"TM-42" + "Fix login crash" → "tm-42-fix-login-crash".</summary>
    public static string BranchName(string key, string title)
    {
        var sb = new StringBuilder(key.ToLowerInvariant());
        var dash = true;
        sb.Append('-');
        foreach (var ch in Transliterate(title.ToLowerInvariant()))
        {
            if (char.IsAsciiLetterOrDigit(ch)) { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
            if (sb.Length >= 60) break;
        }
        return sb.ToString().TrimEnd('-');
    }

    static string Transliterate(string s)
    {
        const string ru = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        string[] lat = ["a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "ts", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya"];
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var i = ru.IndexOf(ch);
            sb.Append(i >= 0 ? lat[i] : ch.ToString());
        }
        return sb.ToString();
    }

    [RelayCommand]
    void Edit() => IsEditing = true;

    [RelayCommand]
    void Preview()
    {
        Flush();
        if (!string.IsNullOrWhiteSpace(Description)) IsEditing = false;
    }

    public void ToggleTask(int offset)
    {
        Description = MarkdownRenderer.ToggleTaskAt(Description, offset);
        Flush();
    }

    public void OpenLink(string url) => _main.OpenLink(url);

    // ---- Knowledge base links ----

    [RelayCommand]
    void OpenKbLink(KbLink link)
    {
        Flush();
        _main.OpenKbEntry(link.SourceId, link.Path);
    }

    [RelayCommand]
    void UnlinkKb(KbLink link)
    {
        _main.S.Kb.Unlink(Id, link.SourceId, link.Path);
        Refresh();
    }

    [RelayCommand]
    void LinkKb() => _main.Palette.OpenEntryPicker("Привязать запись из базы…", LinkEntry);

    public void LinkEntry(KbEntry e)
    {
        _main.S.Kb.Link(Id, e.SourceId, e.Path);
        Refresh();
    }
}