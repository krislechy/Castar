using Castar.Api;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Castar.Compontents;

public partial class ManualPurchaseEditor : UserControl
{
    private static readonly (RecurringPurchaseZoneRule Rule, TimeZoneInfo Zone)[] ZoneRules = RecurringPurchaseTime.ZoneRules
        .Select(rule => (Rule: rule, Zone: rule.TryResolve(out var zone) ? zone : null))
        .Where(rule => rule.Zone is not null).Select(rule => (rule.Rule, rule.Zone!)).ToArray();

    private readonly PurchaseEditor header;
    private readonly string defaultCurrency;
    private readonly IReadOnlyList<string>? allowedCurrencies;
    private string lastItemCurrency;
    private readonly IReadOnlyList<BudgetCategoryDto>? categories;
    private readonly Guid purchaseId;
    private readonly PurchaseWriteDto? originalHeader;
    private readonly RecurringPurchaseFrequency? originalFrequency;
    private readonly TimeZoneInfo initialScheduleZone;
    private readonly string? legacyZoneId;
    private readonly int? defaultRuleId;
    private readonly bool hasDefaultFixedOffset;
    private readonly int? savedFixedOffset;
    private bool updatingOffset;
    private DateTime? offsetLabelsTime;

    internal ManualPurchaseEditor(DateTime localDate, ManualCurrencySettingsDto currencies, IReadOnlyList<User> owners,
        IReadOnlyList<BudgetCategoryDto>? categories = null, RecurringPurchaseDto? recurring = null,
        TimeZoneInfo? scheduleZone = null, bool startRecurring = false, bool supportsItemCurrencies = true)
    {
        defaultCurrency = currencies.DefaultCurrency;
        allowedCurrencies = supportsItemCurrencies ? currencies.Currencies : null;
        lastItemCurrency = defaultCurrency;
        this.categories = categories;
        var template = recurring?.Schedule.Template;
        purchaseId = recurring?.Id ?? Guid.NewGuid();
        originalHeader = template?.Purchase;
        originalFrequency = recurring?.Schedule.Frequency;
        initialScheduleZone = scheduleZone ?? (recurring is not null &&
            RecurringPurchaseTime.TryResolve(recurring.Schedule, out var resolved)
                ? resolved : TimeZoneInfo.Local);
        legacyZoneId = recurring?.Schedule is { UtcOffsetMinutes: null, ZoneRuleId: null } legacy ? legacy.TimeZoneId : null;
        defaultRuleId = recurring is null ? MatchZoneRule(initialScheduleZone.Id) : null;
        var ruleId = recurring is null ? defaultRuleId : recurring.Schedule.ZoneRuleId;
        hasDefaultFixedOffset = recurring is null && ruleId is null;
        savedFixedOffset = recurring?.Schedule is { ZoneRuleId: null } fixedSchedule ? fixedSchedule.UtcOffsetMinutes : null;
        InitializeComponent();
        DisplayZoneHint.ToolTip = initialScheduleZone.DisplayName;
        FrequencyPicker.ItemsSource = new FrequencyOption[]
        {
            new(RecurringPurchaseFrequency.Weekly, "Каждую неделю"),
            new(RecurringPurchaseFrequency.Monthly, "Каждый месяц"),
            new(RecurringPurchaseFrequency.Yearly, "Каждый год")
        };
        FrequencyPicker.SelectedValue = recurring?.Schedule.Frequency ?? RecurringPurchaseFrequency.Monthly;
        RepeatPurchase.IsChecked = recurring is not null || startRecurring;
        RepeatPurchase.IsEnabled = recurring is null && !startRecurring;
        var anchor = originalHeader?.AddedOn ?? localDate;
        var selection = legacyZoneId is not null ? new ZoneKey(null, null) : ruleId.HasValue
            ? new ZoneKey(null, ruleId) : new ZoneKey(savedFixedOffset ?? DefaultOffset(anchor), null);
        UseSeasonalTime.IsChecked = legacyZoneId is not null || ruleId.HasValue;
        UpdateOffsetOptions(anchor, selection);
        header = new PurchaseEditor(
            new(purchaseId, originalHeader?.UserId ?? Guid.Empty, originalHeader?.Name ?? "", 0,
                originalHeader?.Currency ?? defaultCurrency, 0, originalHeader?.AddedOn ?? localDate,
                originalHeader?.RetailPlace, originalHeader?.RetailPlaceAddress, []),
            owners, manualEntry: true);
        HeaderFields.Content = header;
        ScheduleZonePicker.SelectionChanged += (_, _) =>
        {
            if (!updatingOffset)
                UpdateZonePresentation();
        };
        header.OperationDate.SelectedDateChanged += (_, _) => RefreshOffsetOptions();
        header.OperationTime.TextChanged += (_, _) => RefreshOffsetOptions();
        if (template is { Items.Length: > 0 })
        {
            foreach (var item in template.Items)
                AddItem(item);
        }
        else
            AddItem();
    }

    private OffsetOption? SelectedOffset => ScheduleZonePicker.SelectedItem as OffsetOption;
    private bool FollowsSeasonalTime => UseSeasonalTime.IsChecked == true;
    internal int? SelectedUtcOffsetMinutes => SelectedLegacyTimeZoneId is null ? SelectedOffset?.Minutes : null;
    internal int? SelectedZoneRuleId => FollowsSeasonalTime ? SelectedOffset?.Key.RuleId : null;
    internal string? SelectedLegacyTimeZoneId => FollowsSeasonalTime &&
        SelectedOffset is { Key: { FixedMinutes: null, RuleId: null } } ? legacyZoneId : null;
    internal RecurringPurchaseFrequency? SelectedFrequency => RepeatPurchase.IsChecked == true &&
        FrequencyPicker.SelectedValue is RecurringPurchaseFrequency frequency ? frequency : null;
    internal PurchaseCreateDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        var headerCheck = header.Validate();
        if (!headerCheck.Result)
            return headerCheck;
        if (ItemFields.Children.Count == 0)
            return new(false, "Добавьте хотя бы один товар.");
        var draft = header.Result!;
        if (RepeatPurchase.IsChecked == true)
        {
            if (SelectedFrequency is not { } frequency)
                return new(false, "Выберите частоту повторения.");
            UpdateOffsetOptions(draft.AddedOn, GetZoneSelection(draft.AddedOn));
            if (SelectedOffset is not { } selected)
                return new(false, "Выберите часовой пояс.");
            var calendarChanged = draft.AddedOn != originalHeader?.AddedOn || frequency != originalFrequency;
            var now = DateTimeOffset.UtcNow;
            var today = FollowsSeasonalTime && selected.Zone is { } zone ? TimeZoneInfo.ConvertTime(now, zone).Date
                : now.ToOffset(TimeSpan.FromMinutes(selected.Minutes)).Date;
            if (calendarChanged && draft.AddedOn.Date < today)
                return new(false, "Укажите сегодняшнюю или будущую дату для повторения.");
        }

        var items = new List<PurchaseItemWriteDto>(ItemFields.Children.Count);
        foreach (DockPanel row in ItemFields.Children)
        {
            var editor = (PurchaseItemEditor)row.Children[1];
            var check = editor.Validate();
            if (!check.Result)
                return check;
            var item = editor.Result!;
            items.Add(item with { Category = item.Category ?? row.Tag as CategorySelectionDto });
        }
        Result = new(purchaseId, draft with
        {
            Currency = originalHeader is null && allowedCurrencies is null ? defaultCurrency : originalHeader?.Currency,
            UserId = draft.UserId ?? originalHeader?.UserId
        }, items.ToArray());
        return new(true, null);
    }

    private void RefreshOffsetOptions()
    {
        if (header.OperationDate.TryGetDateTime(header.OperationTime.Text, null, out var time))
            UpdateOffsetOptions(time, GetZoneSelection(time));
    }

    private ZoneKey? GetZoneSelection(DateTime localDateTime) => hasDefaultFixedOffset && SelectedOffset?.Key.FixedMinutes is not null
        ? new ZoneKey(DefaultOffset(localDateTime), null) : SelectedOffset?.Key;

    private void UpdateOffsetOptions(DateTime localDateTime, ZoneKey? selection)
    {
        if (offsetLabelsTime == localDateTime)
            return;

        var wallTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        var zoneOptions = ZoneRules.Select(option =>
        {
            var minutes = (int)option.Zone.GetUtcOffset(wallTime).TotalMinutes;
            var name = option.Rule.Id == defaultRuleId ? initialScheduleZone.DisplayName : option.Zone.DisplayName;
            var cities = ZoneCities(name, option.Rule.IanaId);
            return new OffsetOption(new(null, option.Rule.Id), minutes,
                $"{RecurringPurchaseTime.FormatOffset(minutes)} · {cities}", option.Zone);
        });
        IEnumerable<OffsetOption> options = zoneOptions.OrderBy(option => option.Minutes)
            .ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase);
        var fallback = hasDefaultFixedOffset ? DefaultOffset(localDateTime) : savedFixedOffset;
        if (fallback is { } minutes)
            options = options.Prepend(new OffsetOption(new(minutes, null), minutes,
                $"{RecurringPurchaseTime.FormatOffset(minutes)} · фиксированный", null));
        if (legacyZoneId is not null)
        {
            var legacyMinutes = (int)initialScheduleZone.GetUtcOffset(wallTime).TotalMinutes;
            var cities = ZoneCities(initialScheduleZone.DisplayName, legacyZoneId);
            options = options.Prepend(new OffsetOption(new(null, null), legacyMinutes,
                $"{RecurringPurchaseTime.FormatOffset(legacyMinutes)} · {cities} (сохранённый)", initialScheduleZone));
        }

        offsetLabelsTime = localDateTime;
        updatingOffset = true;
        var offsetOptions = options.ToArray();
        ScheduleZonePicker.ItemsSource = offsetOptions;
        ScheduleZonePicker.SelectedItem = offsetOptions.FirstOrDefault(option => option.Key == selection);
        updatingOffset = false;
        UpdateZonePresentation();
    }

    private void UpdateZonePresentation()
    {
        var selected = SelectedOffset;
        UseSeasonalTime.IsEnabled = selected?.Zone is not null;
        if (!UseSeasonalTime.IsEnabled)
            UseSeasonalTime.IsChecked = false;

        var usesConfiguredZone = selected is not null && (defaultRuleId.HasValue
            ? selected.Key.RuleId == defaultRuleId : hasDefaultFixedOffset && selected.Key.FixedMinutes.HasValue);
        DisplayZoneHint.Visibility = usesConfiguredZone ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ZoneCities(string displayName, string ianaId)
    {
        var suffix = displayName.IndexOf(") ", StringComparison.Ordinal);
        if (suffix < 0)
            return ianaId[(ianaId.LastIndexOf('/') + 1)..].Replace('_', ' ');
        return displayName[(suffix + 2)..];
    }

    private static int? MatchZoneRule(string zoneId)
    {
        var windowsId = TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneId, out var mapped) ? mapped : zoneId;
        return ZoneRules.Select(pair => pair.Rule).FirstOrDefault(rule => rule.IanaId.Equals(zoneId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? ZoneRules.Select(pair => pair.Rule).FirstOrDefault(rule => rule.WindowsId.Equals(windowsId, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private int DefaultOffset(DateTime localDateTime) => (int)initialScheduleZone
        .GetUtcOffset(DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified)).TotalMinutes;

    private readonly record struct ZoneKey(int? FixedMinutes, int? RuleId);
    private sealed record OffsetOption(ZoneKey Key, int Minutes, string Label, TimeZoneInfo? Zone);

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem()?.FocusItemName();

    private PurchaseItemEditor? AddItem(PurchaseItemWriteDto? originalItem = null)
    {
        if (ItemFields.Children.Count >= 250)
            return null;

        var row = new DockPanel { Tag = originalItem?.Category };
        var item = new PurchaseItemDto(Guid.Empty, originalItem?.Name ?? "", null, originalItem?.Quantity ?? 1,
            originalItem?.Price ?? 0, 0)
        {
            BudgetCategoryId = originalItem?.Category?.Id,
            Currency = originalItem?.Currency
        };
        var inheritsFrom = originalHeader is null ? defaultCurrency : originalHeader.Currency;
        var editor = new PurchaseItemEditor(item, inheritsFrom, manualEntry: true, categories: categories,
            allowedCurrencies: allowedCurrencies, defaultCurrency: lastItemCurrency,
            preserveInheritedCurrency: originalItem is not null);
        lastItemCurrency = editor.SelectedCurrency ?? lastItemCurrency;
        editor.CurrencyChanged += currency => lastItemCurrency = currency ?? lastItemCurrency;
        editor.ShowHeadings(ItemFields.Children.Count == 0);
        var icon = new Path
        {
            Data = (Geometry)FindResource("Lucide.x"),
            Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        icon.SetResourceReference(Shape.StrokeProperty, "PurchaseForm.Muted");
        var remove = new Button
        {
            Content = icon, Width = 26, Height = 26, Padding = new Thickness(4),
            Margin = new Thickness(8, 0, 0, 8), IsTabStop = false, VerticalAlignment = VerticalAlignment.Bottom
        };
        remove.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        remove.SetResourceReference(BackgroundProperty, "PurchaseForm.RemoveSurface");
        AutomationProperties.SetName(remove, "Удалить товар");
        remove.Click += (_, _) =>
        {
            ItemFields.Children.Remove(row);
            if (ItemFields.Children.Count > 0 && ItemFields.Children[0] is DockPanel first)
                ((PurchaseItemEditor)first.Children[1]).ShowHeadings(true);
        };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(editor);
        ItemFields.Children.Add(row);
        return editor;
    }

    private sealed record FrequencyOption(RecurringPurchaseFrequency Value, string Label);
}
