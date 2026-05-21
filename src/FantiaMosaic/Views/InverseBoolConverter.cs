using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FantiaMosaic.Views;

public sealed class ComplianceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? Brushes.SeaGreen : Brushes.Crimson;
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
