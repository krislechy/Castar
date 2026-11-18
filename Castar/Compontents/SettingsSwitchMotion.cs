using System.Windows;

namespace Castar.Compontents;

public sealed class SettingsSwitchMotion : VisualStateManager
{
    protected override bool GoToStateCore(
        FrameworkElement control,
        FrameworkElement stateGroupsRoot,
        string stateName,
        VisualStateGroup? group,
        VisualState? state,
        bool useTransitions)
    {
        if (group is null || state is null)
            return false;

        return base.GoToStateCore(
            control,
            stateGroupsRoot,
            stateName,
            group,
            state,
            useTransitions && control.IsLoaded && control.IsVisible && SystemParameters.ClientAreaAnimation);
    }
}
