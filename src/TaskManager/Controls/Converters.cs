using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using TaskManager.Data;

namespace TaskManager.Controls;

/// <summary>true/non-null/non-empty → Visible. ConverterParameter="!" inverts.</summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            int i => i > 0,
            _ => true,
        };
        if (parameter as string == "!") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when all bound values are equal (used to highlight the selected board card).</summary>
public sealed class EqualConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] is not null && Equals(values[0], values[1]);

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SortNameConverter : IValueConverter
{
    public static string Of(SortMode m) => m switch
    {
        SortMode.Priority => "По приоритету",
        SortMode.Manual => "Вручную",
        SortMode.Updated => "По обновлению",
        SortMode.Created => "По созданию",
        _ => m.ToString(),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SortMode m ? Of(m) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Makes a button open its ContextMenu on left click, below the button.</summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpenOnClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnClick", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnChanged));

    public static bool GetOpenOnClick(DependencyObject d) => (bool)d.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject d, bool v) => d.SetValue(OpenOnClickProperty, v);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ButtonBase b && e.NewValue is true) b.Click += (_, _) => Open(b);
    }

    public static void Open(FrameworkElement b)
    {
        if (b.ContextMenu is not { } menu) return;
        menu.PlacementTarget = b;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = b.DataContext;
        menu.IsOpen = true;
    }
}
