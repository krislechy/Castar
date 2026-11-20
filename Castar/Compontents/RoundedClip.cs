using System.Windows;
using System.Windows.Media;

namespace Castar.Compontents;

public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedClip), new PropertyMetadata(0d, RadiusChanged),
        value => value is double radius && double.IsFinite(radius) && radius >= 0);

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void RadiusChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element)
            return;

        element.SizeChanged -= UpdateClip;
        if ((double)e.NewValue == 0)
        {
            element.ClearValue(UIElement.ClipProperty);
            return;
        }

        element.SizeChanged += UpdateClip;
        ApplyClip(element, element.RenderSize);
    }

    private static void UpdateClip(object sender, SizeChangedEventArgs e) =>
        ApplyClip((FrameworkElement)sender, e.NewSize);

    private static void ApplyClip(FrameworkElement element, Size size)
    {
        var radius = GetRadius(element);
        var clip = new RectangleGeometry(new Rect(size), radius, radius);
        clip.Freeze();
        element.SetCurrentValue(UIElement.ClipProperty, clip);
    }
}
