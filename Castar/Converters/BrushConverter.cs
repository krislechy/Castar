using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Castar.Converters
{
    public class DrawingColorToSolidColorBrushConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not null && value is System.Drawing.Color)
            {
                var color = (System.Drawing.Color)value;
                return new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    public class HEXConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value is not null && value is System.Drawing.Color)
                {
                    var color = (System.Drawing.Color)value;
                    return System.Drawing.ColorTranslator.ToHtml(color);
                }
            }
            catch
            {

            }
            return -1;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (value != null && value is string)
                {
                    return System.Drawing.ColorTranslator.FromHtml((string)value);
                }
            }
            catch { }
            return -1;
        }
    }
}
