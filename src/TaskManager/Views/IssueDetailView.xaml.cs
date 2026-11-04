using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskManager.Markdown;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class IssueDetailView : UserControl
{
    IssueDetailViewModel? _vm;
    readonly MarkdownRenderer _renderer;

    public IssueDetailView()
    {
        InitializeComponent();
        _renderer = new MarkdownRenderer
        {
            ResolveImage = url => App.Services.Attachments.Resolve(url),
            OpenLink = url => _vm?.OpenLink(url),
            ToggleTask = offset => _vm?.ToggleTask(offset),
            IsProjectKey = key => _vm?.Projects.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) == true,
        };
        MarkdownEditing.Attach(Editor, App.Services.Attachments, InsertKbLink);        DataContextChanged += OnDataContextChanged;
        ApplyReadingStyle();
        // Size/font changes (Ctrl+= / Ctrl+-) apply to every open view at once.
        ReadingStyle.Changed += () => { ApplyReadingStyle(); RenderPreview(); };    }

    void ApplyReadingStyle()
    {
        Editor.FontSize = ReadingStyle.EditorSize;
        TextBlock.SetLineHeight(Editor, ReadingStyle.LineHeight(ReadingStyle.EditorSize));
        TextBlock.SetLineStackingStrategy(Editor, LineStackingStrategy.BlockLineHeight);
        TitleBox.FontSize = Math.Round(ReadingStyle.Size * 1.5);
    }

    static MainViewModel? Main => Application.Current.MainWindow?.DataContext as MainViewModel;

    /// <summary>"[[" in the description: pick an entry, insert a link to it and attach it to the issue.</summary>
    void InsertKbLink(TextBox box)
    {
        if (Main is not { } main || _vm is not { } vm) return;
        if (main.KbNav.Count == 0) { main.Status = "Сначала добавь папку в «База данных»"; return; }
        var caret = box.CaretIndex;
        main.Palette.OpenEntryPicker("Вставить ссылку на запись…", e =>
        {
            MarkdownEditing.ReplaceWikiTrigger(box, caret, Kb.KnowledgeBase.LinkMarkdown(e.SourceId, e.Path));
            vm.LinkEntry(e);
            FocusEditor();
        });
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as IssueDetailViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += Vm_PropertyChanged;
        Scroll.ScrollToTop();
        RenderPreview();
    }

    void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The preview is only rebuilt when visible, so typing in the editor stays cheap.
        if (e.PropertyName == nameof(IssueDetailViewModel.IsEditing) && _vm is { IsEditing: false }) RenderPreview();
        else if (e.PropertyName == nameof(IssueDetailViewModel.Description) && _vm is { IsEditing: false }) RenderPreview();
    }

    void RenderPreview()
    {
        if (_vm is null || _vm.IsEditing) return;
        Preview.Document = _renderer.Render(_vm.Description);
    }

    public void FocusEditor()
    {
        if (_vm is null) return;
        _vm.IsEditing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Editor.Focus();
            Editor.CaretIndex = Editor.Text.Length;
        });
    }

    public void FocusTitle()
    {
        TitleBox.Focus();
        TitleBox.CaretIndex = TitleBox.Text.Length;
    }

    public void FocusSubtask()
    {
        SubtaskBox.Focus();
        SubtaskBox.BringIntoView();
    }

    void Edit_Click(object sender, RoutedEventArgs e) => FocusEditor();

    void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FocusEditor();
            e.Handled = true;
        }
    }

    void Editor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Leaving the editor (but not to a menu or dialog opened from it) switches back to the preview.
        if (e.NewFocus is DependencyObject target && IsAncestorOf(target)) return;
        if (Main?.Palette.IsOpen == true) return; // picking an entry for "[[" — stay in the editor
        if (Window.GetWindow(this)?.IsActive != true) return;
        _vm?.PreviewCommand.Execute(null);
    }

    void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            _vm?.PreviewCommand.Execute(null);
            ReturnFocus();
            e.Handled = true;
        }
    }

    void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter)
        {
            _vm?.Flush();
            FocusEditor();
            e.Handled = true;
        }
        else if (e.Key is Key.Escape)
        {
            _vm?.Flush();
            ReturnFocus();
            e.Handled = true;
        }
    }

    void SubtaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.AddSubtaskCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_vm is not null) _vm.NewSubtask = "";
            ReturnFocus();
            e.Handled = true;
        }
    }

    void ReturnFocus() => (Window.GetWindow(this)?.DataContext as MainViewModel)?.RequestFocus("list");

    void Close_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this)?.DataContext as MainViewModel)?.CloseDetail();

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || Window.GetWindow(this) is not { } w) return;
        if (Dialogs.Confirm(w, "Удалить задачу", $"{_vm.Key} «{_vm.Title}» и её подзадачи будут удалены."))
            (w.DataContext as MainViewModel)?.Archive(_vm.Id);
    }
}
