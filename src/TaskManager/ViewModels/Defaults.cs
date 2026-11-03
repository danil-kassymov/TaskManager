using System.Globalization;
using System.Windows.Data;
using TaskManager.Models;

namespace TaskManager.ViewModels;

/// <summary>Static values referenced from XAML via x:Static.</summary>
public static class Defaults
{
    public static Priority HighPriority => Priority.High;
    public static IReadOnlyList<Priority> PriorityFilters => Names.Priorities;
    public static IValueConverter Not { get; } = new NotConverter();
    public static IValueConverter EqualsParam { get; } = new EqualsParamConverter();

    sealed class EqualsParamConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }

    sealed class NotConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    }
}
