using Dapper;
using Microsoft.Data.Sqlite;

namespace TaskManager.Data;

/// <summary>
/// Single long-lived SQLite connection. The app is single-user and all access happens
/// on the UI thread, so one connection is both the simplest and the fastest option.
/// </summary>
public sealed class Db : IDisposable
{
    static Db() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public SqliteConnection Conn { get; }

    public Db(string path)
    {
        Conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        Conn.Open();
        // The app and the MCP server may write at the same time: wait for the lock instead of failing.
        Conn.Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        if (path != ":memory:")
            Conn.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    /// <summary>Changes whenever another connection (e.g. the MCP server) commits to the database.</summary>
    public long DataVersion() => Conn.ExecuteScalar<long>("PRAGMA data_version;");

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static readonly string[] Migrations =
    [
        """
        CREATE TABLE projects(
            id INTEGER PRIMARY KEY,
            key TEXT NOT NULL UNIQUE COLLATE NOCASE,
            name TEXT NOT NULL,
            color TEXT NOT NULL,
            sort INTEGER NOT NULL DEFAULT 0,
            archived INTEGER NOT NULL DEFAULT 0,
            next_number INTEGER NOT NULL DEFAULT 1);

        CREATE TABLE issues(
            id INTEGER PRIMARY KEY,
            project_id INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            number INTEGER NOT NULL,
            parent_id INTEGER REFERENCES issues(id) ON DELETE SET NULL,
            title TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            status INTEGER NOT NULL,
            priority INTEGER NOT NULL,
            sort REAL NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            completed_at INTEGER,
            archived INTEGER NOT NULL DEFAULT 0,
            UNIQUE(project_id, number));
        CREATE INDEX ix_issues_parent ON issues(parent_id);
        CREATE INDEX ix_issues_project ON issues(project_id, status);

        CREATE TABLE labels(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE,
            color TEXT NOT NULL);

        CREATE TABLE issue_labels(
            issue_id INTEGER NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
            label_id INTEGER NOT NULL REFERENCES labels(id) ON DELETE CASCADE,
            PRIMARY KEY(issue_id, label_id));

        CREATE VIRTUAL TABLE issues_fts USING fts5(
            title, description, content='issues', content_rowid='id', tokenize='unicode61');
        CREATE TRIGGER issues_ai AFTER INSERT ON issues BEGIN
            INSERT INTO issues_fts(rowid, title, description) VALUES (new.id, new.title, new.description);
        END;
        CREATE TRIGGER issues_ad AFTER DELETE ON issues BEGIN
            INSERT INTO issues_fts(issues_fts, rowid, title, description) VALUES ('delete', old.id, old.title, old.description);
        END;
        CREATE TRIGGER issues_au AFTER UPDATE OF title, description ON issues BEGIN
            INSERT INTO issues_fts(issues_fts, rowid, title, description) VALUES ('delete', old.id, old.title, old.description);
            INSERT INTO issues_fts(rowid, title, description) VALUES (new.id, new.title, new.description);
        END;
        """,
        """
        CREATE TABLE kb_sources(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            sort INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE tags(
            name TEXT PRIMARY KEY COLLATE NOCASE,
            color TEXT NOT NULL);

        CREATE TABLE issue_links(
            issue_id INTEGER NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
            source_id INTEGER NOT NULL REFERENCES kb_sources(id) ON DELETE CASCADE,
            path TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY(issue_id, source_id, path));
        CREATE INDEX ix_links_entry ON issue_links(source_id, path);
        """,
        """
        ALTER TABLE kb_sources ADD COLUMN obsidian_sync INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    void Migrate()
    {
        var version = Conn.ExecuteScalar<long>("PRAGMA user_version;");
        for (var v = (int)version; v < Migrations.Length; v++)
        {
            using var tx = Conn.BeginTransaction();
            Conn.Execute(Migrations[v], transaction: tx);
            Conn.Execute($"PRAGMA user_version={v + 1};", transaction: tx);
            tx.Commit();
        }
    }

    /// <summary>Consistent copy of the database to <paramref name="path"/> (used by backups).</summary>
    public void CopyTo(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => Conn.Dispose();
}
