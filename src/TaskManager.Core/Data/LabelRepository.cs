using Dapper;
using TaskManager.Models;

namespace TaskManager.Data;

public sealed class LabelRepository(Db db)
{
    public List<Label> All() =>
        db.Conn.Query<Label>("SELECT * FROM labels ORDER BY name COLLATE NOCASE").ToList();

    public Label Create(string name, string color)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Пустое имя метки");
        if (db.Conn.ExecuteScalar<long>("SELECT COUNT(*) FROM labels WHERE name=@name", new { name }) > 0)
            throw new InvalidOperationException($"Метка «{name}» уже существует");
        var id = db.Conn.ExecuteScalar<long>(
            "INSERT INTO labels(name, color) VALUES (@name, @color) RETURNING id", new { name, color });
        return new Label { Id = id, Name = name, Color = color };
    }

    public void Update(long id, string name, string color) =>
        db.Conn.Execute("UPDATE labels SET name=@name, color=@color WHERE id=@id", new { id, name = name.Trim(), color });

    public void Delete(long id) =>
        db.Conn.Execute("DELETE FROM labels WHERE id=@id", new { id });
}
