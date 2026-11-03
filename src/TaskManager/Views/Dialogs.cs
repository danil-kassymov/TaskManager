using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskManager.Views;

/// <summary>Small dark dialogs built in code: prompts, confirmations and info boxes.</summary>
public static class Dialogs
{
    public sealed record Field(string Label, string Value = "", string? Hint = null);

    /// <summary>Returns entered values, or null if cancelled. <paramref name="onChanged"/> can update other fields live.</summary>
    public static string[]? Prompt(Window owner, string title, Field[] fields, string ok = "Сохранить",
        Action<int, TextBox[]>? onChanged = null)
    {
        var boxes = new TextBox[fields.Length];
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 0) };
        for (var i = 0; i < fields.Length; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = fields[i].Label,
                Foreground = (Brush)Application.Current.Resources["Text2"],
                FontSize = 12.5,
                Margin = new Thickness(0, i == 0 ? 0 : 12, 0, 4),
            });
            var box = new TextBox
            {
                Text = fields[i].Value,
                Tag = fields[i].Hint ?? "",
                Style = (Style)Application.Current.Resources["InputTextBox"],
            };
            var index = i;
            box.TextChanged += (_, _) => onChanged?.Invoke(index, boxes);
            boxes[i] = box;
            panel.Children.Add(box);
        }

        var window = Create(owner, title, panel, ok, out var okButton);
        window.Loaded += (_, _) =>
        {
            boxes[0].Focus();
            boxes[0].SelectAll();
        };
        okButton.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
    }

    public static bool Confirm(Window owner, string title, string message, string ok = "Удалить")
    {
        var window = Create(owner, title, Message(message), ok, out var okButton);
        okButton.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => okButton.Focus();
        return window.ShowDialog() == true;
    }

    public static void Info(Window owner, string title, string message, bool mono = false)
    {
        var text = Message(message);
        if (mono) text.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
        var window = Create(owner, title, text, "OK", out var okButton, cancel: false);
        okButton.Click += (_, _) => window.DialogResult = true;
        window.ShowDialog();
    }

    static TextBlock Message(string message) => new()
    {
        Text = message,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20, 16, 20, 0),
        Foreground = (Brush)Application.Current.Resources["Text"],
        LineHeight = 20,
    };

    static Window Create(Window owner, string title, UIElement content, string ok, out Button okButton, bool cancel = true)
    {
        var res = Application.Current.Resources;
        okButton = new Button { Content = ok, IsDefault = true, Style = (Style)res["PrimaryButton"], MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 18, 20, 18) };
        if (cancel)
            buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true, Style = (Style)res["FlatButton"], Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) });
        buttons.Children.Add(okButton);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(content);

        var window = new Window
        {
            Title = title,
            Owner = owner,
            Content = root,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)res["Panel"],
            Foreground = (Brush)res["Text"],
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 13.5,
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.DialogResult = false;
        };
        return window;
    }
}
