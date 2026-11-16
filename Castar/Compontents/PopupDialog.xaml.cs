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
    private Request? active;
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

    public static readonly DependencyProperty TypeDialogProperty =
        DependencyProperty.Register(nameof(TypeDialog), typeof(TypeDialogEnum), typeof(PopupDialog));
    public TypeDialogEnum TypeDialog => (TypeDialogEnum)GetValue(TypeDialogProperty);

    public static readonly DependencyProperty ObjectContentProperty =
        DependencyProperty.Register(nameof(ObjectContent), typeof(object), typeof(PopupDialog));
    public object? ObjectContent => GetValue(ObjectContentProperty);

    public static readonly DependencyProperty DefaultButtonTextProperty =
        DependencyProperty.Register(nameof(DefaultButtonText), typeof(string), typeof(PopupDialog));
    public string? DefaultButtonText => (string?)GetValue(DefaultButtonTextProperty);

    public static readonly DependencyProperty SuccessConfirmButtonTextProperty =
        DependencyProperty.Register(nameof(SuccessConfirmButtonText), typeof(string), typeof(PopupDialog));
    public string? SuccessConfirmButtonText => (string?)GetValue(SuccessConfirmButtonTextProperty);

    public static readonly DependencyProperty CancelConfirmButtonTextProperty =
        DependencyProperty.Register(nameof(CancelConfirmButtonText), typeof(string), typeof(PopupDialog));
    public string? CancelConfirmButtonText => (string?)GetValue(CancelConfirmButtonTextProperty);

    public static readonly DependencyProperty BackgroundModalProperty =
        DependencyProperty.Register(nameof(BackgroundModal), typeof(SolidColorBrush), typeof(PopupDialog));
    public SolidColorBrush? BackgroundModal => (SolidColorBrush?)GetValue(BackgroundModalProperty);

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PopupDialog));
    public string? Title => (string?)GetValue(TitleProperty);

    public static readonly DependencyProperty ValidateMessageProperty =
        DependencyProperty.Register(nameof(ValidateMessage), typeof(string), typeof(PopupDialog));
    public string? ValidateMessage => (string?)GetValue(ValidateMessageProperty);

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
        if (Application.Current?.MainWindow is MainWindow { MainDialog: { active: not null } dialog }
            && dialog.DialogContent.IsAncestorOf(content))
            dialog.SetValue(ValidateMessageProperty, message);
    }

    private void ShowNext()
    {
        if (active is not null || !pending.TryDequeue(out var request))
            return;

        active = request;
        if (!IsVisible && Window.GetWindow(this) is { } owner)
        {
            previousFocus = Keyboard.FocusedElement is UIElement focused && owner.IsAncestorOf(focused)
                ? focused : FocusManager.GetFocusedElement(owner);
        }

        var options = request.Options;
        SetValue(TypeDialogProperty, request.Type);
        SetValue(TitleProperty, options.Title);
        SetValue(BackgroundModalProperty, options.Background);
        SetValue(ObjectContentProperty, request.Content);
        SetValue(ValidateMessageProperty, null);
        SetValue(DefaultButtonTextProperty, options.DefaultButton.DefaultButtonText);
        SetValue(SuccessConfirmButtonTextProperty, options.SuccessButton.SuccessConfirmButtonText);
        SetValue(CancelConfirmButtonTextProperty, options.CancelButton.CancelConfirmButtonText);
        DefaultButton.Foreground = options.DefaultButton.DefaultButtonForeground;
        ConfirmButton.Foreground = options.SuccessButton.SuccessConfirmButtonForeground;
        CancelButton.Foreground = options.CancelButton.CancelConfirmButtonForeground;
        Visibility = Visibility.Visible;

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (active != request || closing || !IsVisible)
                return;
            if (request.Content is UIElement { } editor && editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
                return;
            (request.Type == TypeDialogEnum.Default ? DefaultButton : CancelButton).Focus();
        }, DispatcherPriority.Input);
    }

    private void Close(bool? result)
    {
        if (active is not { } request || closing)
            return;

        if (result == true && request.Validation?.Invoke() is { Result: false } invalid)
        {
            SetValue(ValidateMessageProperty, invalid.Message);
            return;
        }

        closing = true;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(200), FillBehavior.Stop)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            if (active != request)
                return;

            active = null;
            closing = false;
            SetValue(ObjectContentProperty, null);
            SetValue(ValidateMessageProperty, null);
            if (pending.Count == 0)
            {
                Visibility = Visibility.Collapsed;
                if (previousFocus is UIElement { IsVisible: true, IsEnabled: true } element)
                    Keyboard.Focus(element);
                previousFocus = null;
            }
            BeginAnimation(OpacityProperty, null);
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
        var request = active;
        active = null;
        closing = false;
        previousFocus = null;
        Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null);
        SetValue(ObjectContentProperty, null);
        SetValue(ValidateMessageProperty, null);

        request?.Completion.TrySetResult(null);
        while (pending.TryDequeue(out var queued))
            queued.Completion.TrySetResult(null);
    }

    private void Dialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close(active?.Type == TypeDialogEnum.Confirm ? false : null);
    }

    private void SuccessConfirmEvent(object sender, RoutedEventArgs e) => Close(true);
    private void CancelConfirmEvent(object sender, RoutedEventArgs e) => Close(false);
    private void DefaultEvent(object sender, RoutedEventArgs e) => Close(null);

    private void ButtonOutsideClickEvent(object sender, MouseButtonEventArgs e)
    {
        if (!border.IsMouseOver && active?.Options.AllowMissClick == true)
            Close(active.Type == TypeDialogEnum.Confirm ? false : null);
    }

    private sealed record Request(object Content, TypeDialogEnum Type, Options Options, Func<ValidatePopupDialog>? Validation)
    {
        public TaskCompletionSource<bool?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
