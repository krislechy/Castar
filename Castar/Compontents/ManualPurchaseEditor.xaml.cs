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
using ClientSettings = Castar.Settings.ClientSettings;

namespace Castar.Compontents;

public partial class ManualPurchaseEditor : UserControl
{
    private readonly PurchaseEditor header;
    private readonly string defaultCurrency;
    private readonly IReadOnlyList<BudgetCategoryDto>? categories;
    private readonly Guid purchaseId;
    private readonly PurchaseWriteDto? originalHeader;
    private readonly RecurringPurchaseFrequency? originalFrequency;
    private readonly TimeZoneInfo initialScheduleZone;

    internal ManualPurchaseEditor(DateTime localDate, ManualCurrencySettingsDto currencies, IReadOnlyList<User> owners,
        IReadOnlyList<BudgetCategoryDto>? categories = null, RecurringPurchaseDto? recurring = null,
        TimeZoneInfo? scheduleZone = null, bool startRecurring = false)
    {
        defaultCurrency = currencies.DefaultCurrency;
        this.categories = categories;
        var template = recurring?.Schedule.Template;
        purchaseId = recurring?.Id ?? Guid.NewGuid();
        originalHeader = template?.Purchase;
        originalFrequency = recurring?.Schedule.Frequency;
        initialScheduleZone = scheduleZone ?? (recurring is null
            ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(recurring.Schedule.TimeZoneId));
        InitializeComponent();
        FrequencyPicker.ItemsSource = new FrequencyOption[]
        {
            new(RecurringPurchaseFrequency.Weekly, "Каждую неделю"),
            new(RecurringPurchaseFrequency.Monthly, "Каждый месяц"),
            new(RecurringPurchaseFrequency.Yearly, "Каждый год")
        };
        FrequencyPicker.SelectedValue = recurring?.Schedule.Frequency ?? RecurringPurchaseFrequency.Monthly;
        RepeatPurchase.IsChecked = recurring is not null || startRecurring;
        RepeatPurchase.IsEnabled = recurring is null && !startRecurring;
        var zoneOptions = ClientSettings.DisplayTimeZoneOptions.Where(option => option.Id.Length > 0).ToList();
        if (!zoneOptions.Any(option => option.Id == initialScheduleZone.Id))
            zoneOptions.Insert(0, new(initialScheduleZone.Id, initialScheduleZone.DisplayName));
        ScheduleZonePicker.ItemsSource = zoneOptions;
        ScheduleZonePicker.SelectedValue = initialScheduleZone.Id;
        header = new PurchaseEditor(
            new(purchaseId, originalHeader?.UserId ?? Guid.Empty, originalHeader?.Name ?? "", 0,
                originalHeader?.Currency ?? defaultCurrency, 0, originalHeader?.AddedOn ?? localDate,
                originalHeader?.RetailPlace, originalHeader?.RetailPlaceAddress, []),
            currencies.Currencies, owners, manualEntry: true);
        HeaderFields.Content = header;
        if (template is { Items.Length: > 0 })
        {
            foreach (var item in template.Items)
                AddItem(item);
        }
        else
            AddItem();
    }

    internal TimeZoneInfo ScheduleZone => ScheduleZonePicker.SelectedValue is string id &&
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var selectedZone) ? selectedZone : initialScheduleZone;
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
            var calendarChanged = draft.AddedOn != originalHeader?.AddedOn || frequency != originalFrequency;
            if (calendarChanged && draft.AddedOn.Date < TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ScheduleZone).Date)
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
            Currency = draft.Currency ?? originalHeader?.Currency ?? defaultCurrency,
            UserId = draft.UserId ?? originalHeader?.UserId
        }, items.ToArray());
        return new(true, null);
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem()?.FocusItemName();

    private PurchaseItemEditor? AddItem(PurchaseItemWriteDto? originalItem = null)
    {
        if (ItemFields.Children.Count >= 250)
            return null;

        var row = new DockPanel { Tag = originalItem?.Category };
        var item = new PurchaseItemDto(Guid.Empty, originalItem?.Name ?? "", null, originalItem?.Quantity ?? 1,
            originalItem?.Price ?? 0, 0)
        {
            BudgetCategoryId = originalItem?.Category?.Id
        };
        var editor = new PurchaseItemEditor(item, null, manualEntry: true, categories: categories);
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
