using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace Castar.Compontents;

/// <summary>Shared loading state for explicitly awaited button actions.</summary>
public static class ButtonLoading
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.RegisterAttached(
        "IsBusy", typeof(bool), typeof(ButtonLoading), new PropertyMetadata(false, UpdateInteraction));

    public static readonly DependencyProperty AllowInteractionWhileBusyProperty = DependencyProperty.RegisterAttached(
        "AllowInteractionWhileBusy", typeof(bool), typeof(ButtonLoading), new PropertyMetadata(false, UpdateInteraction));

    private static readonly DependencyProperty BusyClockProperty = DependencyProperty.RegisterAttached(
        "BusyClock", typeof(AnimationClock), typeof(ButtonLoading));

    public static bool GetIsBusy(DependencyObject target) => (bool)target.GetValue(IsBusyProperty);
    public static void SetIsBusy(DependencyObject target, bool value) => target.SetValue(IsBusyProperty, value);

    // Cancellation buttons may keep their existing click handler while displaying progress.
    public static bool GetAllowInteractionWhileBusy(DependencyObject target) =>
        (bool)target.GetValue(AllowInteractionWhileBusyProperty);
    public static void SetAllowInteractionWhileBusy(DependencyObject target, bool value) =>
        target.SetValue(AllowInteractionWhileBusyProperty, value);

    public static async Task RunAsync(object? source, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var target = ResolveTarget(source);
        if (target is null)
        {
            await action();
            return;
        }

        target.VerifyAccess();
        if (GetIsBusy(target))
            return;

        target.SetCurrentValue(IsBusyProperty, true);
        try
        {
            await action();
        }
        finally
        {
            target.SetCurrentValue(IsBusyProperty, false);
        }
    }

    private static UIElement? ResolveTarget(object? source)
    {
        if (source is ButtonBase button)
            return button;
        if (source is not MenuItem item)
            return null;

        ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(item);
        while (parent is MenuItem parentItem)
            parent = ItemsControl.ItemsControlFromItemContainer(parentItem);
        return parent is ContextMenu { PlacementTarget: ButtonBase placement } ? placement : item;
    }

    private static void UpdateInteraction(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element)
            return;

        var disable = GetIsBusy(target) && !GetAllowInteractionWhileBusy(target);
        if (disable && target.GetValue(BusyClockProperty) is null)
        {
            // Animation precedence preserves the live IsEnabled binding/local value and CanExecute.
            // Compose and removing only our clock also preserve any pre-existing animation.
            var animation = new BooleanAnimationUsingKeyFrames { Duration = Duration.Forever };
            animation.KeyFrames.Add(new DiscreteBooleanKeyFrame(false, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            var clock = (AnimationClock)animation.CreateClock(true);
            target.SetValue(BusyClockProperty, clock);
            element.ApplyAnimationClock(UIElement.IsEnabledProperty, clock, HandoffBehavior.Compose);
        }
        else if (!disable && target.GetValue(BusyClockProperty) is AnimationClock clock)
        {
            clock.Controller?.Remove();
            target.ClearValue(BusyClockProperty);
        }
    }
}
