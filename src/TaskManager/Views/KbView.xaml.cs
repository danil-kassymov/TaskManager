using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TaskManager.Controls;
using TaskManager.Markdown;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class KbView : UserControl
{
    KbViewModel? _vm;
    KbEntryViewModel? _entry;
    readonly MarkdownRenderer _renderer;

    public KbView()
    {
        InitializeComponent();
        _renderer = new MarkdownRenderer
        {
            ResolveImage = url => App.Services.Attachments.Resolve(url) ?? ResolveRelative(url),
            OpenLink = url => _vm?.Main.OpenLink(url),
            ToggleTask = offset => _entry?.ToggleTask(offset),
            IsProjectKey = key => _vm?.Main.Projects.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == true,
            TagColor = tag => _entry?.Tags.FirstOrDefault(t => t.Name.Equals(tag, StringComparison.CurrentCultureIgnoreCase))?.Color,
        };
        MarkdownEditing.Attach(Editor, App.Services.Attachments);
        ApplyReadingStyle();
        // Size/font changes (Ctrl+= / Ctrl+-) apply to every open view at once.
        ReadingStyle.Changed += () => { ApplyReadingStyle(); RenderPreview(); };        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm = DataContext as KbViewModel;
            if (_vm is not null) _vm.PropertyChanged += Vm_PropertyChanged;
        };
    }

    void ApplyReadingStyle()
    {
        Editor.FontSize = ReadingStyle.EditorSize;
        TextBlock.SetLineHeight(Editor, ReadingStyle.LineHeight(ReadingStyle.EditorSize));
        TextBlock.SetLineStackingStrategy(Editor, LineStackingStrategy.BlockLineHeight);
        TitleBox.FontSize = Math.Round(ReadingStyle.Size * 1.5);
    }

    /// <summary>Images referenced relatively from a .md file (e.g. "img/a.png") resolve against that file's folder.</summary>
    Uri? ResolveRelative(string url)
    {
        if (_vm is null || _entry is null || Uri.TryCreate(url, UriKind.Absolute, out _)) return null;
        try
        {
            var folder = Path.GetDirectoryName(_vm.Kb.Resolve(_entry.SourceId, _entry.Path))!;
            var full = Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(url)));
            return File.Exists(full) ? new Uri(full) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KbViewModel.Entry)) return;
        if (_entry is not null) _entry.PropertyChanged -= Entry_PropertyChanged;
        _entry = _vm?.Entry;
        if (_entry is null) return;
        _entry.PropertyChanged += Entry_PropertyChanged;
        Scroll.ScrollToTop();
        RenderPreview();
    }

    void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KbEntryViewModel.IsEditing) or nameof(KbEntryViewModel.Content) && _entry is { IsEditing: false })
            RenderPreview();
    }

    void RenderPreview()
    {
        if (_entry is null || _entry.IsEditing) return;
        Preview.Document = _renderer.Render(_entry.Content);
    }

    public void FocusTitle() => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
    });

    public void FocusEditor()
    {
        if (_entry is null) return;
        _entry.IsEditing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Editor.Focus();
            Editor.CaretIndex = Editor.Text.Length;
        });
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    Window Owner => Window.GetWindow(this)!;

    // ---------------- Tree ----------------

    void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is KbNode { IsFolder: false } node) _vm?.OpenEntry(node.Path);
    }

    void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is KbNode node) _vm?.OpenEntry(node.Path);
    }

    static KbNode? NodeOf(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is KbNode node) return node;
        return ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement { DataContext: KbNode n } ? n : null;
    }

    void NodeNewEntry_Click(object sender, RoutedEventArgs e) => _vm?.NewEntry(NodeOf(sender));

    void RootNewEntry_Click(object sender, RoutedEventArgs e) => _vm?.NewEntry(null);

    void NodeNewFolder_Click(object sender, RoutedEventArgs e) => AskFolder(NodeOf(sender));

    void NewFolder_Click(object sender, RoutedEventArgs e) => AskFolder(null);

    void AskFolder(KbNode? parent)
    {
        var r = Dialogs.Prompt(Owner, "Новая папка", [new("Название", Hint: "Например, Архитектура")], "Создать");
        if (r is not null && r[0].Length > 0) _vm?.CreateFolder(parent, r[0]);
    }

    void NodeRename_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is not { } node) return;
        var r = Dialogs.Prompt(Owner, node.IsFolder ? "Переименовать папку" : "Переименовать запись", [new("Название", node.Name)]);
        if (r is not null && r[0].Length > 0) _vm?.Rename(node, r[0]);
    }

    void NodeReveal_Click(object sender, RoutedEventArgs e) => _vm?.Reveal(NodeOf(sender));

    void RevealRoot_Click(object sender, RoutedEventArgs e) => _vm?.Reveal(null);

    void NodeDelete_Click(object sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node) ConfirmDelete(node);
    }

    void ConfirmDelete(KbNode node)
    {
        var what = node.IsFolder ? $"Папка «{node.Name}» со всем содержимым" : $"Запись «{node.Name}»";
        if (Dialogs.Confirm(Owner, "Удалить", $"{what} будет перемещена в корзину Windows."))
            _vm?.Delete(node);
    }

    // Drag a node onto a folder (or empty space = root) to move it.
    Point _dragStart;
    KbNode? _dragNode;

    void Tree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Tree);
        _dragNode = (e.OriginalSource as FrameworkElement)?.DataContext as KbNode;
        // Folders open on a single click (see Tree_PreviewMouseUp); swallow the built-in double-click
        // toggle so a double click doesn't open and immediately close the folder.
        _clickCount = e.ClickCount;
        if (e.ClickCount >= 2 && FolderHeaderAt(e.OriginalSource) is not null) e.Handled = true;
    }

    int _clickCount;

    void Tree_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragNode = null; // a stale node must never be dragged by a later mouse move
        // The button-up event doesn't carry the click count, so use the one from button-down.
        if (_clickCount != 1 || FolderHeaderAt(e.OriginalSource) is not { } node) return;
        node.IsExpanded = !node.IsExpanded;
    }

    /// <summary>The folder whose row was clicked, unless the click hit the expander arrow (it toggles by itself).</summary>
    static KbNode? FolderHeaderAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton) return null;
            if (d is TreeViewItem item) return item.DataContext is KbNode { IsFolder: true } n ? n : null;
            if (d is ContentElement) return null;
        }
        return null;
    }

    void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(Tree) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _dragNode;
        _dragNode = null;
        DragDrop.DoDragDrop(Tree, new DataObject(typeof(KbNode), node), DragDropEffects.Move);
    }

    void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(KbNode)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void Tree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(KbNode)) is not KbNode node || _vm is null) return;
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as KbNode;
        var folder = target is null ? null : target.IsFolder ? target : FolderNode(target.Folder);
        if (folder?.Path == node.Path) return;
        if (folder is null && node.Folder.Length == 0) return;
        if (folder is not null && folder.Path == node.Folder) return;
        _vm.Move(node, folder);
    }

    KbNode? FolderNode(string path) => path.Length == 0 ? null : new KbNode(_vm!, true, path);

    // ---------------- Tags ----------------

    void TagMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { DataContext: TagChip chip } button || button.ContextMenu is not { } menu) return;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = $"Цвет тега #{chip.Name}", IsEnabled = false });
        foreach (var hex in TaskManager.Kb.KnowledgeBase.TagPalette)
        {
            var item = new MenuItem
            {
                Header = new Border { Width = 48, Height = 12, CornerRadius = new CornerRadius(3), Background = HexBrushConverter.Get(hex) },
                IsChecked = chip.Color.Equals(hex, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) => _vm?.SetTagColor(chip.Name, hex);
            menu.Items.Add(item);
        }
    }

    // ---------------- Entry ----------------

    void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _entry?.CommitTitle();
            FocusEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_entry is not null) _entry.Title = TaskManager.Kb.KnowledgeBase.TitleOf(_entry.Path);
            Tree.Focus();
            e.Handled = true;
        }
    }

    void TitleBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => _entry?.CommitTitle();

    void Edit_Click(object sender, RoutedEventArgs e) => FocusEditor();

    void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { FocusEditor(); e.Handled = true; }
    }

    void Editor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Window.GetWindow(this)?.IsActive != true || _vm?.Main.Palette.IsOpen == true) return;
        _entry?.PreviewCommand.Execute(null);
    }

    void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            _entry?.PreviewCommand.Execute(null);
            Tree.Focus();
            e.Handled = true;
        }
    }

    void EntryReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is not null) _vm?.Reveal(new KbNode(_vm, false, _entry.Path));
    }

    public void DeleteCurrent()
    {
        if (_entry is not null && _vm is not null) ConfirmDelete(new KbNode(_vm, false, _entry.Path));
    }

    void EntryDelete_Click(object sender, RoutedEventArgs e) => DeleteCurrent();

    void EntryClose_Click(object sender, RoutedEventArgs e) => _vm?.CloseEntry();
}
