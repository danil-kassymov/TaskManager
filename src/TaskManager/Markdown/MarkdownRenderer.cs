using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TaskManager.Controls;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using Table = System.Windows.Documents.Table;
using TableCell = System.Windows.Documents.TableCell;
using TableRow = System.Windows.Documents.TableRow;

namespace TaskManager.Markdown;

/// <summary>
/// Renders Markdig's AST straight into a WPF FlowDocument. Supports what is useful in task
/// descriptions: headings, emphasis, code, lists, clickable task lists, quotes, tables,
/// links, images and issue references like TM-42.
/// </summary>
public sealed partial class MarkdownRenderer
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    /// <summary>Markdown url → loadable image uri (handles <c>attachment:</c>).</summary>
    public Func<string, Uri?> ResolveImage { get; init; } = url => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;
    /// <summary>Called with a url, or with <c>issue:KEY-1</c> for issue references.</summary>
    public Action<string>? OpenLink { get; init; }
    /// <summary>Only "KEY-1" whose KEY passes this check become issue links (so "UTF-8" stays text).</summary>
    public Func<string, bool> IsProjectKey { get; init; } = _ => true;
    /// <summary>Color for a #tag, or null when it should be rendered as plain text.</summary>
    public Func<string, string?> TagColor { get; init; } = _ => null;
    /// <summary>Called with the source offset of the <c>[ ]</c> of a clicked task-list checkbox.</summary>
    public Action<int>? ToggleTask { get; init; }

    // Body text is a touch softer than pure white (less glare on the dark background), headings are brighter.
    static readonly Brush TextBrush = Palette.Brush("#DCDDE1");
    static readonly Brush HeadingBrush = Palette.Brush("#F4F4F6");
    static readonly Brush DimBrush = Palette.Brush("#A9ABB3");
    static readonly Brush LinkBrush = Palette.Brush("#9AA3F5");
    static readonly Brush CodeFg = Palette.Brush("#E8C48A");
    static readonly Brush CodeBg = Palette.Brush("#24252B");
    static readonly Brush CodeBlockBg = Palette.Brush("#1A1B1F");
    static readonly Brush BorderBrush = Palette.Brush("#30323A");
    static readonly Brush QuoteBar = Palette.Brush("#4A5180");
    static FontFamily Mono => ReadingStyle.Mono;

    /// <summary>Base font size of the document being rendered; everything else is relative to it.</summary>
    double _s = ReadingStyle.DefaultSize;

    public FlowDocument Render(string markdown)
    {
        _s = ReadingStyle.Size;
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = ReadingStyle.Family,
            FontSize = _s,
            Foreground = TextBrush,
            TextAlignment = TextAlignment.Left,
            LineHeight = ReadingStyle.LineHeight(_s),
            // MaxHeight: lines are at least LineHeight tall but grow for inline UI (checkboxes, images) instead of clipping them.
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
        };
        var ast = Markdig.Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var b in ast)
            if (RenderBlock(b) is { } block) doc.Blocks.Add(block);
        return doc;
    }

    Block? RenderBlock(MdBlock block)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                var size = _s * h.Level switch { 1 => 1.75, 2 => 1.4, 3 => 1.18, _ => 1.0 };
                var p = new Paragraph
                {
                    FontSize = size,
                    LineHeight = Math.Round(size * 1.3),
                    FontWeight = h.Level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
                    Foreground = HeadingBrush,
                    Margin = new Thickness(0, _s * (h.Level <= 2 ? 1.4 : 1.0), 0, _s * 0.45),
                };
                AddInlines(p.Inlines, h.Inline);
                return p;
            }
            case ParagraphBlock pb:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, _s * 0.8) };
                AddInlines(p.Inlines, pb.Inline);
                return p;
            }
            case ListBlock lb:
            {
                var isTasks = lb.OfType<ListItemBlock>().Any(IsTaskItem);
                var list = new List
                {
                    MarkerStyle = isTasks ? TextMarkerStyle.None : lb.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, _s * 0.8),
                    Padding = new Thickness(isTasks ? 2 : _s * 1.6, 0, 0, 0),
                    MarkerOffset = _s * 0.5,
                };
                if (lb.IsOrdered && int.TryParse(lb.OrderedStart, out var start)) list.StartIndex = start;
                foreach (var item in lb.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item)
                        if (RenderBlock(child) is { } cb)
                        {
                            cb.Margin = new Thickness(0, _s * 0.12, 0, _s * 0.12);
                            li.Blocks.Add(cb);
                        }
                    list.ListItems.Add(li);
                }
                return list;
            }
            case QuoteBlock qb:
            {
                var s = new Section
                {
                    BorderBrush = QuoteBar,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(_s * 0.9, _s * 0.1, 0, _s * 0.1),
                    Margin = new Thickness(0, 0, 0, _s * 0.8),
                    Foreground = DimBrush,
                };
                foreach (var child in qb)
                    if (RenderBlock(child) is { } cb) s.Blocks.Add(cb);
                return s;
            }
            case CodeBlock cb: // includes fenced blocks
            {
                var p = new Paragraph
                {
                    FontFamily = Mono,
                    FontSize = _s * 0.86,
                    LineHeight = Math.Round(_s * 0.86 * 1.55),
                    Background = CodeBlockBg,
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(_s * 0.9, _s * 0.6, _s * 0.9, _s * 0.6),
                    Margin = new Thickness(0, _s * 0.1, 0, _s * 0.9),
                };
                var lines = cb.Lines.ToString().Replace("\r", "").TrimEnd('\n').Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) p.Inlines.Add(new LineBreak());
                    p.Inlines.Add(new Run(lines[i]));
                }
                return p;
            }
            case ThematicBreakBlock:
                return new Paragraph
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, _s * 0.5, 0, _s * 1.2),
                    FontSize = 2,
                    LineHeight = 2,
                };
            case Markdig.Extensions.Tables.Table t:
                return RenderTable(t);
            case HtmlBlock html:
                return new Paragraph(new Run(html.Lines.ToString())) { FontFamily = Mono, Foreground = DimBrush };
            case ContainerBlock container:
            {
                var s = new Section();
                foreach (var child in container)
                    if (RenderBlock(child) is { } c) s.Blocks.Add(c);
                return s;
            }
            default:
                return null;
        }
    }

    static bool IsTaskItem(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: TaskList };

    Table RenderTable(Markdig.Extensions.Tables.Table t)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, _s * 0.9), BorderBrush = BorderBrush, BorderThickness = new Thickness(1, 1, 0, 0) };
        var cols = t.ColumnDefinitions.Count;
        for (var i = 0; i < cols; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var row in t.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var tr = new TableRow();
            if (row.IsHeader)
            {
                tr.FontWeight = FontWeights.SemiBold;
                tr.Background = CodeBg;
            }
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var tc = new TableCell
                {
                    BorderBrush = BorderBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(_s * 0.6, _s * 0.3, _s * 0.6, _s * 0.3),
                };
                foreach (var child in cell)
                    if (RenderBlock(child) is { } cb)
                    {
                        cb.Margin = new Thickness(0);
                        tc.Blocks.Add(cb);
                    }
                tr.Cells.Add(tc);
            }
            group.Rows.Add(tr);
        }
        table.RowGroups.Add(group);
        return table;
    }

    void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null) return;
        foreach (var inline in container)
            if (RenderInline(inline) is { } r) target.Add(r);
    }

    Inline? RenderInline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline lit:
                return TextWithIssueRefs(lit.Content.ToString());
            case EmphasisInline em:
            {
                var span = new Span();
                if (em.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                else if (em.DelimiterCount >= 2) span.FontWeight = FontWeights.SemiBold;
                else span.FontStyle = FontStyles.Italic;
                AddInlines(span.Inlines, em);
                return span;
            }
            case CodeInline code:
                return new Run(code.Content)
                {
                    FontFamily = Mono,
                    FontSize = _s * 0.88,
                    Foreground = CodeFg,
                    Background = CodeBg,
                };
            case LinkInline { IsImage: true } img:
                return RenderImage(img.Url, img.Title);
            case LinkInline link:
            {
                var h = MakeLink(link.Url ?? "");
                if (TaskManager.Kb.KnowledgeBase.TryParseLink(link.Url ?? "", out _, out var rel))
                {
                    h.Inlines.Add(new Run("📄 ") { FontSize = _s * 0.85 });
                    h.ToolTip = rel;
                }
                AddInlines(h.Inlines, link);
                if (h.Inlines.Count == 0) h.Inlines.Add(new Run(link.Url));
                return h;
            }
            case AutolinkInline auto:
            {
                var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                var h = MakeLink(url);
                h.Inlines.Add(new Run(auto.Url));
                return h;
            }
            case LineBreakInline br:
                return br.IsHard ? new LineBreak() : new Run(" ");
            case TaskList task:
                return RenderTask(task);
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case HtmlInline html:
                return new Run(html.Tag) { Foreground = DimBrush };
            case ContainerInline c:
            {
                var span = new Span();
                AddInlines(span.Inlines, c);
                return span;
            }
            default:
                return null;
        }
    }

    [GeneratedRegex(@"(?<issue>\b[A-Z][A-Z0-9]{0,9}-\d+\b)|(?<![\p{L}\p{N}_&/#\]\)\\:])#(?<tag>[\p{L}\p{N}_][\p{L}\p{N}_\-/]*)")]
    private static partial Regex RefOrTag();

    /// <summary>Plain text with clickable issue keys (TM-42) and colored #tags.</summary>
    Inline TextWithIssueRefs(string text)
    {
        var parts = new List<Inline>();
        var pos = 0;
        foreach (Match m in RefOrTag().Matches(text))
        {
            Inline? inline = null;
            if (m.Groups["issue"].Success && OpenLink is not null && IsProjectKey(m.Value[..m.Value.LastIndexOf('-')]))
            {
                var h = MakeLink("issue:" + m.Value);
                h.Inlines.Add(new Run(m.Value) { FontWeight = FontWeights.SemiBold });
                inline = h;
            }
            else if (m.Groups["tag"].Success && TagColor(m.Groups["tag"].Value.TrimEnd('-', '/', '_')) is { } color)
            {
                var brush = HexBrushConverter.Get(color);
                var tint = new SolidColorBrush(Color.FromArgb(0x30, brush.Color.R, brush.Color.G, brush.Color.B));
                tint.Freeze();
                var run = new Run(m.Value) { Foreground = brush, Background = tint, FontWeight = FontWeights.SemiBold };
                if (OpenLink is not null)
                {
                    var h = new Hyperlink(run) { TextDecorations = null, Cursor = Cursors.Hand, Foreground = brush, ToolTip = "Показать записи с этим тегом" };
                    var tag = m.Groups["tag"].Value;
                    h.Click += (_, _) => OpenLink?.Invoke("tag:" + tag);
                    inline = h;
                }
                else inline = run;
            }
            if (inline is null) continue;
            if (m.Index > pos) parts.Add(new Run(text[pos..m.Index]));
            parts.Add(inline);
            pos = m.Index + m.Length;
        }
        if (parts.Count == 0) return new Run(text);
        if (pos < text.Length) parts.Add(new Run(text[pos..]));
        var span = new Span();
        span.Inlines.AddRange(parts);
        return span;
    }
    Hyperlink MakeLink(string url)
    {
        var h = new Hyperlink
        {
            Foreground = LinkBrush,
            TextDecorations = null,
            Cursor = Cursors.Hand,
            ToolTip = url.StartsWith("issue:") ? null : url,
        };
        h.Click += (_, _) => OpenLink?.Invoke(url);
        return h;
    }

    Inline RenderImage(string? url, string? title)
    {
        var uri = url is null ? null : ResolveImage(url);
        if (uri is null) return new Run($"[изображение не найдено: {url}]") { Foreground = DimBrush };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            bmp.EndInit();
            var image = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                MaxWidth = 720,
                MaxHeight = 520,
                Cursor = Cursors.Hand,
                ToolTip = title ?? "Открыть изображение",
                Margin = new Thickness(0, _s * 0.4, 0, _s * 0.4),
            };
            image.MouseLeftButtonUp += (_, e) =>
            {
                OpenLink?.Invoke(uri.IsFile ? uri.LocalPath : uri.ToString());
                e.Handled = true;
            };
            return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return new Run($"[не удалось загрузить изображение: {url}]") { Foreground = DimBrush };
        }
    }

    Inline RenderTask(TaskList task)
    {
        var box = new CheckBox
        {
            IsChecked = task.Checked,
            Margin = new Thickness(0, 0, _s * 0.45, 0),
            // The Fluent check glyph is a font icon sized by FontSize: don't inherit the document's (larger) text size.
            FontSize = 14,
            FontFamily = new FontFamily("Segoe UI"),
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
        };
        // Line height is inherited too and would shift the glyph inside the box: reset it to auto.
        box.SetValue(Block.LineHeightProperty, double.NaN);
        box.SetValue(Block.LineStackingStrategyProperty, LineStackingStrategy.MaxHeight);
        var offset = task.Span.Start;
        box.Click += (_, _) => ToggleTask?.Invoke(offset);
        return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>Flips <c>[ ]</c> ↔ <c>[x]</c> at the offset reported by <see cref="ToggleTask"/>.</summary>
    public static string ToggleTaskAt(string markdown, int offset)
    {
        var i = markdown.IndexOf('[', Math.Max(0, offset));
        if (i < 0 || i + 2 >= markdown.Length || markdown[i + 2] != ']') return markdown;
        var mark = markdown[i + 1] is 'x' or 'X' ? ' ' : 'x';
        return string.Concat(markdown.AsSpan(0, i + 1), mark.ToString(), markdown.AsSpan(i + 2));
    }
}
