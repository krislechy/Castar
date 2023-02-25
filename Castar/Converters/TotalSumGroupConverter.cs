using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace Castar.Converters
{
    public class TotalSumGroupConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value != null && value is CollectionViewGroup)
            {
                var collection = (CollectionViewGroup)value;
                var items=collection.Items.Cast<Income>();
                return items.Sum(x => x.Sum);
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
