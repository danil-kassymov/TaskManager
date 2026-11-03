using Dapper;
using TaskManager.Models;

namespace TaskManager.Data;

public sealed class ProjectRepository(Db db)
{
    public List<Project> All() =>
        db.Conn.Query<Project>("SELECT * FROM projects WHERE archived=0 ORDER BY sort, id").ToList();

    public Project? Get(long id) =>
        db.Conn.QuerySingleOrDefault<Project>("SELECT * FROM projects WHERE id=@id", new { id });

    public Project Create(string name, string key, string color)
    {
        key = NormalizeKey(key);
        if (key.Length == 0) throw new ArgumentException("Пустой ключ проекта");
        if (db.Conn.ExecuteScalar<long>("SELECT COUNT(*) FROM projects WHERE key=@key", new { key }) > 0)
            throw new InvalidOperationException($"Проект с ключом {key} уже существует");
        var sort = db.Conn.ExecuteScalar<long>("SELECT COALESCE(MAX(sort), 0) + 1 FROM projects");
        var id = db.Conn.ExecuteScalar<long>(
            "INSERT INTO projects(key, name, color, sort) VALUES (@key, @name, @color, @sort) RETURNING id",
            new { key, name = name.Trim(), color, sort });
        return Get(id)!;
    }

    public void Update(long id, string name, string color) =>
        db.Conn.Execute("UPDATE projects SET name=@name, color=@color WHERE id=@id", new { id, name = name.Trim(), color });

    public void Archive(long id) =>
        db.Conn.Execute("UPDATE projects SET archived=1 WHERE id=@id", new { id });

    public static string NormalizeKey(string key) =>
        new string(key.Trim().ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>"Task Manager" → "TM", "backend" → "BAC".</summary>
    public static string SuggestKey(string name)
    {
        var words = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return "";
        var key = words.Length == 1
            ? words[0][..Math.Min(3, words[0].Length)]
            : string.Concat(words.Take(4).Select(w => w[0]));
        return NormalizeKey(key);
    }
}
