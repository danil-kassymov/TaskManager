using TaskManager.Data;
using TaskManager.Mcp;
using TaskManager.Models;
using TaskManager.Services;

namespace TaskManager.Tests;

public sealed class McpTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"tm-mcp-{Guid.NewGuid():N}");
    readonly Db _db;
    readonly TaskService _tasks;

    public McpTests()
    {
        var paths = new AppPaths(_root);
        _db = new Db(paths.Database);
        _tasks = new TaskService(_db, paths);
        _tasks.CreateProject("Task Manager", "TM");
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Fact]
    public void Create_WithLabelsAndSubtask()
    {
        var parent = _tasks.Create("tm", "Parent", "Some *md*", "in progress", "high", ["Bug"], null);
        Assert.Equal("TM-1", parent.Key);
        Assert.Equal("in_progress", parent.Status);
        Assert.Equal("high", parent.Priority);
        Assert.Equal(["Bug"], parent.Labels);

        var child = _tasks.Create("", "Child", null, null, null, null, "TM-1");
        Assert.Equal("TM-1", child.Parent);
        Assert.Single(_tasks.Get("TM-1").Issue.Subtasks);
    }

    [Fact]
    public void List_DefaultsToOpenTopLevel()
    {
        _tasks.Create("TM", "open", null, "todo", null, null, null);
        _tasks.Create("TM", "closed", null, "done", null, null, null);
        _tasks.Create("TM", "child", null, null, null, null, "TM-1");

        Assert.Equal(["open"], _tasks.List(null, null, null, null, null, false, false, 100).Select(i => i.Title));
        Assert.Equal(3, _tasks.List(null, null, null, null, null, true, true, 100).Count);
        Assert.Equal(["closed"], _tasks.List(null, "done", null, null, null, false, false, 100).Select(i => i.Title));
    }

    [Fact]
    public void Update_AppendsDescriptionAndChangesLabels()
    {
        _tasks.Create("TM", "X", "Start", null, null, ["A", "B"], null);
        var u = _tasks.Update("tm-1", null, null, "Done: fixed it", "done", "low", ["C"], ["A"], null, null);
        Assert.Equal("Start\n\nDone: fixed it", u.Description);
        Assert.Equal("done", u.Status);
        Assert.Equal(["B", "C"], u.Labels.Order());
        Assert.NotNull(u.Completed);
    }

    [Fact]
    public void AttachImage_AppendsMarkdownAndGetReturnsFile()
    {
        _tasks.Create("TM", "X", null, null, null, null, null);
        var img = Path.Combine(_root, "shot.png");
        File.WriteAllBytes(img, [0x89, 0x50, 0x4E, 0x47]);
        var u = _tasks.AttachImage("TM-1", img, "screen");
        Assert.StartsWith("![screen](attachment:", u.Description);
        var (_, images) = _tasks.Get("TM-1");
        Assert.True(File.Exists(Assert.Single(images)));
    }

    [Fact]
    public void Errors_AreDescriptive()
    {
        Assert.Contains("TM (Task Manager)", Assert.Throws<ArgumentException>(() => _tasks.Create("nope", "x", null, null, null, null, null)).Message);
        Assert.Throws<ArgumentException>(() => _tasks.Get("TM-99"));
        Assert.Throws<ArgumentException>(() => TaskService.ParseStatus("whatever"));
    }

    [Fact]
    public void ChangesFromAnotherConnection_BumpDataVersion()
    {
        using var app = new Db(Path.Combine(_root, "data.db"));
        var before = app.DataVersion();
        _tasks.Create("TM", "from claude", null, null, null, null, null);
        Assert.NotEqual(before, app.DataVersion());
        Assert.Equal("from claude", Assert.Single(new IssueRepository(app).Search("claude")).Title);
    }

    [Fact]
    public void KnowledgeBase_WriteReadSearchAndLink()
    {
        var notes = Directory.CreateDirectory(Path.Combine(_root, "notes")).FullName;
        _tasks.KbAddSource(notes, "Notes");
        var w = _tasks.KbWrite("Notes", "arch/Overview", "Design #backend #api", null);
        Assert.Contains("\"arch/Overview.md\"", System.Text.Json.JsonSerializer.Serialize(w));
        _tasks.KbWrite("notes", "arch/Overview.md", null, "More #later");
        Assert.Equal("Design #backend #api\n\nMore #later", File.ReadAllText(Path.Combine(notes, "arch", "Overview.md")));

        var found = System.Text.Json.JsonSerializer.Serialize(_tasks.KbList(null, null, "later", null, 10));
        Assert.Contains("Overview", found);

        _tasks.Create("TM", "Issue", null, null, null, null, null);
        var issue = _tasks.LinkEntry("TM-1", "Notes", "arch/Overview", unlink: false);
        Assert.Equal("arch/Overview.md", Assert.Single(issue.LinkedEntries).Path);
        var read = System.Text.Json.JsonSerializer.Serialize(_tasks.KbRead("Notes", "arch/Overview.md"));
        Assert.Contains("TM-1", read);

        _tasks.KbRename("Notes", "arch", "architecture");
        Assert.Equal("architecture/Overview.md", Assert.Single(_tasks.Get("TM-1").Issue.LinkedEntries).Path);
        Assert.Throws<ArgumentException>(() => _tasks.KbWrite("Notes", "../escape.md", "x", null));
    }

    [Theory]
    [InlineData("В работе", IssueStatus.InProgress)]
    [InlineData("cancelled", IssueStatus.Canceled)]
    [InlineData("to-do", IssueStatus.Todo)]
    public void ParseStatus_Aliases(string input, IssueStatus expected) => Assert.Equal(expected, TaskService.ParseStatus(input));
}
