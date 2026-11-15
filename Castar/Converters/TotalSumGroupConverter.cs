using Castar.Domain.Models.DataBase;
using Castar.Contents;
using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Castar.Converters
{
    public class TotalSumGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is CollectionViewGroup group
                ? IncomeDisplayRow.FormatTotals(group.Items.Cast<IncomeDisplayRow>()) : "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
