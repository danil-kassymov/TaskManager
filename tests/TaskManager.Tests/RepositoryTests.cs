using TaskManager.Data;
using TaskManager.Models;

namespace TaskManager.Tests;

public sealed class RepositoryTests : IDisposable
{
    readonly Db _db = new(":memory:");
    readonly ProjectRepository _projects;
    readonly IssueRepository _issues;
    readonly LabelRepository _labels;
    readonly Project _project;

    public RepositoryTests()
    {
        _projects = new ProjectRepository(_db);
        _issues = new IssueRepository(_db);
        _labels = new LabelRepository(_db);
        _project = _projects.Create("Task Manager", "tm", "#5E6AD2");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Create_AssignsSequentialNumbersAndKeys()
    {
        var a = _issues.Create(_project.Id, "First");
        var b = _issues.Create(_project.Id, "Second");
        Assert.Equal("TM-1", a.Key);
        Assert.Equal("TM-2", b.Key);
    }

    [Fact]
    public void Project_DuplicateKeyIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => _projects.Create("Other", "TM", "#fff"));
    }

    [Theory]
    [InlineData("Task Manager", "TM")]
    [InlineData("backend", "BAC")]
    [InlineData("my-cool-app", "MCA")]
    public void SuggestKey(string name, string expected) => Assert.Equal(expected, ProjectRepository.SuggestKey(name));

    [Fact]
    public void Subtasks_AreCountedAndHiddenFromTopLevel()
    {
        var parent = _issues.Create(_project.Id, "Parent");
        var c1 = _issues.Create(_project.Id, "Child 1", parentId: parent.Id);
        _issues.Create(_project.Id, "Child 2", parentId: parent.Id);
        _issues.SetStatus(c1.Id, IssueStatus.Done);

        var top = _issues.Query(new IssueQuery());
        var p = Assert.Single(top);
        Assert.Equal(2, p.SubTotal);
        Assert.Equal(1, p.SubDone);
        Assert.Equal(["Child 1", "Child 2"], _issues.Children(parent.Id).Select(i => i.Title));
    }

    [Fact]
    public void Status_SetsAndClearsCompletedAt()
    {
        var i = _issues.Create(_project.Id, "X");
        _issues.SetStatus(i.Id, IssueStatus.Done);
        Assert.NotNull(_issues.Get(i.Id)!.CompletedAt);
        _issues.SetStatus(i.Id, IssueStatus.InProgress);
        Assert.Null(_issues.Get(i.Id)!.CompletedAt);
    }

    [Fact]
    public void Labels_FilterAndRoundTrip()
    {
        var bug = _labels.Create("Bug", "#f00");
        var feat = _labels.Create("Feature", "#0f0");
        var a = _issues.Create(_project.Id, "A");
        _issues.Create(_project.Id, "B");
        _issues.SetLabels(a.Id, [bug.Id, feat.Id]);

        var withBug = _issues.Query(new IssueQuery { LabelId = bug.Id });
        Assert.Equal("A", Assert.Single(withBug).Title);
        Assert.Equal(new[] { bug.Id, feat.Id }.Order(), withBug[0].GetLabelIds().Order());

        _labels.Delete(bug.Id);
        Assert.Equal([feat.Id], _issues.Get(a.Id)!.GetLabelIds());
    }

    [Fact]
    public void Search_ByWordsPrefixAndKey()
    {
        _issues.Create(_project.Id, "Fix login crash", description: "Происходит при пустом пароле");
        var b = _issues.Create(_project.Id, "Add dark theme");

        Assert.Equal("Fix login crash", Assert.Single(_issues.Search("log cra")).Title);
        Assert.Single(_issues.Search("пароле"));
        Assert.Equal(b.Id, Assert.Single(_issues.Search("tm-2")).Id);
        Assert.Empty(_issues.Search("nothing"));
    }

    [Fact]
    public void Search_SeesTitleUpdates()
    {
        var a = _issues.Create(_project.Id, "Old title");
        _issues.SetTitle(a.Id, "Brand new");
        Assert.Empty(_issues.Search("old"));
        Assert.Single(_issues.Search("brand"));
    }

    [Fact]
    public void Search_QuotesAreSanitized()
    {
        _issues.Create(_project.Id, "Quote \"test\"");
        Assert.Single(_issues.Search("\"test"));
    }

    [Fact]
    public void Archive_HidesIssueAndSubtasks()
    {
        var parent = _issues.Create(_project.Id, "Parent");
        _issues.Create(_project.Id, "Child", parentId: parent.Id);
        _issues.Archive(parent.Id);
        Assert.Empty(_issues.Query(new IssueQuery { TopLevelOnly = false }));
    }

    [Fact]
    public void Sort_PriorityPutsNoneLast_AndStatusGroupsInProgressFirst()
    {
        var none = _issues.Create(_project.Id, "none");
        var low = _issues.Create(_project.Id, "low", priority: Priority.Low);
        var urgent = _issues.Create(_project.Id, "urgent", priority: Priority.Urgent);
        var wip = _issues.Create(_project.Id, "wip", IssueStatus.InProgress);

        var ids = _issues.Query(new IssueQuery()).Select(i => i.Id).ToArray();
        Assert.Equal([wip.Id, urgent.Id, low.Id, none.Id], ids);
    }

    [Fact]
    public void Move_ChangesStatusAndOrder()
    {
        var a = _issues.Create(_project.Id, "a");
        var b = _issues.Create(_project.Id, "b");
        _issues.Move(a.Id, IssueStatus.Todo, b.Sort - 1);
        var ids = _issues.Query(new IssueQuery { Sort = SortMode.Manual }).Select(i => i.Id).ToArray();
        Assert.Equal([a.Id, b.Id], ids);
    }

    [Fact]
    public void MoveToProject_RenumbersIssue()
    {
        var other = _projects.Create("Other", "OT", "#fff");
        var a = _issues.Create(_project.Id, "a");
        _issues.MoveToProject(a.Id, other.Id);
        Assert.Equal("OT-1", _issues.Get(a.Id)!.Key);
        Assert.Equal(a.Id, _issues.GetByKey("ot-1")!.Id);
    }

    [Fact]
    public void FileDatabase_SurvivesReopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tm-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new Db(path))
            {
                var p = new ProjectRepository(db).Create("P", "P", "#fff");
                new IssueRepository(db).Create(p.Id, "persisted");
            }
            using (var db = new Db(path))
                Assert.Equal("persisted", Assert.Single(new IssueRepository(db).Search("persisted")).Title);
        }
        finally
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
