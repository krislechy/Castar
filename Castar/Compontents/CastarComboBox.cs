using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Castar.Compontents;

public sealed class CastarComboBox : ComboBox
{
    public static readonly DependencyProperty CompactGlyphProperty = DependencyProperty.RegisterAttached(
        "CompactGlyph", typeof(Geometry), typeof(CastarComboBox), new PropertyMetadata(null));

    public static Geometry? GetCompactGlyph(DependencyObject target) => (Geometry?)target.GetValue(CompactGlyphProperty);

    public static void SetCompactGlyph(DependencyObject target, Geometry? value) =>
        target.SetValue(CompactGlyphProperty, value);
}
