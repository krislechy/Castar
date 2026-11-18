using Castar.Currency;
using Castar.Statistics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Castar.Compontents;

internal sealed record ExpenseHeatmapMonth(DateOnly Month, string Currency,
    IReadOnlyList<StatisticsDailyPoint> Days, Action<DateOnly> Open);

public sealed class ExpenseHeatmapMonthView : UserControl
{
    public ExpenseHeatmapMonthView() => DataContextChanged += (_, _) => Draw();
    private void Draw()
    {
        if (DataContext is not ExpenseHeatmapMonth month) return;
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var fill = new FrameworkElementFactory(typeof(Border));
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        fill.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        fill.AppendChild(content);
        buttonTemplate.VisualTree = fill;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, 0.75));
        buttonTemplate.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Cell"));
        fill.Name = "Cell";
        fill.SetResourceReference(Border.BorderBrushProperty, "Highlight");
        buttonTemplate.Triggers.Add(focus);
        var stack = new StackPanel { Margin = new Thickness(4, 8, 4, 12) };
        var heading = new TextBlock { Text = month.Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture), Margin = new Thickness(3, 0, 0, 8) };
        heading.SetResourceReference(ForegroundProperty, "Foreground.White");
        stack.Children.Add(heading);
        var grid = new UniformGrid { Columns = 7 };
        foreach (var label in new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" })
        {
            var day = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(3) };
            day.SetResourceReference(ForegroundProperty, "Foreground.Gray");
            grid.Children.Add(day);
        }
        var offset = ((int)month.Month.DayOfWeek + 6) % 7;
        for (var i = 0; i < offset; i++) grid.Children.Add(new Border());
        var selected = month.Days.ToDictionary(day => day.Date);
        var max = month.Days.Where(day => day.Expenses.HasValue).Select(day => Math.Abs(day.Expenses!.Value)).DefaultIfEmpty(0).Max();
        for (var number = 1; number <= DateTime.DaysInMonth(month.Month.Year, month.Month.Month); number++)
        {
            var date = new DateOnly(month.Month.Year, month.Month.Month, number);
            if (!selected.TryGetValue(date, out var day)) { grid.Children.Add(new Border()); continue; }
            var cell = new Button { Content = number.ToString(), Margin = new Thickness(3), Height = 30,
                MinWidth = 28, Padding = new Thickness(2), Template = buttonTemplate };
            cell.SetResourceReference(ForegroundProperty, "Foreground.White");
            if (day.Expenses is { } amount)
            {
                var color = ((SolidColorBrush)FindResource(amount < 0 ? "Button.Foreground.Green" : "Highlight")).Color;
                var strength = max == 0 ? 0 : Math.Abs(amount) / max;
                cell.Background = new SolidColorBrush(Color.FromArgb((byte)(28 + 150 * strength), color.R, color.G, color.B));
                cell.ToolTip = date.ToString("dd.MM.yyyy") + " · " + HistoricalExchangeRates.Format(amount, month.Currency);
            }
            else
            {
                cell.SetResourceReference(BackgroundProperty, "Main.Background.lvl3");
                cell.ToolTip = date.ToString("dd.MM.yyyy") + " · Нет валюты или курса";
            }
            System.Windows.Automation.AutomationProperties.SetName(cell, cell.ToolTip.ToString());
            cell.Click += (_, _) => month.Open(date);
            grid.Children.Add(cell);
        }
        stack.Children.Add(grid);
        var legend = new DockPanel { Margin = new Thickness(3, 8, 3, 0) };
        var low = new TextBlock { Text = HistoricalExchangeRates.Format(0, month.Currency), Margin = new Thickness(0, 0, 8, 0) };
        var high = new TextBlock { Text = HistoricalExchangeRates.Format(max, month.Currency), Margin = new Thickness(8, 0, 0, 0) };
        low.SetResourceReference(ForegroundProperty, "Foreground.Gray");
        high.SetResourceReference(ForegroundProperty, "Foreground.Gray");
        DockPanel.SetDock(low, Dock.Left);
        DockPanel.SetDock(high, Dock.Right);
        legend.Children.Add(low);
        legend.Children.Add(high);
        var accent = ((SolidColorBrush)FindResource("Highlight")).Color;
        legend.Children.Add(new Border { Height = 5, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush(Color.FromArgb(28, accent.R, accent.G, accent.B),
                Color.FromArgb(178, accent.R, accent.G, accent.B), 0) });
        stack.Children.Add(legend);
        Content = stack;
    }
}
