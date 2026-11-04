using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskManager.Models;

namespace TaskManager.ViewModels;

public enum PaletteScope { All, Status, Priority, Label, Project, Picker }

public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string? Hint { get; init; }
    public string? Shortcut { get; init; }
    public IssueStatus? Status { get; init; }
    public Priority? Priority { get; init; }
    public string? Color { get; init; }
    public string? Glyph { get; init; }
    public bool IsChecked { get; init; }
    public PaletteScope Scope { get; init; } = PaletteScope.All;
    public required Action Run { get; init; }

    public bool HasStatus => Status is not null;
    public bool HasPriority => Priority is not null;
    public bool HasColor => Color is not null;
    public bool HasGlyph => Glyph is not null;
}

/// <summary>Ctrl+K: one box to jump to issues and run any action on the current one.</summary>
public sealed partial class CommandPaletteViewModel(MainViewModel main) : ObservableObject
{
    PaletteScope _scope;
    Func<string, IEnumerable<PaletteItem>>? _picker;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _placeholder = "";
    [ObservableProperty] private PaletteItem? _selected;
    public ObservableCollection<PaletteItem> Items { get; } = [];

    /// <summary>Wired by the view: commands that need dialogs or windows.</summary>
    public Dictionary<string, Action> ViewActions { get; } = [];

    public void Open(PaletteScope scope = PaletteScope.All)
    {
        if (scope != PaletteScope.All && main.Detail is null) return;
        _scope = scope;
        _picker = null;
        Placeholder = scope switch
        {
            PaletteScope.Status => "Сменить статус…",
            PaletteScope.Priority => "Сменить приоритет…",
            PaletteScope.Label => "Метки…",
            PaletteScope.Project => "Перенести в проект…",
            _ => "Найти задачу или выполнить команду…",
        };
        Query = "";
        Rebuild();
        IsOpen = true;
    }


    /// <summary>A one-off list (choose an entry, choose an issue) whose items are produced from the query.</summary>
    public void OpenPicker(string placeholder, Func<string, IEnumerable<PaletteItem>> items)
    {
        _scope = PaletteScope.Picker;
        _picker = items;
        Placeholder = placeholder;
        Query = "";
        Rebuild();
        IsOpen = true;
        main.RequestFocus("palette");
    }

    public void OpenEntryPicker(string placeholder, Action<TaskManager.Kb.KbEntry> picked) =>
        OpenPicker(placeholder, q => EntryItems(q, picked));

    public void OpenIssuePicker(Action<long> picked) =>
        OpenPicker("Привязать к задаче…", q => (q.Length == 0 ? main.S.Issues.Recent(15) : main.S.Issues.Search(q, 30))
            .Select(i => new PaletteItem { Title = i.Title, Hint = i.Key, Status = i.Status, Run = () => picked(i.Id) }));

    IEnumerable<PaletteItem> EntryItems(string q, Action<TaskManager.Kb.KbEntry> run) =>
        main.S.Kb.Search(null, q, null, 40)
            .OrderByDescending(e => e.Modified)
            .Select(e => new PaletteItem
            {
                Title = e.Title,
                Hint = SourceName(e.SourceId) + (e.Folder.Length > 0 ? " / " + e.Folder : ""),
                Glyph = "📄",
                Run = () => run(e),
            });

    string SourceName(long id) => main.KbNav.FirstOrDefault(n => n.Id == id)?.Title ?? "";

    partial void OnQueryChanged(string value) => Rebuild();

    void Rebuild()
    {
        var q = Query.Trim();
        if (_scope == PaletteScope.Picker && _picker is not null)
        {
            Items.Clear();
            foreach (var r in _picker(q).Take(60)) Items.Add(r);
            Selected = Items.FirstOrDefault();
            return;
        }
        var actions = BuildActions()
            .Where(a => _scope == PaletteScope.All || a.Scope == _scope)
            .Where(a => q.Length == 0 || Matches(a.Title, q) || (a.Hint is not null && Matches(a.Hint, q)));
        var results = actions.ToList();

        if (_scope == PaletteScope.All)
        {
            var issues = q.Length == 0 ? main.S.Issues.Recent(8) : main.S.Issues.Search(q, 30);
            var issueItems = issues.Select(i => new PaletteItem
            {
                Title = i.Title,
                Hint = i.Key,
                Status = i.Status,
                Run = () => main.OpenIssue(i.Id),
            });
            // While typing, issues are what you are most likely looking for.
            var entryItems = q.Length == 0 ? [] : EntryItems(q, e => main.OpenKbEntry(e.SourceId, e.Path)).Take(15).ToList();
            results = q.Length == 0 ? [.. results, .. issueItems] : [.. issueItems, .. entryItems, .. results];
        }

        Items.Clear();
        foreach (var r in results.Take(60)) Items.Add(r);
        Selected = Items.FirstOrDefault();
    }

    static bool Matches(string text, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));

    IEnumerable<PaletteItem> BuildActions()
    {
        var d = main.Detail;
        if (d is not null)
        {
            foreach (var s in Names.Statuses)
                yield return new PaletteItem
                {
                    Title = $"Статус: {Names.Of(s)}", Status = s, IsChecked = d.Status == s, Scope = PaletteScope.Status,
                    Hint = d.Key, Run = () => main.SetStatus(d.Id, s),
                };
            foreach (var p in Names.Priorities)
                yield return new PaletteItem
                {
                    Title = $"Приоритет: {Names.Of(p)}", Priority = p, IsChecked = d.Priority == p, Scope = PaletteScope.Priority,
                    Hint = d.Key, Run = () => main.SetPriority(d.Id, p),
                };
            foreach (var l in main.Labels)
            {
                var on = d.SelectedLabels.Any(x => x.Id == l.Id);
                yield return new PaletteItem
                {
                    Title = $"{(on ? "Снять метку" : "Метка")}: {l.Name}", Color = l.Color, IsChecked = on, Scope = PaletteScope.Label,
                    Hint = d.Key, Run = () => d.ToggleLabel(l),
                };
            }
            if (Query.Trim() is { Length: > 0 } newLabel && _scope == PaletteScope.Label &&
                !main.Labels.Any(l => l.Name.Equals(newLabel, StringComparison.CurrentCultureIgnoreCase)))
            {
                yield return new PaletteItem
                {
                    Title = $"Создать метку «{newLabel}»", Scope = PaletteScope.Label,
                    Run = () => { if (main.AddLabel(newLabel) is { } l) d.ToggleLabel(l); },
                };
            }
            foreach (var p in main.Projects.Where(p => p.Id != d.Project?.Id))
                yield return new PaletteItem
                {
                    Title = $"Перенести в проект: {p.Name}", Color = p.Color, Scope = PaletteScope.Project,
                    Hint = d.Key, Run = () => d.MoveToProjectCommand.Execute(p),
                };
            yield return new PaletteItem { Title = "Копировать ID и заголовок", Hint = d.Key, Run = () => d.CopyKeyCommand.Execute(null) };
            yield return new PaletteItem { Title = "Копировать имя git-ветки", Hint = d.Key, Run = () => d.CopyBranchCommand.Execute(null) };
            yield return new PaletteItem { Title = "Добавить подзадачу", Hint = d.Key, Run = () => main.RequestFocus("subtask") };
            yield return new PaletteItem { Title = "Удалить задачу", Hint = d.Key, Shortcut = "Del", Run = () => ViewActions.GetValueOrDefault("delete")?.Invoke() };
        }

        if (d is not null)
            yield return new PaletteItem { Title = "Привязать запись из базы", Hint = d.Key, Glyph = "📄", Run = () => d.LinkKbCommand.Execute(null) };
        if (main.IsKbMode)
            yield return new PaletteItem { Title = "Новая запись", Shortcut = "C", Glyph = "📄", Run = () => main.Kb.NewEntry(null) };
        yield return new PaletteItem { Title = "Новая задача", Shortcut = main.IsKbMode ? null : "C", Run = () => main.NewIssue(null) };
        foreach (var k in main.KbNav)
            yield return new PaletteItem { Title = $"Перейти: {k.Title}", Hint = "база данных", Glyph = "🗂", Run = () => main.SelectedNav = k };
        yield return new PaletteItem { Title = "Добавить папку в базу данных…", Glyph = "🗂", Run = () => ViewActions.GetValueOrDefault("add-kb")?.Invoke() };
        yield return new PaletteItem { Title = "Показать список", Shortcut = "Ctrl+B", Run = () => main.ShowTasks(false) };
        yield return new PaletteItem { Title = "Показать канбан", Shortcut = "Ctrl+B", Run = () => main.ShowTasks(true) };
        yield return new PaletteItem { Title = main.HideClosed ? "Показать завершённые" : "Скрыть завершённые", Run = () => main.HideClosed = !main.HideClosed };
        foreach (var v in main.Views)
            yield return new PaletteItem { Title = $"Перейти: {v.Title}", Run = () => main.SelectedNav = v };
        foreach (var p in main.ProjectNav)
            yield return new PaletteItem { Title = $"Перейти: {p.Title}", Color = p.Color, Hint = "проект", Run = () => main.SelectedNav = p };
        foreach (var l in main.LabelNav)
            yield return new PaletteItem { Title = $"Перейти: {l.Title}", Color = l.Color, Hint = "метка", Run = () => main.SelectedNav = l };
        foreach (var (key, title) in new[]
                 {
                     ("new-project", "Новый проект"), ("new-label", "Новая метка"),
                     ("export", "Экспорт бэкапа…"), ("import", "Импорт бэкапа…"), ("open-data", "Открыть папку с данными"),
                     ("shortcuts", "Горячие клавиши"),
                     ("text-bigger", "Текст заметок крупнее"), ("text-smaller", "Текст заметок мельче"),
                     ("text-serif", "Шрифт заметок: с засечками / без"),
                 })
            yield return new PaletteItem { Title = title, Run = () => ViewActions.GetValueOrDefault(key)?.Invoke() };
    }

    [RelayCommand]
    public void Execute(PaletteItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        IsOpen = false;
        item.Run();
    }

    public void Move(int delta)
    {
        if (Items.Count == 0) return;
        var i = Selected is null ? -1 : Items.IndexOf(Selected);
        Selected = Items[(i + delta + Items.Count) % Items.Count];
    }
}
