using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskManager.Models;

namespace TaskManager.ViewModels;

public sealed partial class QuickCreateViewModel(MainViewModel main) : ObservableObject
{
    long? _lastProjectId;

    public IReadOnlyList<IssueStatus> Statuses => Names.Statuses;
    public IReadOnlyList<Priority> Priorities => Names.Priorities;
    public IReadOnlyList<Project> Projects => main.Projects;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private IssueStatus _status = IssueStatus.Todo;
    [ObservableProperty] private Priority _priority;
    [ObservableProperty] private Project? _project;
    [ObservableProperty] private bool _keepOpen;
    [ObservableProperty] private long? _parentId;
    [ObservableProperty] private string? _parentKey;

    public ObservableCollection<LabelToggle> LabelChoices { get; } = [];
    public IEnumerable<Label> SelectedLabels => LabelChoices.Where(l => l.IsChecked).Select(l => l.Label);

    public void Open(IssueStatus status)
    {
        Title = "";
        Description = "";
        Status = status;
        Priority = Priority.None;
        ParentId = null;
        ParentKey = null;
        OnPropertyChanged(nameof(Projects));
        Project = main.CurrentProject
                  ?? main.Projects.FirstOrDefault(p => p.Id == _lastProjectId)
                  ?? main.Projects.FirstOrDefault();
        LabelChoices.Clear();
        var preset = main.SelectedNav is { Kind: NavKind.Label } n ? n.Id : (long?)null;
        foreach (var l in main.Labels) LabelChoices.Add(new LabelToggle(l, l.Id == preset));
        OnPropertyChanged(nameof(SelectedLabels));
        IsOpen = true;
    }

    [RelayCommand]
    void SetStatus(IssueStatus s) => Status = s;

    [RelayCommand]
    void SetPriority(Priority p) => Priority = p;

    [RelayCommand]
    void SetProject(Project p) => Project = p;

    [RelayCommand]
    void ToggleLabel(LabelToggle t)
    {
        t.IsChecked = !t.IsChecked;
        OnPropertyChanged(nameof(SelectedLabels));
    }

    [RelayCommand]
    void Cancel() => IsOpen = false;

    [RelayCommand]
    void Submit()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            main.Status = "Введите заголовок задачи";
            return;
        }
        if (Project is null)
        {
            main.ShowError("Новая задача", "Сначала создайте проект");
            return;
        }
        var issue = main.S.Issues.Create(Project.Id, Title, Status, Priority, ParentId, Description);
        var labels = SelectedLabels.Select(l => l.Id).ToList();
        if (labels.Count > 0) main.S.Issues.SetLabels(issue.Id, labels);
        _lastProjectId = Project.Id;
        main.Reload();
        main.Status = $"Создана {issue.Key}";
        if (KeepOpen)
        {
            Title = "";
            Description = "";
            main.RequestFocus("create-title");
        }
        else
        {
            IsOpen = false;
            main.OpenIssue(issue.Id);
        }
    }
}
