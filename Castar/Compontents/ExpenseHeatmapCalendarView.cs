using Castar.Currency;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using AutomationProperties = System.Windows.Automation.AutomationProperties;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Castar.Compontents;

internal sealed record ExpenseHeatmapCell(DateOnly Date, decimal? Amount);
internal enum ExpenseHeatmapGranularity { Day, Month, Year }

internal sealed record ExpenseHeatmapPeriod(DateOnly Start, string Currency, ExpenseHeatmapGranularity Granularity,
    IReadOnlyList<ExpenseHeatmapCell> Cells, decimal Maximum, decimal? Average, ClientSettings Preferences, Action<DateOnly> Open)
{
    public bool IsGrouped => Granularity != ExpenseHeatmapGranularity.Day;

    public string ColorSensitivityProperty => Granularity switch
    {
        ExpenseHeatmapGranularity.Year => nameof(ClientSettings.YearlyHeatmapColorSensitivity),
        ExpenseHeatmapGranularity.Month => nameof(ClientSettings.MonthlyHeatmapColorSensitivity),
        _ => nameof(ClientSettings.DailyHeatmapColorSensitivity)
    };

    public int ColorSensitivity
    {
        get => Granularity switch
        {
            ExpenseHeatmapGranularity.Year => Preferences.YearlyHeatmapColorSensitivity,
            ExpenseHeatmapGranularity.Month => Preferences.MonthlyHeatmapColorSensitivity,
            _ => Preferences.DailyHeatmapColorSensitivity
        };
        set
        {
            switch (Granularity)
            {
                case ExpenseHeatmapGranularity.Year:
                    Preferences.YearlyHeatmapColorSensitivity = value;
                    break;
                case ExpenseHeatmapGranularity.Month:
                    Preferences.MonthlyHeatmapColorSensitivity = value;
                    break;
                default:
                    Preferences.DailyHeatmapColorSensitivity = value;
                    break;
            }
        }
    }
}

public sealed partial class ExpenseHeatmapCalendarView : UserControl
{
    private static readonly Color Blue = Color.FromRgb(71, 122, 245);
    private static readonly Color Violet = Color.FromRgb(150, 93, 244);
    private static readonly Color SaturatedViolet = Color.FromRgb(190, 82, 250);
    private readonly List<Action<double>> colorUpdates = [];
    private ClientSettings? preferences;
    private Slider? sensitivitySlider;
    private bool draggingSensitivity;

    public ExpenseHeatmapCalendarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Draw();
    }

    private void Draw()
    {
        colorUpdates.Clear();
        sensitivitySlider = null;
        draggingSensitivity = false;
        if (DataContext is not ExpenseHeatmapPeriod { Cells.Count: > 0 } period)
        {
            ListenToPreferences(null);
            Content = null;
            return;
        }

        ListenToPreferences(period.Preferences);
        var stack = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };
        var culture = CultureInfo.CurrentCulture;
        if (period.Granularity != ExpenseHeatmapGranularity.Year)
        {
            var heading = Text(period.Start.ToString(period.IsGrouped ? "yyyy" : "MMMM yyyy", culture), 14, "Foreground.White");
            heading.Margin = new Thickness(3, 0, 0, 8);
            stack.Children.Add(heading);
        }

        var grid = new UniformGrid
        {
            Columns = period.Granularity switch
            {
                ExpenseHeatmapGranularity.Year => Math.Min(4, period.Cells.Count),
                ExpenseHeatmapGranularity.Month => 4,
                _ => 7
            }
        };
        if (!period.IsGrouped)
        {
            var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 3) };
            foreach (var label in new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" })
            {
                var day = Text(label, 12, "Foreground.Gray");
                day.HorizontalAlignment = HorizontalAlignment.Center;
                day.Margin = new Thickness(3);
                weekdays.Children.Add(day);
            }
            stack.Children.Add(weekdays);
        }
        var firstDayOffset = ((int)period.Start.DayOfWeek + 6) % 7;
        var (offset, slotCount) = period.Granularity switch
        {
            ExpenseHeatmapGranularity.Year => (0, period.Cells.Count),
            ExpenseHeatmapGranularity.Month => (period.Cells[0].Date.Month - 1, 12),
            _ => (firstDayOffset + period.Cells[0].Date.Day - 1,
                firstDayOffset + DateTime.DaysInMonth(period.Start.Year, period.Start.Month))
        };
        for (var i = 0; i < offset; i++) grid.Children.Add(new Border());
        foreach (var entry in period.Cells)
            grid.Children.Add(CreateCell(period, entry, culture));
        for (var i = grid.Children.Count; i < slotCount; i++)
            grid.Children.Add(new Border());
        stack.Children.Add(grid);
        if (period.Average.HasValue || period.Maximum > 0)
            stack.Children.Add(CreateLegend(period));
        Content = stack;
    }

    private Button CreateCell(ExpenseHeatmapPeriod period, ExpenseHeatmapCell entry, CultureInfo culture)
    {
        var strength = period.Maximum > 0 ? (double)(Math.Abs(entry.Amount.GetValueOrDefault()) / period.Maximum) : 0;
        var surface = ThemeColor("Main.Background.lvl1");
        var border = ThemeColor("Main.Background.lvl3");
        var accent = ThemeColor("Highlight");
        var green = ThemeColor("Button.Foreground.Green");
        var white = ThemeColor("Foreground.White");
        var neutralSurface = Blend(surface, border, 0.6);
        var background = new LinearGradientBrush(neutralSurface, neutralSurface, 44);
        var outline = new SolidColorBrush(border);
        var textAccent = new SolidColorBrush();
        var (labelFormat, tooltipFormat) = period.Granularity switch
        {
            ExpenseHeatmapGranularity.Year => ("yyyy", "yyyy"),
            ExpenseHeatmapGranularity.Month => ("MMM", "MMMM yyyy"),
            _ => ("%d", "dd.MM.yyyy")
        };
        var label = Text(entry.Date.ToString(labelFormat, culture), period.IsGrouped ? 14 : 12, "Foreground.White");
        label.Foreground = textAccent;
        label.FontWeight = FontWeights.Medium;
        var value = Text(entry.Amount is { } amount ? FormatAmount(amount) : "—", period.IsGrouped ? 18 : 13, "Foreground.White");
        value.FontWeight = FontWeights.SemiBold;
        var amountRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var delta = Delta(entry.Amount, period.Average, culture);
        if (period.IsGrouped && delta.Length > 0)
        {
            var change = Text(delta, 12, "Foreground.White");
            change.Foreground = textAccent;
            change.VerticalAlignment = VerticalAlignment.Center;
            change.Margin = new Thickness(4, 0, 0, 0);
            change.MaxWidth = 50;
            change.TextTrimming = TextTrimming.CharacterEllipsis;
            DockPanel.SetDock(change, Dock.Right);
            amountRow.Children.Add(change);
        }
        amountRow.Children.Add(new Viewbox { Child = value, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = period.IsGrouped ? 24 : 18 });

        var content = new StackPanel();
        content.Children.Add(label);
        content.Children.Add(amountRow);
        var trackColor = new SolidColorBrush();
        var indicatorColor = new SolidColorBrush();
        if (entry.Amount.HasValue)
            content.Children.Add(CreateIndicator(strength, trackColor, indicatorColor));
        var tooltip = entry.Date.ToString(tooltipFormat, culture)
            + " · " + (entry.Amount is { } total ? FormatAmount(total, period.Currency) : "Нет валюты или курса");
        if (delta.Length > 0)
            tooltip += "\nК среднему · " + delta;
        else if (entry.Amount is { } againstZero && period.Average == 0)
            tooltip += "\nСреднее · " + FormatAmount(0, period.Currency)
                + "\nК среднему · " + (againstZero > 0 ? "+" : "") + FormatAmount(againstZero, period.Currency);

        var cell = new Button
        {
            Style = (Style)FindResource("HeatmapCellButton"), Tag = entry.Date, Content = content, ToolTip = tooltip,
            MinHeight = period.IsGrouped ? 72 : 58, Background = background,
            BorderBrush = outline
        };
        AutomationProperties.SetName(cell, tooltip.Replace("\n", ", "));
        cell.Click += (_, _) => period.Open(entry.Date);
        PaintCell(ColorExponent(period.ColorSensitivity));
        if (strength > 0)
            colorUpdates.Add(PaintCell);
        return cell;

        void PaintCell(double exponent)
        {
            var adjusted = Math.Pow(strength, exponent);
            var tint = entry.Amount < 0 ? green : ScaleColor(accent, adjusted);
            textAccent.Color = Blend(white, tint, 0.45);
            trackColor.Color = Color.FromArgb(60, tint.R, tint.G, tint.B);
            indicatorColor.Color = tint;
            if (strength == 0)
                return;

            background.GradientStops[0].Color = Blend(surface, tint, 0.20 + 0.34 * adjusted);
            background.GradientStops[1].Color = Blend(surface, tint, 0.08 + 0.24 * adjusted);
            outline.Color = Blend(border, tint, 0.48 * adjusted);
        }
    }

    private static Grid CreateIndicator(double strength, Brush trackColor, Brush indicatorColor)
    {
        var bar = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(strength, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - strength, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(1.5), Background = trackColor };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = indicatorColor });
        return bar;
    }

    private DockPanel CreateLegend(ExpenseHeatmapPeriod period)
    {
        var legend = new DockPanel { Margin = new Thickness(3, 10, 3, 0) };
        if (period.Average is { } average)
        {
            var averageCaption = period.Granularity switch
            {
                ExpenseHeatmapGranularity.Year => "Среднее за год · ",
                ExpenseHeatmapGranularity.Month => "Среднее за месяц · ",
                _ => "Среднее за день · "
            };
            var averageLabel = Text(averageCaption
                + FormatAmount(average, period.Currency), 12, "Foreground.White");
            averageLabel.Margin = new Thickness(period.Maximum > 0 ? 16 : 0, 0, 0, 0);
            DockPanel.SetDock(averageLabel, Dock.Right);
            legend.Children.Add(averageLabel);
        }
        if (period.Maximum > 0)
        {
            var scale = new DockPanel();
            var low = Text(FormatAmount(0), 12, "Foreground.Gray");
            low.Margin = new Thickness(0, 0, 8, 0);
            var high = Text(FormatAmount(period.Maximum, period.Currency), 12, "Foreground.Gray");
            high.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(high, Dock.Right);
            scale.Children.Add(low);
            scale.Children.Add(high);
            scale.Children.Add(CreateSensitivitySlider(period.ColorSensitivity));
            legend.Children.Add(scale);
        }
        return legend;
    }

    private Slider CreateSensitivitySlider(int sensitivity)
    {
        var accent = ThemeColor("Highlight");
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        const int segments = 20;
        for (var index = 0; index <= segments; index++)
            gradient.GradientStops.Add(new GradientStop(accent, (double)index / segments));
        void PaintGradient(double exponent)
        {
            foreach (var stop in gradient.GradientStops)
                stop.Color = ScaleColor(accent, Math.Pow(stop.Offset, exponent));
        }

        PaintGradient(ColorExponent(sensitivity));
        colorUpdates.Add(PaintGradient);
        const string label = "Чувствительность цвета";
        var slider = new Slider
        {
            Name = "HeatmapColorSlider", Style = (Style)FindResource("FormSlider"),
            Background = gradient, MinWidth = 42, VerticalAlignment = VerticalAlignment.Center,
            Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 10, IsMoveToPointEnabled = true,
            Value = sensitivity, ToolTip = label
        };
        AutomationProperties.SetName(slider, label);
        slider.ValueChanged += SensitivityChanged;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SensitivityDragStarted));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SensitivityDragCompleted));
        sensitivitySlider = slider;
        return slider;
    }

    private static double ColorExponent(double value) => Math.Pow(2, (50 - value) / 15);

    private void SensitivityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ReferenceEquals(sender, sensitivitySlider) || DataContext is not ExpenseHeatmapPeriod period)
            return;
        var exponent = ColorExponent(e.NewValue);
        foreach (var paint in colorUpdates)
            paint(exponent);
        if (!draggingSensitivity)
            period.ColorSensitivity = (int)Math.Round(e.NewValue);
    }

    private void SensitivityDragStarted(object sender, DragStartedEventArgs e)
    {
        if (ReferenceEquals(sender, sensitivitySlider))
            draggingSensitivity = true;
    }

    private void SensitivityDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Slider slider || !ReferenceEquals(slider, sensitivitySlider))
            return;
        draggingSensitivity = false;
        if (DataContext is ExpenseHeatmapPeriod period)
            period.ColorSensitivity = (int)Math.Round(slider.Value);
    }

    private void ListenToPreferences(ClientSettings? settings)
    {
        if (ReferenceEquals(settings, preferences))
            return;
        if (preferences is { } previous)
            PropertyChangedEventManager.RemoveHandler(previous, PreferencesChanged, "");
        preferences = settings;
        if (settings is not null)
            PropertyChangedEventManager.AddHandler(settings, PreferencesChanged, "");
    }

    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, preferences) && sensitivitySlider is { } slider
            && DataContext is ExpenseHeatmapPeriod period && e.PropertyName == period.ColorSensitivityProperty)
            slider.Value = period.ColorSensitivity;
    }

    private static string Delta(decimal? amount, decimal? average, CultureInfo culture)
    {
        if (amount is not { } value || average is not { } comparison || comparison == 0)
            return "";
        var percent = ((double)value - (double)comparison) / Math.Abs((double)comparison) * 100;
        return Math.Round(percent, MidpointRounding.AwayFromZero).ToString("+0;−0;0", culture) + "%";
    }

    private static string FormatAmount(decimal value, string? currency = null) =>
        MoneyText.FormatRounded(value, currency);

    private static TextBlock Text(string value, double size, string foreground)
    {
        var text = new TextBlock { Text = value, FontSize = size };
        text.SetResourceReference(ForegroundProperty, foreground);
        return text;
    }

    private Color ThemeColor(string key) => ((SolidColorBrush)FindResource(key)).Color;

    private static Color ScaleColor(Color accent, double strength)
    {
        const double blue = 0.5;
        const double violet = 0.67;
        if (strength <= blue)
            return Blend(accent, Blue, strength / blue);
        if (strength <= violet)
            return Blend(Blue, Violet, (strength - blue) / (violet - blue));
        return Blend(Violet, SaturatedViolet, (strength - violet) / (1 - violet));
    }

    private static Color Blend(Color from, Color to, double weight) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * weight),
        (byte)Math.Round(from.G + (to.G - from.G) * weight),
        (byte)Math.Round(from.B + (to.B - from.B) * weight));
}
