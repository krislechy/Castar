using Castar.Domain.Models.SettingsModel;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Castar.Compontents;

public sealed record ValidatePopupDialog(bool Result, string? Message);

public partial class PopupDialog : UserControl
{
    private readonly Queue<Request> pending = new();
    private IInputElement? previousFocus;
    private bool closing;

    public PopupDialog() => InitializeComponent();

    public enum TypeDialogEnum
    {
        Default,
        Confirm
    }

    public sealed class Options
    {
        public bool AllowMissClick { get; set; } = true;
        public DefaultButtonClass DefaultButton { get; } = new();
        public SuccessConfirmButtonClass SuccessButton { get; } = new();
        public CancelConfirmButtonClass CancelButton { get; } = new();
        public SolidColorBrush Background { get; set; } =
            (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
        public string? Title { get; set; }

        public sealed class DefaultButtonClass
        {
            public string DefaultButtonText { get; set; } = "ОК";
            public Brush DefaultButtonForeground { get; set; } = (Brush)Application.Current.FindResource("Button.Foreground");
        }

        public sealed class SuccessConfirmButtonClass
        {
            public string SuccessConfirmButtonText { get; set; } = "ОК";
            public Brush SuccessConfirmButtonForeground { get; set; } = (Brush)Application.Current.FindResource("Button.Foreground.Green");
        }

        public sealed class CancelConfirmButtonClass
        {
            public string CancelConfirmButtonText { get; set; } = "Отмена";
            public Brush CancelConfirmButtonForeground { get; set; } = (Brush)Application.Current.FindResource("Button.Foreground");
        }
    }

    private static readonly DependencyPropertyKey ActiveDialogPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActiveDialog), typeof(Request), typeof(PopupDialog), new PropertyMetadata(null));

    public static readonly DependencyProperty ActiveDialogProperty = ActiveDialogPropertyKey.DependencyProperty;

    public Request? ActiveDialog => (Request?)GetValue(ActiveDialogProperty);

    public static Task<bool?> Show(object content, TypeDialogEnum typeDialog,
        Func<Options, Options>? options = null, Func<ValidatePopupDialog>? validation = null)
    {
        if (Application.Current is not { } app || app.Dispatcher.HasShutdownStarted)
            return Task.FromResult<bool?>(null);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.InvokeAsync(() => Show(content, typeDialog, options, validation)).Task.Unwrap();
        if (app.MainWindow is not MainWindow { IsVisible: true } window)
            return Task.FromResult<bool?>(null);

        var dialog = window.MainDialog;
        var configuration = options?.Invoke(new Options()) ?? new Options();
        var request = new Request(content, typeDialog, configuration, validation);
        dialog.pending.Enqueue(request);
        dialog.ShowNext();
        return request.Completion.Task;
    }

    internal static void SetContentWarning(UIElement content, string? message)
    {
        if (Application.Current?.MainWindow is MainWindow { MainDialog: { ActiveDialog: { } request } dialog }
            && dialog.DialogContent.IsAncestorOf(content))
            request.Warning = message;
    }

    private void ShowNext()
    {
        if (closing || ActiveDialog is not null || !pending.TryDequeue(out var request))
            return;

        if (!IsVisible && Window.GetWindow(this) is { } owner)
        {
            previousFocus = Keyboard.FocusedElement is UIElement focused && owner.IsAncestorOf(focused)
                ? focused : FocusManager.GetFocusedElement(owner);
        }

        SetValue(ActiveDialogPropertyKey, request);
        Visibility = Visibility.Visible;

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ActiveDialog != request || closing || !IsVisible)
                return;
            if (request.Content is UIElement { } editor && editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
                return;
            (request.Type == TypeDialogEnum.Default ? DefaultButton : CancelButton).Focus();
        }, DispatcherPriority.Input);
    }

    private void Close(bool? result)
    {
        if (ActiveDialog is not { } request || closing)
            return;

        if (result == true && request.Validation?.Invoke() is { Result: false } invalid)
        {
            request.Warning = invalid.Message;
            return;
        }

        closing = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200), FillBehavior.Stop)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (ActiveDialog != request)
                return;

            SetValue(ActiveDialogPropertyKey, null);
            if (pending.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                if (previousFocus is UIElement { IsVisible: true, IsEnabled: true } element)
                    Keyboard.Focus(element);
                previousFocus = null;
            }
            BeginAnimation(OpacityProperty, null);
            closing = false;
            ShowNext();
            request.Completion.TrySetResult(result);
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Dialog_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue && Window.GetWindow(this)?.IsVisible != true)
            CancelPending();
    }

    private void Dialog_Unloaded(object sender, RoutedEventArgs e) => CancelPending();

    private void CancelPending()
    {
        var request = ActiveDialog;
        closing = true;
        SetValue(ActiveDialogPropertyKey, null);
        previousFocus = null;
        Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null);

        request?.Completion.TrySetResult(null);
        while (pending.TryDequeue(out var queued))
            queued.Completion.TrySetResult(null);
        closing = false;
    }

    private void Dialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close(ActiveDialog?.Type == TypeDialogEnum.Confirm ? false : null);
    }

    private void SuccessConfirmEvent(object sender, RoutedEventArgs e) => Close(true);
    private void CancelConfirmEvent(object sender, RoutedEventArgs e) => Close(false);
    private void DefaultEvent(object sender, RoutedEventArgs e) => Close(null);

    private void ButtonOutsideClickEvent(object sender, MouseButtonEventArgs e)
    {
        if (!border.IsMouseOver && ActiveDialog is { AllowMissClick: true } request)
            Close(request.Type == TypeDialogEnum.Confirm ? false : null);
    }

    public sealed class Request : BaseSettingsModel
    {
        private string? warning;

        internal Request(object content, TypeDialogEnum type, Options options, Func<ValidatePopupDialog>? validation)
        {
            Content = content;
            Type = type;
            Title = options.Title;
            Background = options.Background;
            DefaultButton = new(options.DefaultButton.DefaultButtonText, options.DefaultButton.DefaultButtonForeground);
            ConfirmButton = new(options.SuccessButton.SuccessConfirmButtonText, options.SuccessButton.SuccessConfirmButtonForeground);
            CancelButton = new(options.CancelButton.CancelConfirmButtonText, options.CancelButton.CancelConfirmButtonForeground);
            AllowMissClick = options.AllowMissClick;
            Validation = validation;
        }

        public object Content { get; }
        public TypeDialogEnum Type { get; }
        public string? Title { get; }
        public SolidColorBrush Background { get; }
        public ButtonPresentation DefaultButton { get; }
        public ButtonPresentation ConfirmButton { get; }
        public ButtonPresentation CancelButton { get; }
        public string? Warning { get => warning; internal set => SetField(ref warning, value); }
        internal bool AllowMissClick { get; }
        internal Func<ValidatePopupDialog>? Validation { get; }
        internal TaskCompletionSource<bool?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed record ButtonPresentation(string Text, Brush Foreground);
}
