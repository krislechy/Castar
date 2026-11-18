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
            this.EndDate = endDate.Date.AddTicks(TimeSpan.TicksPerDay - 1);
        }
    }
    public partial class SelectorRangeDates : UserControl
    {
        public static readonly DependencyProperty UseLedgerStyleProperty = DependencyProperty.Register(
            nameof(UseLedgerStyle), typeof(bool), typeof(SelectorRangeDates), new PropertyMetadata(false));

        public bool UseLedgerStyle
        {
            get => (bool)GetValue(UseLedgerStyleProperty);
            set => SetValue(UseLedgerStyleProperty, value);
        }

        private enum TypeRange
        {
            month,
            year
        }
        private static readonly DateTime DateTimeNow = DateTime.Now;
        public static readonly DependencyProperty RangeDateProperty = DependencyProperty.Register(
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
            var selector = (SelectorRangeDates)d;
            selector.DateRangeChanged?.Invoke(selector, selector.RangeDate);
        }
        public SelectorRangeDates()
        {
            InitializeComponent();
        }

        internal bool IncludeOperationDate(DateTime? operationDate)
        {
            if (operationDate is not { } date || date >= RangeDate.StartDate && date <= RangeDate.EndDate)
                return false;

            SetMonth(date);
            return true;
        }

        internal Func<TimeZoneInfo> DisplayTimeZone { get; set; } = () => TimeZoneInfo.Local;

        internal void SetCurrentMonth() =>
            SetMonth(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, DisplayTimeZone()).DateTime);

        private void CurrentMonth_Click(object sender, RoutedEventArgs e) => SetCurrentMonth();

        private void SetMonth(DateTime date)
        {
            var start = new DateTime(date.Year, date.Month, 1);
            var end = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
            RangeDate = new RangeDate(start, end);
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

        private void DatePicker_SelectedStartDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (((CastarDatePicker)sender).SelectedDate is { } start)
                RangeDate = new RangeDate(start.Date, RangeDate.EndDate);
        }
        private void DatePicker_SelectedEndDateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (((CastarDatePicker)sender).SelectedDate is { } end)
                RangeDate = new RangeDate(RangeDate.StartDate, end.Date);
        }
    }
}
