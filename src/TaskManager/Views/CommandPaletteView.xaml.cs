using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        Input.PreviewKeyDown += Input_PreviewKeyDown;
    }

    CommandPaletteViewModel? Vm => DataContext as CommandPaletteViewModel;

    public void FocusInput() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Input.Focus());

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.Move(1);
                break;
            case Key.Up:
                vm.Move(-1);
                break;
            case Key.Tab:
                vm.Move(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
                break;
            case Key.Enter:
                vm.Execute(null);
                break;
            case Key.Escape:
                vm.IsOpen = false;
                break;
            default:
                return;
        }
        if (vm.Selected is { } s) Results.ScrollIntoView(s);
        e.Handled = true;
    }

    void Results_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteItem item) Vm?.Execute(item);
    }

    void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.IsOpen = false;
    }

    void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
