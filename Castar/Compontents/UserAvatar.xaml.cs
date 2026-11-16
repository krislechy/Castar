using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Castar.Compontents;

public partial class UserAvatar : UserControl
{
    public static readonly DependencyProperty UserNameProperty = DependencyProperty.Register(
        nameof(UserName), typeof(string), typeof(UserAvatar), new PropertyMetadata(null, UserNameChanged));

    public static readonly DependencyProperty PhotoProperty =
        DependencyProperty.Register(nameof(Photo), typeof(ImageSource), typeof(UserAvatar));

    private static readonly DependencyPropertyKey InitialsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Initials), typeof(string), typeof(UserAvatar), new PropertyMetadata("?"));

    public static readonly DependencyProperty InitialsProperty = InitialsPropertyKey.DependencyProperty;

    public UserAvatar() => InitializeComponent();

    public string? UserName
    {
        get => (string?)GetValue(UserNameProperty);
        set => SetValue(UserNameProperty, value);
    }

    public ImageSource? Photo
    {
        get => (ImageSource?)GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    public string Initials => (string)GetValue(InitialsProperty);

    private static void UserNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var words = (e.NewValue as string)?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var initials = words is { Length: > 0 }
            ? string.Concat(words.Take(2).Select(word => StringInfo.GetNextTextElement(word))).ToUpperInvariant()
            : "?";
        sender.SetValue(InitialsPropertyKey, initials);
    }
}
