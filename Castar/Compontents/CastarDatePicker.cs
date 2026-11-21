using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Castar.Compontents;

public sealed class CastarDatePicker : DatePicker
{
    private const string DateFormat = "dd.MM.yyyy";
    private const string TimeFormat = "HH:mm:ss";
    private static readonly string[] EditableTimeFormats = [TimeFormat, "HH:mm"];
    private TextBox? input;

    private static readonly DependencyPropertyKey HasInvalidInputPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasInvalidInput), typeof(bool), typeof(CastarDatePicker), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HasInvalidInputProperty = HasInvalidInputPropertyKey.DependencyProperty;

    public bool HasInvalidInput => (bool)GetValue(HasInvalidInputProperty);

    internal Action<DateTime?>? CommitManualDate { get; set; }

    public override void OnApplyTemplate()
    {
        if (input is not null)
        {
            input.PreviewTextInput -= Input_PreviewTextInput;
            input.PreviewKeyDown -= Input_PreviewKeyDown;
            input.LostKeyboardFocus -= Input_LostKeyboardFocus;
            input.TextChanged -= Input_TextChanged;
            DataObject.RemovePastingHandler(input, Input_Pasting);
        }

        base.OnApplyTemplate();
        input = GetTemplateChild("DateInput") as TextBox;
        if (input is null)
            return;

        RestoreSelectedDate();
        input.PreviewTextInput += Input_PreviewTextInput;
        input.PreviewKeyDown += Input_PreviewKeyDown;
        input.LostKeyboardFocus += Input_LostKeyboardFocus;
        input.TextChanged += Input_TextChanged;
        DataObject.AddPastingHandler(input, Input_Pasting);
    }

    public bool TryGetDate(out DateTime date)
    {
        if (input is null && SelectedDate is { } selected)
        {
            date = selected.Date;
            return true;
        }

        var valid = TryReadInput(out date)
            && (DisplayDateStart is null || date >= DisplayDateStart.Value.Date)
            && (DisplayDateEnd is null || date <= DisplayDateEnd.Value.Date)
            && !BlackoutDates.Contains(date);
        SetValue(HasInvalidInputPropertyKey, !valid);
        return valid;
    }

    internal static string FormatTime(DateTime? value) => value?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "";

    internal bool TryGetDateTime(string timeText, DateTime? original, out DateTime result)
    {
        timeText = timeText.Trim();
        result = default;
        if (!TryGetDate(out var date)
            || !TimeOnly.TryParseExact(timeText, EditableTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;

        var timeOfDay = original is { } old && timeText == FormatTime(old) ? old.TimeOfDay : time.ToTimeSpan();
        result = DateTime.SpecifyKind(date.Add(timeOfDay), DateTimeKind.Unspecified);
        return true;
    }

    protected override void OnSelectedDateChanged(SelectionChangedEventArgs e)
    {
        RestoreSelectedDate();
        base.OnSelectedDateChanged(e);
    }

    private void RestoreSelectedDate()
    {
        if (input is not null)
            input.Text = SelectedDate?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? "";
        SetValue(HasInvalidInputPropertyKey, false);
    }

    private bool Commit()
    {
        if (CommitManualDate is { } commit)
        {
            if (string.IsNullOrWhiteSpace(input?.Text))
            {
                commit(null);
                return true;
            }
            var valid = TryReadInput(out var manualDate);
            SetValue(HasInvalidInputPropertyKey, !valid);
            if (valid)
                commit(manualDate);
            return valid;
        }
        if (!TryGetDate(out var date))
            return false;
        SetCurrentValue(SelectedDateProperty, date);
        return true;
    }

    private bool TryReadInput(out DateTime date) =>
        DateTime.TryParseExact(input?.Text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private void Input_TextChanged(object sender, TextChangedEventArgs e) => SetValue(HasInvalidInputPropertyKey, false);

    private void Input_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            e.Handled = !Commit();
        else if (e.Key == Key.F4 || e is { Key: Key.System, SystemKey: Key.Down } && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            SetCurrentValue(IsDropDownOpenProperty, !IsDropDownOpen);
            e.Handled = true;
        }
    }

    private void Input_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length == 0)
            return;
        ReplaceSelection(e.Text);
        e.Handled = true;
    }

    private void Input_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string text)
            ReplaceSelection(text.Trim());
        e.CancelCommand();
    }

    private void ReplaceSelection(string text)
    {
        if (input is null)
            return;

        var start = input.SelectionStart;
        var value = input.Text.Remove(start, input.SelectionLength).Insert(start, text);
        if (!TryMask(value, out var masked))
            return;
        if (string.Equals(value, masked, StringComparison.Ordinal))
        {
            input.SelectedText = text;
            input.CaretIndex = start + text.Length;
        }
        else
        {
            TryMask(value[..(start + text.Length)], out var prefix);
            input.Text = masked;
            input.CaretIndex = prefix.Length;
        }
    }

    private static bool TryMask(string text, out string masked)
    {
        var result = new StringBuilder(DateFormat.Length);
        var segment = 0;
        var digits = 0;
        foreach (var character in text)
        {
            if (character == '.' && segment < 2)
            {
                result.Append('.');
                segment++;
                digits = 0;
                continue;
            }
            if (character is < '0' or > '9' || segment == 2 && digits == 4)
            {
                masked = "";
                return false;
            }

            if (segment < 2 && digits == 2)
            {
                result.Append('.');
                segment++;
                digits = 0;
            }
            result.Append(character);
            digits++;
        }

        masked = result.ToString();
        return true;
    }
}
