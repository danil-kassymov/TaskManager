using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using TaskManager.Models;

namespace TaskManager.Data;

public enum SortMode { Priority, Manual, Updated, Created }

public sealed record IssueQuery
{
    public long? ProjectId { get; init; }
    public long? LabelId { get; init; }
    /// <summary>When set, returns only children of this issue (ignores <see cref="TopLevelOnly"/>).</summary>
    public long? ParentId { get; init; }
    public bool TopLevelOnly { get; init; } = true;
    public IReadOnlyCollection<IssueStatus>? Statuses { get; init; }
    public Priority? Priority { get; init; }
    public string? Text { get; init; }
    public SortMode Sort { get; init; } = SortMode.Priority;
    public int Limit { get; init; } = 5000;
}

public sealed partial class IssueRepository(Db db)
{
    const string Select = """
        SELECT i.*, p.key AS project_key,
          (SELECT COUNT(*) FROM issues c WHERE c.parent_id = i.id AND c.archived = 0) AS sub_total,
          (SELECT COUNT(*) FROM issues c WHERE c.parent_id = i.id AND c.archived = 0 AND c.status IN (3, 4)) AS sub_done,
          (SELECT group_concat(label_id) FROM issue_labels il WHERE il.issue_id = i.id) AS label_ids
        FROM issues i JOIN projects p ON p.id = i.project_id
        WHERE i.archived = 0 AND p.archived = 0
        """;

    // Display order of groups: what is being worked on comes first.
    const string StatusOrder = "CASE i.status WHEN 2 THEN 0 WHEN 1 THEN 1 WHEN 0 THEN 2 WHEN 3 THEN 3 ELSE 4 END";
    const string PriorityOrder = "CASE i.priority WHEN 0 THEN 5 ELSE i.priority END";

    public List<Issue> Query(IssueQuery q)
    {
        var sql = new StringBuilder(Select);
        var p = new DynamicParameters();
        if (q.ProjectId is { } pid) { sql.Append(" AND i.project_id = @pid"); p.Add("pid", pid); }
        if (q.ParentId is { } parent) { sql.Append(" AND i.parent_id = @parent"); p.Add("parent", parent); }
        else if (q.TopLevelOnly) sql.Append(" AND i.parent_id IS NULL");
        if (q.LabelId is { } lid)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM issue_labels il WHERE il.issue_id = i.id AND il.label_id = @lid)");
            p.Add("lid", lid);
        }
        if (q.Statuses is { Count: > 0 })
        {
            sql.Append(" AND i.status IN @statuses");
            p.Add("statuses", q.Statuses.Select(s => (int)s).ToArray());
        }
        if (q.Priority is { } prio) { sql.Append(" AND i.priority = @prio"); p.Add("prio", (int)prio); }
        if (!string.IsNullOrWhiteSpace(q.Text)) AppendTextFilter(sql, p, q.Text);

        sql.Append(" ORDER BY ");
        if (q.ParentId is null) sql.Append(StatusOrder).Append(", ");
        sql.Append(q.Sort switch
        {
            SortMode.Priority => $"{PriorityOrder}, i.sort",
            SortMode.Manual => "i.sort",
            SortMode.Updated => "i.updated_at DESC",
            SortMode.Created => "i.created_at DESC",
            _ => "i.sort",
        });
        sql.Append(", i.id LIMIT @limit");
        p.Add("limit", q.Limit);
        return db.Conn.Query<Issue>(sql.ToString(), p).ToList();
    }

    void AppendTextFilter(StringBuilder sql, DynamicParameters p, string text)
    {
        text = text.Trim();
        if (KeyRegex().Match(text) is { Success: true } m)
        {
            sql.Append(" AND p.key = @fkey AND i.number = @fnum");
            p.Add("fkey", m.Groups[1].Value);
            p.Add("fnum", long.Parse(m.Groups[2].Value));
            return;
        }
        var fts = ToFtsQuery(text);
        if (fts.Length == 0) return;
        sql.Append(" AND i.id IN (SELECT rowid FROM issues_fts WHERE issues_fts MATCH @fts)");
        p.Add("fts", fts);
    }

    /// <summary>Each word becomes a quoted prefix term, terms are AND-ed: <c>"foo"* "bar"*</c>.</summary>
    internal static string ToFtsQuery(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\"", ""))
            .Where(w => w.Length > 0)
            .Select(w => $"\"{w}\"*"));

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]*)-(\d+)$")]
    private static partial Regex KeyRegex();

    public List<Issue> Search(string text, int limit = 20) =>
        Query(new IssueQuery { Text = text, TopLevelOnly = false, Sort = SortMode.Updated, Limit = limit });

    public List<Issue> Recent(int limit = 10) =>
        Query(new IssueQuery { TopLevelOnly = false, Sort = SortMode.Updated, Limit = limit })
            .OrderByDescending(i => i.UpdatedAt).ToList();

    public List<Issue> Children(long parentId) =>
        Query(new IssueQuery { ParentId = parentId, Sort = SortMode.Manual });

    public Issue? Get(long id) =>
        db.Conn.QuerySingleOrDefault<Issue>(Select + " AND i.id = @id", new { id });

    public Issue? GetByKey(string key)
    {
        var m = KeyRegex().Match(key.Trim());
        if (!m.Success) return null;
        return db.Conn.QuerySingleOrDefault<Issue>(Select + " AND p.key = @k AND i.number = @n",
            new { k = m.Groups[1].Value, n = long.Parse(m.Groups[2].Value) });
    }

    public Issue Create(long projectId, string title, IssueStatus status = IssueStatus.Todo,
        Priority priority = Priority.None, long? parentId = null, string description = "")
    {
        using var tx = db.Conn.BeginTransaction();
        var number = db.Conn.ExecuteScalar<long>(
            "UPDATE projects SET next_number = next_number + 1 WHERE id = @projectId RETURNING next_number - 1",
            new { projectId }, tx);
        // New issues go to the top of their column; subtasks are appended to the end.
        var sort = parentId is null
            ? db.Conn.ExecuteScalar<double>("SELECT COALESCE(MIN(sort), 0) - 1 FROM issues WHERE status = @s", new { s = (int)status }, tx)
            : db.Conn.ExecuteScalar<double>("SELECT COALESCE(MAX(sort), 0) + 1 FROM issues WHERE parent_id = @parentId", new { parentId }, tx);
        var now = Db.Now();
        var id = db.Conn.ExecuteScalar<long>("""
            INSERT INTO issues(project_id, number, parent_id, title, description, status, priority, sort, created_at, updated_at, completed_at)
            VALUES (@projectId, @number, @parentId, @title, @description, @status, @priority, @sort, @now, @now, @completed)
            RETURNING id
            """,
            new
            {
                projectId, number, parentId, title = title.Trim(), description, status = (int)status, priority = (int)priority,
                sort, now, completed = status is IssueStatus.Done or IssueStatus.Canceled ? now : (long?)null,
            }, tx);
        tx.Commit();
        return Get(id)!;
    }

    void Touch(string set, object args) =>
        db.Conn.Execute($"UPDATE issues SET {set}, updated_at = @now WHERE id = @id",
            new DynamicParameters(args).Also(d => d.Add("now", Db.Now())));

    public void SetTitle(long id, string title) => Touch("title = @title", new { id, title = title.Trim() });

    public void SetDescription(long id, string description) => Touch("description = @description", new { id, description });

    public void SetPriority(long id, Priority priority) => Touch("priority = @p", new { id, p = (int)priority });

    public void SetStatus(long id, IssueStatus status) =>
        Touch("status = @s, completed_at = CASE WHEN @s IN (3, 4) THEN COALESCE(completed_at, @now) ELSE NULL END",
            new { id, s = (int)status });

    /// <summary>Board drag&amp;drop: change status and position at once.</summary>
    public void Move(long id, IssueStatus status, double sort)
    {
        SetStatus(id, status);
        db.Conn.Execute("UPDATE issues SET sort = @sort WHERE id = @id", new { id, sort });
    }

    public void SetParent(long id, long? parentId)
    {
        if (parentId == id) throw new InvalidOperationException("Задача не может быть родителем самой себя");
        Touch("parent_id = @parentId", new { id, parentId });
    }

    /// <summary>Moving to another project assigns a new number there (the key changes).</summary>
    public void MoveToProject(long id, long projectId)
    {
        using var tx = db.Conn.BeginTransaction();
        var current = db.Conn.ExecuteScalar<long>("SELECT project_id FROM issues WHERE id = @id", new { id }, tx);
        if (current != projectId)
        {
            var number = db.Conn.ExecuteScalar<long>(
                "UPDATE projects SET next_number = next_number + 1 WHERE id = @projectId RETURNING next_number - 1",
                new { projectId }, tx);
            db.Conn.Execute("UPDATE issues SET project_id = @projectId, number = @number, updated_at = @now WHERE id = @id",
                new { id, projectId, number, now = Db.Now() }, tx);
        }
        tx.Commit();
    }

    public void SetLabels(long id, IEnumerable<long> labelIds)
    {
        using var tx = db.Conn.BeginTransaction();
        db.Conn.Execute("DELETE FROM issue_labels WHERE issue_id = @id", new { id }, tx);
        foreach (var lid in labelIds.Distinct())
            db.Conn.Execute("INSERT INTO issue_labels(issue_id, label_id) VALUES (@id, @lid)", new { id, lid }, tx);
        db.Conn.Execute("UPDATE issues SET updated_at = @now WHERE id = @id", new { id, now = Db.Now() }, tx);
        tx.Commit();
    }

    /// <summary>Soft delete of the issue together with its subtasks.</summary>
    public void Archive(long id) =>
        db.Conn.Execute("UPDATE issues SET archived = 1, updated_at = @now WHERE id = @id OR parent_id = @id",
            new { id, now = Db.Now() });

    public long Count() => db.Conn.ExecuteScalar<long>("SELECT COUNT(*) FROM issues WHERE archived = 0");
}

static class DapperExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
