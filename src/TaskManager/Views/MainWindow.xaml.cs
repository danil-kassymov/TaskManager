using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TaskManager.Controls;
using TaskManager.Markdown;
using TaskManager.Models;
using TaskManager.Services;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        ApplyTitleSize();
        ReadingStyle.Changed += ApplyTitleSize;
        RestoreLayout(vm.S.Settings);

        vm.PropertyChanged += Vm_PropertyChanged;
        Loaded += (_, _) => UpdateDetailLayout();
        vm.FocusRequested += OnFocusRequested;
        vm.ErrorRaised += (title, message) => Dialogs.Info(this, title, message);
        vm.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && !vm.Palette.IsOpen) RestoreFocus();
        };
        vm.Create.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QuickCreateViewModel.IsOpen) && !vm.Create.IsOpen) RestoreFocus();
        };

        var actions = vm.Palette.ViewActions;
        actions["new-project"] = () => NewProject_Click(this, new RoutedEventArgs());
        actions["new-label"] = () => NewLabel_Click(this, new RoutedEventArgs());
        actions["export"] = () => Export_Click(this, new RoutedEventArgs());
        actions["import"] = () => Import_Click(this, new RoutedEventArgs());
        actions["open-data"] = () => OpenData_Click(this, new RoutedEventArgs());
        actions["shortcuts"] = () => Shortcuts_Click(this, new RoutedEventArgs());
        actions["delete"] = DeleteCurrent;
        actions["add-kb"] = () => AddKbSource_Click(this, new RoutedEventArgs());
        actions["text-bigger"] = ReadingStyle.Bigger;
        actions["text-smaller"] = ReadingStyle.Smaller;
        actions["text-serif"] = ReadingStyle.ToggleSerif;

        PreviewKeyDown += Window_PreviewKeyDown;        KeyDown += Window_KeyDown;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Closing += Window_Closing;
        Loaded += (_, _) => IssueList.Focus();
    }

    // ---------------- Layout persistence ----------------

    void RestoreLayout(Settings s)
    {
        Width = s.Width;
        Height = s.Height;
        if (s.Left is { } l && s.Top is { } t &&
            l > SystemParameters.VirtualScreenLeft - 50 && l < SystemParameters.VirtualScreenWidth - 100 &&
            t > SystemParameters.VirtualScreenTop - 50 && t < SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.Maximized) WindowState = WindowState.Maximized;
        SidebarColumn.Width = new GridLength(s.SidebarWidth);
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _vm.Detail?.Flush();
        var s = _vm.S.Settings;
        s.Maximized = WindowState == WindowState.Maximized;
        var bounds = RestoreBounds;
        if (!bounds.IsEmpty)
        {
            s.Width = bounds.Width;
            s.Height = bounds.Height;
            s.Left = bounds.Left;
            s.Top = bounds.Top;
        }
        s.SidebarWidth = SidebarColumn.ActualWidth;
        if (_detailShown) s.ListWidth = MainColumn.ActualWidth;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        var caption = 0x00120F0F; // COLORREF (BGR) of #0F1012 — Windows 11 only, ignored elsewhere
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Detail):
            case nameof(MainViewModel.IsBoard):
            case nameof(MainViewModel.IsKbMode):
                UpdateDetailLayout();
                break;
            case nameof(MainViewModel.SelectedRow):
                if (_vm.SelectedRow is { } row && !_vm.IsBoard) IssueList.ScrollIntoView(row);
                break;
        }
    }

    bool _detailShown;

    /// <summary>In list mode the detail pane is always there (with a hint when nothing is selected); on the board it appears with a selection.</summary>
    void UpdateDetailLayout()
    {
        var open = !_vm.IsKbMode && (_vm.Detail is not null || !_vm.IsBoard);
        LayoutDetail(open);
        DetailSplitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        DetailPlaceholder.Visibility = open && _vm.Detail is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>With an issue open the list becomes a narrow column and the issue gets the rest of the window.</summary>
    void LayoutDetail(bool open)
    {
        if (open == _detailShown) return;
        _detailShown = open;
        var s = _vm.S.Settings;
        if (open)
        {
            // 480 px keeps the list toolbar (filter, view menu, list/board switch) on one line.
            const double minList = 480;
            var max = Math.Max(minList, Root.ActualWidth - SidebarColumn.ActualWidth - 480);
            MainColumn.Width = new GridLength(Math.Clamp(s.ListWidth, minList, max));
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.MinWidth = 420;
        }
        else
        {
            if (MainColumn.ActualWidth > 0) s.ListWidth = MainColumn.ActualWidth;
            MainColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.MinWidth = 0;
            DetailColumn.Width = new GridLength(0);
        }
    }

    // ---------------- Keyboard ----------------

    static bool IsTyping => Keyboard.FocusedElement is TextBoxBase;
    bool OverlayOpen => _vm.Palette.IsOpen || _vm.Create.IsOpen;

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var inEditor = MarkdownEditing.IsEditor(Keyboard.FocusedElement as DependencyObject);
        if (mods == ModifierKeys.Control && e.Key == Key.K && !inEditor)
        {
            if (_vm.Palette.IsOpen) _vm.Palette.IsOpen = false;
            else OpenPalette(PaletteScope.All);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.B && !inEditor && !OverlayOpen)
        {
            _vm.IsBoard = !_vm.IsBoard;
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.N && !OverlayOpen)
        {
            _vm.NewIssue(null);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add)
        {
            ReadingStyle.Bigger();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key is Key.OemMinus or Key.Subtract)
        {
            ReadingStyle.Smaller();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
        {
            ReadingStyle.Reset();
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Control && e.Key == Key.F && !OverlayOpen)
        {
            FocusFilter();
            e.Handled = true;
        }
    }

    void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (OverlayOpen || e.Handled) return;

        if (e.Key == Key.Escape)
        {
            if (Keyboard.FocusedElement == FilterBox)
            {
                if (_vm.FilterText.Length > 0) _vm.FilterText = "";
                IssueList.Focus();
            }
            else if (IsTyping) IssueList.Focus();
            else if (_vm.IsKbMode && _vm.Kb.Entry is not null) _vm.Kb.CloseEntry();
            else if (_vm.IsKbMode && _vm.Kb.IsFiltering) _vm.Kb.ClearFiltersCommand.Execute(null);
            else if (_vm.Detail is not null) _vm.CloseDetail();
            else if (_vm.FilterText.Length > 0) _vm.FilterText = "";
            e.Handled = true;
            return;
        }
        if (IsTyping || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;

        if (_vm.IsKbMode)
        {
            e.Handled = true;
            switch (e.Key)
            {
                case Key.C or Key.N: _vm.Kb.NewEntry(null); break;
                case Key.OemQuestion or Key.Divide: KbView.FocusFilter(); break;
                case Key.E or Key.Enter when _vm.Kb.Entry is not null: KbView.FocusEditor(); break;
                case Key.T when _vm.Kb.Entry is not null: KbView.FocusTitle(); break;
                case Key.Delete when _vm.Kb.Entry is not null: KbView.DeleteCurrent(); break;
                default: e.Handled = false; break;
            }
            return;
        }

        var d = _vm.Detail;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.C: _vm.NewIssue(null); break;
            case Key.OemQuestion or Key.Divide:
                FocusFilter();
                break;
            case Key.J or Key.Down: _vm.MoveSelection(1); break;
            case Key.K or Key.Up: _vm.MoveSelection(-1); break;
            case Key.S when d is not null: OpenPalette(PaletteScope.Status); break;
            case Key.P when d is not null: OpenPalette(PaletteScope.Priority); break;
            case Key.L when d is not null: OpenPalette(PaletteScope.Label); break;
            case Key.M when d is not null: OpenPalette(PaletteScope.Project); break;
            case Key.E or Key.Enter when d is not null: DetailView.FocusEditor(); break;
            case Key.T when d is not null: DetailView.FocusTitle(); break;
            case Key.D1 or Key.NumPad1 when d is not null: _vm.SetPriority(d.Id, Priority.Urgent); break;
            case Key.D2 or Key.NumPad2 when d is not null: _vm.SetPriority(d.Id, Priority.High); break;
            case Key.D3 or Key.NumPad3 when d is not null: _vm.SetPriority(d.Id, Priority.Medium); break;
            case Key.D4 or Key.NumPad4 when d is not null: _vm.SetPriority(d.Id, Priority.Low); break;
            case Key.D0 or Key.NumPad0 when d is not null: _vm.SetPriority(d.Id, Priority.None); break;
            case Key.Delete when d is not null: DeleteCurrent(); break;
            default: e.Handled = false; break;
        }
    }

    void OpenPalette(PaletteScope scope)
    {
        if (_vm.Create.IsOpen) return;
        _vm.Palette.Open(scope);
        PaletteOverlay.FocusInput();
    }

    void RestoreFocus()
    {
        if (!IsTyping) IssueList.Focus();
    }

    void OnFocusRequested(string target)
    {
        switch (target)
        {
            case "create-title": CreateOverlay.FocusTitle(); break;
            case "subtask": DetailView.FocusSubtask(); break;
            case "filter": FocusFilter(); break;
            case "list": IssueList.Focus(); break;
            case "palette": PaletteOverlay.FocusInput(); break;
            case "kb-title": KbView.FocusTitle(); break;
        }
    }

    void DeleteCurrent()
    {
        if (_vm.Detail is not { } d) return;
        if (Dialogs.Confirm(this, "Удалить задачу", $"{d.Key} «{d.Title}» и её подзадачи будут удалены."))
            _vm.Archive(d.Id);
    }

    void Palette_Click(object sender, RoutedEventArgs e) => OpenPalette(PaletteScope.All);

    // ---------------- Sidebar: projects, labels, app menu ----------------

    void AppMenu_Click(object sender, RoutedEventArgs e) => MenuButton.Open((FrameworkElement)sender);

    void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var r = Dialogs.Prompt(this, "Новый проект",
            [new("Название", Hint: "Например, Backend"), new("Ключ (префикс ID задач)", Hint: "BE")], "Создать",
            onChanged: (i, boxes) =>
            {
                if (i == 0) boxes[1].Text = Data.ProjectRepository.SuggestKey(boxes[0].Text);
            });
        if (r is null || r[0].Length == 0) return;
        _vm.AddProject(r[0], r[1].Length > 0 ? r[1] : Data.ProjectRepository.SuggestKey(r[0]));
    }

    void NewLabel_Click(object sender, RoutedEventArgs e)
    {
        var r = Dialogs.Prompt(this, "Новая метка", [new("Название", Hint: "Например, UI")], "Создать");
        if (r is not null && r[0].Length > 0) _vm.AddLabel(r[0]);
    }

    static NavItem? NavOf(object sender) => (sender as FrameworkElement)?.DataContext as NavItem;

    void RenameNav_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        var r = Dialogs.Prompt(this, "Переименовать", [new("Название", nav.Title)]);
        if (r is null || r[0].Length == 0) return;
        if (nav.Kind == NavKind.Project) _vm.UpdateProject(nav.Id, r[0], nav.Color ?? "#5E6AD2");
        else _vm.UpdateLabel(nav.Id, r[0], nav.Color ?? "#9A9CA5");
    }

    void ColorMenu_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu || menu.Items.Count > 0) return;
        foreach (var hex in Palette.LabelColors)
        {
            var item = new MenuItem
            {
                Header = new Border { Width = 40, Height = 12, CornerRadius = new CornerRadius(3), Background = HexBrushConverter.Get(hex) },
            };
            item.Click += (_, _) =>
            {
                if (NavOf(menu) is not { } nav) return;
                if (nav.Kind == NavKind.Project) _vm.UpdateProject(nav.Id, nav.Title, hex);
                else _vm.UpdateLabel(nav.Id, nav.Title, hex);
            };
            menu.Items.Add(item);
        }
    }

    void ArchiveProject_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        if (Dialogs.Confirm(this, "Архивировать проект", $"Проект «{nav.Title}» и его задачи будут скрыты.", "Архивировать"))
            _vm.ArchiveProject(nav.Id);
    }

    void DeleteLabel_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        if (Dialogs.Confirm(this, "Удалить метку", $"Метка «{nav.Title}» будет снята со всех задач."))
            _vm.DeleteLabel(nav.Id);
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        _vm.Detail?.Flush();
        var dlg = new SaveFileDialog
        {
            Title = "Экспорт бэкапа",
            Filter = "Бэкап TaskManager (*.zip)|*.zip",
            FileName = $"taskmanager-{DateTime.Now:yyyy-MM-dd}.zip",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            BackupService.Export(_vm.S.Db, _vm.S.Paths, dlg.FileName);
            _vm.Status = $"Бэкап сохранён: {dlg.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Info(this, "Экспорт", ex.Message);
        }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Импорт бэкапа", Filter = "Бэкап TaskManager (*.zip)|*.zip" };
        if (dlg.ShowDialog(this) != true) return;
        if (!Dialogs.Confirm(this, "Импорт бэкапа",
                "Текущие данные будут заменены содержимым бэкапа. Копия текущей базы сохранится рядом с ней.", "Импортировать"))
            return;
        _vm.Detail?.Flush();
        try
        {
            _vm.S.ImportBackup(dlg.FileName);
            _vm.AfterImport();
            _vm.Status = "Бэкап импортирован";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Dialogs.Info(this, "Импорт", ex.Message);
            _vm.AfterImport();
        }
    }

    // ---------------- Knowledge base sources ----------------

    void AddKbSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Папка для базы данных (записи — .md файлы в ней)" };
        if (dlg.ShowDialog(this) == true) _vm.AddKbSource(dlg.FolderName);
    }

    void RenameKbSource_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        var r = Dialogs.Prompt(this, "Переименовать источник", [new("Название", nav.Title)]);
        if (r is not null && r[0].Length > 0) _vm.RenameKbSource(nav.Id, r[0]);
    }

    void RevealKbSource_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        var path = _vm.S.Kb.Source(nav.Id).Path;
        if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    // ---------------- Google Drive ----------------

    bool EnsureGoogleSignIn()
    {
        if (_vm.S.GDrive.Auth.IsSignedIn) return true;
        new GoogleDriveDialog(this, _vm.S.GDrive.Auth).ShowDialog();
        return _vm.S.GDrive.Auth.IsSignedIn;
    }

    void GoogleAccount_Click(object sender, RoutedEventArgs e)
    {
        new GoogleDriveDialog(this, _vm.S.GDrive.Auth).ShowDialog();
        _ = _vm.S.GDrive.SyncAll();
    }

    async void EnableGDrive_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav || !EnsureGoogleSignIn()) return;
        var r = Dialogs.Prompt(this, "Google Диск: подключить источник",
            [new("Папка на Google Диске", Hint: $"Пусто — создать новую папку «{nav.Title}». Или ссылка/ID папки, созданной TaskManager на другом компьютере")],
            "Подключить");
        if (r is null) return;
        if (await _vm.EnableGDrive(nav.Id, r[0]))
            Dialogs.Info(this, "Google Диск", "Готово. Изменения будут отправляться на Диск через несколько секунд после правок, " +
                "а изменения с Диска — подтягиваться раз в минуту.\n\nЧтобы подключить эту же базу на другом компьютере, " +
                "скопируйте ссылку: правый клик по источнику → «Открыть папку на Google Диске».");
    }

    void SyncGDrive_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is { } nav) _vm.SyncGDriveNow(nav.Id);
    }

    void OpenGDriveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is { } nav && TaskManager.GDrive.GDriveService.Info(_vm.S.Db, nav.Id) is { } info)
            Process.Start(new ProcessStartInfo(info.Link) { UseShellExecute = true });
    }

    void DisableGDrive_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        if (Dialogs.Confirm(this, "Отключить Google Диск", $"Синхронизация «{nav.Title}» остановится. Файлы останутся и на компьютере, и на Диске.", "Отключить"))
            _vm.DisableGDrive(nav.Id);
    }

    void RemoveKbSource_Click(object sender, RoutedEventArgs e)
    {
        if (NavOf(sender) is not { } nav) return;
        if (Dialogs.Confirm(this, "Убрать источник", $"«{nav.Title}» пропадёт из приложения, привязки задач к его записям удалятся. Файлы на диске останутся.", "Убрать"))
            _vm.RemoveKbSource(nav.Id);
    }

    /// <summary>Task titles in lists and on board cards follow the reading text size (13.5 at the default size).</summary>
    void ApplyTitleSize()
    {
        var k = ReadingStyle.Size / ReadingStyle.DefaultSize;
        var size = Math.Round(13.5 * k * 2) / 2;
        Resources["RowTitleSize"] = size;
        Resources["RowKeySize"] = Math.Round(12.5 * k * 2) / 2;
        Resources["RowHeight"] = Math.Max(36, Math.Round(size * 2.4));
    }

    void TextBigger_Click(object sender, RoutedEventArgs e) => ReadingStyle.Bigger();
    void TextSmaller_Click(object sender, RoutedEventArgs e) => ReadingStyle.Smaller();
    void TextReset_Click(object sender, RoutedEventArgs e) => ReadingStyle.Reset();
    void TextSerif_Click(object sender, RoutedEventArgs e) => ReadingStyle.ToggleSerif();

    void AppMenu_Opened(object sender, RoutedEventArgs e) => SerifMenuItem.IsChecked = ReadingStyle.Serif;

    void OpenData_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_vm.S.Paths.Root) { UseShellExecute = true });

    void Shortcuts_Click(object sender, RoutedEventArgs e) => Dialogs.Info(this, "Горячие клавиши", """
        C / Ctrl+N    новая задача
        Ctrl+K        поиск и команды
        /             фильтр списка
        J K ↑ ↓       навигация
        E / Enter     редактировать описание
        T             редактировать заголовок
        S P L M       статус, приоритет, метки, проект
        1-4, 0        приоритет / без приоритета
        Delete        удалить задачу
        Ctrl+B        список ↔ канбан
        Ctrl+= / −    размер текста заметок, Ctrl+0 — обычный
        Esc           закрыть / назад

        В редакторе описания:
        Ctrl+V        вставить картинку или ссылку
        Ctrl+B / I    жирный / курсив
        Ctrl+K        ссылка
        Tab           вложить пункт списка
        """, mono: true);

    // ---------------- Board drag & drop ----------------

    Point _dragStart;
    IssueRow? _dragRow;

    void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragRow = (sender as FrameworkElement)?.DataContext as IssueRow;
    }

    void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragRow = null;
        var card = (FrameworkElement)sender;
        card.Opacity = 0.5;
        try { DragDrop.DoDragDrop(card, new DataObject(typeof(IssueRow), row), DragDropEffects.Move); }
        finally { card.Opacity = 1; }
    }

    void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragRow is { } row) _vm.SelectedRow = row;
        _dragRow = null;
    }

    void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(IssueRow)) ? DragDropEffects.Move : DragDropEffects.None;
        if (FindChild<Border>((DependencyObject)sender, "DropZone") is { } zone) zone.Background = (Brush)FindResource("Hover");
        e.Handled = true;
    }

    void Column_DragLeave(object sender, DragEventArgs e)
    {
        if (FindChild<Border>((DependencyObject)sender, "DropZone") is { } zone) zone.Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF));
    }

    void Column_Drop(object sender, DragEventArgs e)
    {
        Column_DragLeave(sender, e);
        if (e.Data.GetData(typeof(IssueRow)) is not IssueRow row || sender is not FrameworkElement column) return;
        var status = (IssueStatus)column.Tag;
        IssueRow? before = null;
        if (FindChild<ItemsControl>(column, null, skipSelf: true) is { } items)
        {
            for (var i = 0; i < items.Items.Count; i++)
            {
                if (items.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) continue;
                var top = c.TranslatePoint(new Point(0, 0), column).Y;
                if (e.GetPosition(column).Y < top + c.ActualHeight / 2)
                {
                    before = (IssueRow)items.Items[i];
                    break;
                }
            }
        }
        if (before?.Id == row.Id) return;
        _vm.MoveOnBoard(row, status, before);
        _vm.SelectedRow = _vm.Issues.FirstOrDefault(r => r.Id == row.Id);
    }

    static T? FindChild<T>(DependencyObject parent, string? name, bool skipSelf = false) where T : FrameworkElement
    {
        if (!skipSelf && parent is T self && (name is null || self.Name == name)) return self;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i), name) is { } found) return found;
        return null;
    }
}
