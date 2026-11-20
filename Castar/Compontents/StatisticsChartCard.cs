using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Castar.ServerConnection;
using Castar.Statistics;

namespace Castar.Compontents;

public sealed class StatisticsChartCard : UserControl
{
    private readonly TextBlock title = new();
    private readonly Path icon = new()
    {
        Name = "HeaderIconGlyph",
        StrokeThickness = 1.8,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round
    };
    private readonly Path iconFill = new()
    {
        Name = "HeaderIconFillGlyph",
        Opacity = 0.34
    };
    private readonly ContentControl iconBadge = new()
    {
        Name = "HeaderIconBadge",
        Width = 44,
        Height = 44,
        Margin = new Thickness(0, 0, 12, 0),
        Focusable = false,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 2, Visibility = Visibility.Collapsed };
    private readonly ContentControl action = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0)
    };
    private int generation;
    private bool populated;
    internal bool LastLoadSucceeded { get; private set; }
    public Grid Plot { get; } = new() { Height = 240 };
    public string Title { get => title.Text; set => title.Text = value; }
    public Geometry? HeaderIcon
    {
        get => icon.Data;
        set
        {
            icon.Data = value;
            iconBadge.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    public Geometry? HeaderIconFill { get => iconFill.Data; set => iconFill.Data = value; }
    public object? HeaderAction { get => action.Content; set => action.Content = value; }
    public StatisticsChartCard()
    {
        title.SetResourceReference(StyleProperty, "StatisticsChartTitle");
        icon.SetResourceReference(Shape.StrokeProperty, "Highlight");
        iconFill.SetResourceReference(Shape.FillProperty, "Highlight");
        iconBadge.SetResourceReference(StyleProperty, "LedgerSummaryIcon");
        var iconCanvas = new Canvas { Width = 22, Height = 22 };
        iconCanvas.Children.Add(iconFill);
        iconCanvas.Children.Add(icon);
        iconBadge.Content = new Viewbox
        {
            Name = "HeaderIconViewport",
            Width = 20.655,
            Height = 20.655,
            Stretch = Stretch.Uniform,
            Child = iconCanvas
        };
        status.SetResourceReference(StyleProperty, "GrayTextBlock");
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(action, Dock.Right);
        header.Children.Add(action);
        header.Children.Add(iconBadge);
        header.Children.Add(title);
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(Plot);
        stack.Children.Add(progress);
        stack.Children.Add(status);
        var border = new Border { Child = stack };
        border.SetResourceReference(StyleProperty, "StatisticsChartSurface");
        Content = border;
    }
    public void Message(string message)
    {
        status.Text = message;
        status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    public void Reset()
    {
        generation++;
        populated = false;
        LastLoadSucceeded = false;
        Plot.Children.Clear();
        progress.Visibility = Visibility.Collapsed;
        Message("");
    }
    public void Stop()
    {
        generation++;
        progress.Visibility = Visibility.Collapsed;
    }
    public async Task LoadAsync<T>(Task<T> task, Action<T> render, CancellationToken token)
    {
        var current = ++generation;
        LastLoadSucceeded = false;
        if (!populated)
            progress.Visibility = Visibility.Visible;
        try
        {
            var result = await task.WaitAsync(token);
            if (current != generation || token.IsCancellationRequested)
                return;
            Message("");
            render(result);
            populated = true;
            LastLoadSucceeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (StatisticsCacheInvalidatedException) { }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (current == generation && !token.IsCancellationRequested)
                Message(CastarServerClient.DescribeError(error));
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Statistics chart {0}: {1}", Title, error);
            if (current == generation && !token.IsCancellationRequested)
                Message("Не удалось построить график.");
        }
        finally
        {
            if (current == generation)
                progress.Visibility = Visibility.Collapsed;
        }
    }
}
