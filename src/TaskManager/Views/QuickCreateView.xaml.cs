using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TaskManager.Markdown;
using TaskManager.ViewModels;

namespace TaskManager.Views;

public partial class QuickCreateView : UserControl
{
    public QuickCreateView()
    {
        InitializeComponent();
        MarkdownEditing.Attach(DescriptionBox, App.Services.Attachments);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) FocusTitle();
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    QuickCreateViewModel? Vm => DataContext as QuickCreateViewModel;

    public void FocusTitle() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TitleBox.Focus());

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Vm?.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.FocusedElement == TitleBox))
        {
            Vm?.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }

    void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Vm?.CancelCommand.Execute(null);

    void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
