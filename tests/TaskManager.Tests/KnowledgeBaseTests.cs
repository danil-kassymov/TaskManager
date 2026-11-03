using TaskManager.Data;
using TaskManager.Kb;

namespace TaskManager.Tests;

public sealed class KnowledgeBaseTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"tm-kb-{Guid.NewGuid():N}");
    readonly Db _db = new(":memory:");
    readonly KnowledgeBase _kb;
    readonly KbSource _src;

    public KnowledgeBaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "notes", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, "readme.md"), "Root #project");
        File.WriteAllText(Path.Combine(_dir, "notes", "api.md"), "API design #backend #api\n\n`#notatag`");
        File.WriteAllText(Path.Combine(_dir, "notes", "deep", "db.md"), "Schema #backend");
        File.WriteAllText(Path.Combine(_dir, ".git", "hidden.md"), "#secret");
        File.WriteAllText(Path.Combine(_dir, "notes", "image.png"), "x");
        _kb = new KnowledgeBase(_db);
        _src = _kb.AddSource(_dir);
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("Plain #tag and #другой.", new[] { "tag", "другой" })]
    [InlineData("# Heading\n## Sub #real", new[] { "real" })]
    [InlineData("color #EB5757 issue #123 url http://x.com/#anchor a#b", new string[0])]
    [InlineData("code `#no` and\n```\n#nope\n```\n#yes", new[] { "yes" })]
    [InlineData("#Tag #tag #nested/sub-tag, #a1", new[] { "Tag", "nested/sub-tag", "a1" })]
    [InlineData("[link](#section) (#paren)", new[] { "paren" })]
    public void ParseTags(string md, string[] expected) => Assert.Equal(expected, KnowledgeBase.ParseTags(md));

    [Fact]
    public void Entries_AreMarkdownFilesSkippingHiddenFolders()
    {
        var paths = _kb.Entries(_src.Id).Select(e => e.Path).Order().ToArray();
        Assert.Equal(["notes/api.md", "notes/deep/db.md", "readme.md"], paths);
        Assert.Equal(["notes", "notes/deep"], _kb.Folders(_src.Id));
        Assert.Equal(["backend", "api"], _kb.Entry(_src.Id, "notes/api.md")!.Tags);
    }

    [Fact]
    public void Search_ByTextAndTag()
    {
        Assert.Equal(["notes/api.md"], _kb.Search(_src.Id, "design").Select(e => e.Path));
        Assert.Equal(2, _kb.Search(null, null, "#backend").Count);
        Assert.Equal(("backend", 2), _kb.TagCounts(_src.Id)[0]);
    }

    [Fact]
    public void TagColors_ArePersistedFromPaletteAndEditable()
    {
        var first = _kb.TagColors(["backend"])["backend"];
        Assert.Contains(first, KnowledgeBase.TagPalette);
        Assert.Equal(first, _kb.TagColors(["Backend"])["backend"]);
        _kb.SetTagColor("#backend", "#123456");
        Assert.Equal("#123456", _kb.TagColors(["backend"])["backend"]);
    }

    [Fact]
    public void Create_UniqueNamesAndFolders()
    {
        var a = _kb.Create(_src.Id, "notes", "api", "x");
        Assert.Equal("notes/api 2.md", a.Path);
        var folder = _kb.CreateFolder(_src.Id, "notes", "new: folder");
        Assert.Equal("notes/new  folder", folder);
        Assert.True(Directory.Exists(Path.Combine(_dir, "notes", "new  folder")));
    }

    [Fact]
    public void Resolve_RejectsTraversal()
    {
        Assert.Throws<ArgumentException>(() => _kb.Read(_src.Id, "../outside.md"));
        Assert.Throws<ArgumentException>(() => _kb.Write(_src.Id, "notes/../../x.md", "boom"));
    }

    [Fact]
    public void Rename_UpdatesLinksAndDescriptions()
    {
        var p = new ProjectRepository(_db).Create("P", "P", "#fff");
        var issues = new IssueRepository(_db);
        var issue = issues.Create(p.Id, "uses api", description: "See " + KnowledgeBase.LinkMarkdown(_src.Id, "notes/api.md"));
        _kb.Link(issue.Id, _src.Id, "notes/api.md");
        _kb.Link(issue.Id, _src.Id, "notes/deep/db.md");

        var newRel = _kb.Rename(_src.Id, "notes/api.md", "API v2");
        Assert.Equal("notes/API v2.md", newRel);
        _kb.Rename(_src.Id, "notes/deep", "storage");

        var links = _kb.LinksOf(issue.Id).Select(l => l.Path).Order().ToArray();
        Assert.Equal(["notes/API v2.md", "notes/storage/db.md"], links);
        Assert.All(_kb.LinksOf(issue.Id), l => Assert.True(l.Exists));
        Assert.Contains(KnowledgeBase.LinkUrl(_src.Id, "notes/API v2.md"), issues.Get(issue.Id)!.Description);
        Assert.Equal([issue.Id], _kb.IssuesLinking(_src.Id, "notes/API v2.md"));
    }

    [Fact]
    public void Move_IntoFolder()
    {
        var rel = _kb.Move(_src.Id, "readme.md", "notes/deep");
        Assert.Equal("notes/deep/readme.md", rel);
        Assert.Throws<InvalidOperationException>(() => _kb.Move(_src.Id, "notes", "notes/deep"));
    }

    [Theory]
    [InlineData("notes/My note.md")]
    [InlineData("a (1)/b#c.md")]
    public void LinkUrl_RoundTrips(string rel)
    {
        var url = KnowledgeBase.LinkUrl(7, rel);
        Assert.DoesNotContain(' ', url);
        Assert.DoesNotContain(')', url);
        Assert.True(KnowledgeBase.TryParseLink(url, out var id, out var back));
        Assert.Equal(7, id);
        Assert.Equal(rel, back);
    }
}
