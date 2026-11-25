using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        private static readonly DependencyPropertyKey HasCompleteRangePropertyKey = DependencyProperty.RegisterReadOnly(
            nameof(HasCompleteRange), typeof(bool), typeof(SelectorRangeDates), new PropertyMetadata(true));

        public static readonly DependencyProperty HasCompleteRangeProperty = HasCompleteRangePropertyKey.DependencyProperty;

        public bool HasCompleteRange => (bool)GetValue(HasCompleteRangeProperty);

        private bool updatingDates;

        public static readonly DependencyProperty RangeDateProperty = DependencyProperty.Register(
    nameof(RangeDate),
    typeof(RangeDate),
    typeof(SelectorRangeDates),
    new PropertyMetadata(
        new RangeDate(
            new DateTime(DateTimeNow.Year, DateTimeNow.Month, 1), 
            new DateTime(DateTimeNow.Year, DateTimeNow.Month, DateTime.DaysInMonth(DateTimeNow.Year, DateTimeNow.Month))
            ), 
        OnChanged), value => value is RangeDate range && range.StartDate.Date <= range.EndDate.Date);
        public RangeDate RangeDate
        {
            get
            {
                return (RangeDate)GetValue(RangeDateProperty);
            }
            set
            {
                if (HasCompleteRange && RangeDate.EndDate == value.EndDate && RangeDate.StartDate == value.StartDate)
                    return;

                if (ReferenceEquals(RangeDate, value))
                    ApplyRange(value);
                else
                    SetCurrentValue(RangeDateProperty, value);
            }
        }

        public event EventHandler? DateRangeChanged;
        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var selector = (SelectorRangeDates)d;
            if (selector.StartDatePicker is not null)
                selector.ApplyRange((RangeDate)e.NewValue);
        }
        public SelectorRangeDates()
        {
            InitializeComponent();
            StartDatePicker.CommitManualDate = date => ChangeBoundary(date, isStart: true);
            EndDatePicker.CommitManualDate = date => ChangeBoundary(date, isStart: false);
            UpdateDatePickers(RangeDate.StartDate.Date, RangeDate.EndDate.Date);
        }

        private void ApplyRange(RangeDate range)
        {
            UpdateDatePickers(range.StartDate.Date, range.EndDate.Date);
            SetValue(HasCompleteRangePropertyKey, true);
            DateRangeChanged?.Invoke(this, range);
        }

        private void UpdateDatePickers(DateTime? start, DateTime? end)
        {
            updatingDates = true;
            try
            {
                // Release the old bounds before assigning a different range.
                StartDatePicker.BlackoutDates.Clear();
                EndDatePicker.BlackoutDates.Clear();
                StartDatePicker.SetCurrentValue(DatePicker.SelectedDateProperty, start);
                EndDatePicker.SetCurrentValue(DatePicker.SelectedDateProperty, end);
                if (end is { } lastDay && lastDay < DateTime.MaxValue.Date)
                    StartDatePicker.BlackoutDates.Add(new CalendarDateRange(lastDay.AddDays(1), DateTime.MaxValue.Date));
                if (start is { } firstDay && firstDay > DateTime.MinValue)
                    EndDatePicker.BlackoutDates.Add(new CalendarDateRange(DateTime.MinValue, firstDay.AddDays(-1)));
            }
            finally
            {
                updatingDates = false;
            }
        }

        private DateTime VisibleRangeAnchor => StartDatePicker.SelectedDate ?? EndDatePicker.SelectedDate ?? RangeDate.StartDate.Date;

        internal bool IncludeOperationDate(DateTime? operationDate)
        {
            if (operationDate is not { } date || HasCompleteRange && date >= RangeDate.StartDate && date <= RangeDate.EndDate)
                return false;

            SetMonth(date);
            return true;
        }

        internal Func<TimeZoneInfo> DisplayTimeZone { get; set; } = () => TimeZoneInfo.Local;

        internal void SetCurrentMonth() =>
            SetMonth(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, DisplayTimeZone()).DateTime);

        private void CurrentMonth_Click(object sender, RoutedEventArgs e) => SetCurrentMonth();

        private void MonthPickerButton_Click(object sender, RoutedEventArgs e) =>
            OpenPeriodPicker(MonthPickerButton, CalendarMode.Year);

        private void YearPickerButton_Click(object sender, RoutedEventArgs e) =>
            OpenPeriodPicker(YearPickerButton, CalendarMode.Decade);

        private void OpenPeriodPicker(Button button, CalendarMode mode)
        {
            if (MonthPopup.IsOpen)
            {
                var sameButton = ReferenceEquals(MonthPopup.PlacementTarget, button);
                ClosePeriodPicker();
                if (sameButton) return;
            }

            var date = VisibleRangeAnchor;
            MonthPopup.PlacementTarget = button;
            MonthCalendar.DisplayMode = mode;
            MonthCalendar.SelectedDate = date;
            MonthCalendar.DisplayDate = date;
            System.Windows.Automation.AutomationProperties.SetName(MonthCalendar,
                mode == CalendarMode.Decade ? "Год периода" : "Месяц периода");
            MonthPopup.IsOpen = true;
        }

        private void MonthPopup_Opened(object sender, EventArgs e) => MonthCalendar.Focus();

        private void MonthCalendar_DisplayModeChanged(object? sender, CalendarModeChangedEventArgs e)
        {
            if (!MonthPopup.IsOpen)
                return;

            if (ReferenceEquals(MonthPopup.PlacementTarget, YearPickerButton))
            {
                if (e.OldMode != CalendarMode.Decade || e.NewMode != CalendarMode.Year)
                    return;
                SetYear(MonthCalendar.DisplayDate.Year);
            }
            else
            {
                if (e.OldMode != CalendarMode.Year || e.NewMode != CalendarMode.Month)
                    return;
                SetMonth(MonthCalendar.DisplayDate);
            }

            ClosePeriodPicker();
        }

        private void MonthCalendar_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            e.Handled = true;
            ClosePeriodPicker();
        }

        private void ClosePeriodPicker()
        {
            MonthPopup.IsOpen = false;
            (MonthPopup.PlacementTarget as Button)?.Focus();
        }

        internal void SetYear(int year) => RangeDate = new RangeDate(new DateTime(year, 1, 1), new DateTime(year, 12, 31));

        internal void SetMonth(DateTime date)
        {
            var start = new DateTime(date.Year, date.Month, 1);
            var end = new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
            RangeDate = new RangeDate(start, end);
        }

        private void ChangeRangeDate(TypeRange type, int offset)
        {
            var currentStartDate = VisibleRangeAnchor;
            if (type == TypeRange.year && HasCompleteRange && currentStartDate.Month == 1 && currentStartDate.Day == 1
                && RangeDate.EndDate.Date == new DateTime(currentStartDate.Year, 12, 31))
            {
                var year = currentStartDate.Year + offset;
                if (year is >= 1 and <= 9999) SetYear(year);
                return;
            }
            var monthIndex = (currentStartDate.Year - 1) * 12 + currentStartDate.Month - 1;
            var targetMonth = monthIndex + (type == TypeRange.year ? offset * 12 : offset);
            if (targetMonth is >= 0 and < 9999 * 12)
                SetMonth(new DateTime(targetMonth / 12 + 1, targetMonth % 12 + 1, 1));
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

        private void DatePicker_SelectedStartDateChanged(object sender, SelectionChangedEventArgs e) =>
            ChangeBoundary(((CastarDatePicker)sender).SelectedDate, isStart: true);

        private void DatePicker_SelectedEndDateChanged(object sender, SelectionChangedEventArgs e) =>
            ChangeBoundary(((CastarDatePicker)sender).SelectedDate, isStart: false);

        private void ChangeBoundary(DateTime? date, bool isStart)
        {
            if (updatingDates)
                return;

            var start = isStart ? date : StartDatePicker.SelectedDate;
            var end = isStart ? EndDatePicker.SelectedDate : date;
            if (start > end)
            {
                if (isStart)
                    end = null;
                else
                    start = null;
            }
            if (start is { } first && end is { } last)
            {
                RangeDate = new RangeDate(first.Date, last.Date);
                return;
            }

            UpdateDatePickers(start, end);
            if (!HasCompleteRange)
                return;

            SetValue(HasCompleteRangePropertyKey, false);
            DateRangeChanged?.Invoke(this, RangeDate);
        }
    }
}
