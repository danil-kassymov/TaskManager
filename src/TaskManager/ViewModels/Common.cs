using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TaskManager.Data;
using TaskManager.Models;
using TaskManager.Services;

namespace TaskManager.ViewModels;

/// <summary>Everything that talks to disk. Recreated on backup import.</summary>
public sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; }
    public Settings Settings { get; }
    public AttachmentService Attachments { get; }
    public TaskManager.GDrive.GDriveService GDrive { get; }
    public Db Db { get; private set; } = null!;
    public ProjectRepository Projects { get; private set; } = null!;
    public IssueRepository Issues { get; private set; } = null!;
    public LabelRepository Labels { get; private set; } = null!;
    public TaskManager.Kb.KnowledgeBase Kb { get; private set; } = null!;

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        Settings = Settings.Load(paths.Settings);
        Attachments = new AttachmentService(paths);
        GDrive = new TaskManager.GDrive.GDriveService(paths);
        Open();
    }

    void Open()
    {
        Db = new Db(Paths.Database);
        Projects = new ProjectRepository(Db);
        Issues = new IssueRepository(Db);
        Labels = new LabelRepository(Db);
        Kb = new TaskManager.Kb.KnowledgeBase(Db) { Changed = GDrive.Schedule };
    }

    public void ImportBackup(string zip)
    {
        Db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { BackupService.Import(Paths, zip); }
        finally { Open(); }
    }

    public void Dispose()
    {
        Settings.Save(Paths.Settings);
        GDrive.Dispose();   // uploads edits still waiting for the debounce
        Db.Dispose();
    }
}

/// <summary>ObservableCollection that can swap all items with a single Reset notification.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Immutable row shown in lists, the board and search results.</summary>
public sealed class IssueRow
{
    public IssueRow(Issue model, IReadOnlyDictionary<long, Label> labels)
    {
        Model = model;
        Labels = model.GetLabelIds().Select(id => labels.GetValueOrDefault(id)).OfType<Label>().ToList();
    }

    public Issue Model { get; }
    public long Id => Model.Id;
    public string Key => Model.Key;
    public string Title => Model.Title;
    public IssueStatus Status => Model.Status;
    public Priority Priority => Model.Priority;
    public bool IsClosed => Model.IsClosed;
    public IReadOnlyList<Label> Labels { get; }
    public bool HasLabels => Labels.Count > 0;
    public string? SubProgress => Model.SubTotal > 0 ? $"{Model.SubDone}/{Model.SubTotal}" : null;
    public string Updated => Ago(Model.UpdatedAt);
    public string StatusName => Names.Of(Model.Status);

    public static string Ago(long unixMs)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "только что";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} мин";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} ч";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} дн";
        return t.ToString(t.Year == DateTime.Now.Year ? "d MMM" : "d MMM yyyy");
    }
}

public enum NavKind { All, Active, Backlog, Project, Label, KbSource }

public sealed partial class NavItem(NavKind kind, string title, long id = 0, string? color = null) : ObservableObject
{
    public NavKind Kind { get; } = kind;
    public long Id { get; } = id;
    [ObservableProperty] private string _title = title;
    [ObservableProperty] private string? _color = color;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Knowledge base source connected to Google Drive.</summary>
    [ObservableProperty] private bool _isGDrive;

    /// <summary>Number of tasks behind this tab (null for tabs without a count, e.g. knowledge base sources).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Display))] private int? _count;
    public string Display => Count is { } n ? $"{Title} ({n})" : Title;
    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(Display));

    public string Token => $"{Kind}:{Id}";
    public bool IsEntity => Kind is NavKind.Project or NavKind.Label or NavKind.KbSource;
}

public sealed class BoardColumn(IssueStatus status, IEnumerable<IssueRow> items)
{
    public IssueStatus Status { get; } = status;
    public string Title => Names.Of(Status);
    public List<IssueRow> Items { get; } = items.ToList();
    public int Count => Items.Count;
}

public sealed partial class LabelToggle(Label label, bool isChecked) : ObservableObject
{
    public Label Label { get; } = label;
    [ObservableProperty] private bool _isChecked = isChecked;
}
