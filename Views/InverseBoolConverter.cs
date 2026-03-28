using System.Globalization;
using System.Windows.Data;

namespace GsmAgent.Views;

/// <summary>Đảo ngược bool: true→false, false→true. Dùng cho IsEnabled khi IsSending.</summary>
public class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
