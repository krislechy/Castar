using Castar.Currency;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Castar.Compontents;

internal sealed record ExpenseHeatmapCell(DateOnly Date, decimal? Amount);
internal sealed record ExpenseHeatmapPeriod(DateOnly Start, string Currency, bool ByMonth,
    IReadOnlyList<ExpenseHeatmapCell> Cells, decimal Maximum, Action<DateOnly> OpenDay);

public sealed class ExpenseHeatmapCalendarView : UserControl
{
    public ExpenseHeatmapCalendarView() => DataContextChanged += (_, _) => Draw();
    private void Draw()
    {
        if (DataContext is not ExpenseHeatmapPeriod period) return;
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
        var culture = CultureInfo.CurrentCulture;
        var heading = new TextBlock { Text = period.Start.ToString(period.ByMonth ? "yyyy" : "MMMM yyyy", culture),
            Margin = new Thickness(3, 0, 0, 8) };
        heading.SetResourceReference(ForegroundProperty, "Foreground.White");
        stack.Children.Add(heading);
        var grid = new UniformGrid { Columns = period.ByMonth ? 4 : 7 };
        if (!period.ByMonth)
        {
            foreach (var label in new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" })
            {
                var day = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(3) };
                day.SetResourceReference(ForegroundProperty, "Foreground.Gray");
                grid.Children.Add(day);
            }
        }
        var offset = period.ByMonth ? period.Cells[0].Date.Month - 1 :
            ((int)period.Start.DayOfWeek + 6) % 7 + period.Cells[0].Date.Day - 1;
        for (var i = 0; i < offset; i++) grid.Children.Add(new Border());
        foreach (var entry in period.Cells)
        {
            var date = entry.Date;
            var label = date.ToString(period.ByMonth ? "MMM" : "%d", culture);
            var tooltip = date.ToString(period.ByMonth ? "MMMM yyyy" : "dd.MM.yyyy", culture) + " · " +
                (entry.Amount is { } amount ? HistoricalExchangeRates.Format(amount, period.Currency) : "Нет валюты или курса");
            var background = CellBackground(entry.Amount, period.Maximum);
            if (period.ByMonth)
            {
                var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center };
                text.SetResourceReference(ForegroundProperty, "Foreground.White");
                var cell = new Border { Child = text, Background = background, ToolTip = tooltip,
                    Margin = new Thickness(3), Height = 30, CornerRadius = new CornerRadius(3) };
                System.Windows.Automation.AutomationProperties.SetName(text, tooltip);
                grid.Children.Add(cell);
            }
            else
            {
                var cell = new Button { Content = label, Background = background, ToolTip = tooltip, Margin = new Thickness(3),
                    Height = 30, MinWidth = 28, Padding = new Thickness(2), Template = buttonTemplate };
                cell.SetResourceReference(ForegroundProperty, "Foreground.White");
                System.Windows.Automation.AutomationProperties.SetName(cell, tooltip);
                cell.Click += (_, _) => period.OpenDay(date);
                grid.Children.Add(cell);
            }
        }
        var slotCount = period.ByMonth ? 12 :
            ((int)period.Start.DayOfWeek + 6) % 7 + DateTime.DaysInMonth(period.Start.Year, period.Start.Month);
        for (var i = grid.Children.Count - (period.ByMonth ? 0 : 7); i < slotCount; i++)
            grid.Children.Add(new Border());
        stack.Children.Add(grid);
        var legend = new DockPanel { Margin = new Thickness(3, 8, 3, 0) };
        var low = new TextBlock { Text = HistoricalExchangeRates.Format(0, period.Currency), Margin = new Thickness(0, 0, 8, 0) };
        var high = new TextBlock { Text = HistoricalExchangeRates.Format(period.Maximum, period.Currency), Margin = new Thickness(8, 0, 0, 0) };
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

    private Brush CellBackground(decimal? value, decimal maximum)
    {
        if (value is not { } amount) return (Brush)FindResource("Main.Background.lvl3");
        var color = ((SolidColorBrush)FindResource(amount < 0 ? "Button.Foreground.Green" : "Highlight")).Color;
        var strength = maximum == 0 ? 0 : Math.Abs(amount) / maximum;
        return new SolidColorBrush(Color.FromArgb((byte)(28 + 150 * strength), color.R, color.G, color.B));
    }
}
