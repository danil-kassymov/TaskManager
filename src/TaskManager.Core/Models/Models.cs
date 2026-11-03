namespace TaskManager.Models;

public enum IssueStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    Done = 3,
    Canceled = 4,
}

public enum Priority
{
    None = 0,
    Urgent = 1,
    High = 2,
    Medium = 3,
    Low = 4,
}

public sealed class Project
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#5E6AD2";
    public long Sort { get; set; }
    public bool Archived { get; set; }
}

public sealed class Label
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#9A9CA5";
}

public sealed class Issue
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long Number { get; set; }
    public long? ParentId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public IssueStatus Status { get; set; }
    public Priority Priority { get; set; }
    public double Sort { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public long? CompletedAt { get; set; }
    public bool Archived { get; set; }

    // Filled by joins in queries.
    public string ProjectKey { get; set; } = "";
    public long SubTotal { get; set; }
    public long SubDone { get; set; }
    public string? LabelIds { get; set; }

    public string Key => $"{ProjectKey}-{Number}";
    public bool IsClosed => Status is IssueStatus.Done or IssueStatus.Canceled;

    public IEnumerable<long> GetLabelIds() =>
        string.IsNullOrEmpty(LabelIds) ? [] : LabelIds.Split(',').Select(long.Parse);
}

public static class Names
{
    public static readonly IssueStatus[] Statuses =
        [IssueStatus.Backlog, IssueStatus.Todo, IssueStatus.InProgress, IssueStatus.Done, IssueStatus.Canceled];

    public static readonly Priority[] Priorities =
        [Priority.Urgent, Priority.High, Priority.Medium, Priority.Low, Priority.None];

    public static string Of(IssueStatus s) => s switch
    {
        IssueStatus.Backlog => "Бэклог",
        IssueStatus.Todo => "К выполнению",
        IssueStatus.InProgress => "В работе",
        IssueStatus.Done => "Готово",
        IssueStatus.Canceled => "Отменено",
        _ => s.ToString(),
    };

    public static string Of(Priority p) => p switch
    {
        Priority.None => "Без приоритета",
        Priority.Urgent => "Срочно",
        Priority.High => "Высокий",
        Priority.Medium => "Средний",
        Priority.Low => "Низкий",
        _ => p.ToString(),
    };
}
