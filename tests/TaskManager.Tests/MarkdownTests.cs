using System.Windows.Documents;
using TaskManager.Markdown;
using TaskManager.ViewModels;

namespace TaskManager.Tests;

public sealed class MarkdownTests
{
    /// <summary>WPF content elements must be created on an STA thread.</summary>
    static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }

    static string Text(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text;

    [Fact]
    public void Renders_CommonBlocks()
    {
        var (blocks, text) = OnSta(() =>
        {
            var doc = new MarkdownRenderer().Render("# Title\n\nSome **bold** and `code`.\n\n- a\n- b\n\n```\nx = 1\n```\n\n> quote");
            return (doc.Blocks.Select(b => b.GetType().Name).ToArray(), Text(doc));
        });
        Assert.Equal(["Paragraph", "Paragraph", "List", "Paragraph", "Section"], blocks);
        Assert.Contains("Some bold and code.", text);
        Assert.Contains("x = 1", text);
    }

    [Fact]
    public void IssueReferences_BecomeLinks()
    {
        var count = OnSta(() =>
        {
            var doc = new MarkdownRenderer { OpenLink = _ => { } }.Render("See TM-12 and ABC-3, not tm-1.");
            return CountLinks(doc.Blocks);
        });
        Assert.Equal(2, count);
    }

    [Fact]
    public void IssueReferences_OnlyForKnownProjects()
    {
        var count = OnSta(() =>
        {
            var doc = new MarkdownRenderer { OpenLink = _ => { }, IsProjectKey = k => k == "TM" }.Render("TM-12 in UTF-8");
            return CountLinks(doc.Blocks);
        });
        Assert.Equal(1, count);
    }

    static int CountLinks(IEnumerable<Block> blocks) =>
        blocks.OfType<Paragraph>().Sum(p => CountLinks(p.Inlines));

    static int CountLinks(InlineCollection inlines) =>
        inlines.Sum(i => i switch
        {
            Hyperlink => 1,
            Span s => CountLinks(s.Inlines),
            _ => 0,
        });

    [Fact]
    public void MissingImage_RendersPlaceholder()
    {
        var text = OnSta(() => Text(new MarkdownRenderer { ResolveImage = _ => null }.Render("![x](attachment:nope.png)")));
        Assert.Contains("изображение не найдено", text);
    }

    [Fact]
    public void TaskToggle_UsesSourceOffsetFromParser()
    {
        const string md = "Intro\n\n- [ ] first\n- [x] second\n";
        var offsets = new List<int>();
        OnSta(() =>
        {
            var doc = new MarkdownRenderer { ToggleTask = offsets.Add }.Render(md);
            // Simulate clicks on every checkbox in the rendered document.
            foreach (var box in Descendants(doc).OfType<InlineUIContainer>().Select(c => c.Child).OfType<System.Windows.Controls.CheckBox>())
                box.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            return 0;
        });
        Assert.Equal(2, offsets.Count);
        var once = MarkdownRenderer.ToggleTaskAt(md, offsets[0]);
        Assert.Contains("- [x] first", once);
        var twice = MarkdownRenderer.ToggleTaskAt(once, offsets[1]);
        Assert.Contains("- [ ] second", twice);
    }

    static IEnumerable<TextElement> Descendants(FlowDocument doc) =>
        doc.Blocks.SelectMany(Walk);

    static IEnumerable<TextElement> Walk(TextElement e)
    {
        yield return e;
        IEnumerable<TextElement> children = e switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            List l => l.ListItems,
            ListItem li => li.Blocks,
            Section s => s.Blocks,
            _ => [],
        };
        foreach (var c in children)
        foreach (var d in Walk(c))
            yield return d;
    }

    [Theory]
    [InlineData("TM-42", "Fix login crash", "tm-42-fix-login-crash")]
    [InlineData("TM-1", "Починить вход!", "tm-1-pochinit-vhod")]
    public void BranchName(string key, string title, string expected) =>
        Assert.Equal(expected, IssueDetailViewModel.BranchName(key, title));
}
