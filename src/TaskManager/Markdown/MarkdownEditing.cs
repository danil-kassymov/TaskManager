using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TaskManager.Services;

namespace TaskManager.Markdown;

/// <summary>
/// Editor conveniences for a plain TextBox holding markdown: paste/drop images as attachments,
/// paste a URL over a selection as a link, continue lists on Enter, Ctrl+B/I/K formatting.
/// </summary>
public static partial class MarkdownEditing
{
    /// <summary>Marks editors so window-level shortcuts (Ctrl+B, Ctrl+K) leave them alone.</summary>
    public static readonly DependencyProperty IsEditorProperty = DependencyProperty.RegisterAttached(
        "IsEditor", typeof(bool), typeof(MarkdownEditing), new PropertyMetadata(false));

    public static bool IsEditor(DependencyObject? d) => d is not null && (bool)d.GetValue(IsEditorProperty);

    /// <param name="onWikiLink">Called after the user types "[[" (to pick a knowledge base entry); the caret is right after it.</param>
    public static void Attach(TextBox box, AttachmentService attachments, Action<TextBox>? onWikiLink = null)
    {
        box.SetValue(IsEditorProperty, true);
        if (onWikiLink is not null)
            box.TextChanged += (_, e) =>
            {
                var c = box.CaretIndex;
                if (e.Changes.Any(ch => ch.AddedLength == 1) && c >= 2 && box.Text[c - 1] == '[' && box.Text[c - 2] == '[' &&
                    (c < 3 || box.Text[c - 3] != '['))
                    box.Dispatcher.BeginInvoke(() => onWikiLink(box));
            };
        box.PreviewKeyDown += (_, e) => OnKeyDown(box, attachments, e);
        box.AllowDrop = true;
        box.PreviewDragOver += (_, e) =>
        {
            if (DroppedImages(e.Data).Any())
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };
        box.PreviewDrop += (_, e) =>
        {
            var files = DroppedImages(e.Data).ToList();
            if (files.Count == 0) return;
            e.Handled = true;
            var pos = box.GetCharacterIndexFromPoint(e.GetPosition(box), true);
            if (pos >= 0) box.Select(pos, 0);
            Insert(box, string.Join("\n", files.Select(f => ImageMarkdown(attachments.SaveFile(f), Path.GetFileNameWithoutExtension(f)))));
        };
    }

    static IEnumerable<string> DroppedImages(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(AttachmentService.IsImageFile)
            : [];

    static string ImageMarkdown(string url, string alt = "image") => $"![{alt}]({url})";

    /// <summary>Replaces the "[[" right before the caret (if still there) with <paramref name="text"/>.</summary>
    public static void ReplaceWikiTrigger(TextBox box, int caret, string text)
    {
        if (caret >= 2 && caret <= box.Text.Length && box.Text.Substring(caret - 2, 2) == "[[")
            box.Select(caret - 2, 2);
        else
            box.Select(Math.Min(caret, box.Text.Length), 0);
        Insert(box, text);
    }

    static void OnKeyDown(TextBox box, AttachmentService attachments, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.V)
        {
            e.Handled = Paste(box, attachments);
        }
        else if (ctrl && e.Key == Key.B) { Wrap(box, "**"); e.Handled = true; }
        else if (ctrl && e.Key == Key.I) { Wrap(box, "*"); e.Handled = true; }
        else if (ctrl && e.Key == Key.K) { MakeLink(box, ""); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = ContinueList(box);
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = Indent(box, Keyboard.Modifiers == ModifierKeys.Shift);
        }
    }

    static bool Paste(TextBox box, AttachmentService attachments)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                if (attachments.SaveFromClipboard() is { } url)
                {
                    Insert(box, ImageMarkdown(url));
                    return true;
                }
                return false;
            }
            var text = Clipboard.GetText().Trim();
            if (box.SelectionLength > 0 && IsUrl(text) && !box.SelectedText.Contains('\n'))
            {
                MakeLink(box, text);
                return true;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException)
        {
            // Clipboard is busy or holds an unsupported format: fall back to the default paste.
        }
        return false;
    }

    public static bool IsUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "mailto");

    static void Insert(TextBox box, string text)
    {
        var start = box.SelectionStart;
        box.SelectedText = text;
        box.Select(start + text.Length, 0);
        box.Focus();
    }

    static void Wrap(TextBox box, string marker)
    {
        var start = box.SelectionStart;
        var sel = box.SelectedText;
        box.SelectedText = marker + sel + marker;
        box.Select(start + marker.Length, sel.Length);
    }

    static void MakeLink(TextBox box, string url)
    {
        var start = box.SelectionStart;
        var sel = box.SelectedText;
        var text = $"[{sel}]({url})";
        box.SelectedText = text;
        if (sel.Length == 0) box.Select(start + 1, 0);          // cursor inside [] to type the text
        else if (url.Length == 0) box.Select(start + sel.Length + 3, 0); // cursor inside () to type the url
        else box.Select(start + text.Length, 0);
    }

    [GeneratedRegex(@"^(\s*)([-*+]|(\d+)[.)])(\s+)(\[[ xX]\]\s+)?")]
    private static partial Regex ListPrefix();

    /// <summary>Enter on "- item" starts "- "; Enter on an empty item ends the list.</summary>
    static bool ContinueList(TextBox box)
    {
        if (box.SelectionLength > 0) return false;
        var caret = box.CaretIndex;
        var text = box.Text;
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var line = text[lineStart..caret];
        var m = ListPrefix().Match(line);
        if (!m.Success) return false;

        if (m.Length == line.Length && text.IndexOf('\n', caret) is var end && (end < 0 ? text.Length : end) == caret)
        {
            // Empty item: remove the marker instead of adding a new one.
            box.Select(lineStart, line.Length);
            box.SelectedText = "";
            box.Select(lineStart, 0);
            return true;
        }

        var marker = m.Groups[3].Success
            ? $"{int.Parse(m.Groups[3].Value) + 1}{m.Groups[2].Value[^1]}"
            : m.Groups[2].Value;
        var prefix = m.Groups[1].Value + marker + m.Groups[4].Value + (m.Groups[5].Success ? "[ ] " : "");
        Insert(box, "\n" + prefix);
        return true;
    }

    static bool Indent(TextBox box, bool outdent)
    {
        var caret = box.SelectionStart;
        var text = box.Text;
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var lineEnd = text.IndexOf('\n', lineStart);
        var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)];
        if (!ListPrefix().IsMatch(line)) return false;
        if (outdent)
        {
            var remove = line.StartsWith("  ") ? 2 : line.StartsWith('\t') ? 1 : 0;
            if (remove == 0) return true;
            box.Select(lineStart, remove);
            box.SelectedText = "";
            box.Select(Math.Max(lineStart, caret - remove), 0);
        }
        else
        {
            box.Select(lineStart, 0);
            box.SelectedText = "  ";
            box.Select(caret + 2, 0);
        }
        return true;
    }
}
