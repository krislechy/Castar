using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;

namespace Castar.Converters
{
    public class BrushOpacityConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not null)
            {
                var brush = value as SolidColorBrush;
                var cloned = brush.Clone();
                cloned.Opacity = double.Parse((string)parameter, CultureInfo.InvariantCulture);
                return cloned;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BrushToColorConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not null)
            {
                var brush = value as SolidColorBrush;
                return brush.Color;
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class DrawingColorToSolidColorBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
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
