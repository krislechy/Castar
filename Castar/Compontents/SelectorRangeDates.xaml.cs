using Castar.Contents;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Castar.Compontents
{
    /// <summary>
    /// Логика взаимодействия для SelectorRangeDates.xaml
    /// </summary>
    public class RangeDate:EventArgs
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        public RangeDate(DateTime startDate, DateTime endDate)
        {
            this.StartDate = startDate;
            this.EndDate = new DateTime(endDate.Year, endDate.Month, endDate.Day,23,59,59);
        }
    }
    public partial class SelectorRangeDates : UserControl
    {
        private enum TypeRange
        {
            month,
            year
        }
        private static DateTime DateTimeNow = DateTime.Now;
        public static DependencyProperty RangeDateProperty = DependencyProperty.Register(
    nameof(RangeDate),
    typeof(RangeDate),
    typeof(SelectorRangeDates),
    new PropertyMetadata(
        new RangeDate(
            new DateTime(DateTimeNow.Year, DateTimeNow.Month, 1), 
            new DateTime(DateTimeNow.Year, DateTimeNow.Month, DateTime.DaysInMonth(DateTimeNow.Year, DateTimeNow.Month))
            ), 
        OnChanged));
        public RangeDate RangeDate
        {
            get
            {
                return (RangeDate)GetValue(RangeDateProperty);
            }
            set
            {
                if (this.RangeDate.EndDate != value.EndDate || this.RangeDate.StartDate != value.StartDate)
                    SetValue(RangeDateProperty, value);
            }
        }

        public event EventHandler? DateRangeChanged;
        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            SelectorRangeDates SelectorRangeDates = (SelectorRangeDates)d;
            EventHandler handler = SelectorRangeDates.DateRangeChanged;
            if (handler != null)
            {
                handler(SelectorRangeDates, SelectorRangeDates.RangeDate);
            }
        }
        public SelectorRangeDates()
        {
            InitializeComponent();
        }
        private void ChangeRangeDate(TypeRange type, int offset)
        {
            var currentStartDate = RangeDate.StartDate;
            DateTime offsetDate = default!;
            switch (type)
            {
                case TypeRange.month:
                    {
                        offsetDate = currentStartDate.AddMonths(offset);
                        break;
                    }
                case TypeRange.year:
                    {
                        offsetDate = currentStartDate.AddYears(offset);
                        break;
                    }
            }
            var days = DateTime.DaysInMonth(offsetDate.Year, offsetDate.Month);
            RangeDate = new RangeDate(new DateTime(offsetDate.Year, offsetDate.Month, 1), new DateTime(offsetDate.Year, offsetDate.Month, days));
        }
        private void OffsetMonthButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var offset = int.Parse((string)button.CommandParameter);
            ChangeRangeDate(TypeRange.month, offset);
        }
        private void OffsetYearButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            var offset = int.Parse((string)button.CommandParameter);
            ChangeRangeDate(TypeRange.year, offset);
        }

        bool isSkipped = false;
        private void DatePicker_SelectedStartDateChanged(object sender, SelectionChangedEventArgs e)
        {
            var newStartDate = ((DatePicker)e.Source).SelectedDate;
            if (newStartDate != null)
                RangeDate = new RangeDate((DateTime)newStartDate, RangeDate.EndDate);
        }
        private void DatePicker_SelectedEndDateChanged(object sender, SelectionChangedEventArgs e)
        {
            var newEndDate = ((DatePicker)e.Source).SelectedDate;
            if (newEndDate != null)
                RangeDate = new RangeDate(RangeDate.StartDate, (DateTime)newEndDate);
        }
    }
}
