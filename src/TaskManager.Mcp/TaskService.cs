using System.Text.RegularExpressions;
using TaskManager.Data;
using TaskManager.Kb;
using TaskManager.Models;
using TaskManager.Services;

namespace TaskManager.Mcp;

public sealed record IssueSummary(
    string Key, string Title, string Status, string Priority, string Project,
    string[] Labels, string? Parent, string? Subtasks, string Updated);

public sealed record IssueDetails(
    string Key, string Title, string Status, string Priority, string Project,
    string[] Labels, string? Parent, string Description, IssueSummary[] Subtasks,
    string Created, string Updated, string? Completed, string[] Images, KbRef[] LinkedEntries);

public sealed record KbRef(string Source, string Path, string Title, bool Exists);

public sealed record KbEntryInfo(string Source, string Path, string Title, string[] Tags, string Modified);

/// <summary>
/// Everything the MCP tools do, on top of the same repositories the desktop app uses.
/// One SQLite connection is not thread-safe, so every call takes a lock.
/// </summary>
public sealed partial class TaskService(Db db, AppPaths paths, ObsidianSync? obsidian = null)
{
    readonly object _lock = new();
    readonly ProjectRepository _projects = new(db);
    readonly IssueRepository _issues = new(db);
    readonly LabelRepository _labels = new(db);
    readonly KnowledgeBase _kb = new(db) { Changed = obsidian is null ? null : obsidian.Schedule };

    T Locked<T>(Func<T> f)
    {
        lock (_lock) return f();
    }

    // ---------------- Reads ----------------

    public List<object> Projects() => Locked(() =>
    {
        var open = _issues.Query(new IssueQuery { TopLevelOnly = false, Statuses = [IssueStatus.Backlog, IssueStatus.Todo, IssueStatus.InProgress] })
            .GroupBy(i => i.ProjectId).ToDictionary(g => g.Key, g => g.Count());
        return _projects.All().Select(p => (object)new { p.Key, p.Name, OpenIssues = open.GetValueOrDefault(p.Id) }).ToList();
    });

    public List<object> Labels() => Locked(() => _labels.All().Select(l => (object)new { l.Name, l.Color }).ToList());

    public List<IssueSummary> List(string? project, string? status, string? label, string? priority, string? query,
        bool includeClosed, bool includeSubtasks, int limit) => Locked(() =>
    {
        IReadOnlyCollection<IssueStatus>? statuses = null;
        if (!string.IsNullOrWhiteSpace(status))
            statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseStatus).ToArray();
        else if (!includeClosed)
            statuses = [IssueStatus.Backlog, IssueStatus.Todo, IssueStatus.InProgress];

        var q = new IssueQuery
        {
            ProjectId = string.IsNullOrWhiteSpace(project) ? null : FindProject(project).Id,
            LabelId = string.IsNullOrWhiteSpace(label) ? null : FindLabel(label).Id,
            Priority = string.IsNullOrWhiteSpace(priority) ? null : ParsePriority(priority),
            Statuses = statuses,
            Text = query,
            TopLevelOnly = !includeSubtasks && string.IsNullOrWhiteSpace(query),
            Sort = SortMode.Priority,
            Limit = Math.Clamp(limit, 1, 500),
        };
        var labels = LabelNames();
        return _issues.Query(q).Select(i => Summary(i, labels)).ToList();
    });

    public (IssueDetails Issue, List<string> ImagePaths) Get(string key) => Locked(() =>
    {
        var issue = FindIssue(key);
        var labels = LabelNames();
        var images = ImageRefs(issue.Description).ToList();
        var paths = images.Select(ResolveAttachment).OfType<string>().ToList();
        var details = new IssueDetails(
            issue.Key, issue.Title, StatusName(issue.Status), PriorityName(issue.Priority), ProjectKey(issue),
            LabelsOf(issue, labels), ParentKey(issue), issue.Description,
            _issues.Children(issue.Id).Select(c => Summary(c, labels)).ToArray(),
            Time(issue.CreatedAt), Time(issue.UpdatedAt), issue.CompletedAt is { } c ? Time(c) : null,
            images.ToArray(),
            _kb.LinksOf(issue.Id).Select(l => new KbRef(l.SourceName, l.Path, l.Title, l.Exists)).ToArray());
        return (details, paths);
    });

    // ---------------- Writes ----------------

    public IssueDetails Create(string project, string title, string? description, string? status, string? priority,
        string[]? labels, string? parent) => Locked(() =>
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("title is required");
        var parentIssue = string.IsNullOrWhiteSpace(parent) ? null : FindIssue(parent);
        var projectId = string.IsNullOrWhiteSpace(project)
            ? parentIssue?.ProjectId ?? throw new ArgumentException("project is required")
            : FindProject(project).Id;
        var issue = _issues.Create(projectId, title,
            string.IsNullOrWhiteSpace(status) ? IssueStatus.Todo : ParseStatus(status),
            string.IsNullOrWhiteSpace(priority) ? Priority.None : ParsePriority(priority),
            parentIssue?.Id, description ?? "");
        if (labels is { Length: > 0 }) _issues.SetLabels(issue.Id, labels.Select(n => EnsureLabel(n).Id));
        return Get(issue.Key).Issue;
    });

    public IssueDetails Update(string key, string? title, string? description, string? appendDescription,
        string? status, string? priority, string[]? addLabels, string[]? removeLabels, string? project, string? parent) => Locked(() =>
    {
        var issue = FindIssue(key);
        if (!string.IsNullOrWhiteSpace(title)) _issues.SetTitle(issue.Id, title);
        if (description is not null || !string.IsNullOrEmpty(appendDescription))
        {
            var text = description ?? issue.Description;
            if (!string.IsNullOrEmpty(appendDescription))
                text = string.IsNullOrWhiteSpace(text) ? appendDescription : text.TrimEnd() + "\n\n" + appendDescription;
            _issues.SetDescription(issue.Id, text);
        }
        if (!string.IsNullOrWhiteSpace(status)) _issues.SetStatus(issue.Id, ParseStatus(status));
        if (!string.IsNullOrWhiteSpace(priority)) _issues.SetPriority(issue.Id, ParsePriority(priority));
        if (addLabels is { Length: > 0 } || removeLabels is { Length: > 0 })
        {
            var ids = issue.GetLabelIds().ToHashSet();
            foreach (var n in addLabels ?? []) ids.Add(EnsureLabel(n).Id);
            foreach (var n in removeLabels ?? []) ids.Remove(FindLabel(n).Id);
            _issues.SetLabels(issue.Id, ids);
        }
        if (parent is not null)
            _issues.SetParent(issue.Id, parent.Length == 0 || parent.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : FindIssue(parent).Id);
        if (!string.IsNullOrWhiteSpace(project)) _issues.MoveToProject(issue.Id, FindProject(project).Id);
        return Get(_issues.Get(issue.Id)!.Key).Issue;
    });

    public string Archive(string key) => Locked(() =>
    {
        var issue = FindIssue(key);
        _issues.Archive(issue.Id);
        return issue.Key;
    });

    public object CreateProject(string name, string? key) => Locked(() =>
    {
        var colors = new[] { "#5E6AD2", "#4CB782", "#F2994A", "#26B5CE", "#BB87FC", "#EB5757" };
        var p = _projects.Create(name, string.IsNullOrWhiteSpace(key) ? ProjectRepository.SuggestKey(name) : key,
            colors[_projects.All().Count % colors.Length]);
        return new { p.Key, p.Name };
    });

    public object CreateLabel(string name, string? color) => Locked(() =>
    {
        var l = _labels.Create(name, NormalizeColor(color) ?? "#9A9CA5");
        return new { l.Name, l.Color };
    });

    /// <summary>Copies an image into the attachments folder and appends it to the description.</summary>
    public IssueDetails AttachImage(string key, string filePath, string? caption) => Locked(() =>
    {
        var issue = FindIssue(key);
        if (!File.Exists(filePath)) throw new ArgumentException($"File not found: {filePath}");
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"))
            throw new ArgumentException("Only image files can be attached (png, jpg, gif, bmp, webp)");
        var name = $"{Guid.NewGuid():N}{ext}";
        File.Copy(filePath, Path.Combine(paths.Attachments, name));
        var md = $"![{caption ?? Path.GetFileNameWithoutExtension(filePath)}](attachment:{name})";
        var text = string.IsNullOrWhiteSpace(issue.Description) ? md : issue.Description.TrimEnd() + "\n\n" + md;
        _issues.SetDescription(issue.Id, text);
        return Get(issue.Key).Issue;
    });

    // ---------------- Knowledge base ----------------

    public List<object> KbSources() => Locked(() =>
        _kb.Sources().Select(s => (object)new
        {
            id = s.Id, name = s.Name, path = s.Path, exists = Directory.Exists(s.Path), obsidian_sync = s.ObsidianSync,
            entries = Directory.Exists(s.Path) ? _kb.Entries(s.Id).Count : 0,
        }).ToList());

    public object KbAddSource(string path, string? name) => Locked(() =>
    {
        var s = _kb.AddSource(path, name);
        return new { id = s.Id, name = s.Name, path = s.Path };
    });

    KbEntryInfo Info(KbEntry e) =>
        new(_kb.Source(e.SourceId).Name, e.Path, e.Title, e.Tags.ToArray(), e.Modified.ToString("yyyy-MM-dd HH:mm"));

    public object KbList(string? source, string? folder, string? tag, string? query, int limit) => Locked<object>(() =>
    {
        long? sourceId = string.IsNullOrWhiteSpace(source) ? null : _kb.FindSource(source).Id;
        limit = Math.Clamp(limit, 1, 1000);
        if (!string.IsNullOrWhiteSpace(query) || !string.IsNullOrWhiteSpace(tag))
            return _kb.Search(sourceId, query, tag, limit).Select(Info).ToList();

        var prefix = KnowledgeBase.Normalize(folder ?? "");
        bool InFolder(string p) => prefix.Length == 0 || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        var entries = _kb.Entries(sourceId).Where(e => InFolder(e.Path))
            .OrderBy(e => e.Path, StringComparer.CurrentCultureIgnoreCase).Take(limit).Select(Info).ToList();
        var folders = sourceId is { } id ? _kb.Folders(id).Where(InFolder).ToList() : [];
        return new { folders, entries };
    });

    public object KbRead(string source, string path) => Locked(() =>
    {
        var s = _kb.FindSource(source);
        var rel = MdPath(path);
        var content = _kb.Read(s.Id, rel);
        var issues = _kb.IssuesLinking(s.Id, rel).Select(id => _issues.Get(id)).OfType<Issue>()
            .Select(i => new { key = i.Key, title = i.Title, status = StatusName(i.Status) }).ToList();
        return new
        {
            source = s.Name, path = rel, title = KnowledgeBase.TitleOf(rel),
            tags = KnowledgeBase.ParseTags(content).ToArray(), linked_issues = issues,
            link_markdown = KnowledgeBase.LinkMarkdown(s.Id, rel), content,
        };
    });

    /// <summary>Creates or overwrites an entry, or appends to it.</summary>
    public object KbWrite(string source, string path, string? content, string? append) => Locked(() =>
    {
        var s = _kb.FindSource(source);
        var rel = MdPath(path);
        var full = _kb.Resolve(s.Id, rel);
        var existed = File.Exists(full);
        string text;
        if (content is not null) text = content;
        else if (append is not null)
        {
            var old = existed ? File.ReadAllText(full) : "";
            text = string.IsNullOrWhiteSpace(old) ? append : old.TrimEnd() + "\n\n" + append;
        }
        else throw new ArgumentException("Pass content (to replace) or append");
        _kb.Write(s.Id, rel, text);
        return new { source = s.Name, path = rel, created = !existed, tags = KnowledgeBase.ParseTags(text).ToArray() };
    });

    public object KbCreateFolder(string source, string path) => Locked(() =>
    {
        var s = _kb.FindSource(source);
        var rel = KnowledgeBase.Normalize(path);
        var parent = KnowledgeBase.FolderOf(rel);
        return new { source = s.Name, path = _kb.CreateFolder(s.Id, parent, rel.Split('/')[^1]) };
    });

    public object KbRename(string source, string path, string newName) => Locked(() =>
    {
        var s = _kb.FindSource(source);
        return new { source = s.Name, path = _kb.Rename(s.Id, path, newName) };
    });

    public object KbMove(string source, string path, string folder) => Locked(() =>
    {
        var s = _kb.FindSource(source);
        return new { source = s.Name, path = _kb.Move(s.Id, path, folder) };
    });

    public List<object> KbTags(string? source) => Locked(() =>
    {
        long? sourceId = string.IsNullOrWhiteSpace(source) ? null : _kb.FindSource(source).Id;
        var counts = _kb.TagCounts(sourceId);
        var colors = _kb.TagColors(counts.Select(c => c.Tag));
        return counts.Select(c => (object)new { tag = c.Tag, color = colors[c.Tag], entries = c.Count }).ToList();
    });

    public object KbSetTagColor(string tag, string color) => Locked(() =>
    {
        var c = NormalizeColor(color) ?? throw new ArgumentException("Color must be hex like #EB5757");
        _kb.SetTagColor(tag, c);
        return new { tag = tag.TrimStart('#'), color = c };
    });

    public IssueDetails LinkEntry(string key, string source, string path, bool unlink) => Locked(() =>
    {
        var issue = FindIssue(key);
        var s = _kb.FindSource(source);
        var rel = MdPath(path);
        if (unlink) _kb.Unlink(issue.Id, s.Id, rel);
        else
        {
            if (_kb.Entry(s.Id, rel) is null) throw new ArgumentException($"Entry not found: {rel}");
            _kb.Link(issue.Id, s.Id, rel);
        }
        return Get(issue.Key).Issue;
    });

    static string MdPath(string path)
    {
        var rel = KnowledgeBase.Normalize(path);
        if (rel.Length == 0) throw new ArgumentException("path is required");
        return rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? rel : rel + ".md";
    }

    // ---------------- Helpers ----------------

    Project FindProject(string keyOrName)
    {
        var all = _projects.All();
        return all.FirstOrDefault(p => p.Key.Equals(keyOrName.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => p.Name.Equals(keyOrName.Trim(), StringComparison.CurrentCultureIgnoreCase))
               ?? throw new ArgumentException($"Project '{keyOrName}' not found. Known: {string.Join(", ", all.Select(p => $"{p.Key} ({p.Name})"))}");
    }

    Label FindLabel(string name) =>
        _labels.All().FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase))
        ?? throw new ArgumentException($"Label '{name}' not found");

    Label EnsureLabel(string name) =>
        _labels.All().FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase))
        ?? _labels.Create(name, "#9A9CA5");

    Issue FindIssue(string key) =>
        _issues.GetByKey(key) ?? throw new ArgumentException($"Issue '{key}' not found (expected a key like TM-12)");

    Dictionary<long, string> LabelNames() => _labels.All().ToDictionary(l => l.Id, l => l.Name);

    static string[] LabelsOf(Issue i, Dictionary<long, string> names) =>
        i.GetLabelIds().Select(id => names.GetValueOrDefault(id)).OfType<string>().ToArray();

    string ProjectKey(Issue i) => i.ProjectKey;

    string? ParentKey(Issue i) => i.ParentId is { } pid ? _issues.Get(pid)?.Key : null;

    IssueSummary Summary(Issue i, Dictionary<long, string> labels) => new(
        i.Key, i.Title, StatusName(i.Status), PriorityName(i.Priority), i.ProjectKey, LabelsOf(i, labels),
        ParentKey(i), i.SubTotal > 0 ? $"{i.SubDone}/{i.SubTotal}" : null, Time(i.UpdatedAt));

    static string Time(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [GeneratedRegex(@"!\[[^\]]*\]\((attachment:[^)\s]+)")]
    private static partial Regex ImageRef();

    static IEnumerable<string> ImageRefs(string markdown) =>
        ImageRef().Matches(markdown).Select(m => m.Groups[1].Value).Distinct();

    string? ResolveAttachment(string url)
    {
        var file = Path.Combine(paths.Attachments, Path.GetFileName(url["attachment:".Length..]));
        return File.Exists(file) ? file : null;
    }

    static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var c = color.Trim().TrimStart('#');
        return c.Length == 6 && c.All(char.IsAsciiHexDigit) ? "#" + c.ToUpperInvariant() : null;
    }

    public static string StatusName(IssueStatus s) => s switch
    {
        IssueStatus.Backlog => "backlog",
        IssueStatus.Todo => "todo",
        IssueStatus.InProgress => "in_progress",
        IssueStatus.Done => "done",
        IssueStatus.Canceled => "canceled",
        _ => s.ToString(),
    };

    public static string PriorityName(Priority p) => p.ToString().ToLowerInvariant();

    public static IssueStatus ParseStatus(string s) => s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
    {
        "backlog" or "бэклог" => IssueStatus.Backlog,
        "todo" or "to_do" or "open" or "к_выполнению" => IssueStatus.Todo,
        "in_progress" or "inprogress" or "doing" or "started" or "в_работе" => IssueStatus.InProgress,
        "done" or "completed" or "closed" or "готово" => IssueStatus.Done,
        "canceled" or "cancelled" or "отменено" => IssueStatus.Canceled,
        _ => throw new ArgumentException($"Unknown status '{s}'. Use: backlog, todo, in_progress, done, canceled"),
    };

    public static Priority ParsePriority(string s) => s.Trim().ToLowerInvariant() switch
    {
        "none" or "no" or "0" or "без_приоритета" => Priority.None,
        "urgent" or "1" or "срочно" => Priority.Urgent,
        "high" or "2" or "высокий" => Priority.High,
        "medium" or "normal" or "3" or "средний" => Priority.Medium,
        "low" or "4" or "низкий" => Priority.Low,
        _ => throw new ArgumentException($"Unknown priority '{s}'. Use: urgent, high, medium, low, none"),
    };
}
