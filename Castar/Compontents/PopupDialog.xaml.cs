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
    private readonly Stack<(Request Request, IInputElement? Focus)> parents = new();
    private Window? owner;
    private IInputElement? previousFocus;
    private PopupDialog? retainedChild;
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
        public bool EscapeReturnsNull { get; set; }
        public DefaultButtonClass DefaultButton { get; } = new();
        public SuccessConfirmButtonClass SuccessButton { get; } = new();
        public CancelConfirmButtonClass CancelButton { get; } = new();
        public SolidColorBrush Background { get; set; } =
            (SolidColorBrush)Application.Current.FindResource("Main.Background.lvl3");
        public string? Title { get; set; }
        public Geometry? EditorIcon { get; set; }

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

    internal static Task<bool?> ShowNested(UIElement parent, object content, Func<Options, Options> options,
        Func<ValidatePopupDialog>? validation = null, bool retainParent = false)
    {
        if (Application.Current?.MainWindow is not MainWindow window
            || window.MainDialog.Topmost is not { ActiveDialog: { } active, closing: false } dialog
            || !dialog.DialogContent.IsAncestorOf(parent))
            return Task.FromResult<bool?>(null);

        var request = new Request(content, TypeDialogEnum.Confirm, options(new Options()), validation);
        if (retainParent)
            return dialog.ShowBesideParentAsync(request);

        dialog.parents.Push((active, Keyboard.FocusedElement));
        dialog.Present(request);
        return request.Completion.Task;
    }

    internal PopupDialog Topmost => retainedChild?.Topmost ?? this;

    private async Task<bool?> ShowBesideParentAsync(Request request)
    {
        var focus = Keyboard.FocusedElement;
        var child = new PopupDialog { HorizontalAlignment = HorizontalAlignment.Left };
        var wasDefault = DefaultButton.IsDefault;
        retainedChild = child;
        PresentationLayer.IsHitTestVisible = false;
        PresentationLayer.HorizontalAlignment = HorizontalAlignment.Right;
        DefaultButton.IsDefault = false;
        RetainedGap.Width = new GridLength(12);
        RetainedColumn.Width = new GridLength(1, GridUnitType.Star);
        RetainedDialogLayer.Children.Add(child);
        child.pending.Enqueue(request);
        child.ShowNext();
        try
        {
            return await request.Completion.Task;
        }
        finally
        {
            retainedChild = null;
            RetainedDialogLayer.Children.Remove(child);
            RetainedGap.Width = new GridLength(0);
            RetainedColumn.Width = new GridLength(0);
            PresentationLayer.IsHitTestVisible = true;
            PresentationLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            DefaultButton.IsDefault = wasDefault;
            if (focus is UIElement { IsVisible: true, IsEnabled: true } element && IsAncestorOf(element))
                Keyboard.Focus(element);
        }
    }

    private void ShowNext()
    {
        if (closing || ActiveDialog is not null || !pending.TryDequeue(out var request))
            return;

        if (!IsVisible && Window.GetWindow(this) is { } window)
        {
            previousFocus = Keyboard.FocusedElement is UIElement focused && window.IsAncestorOf(focused)
                ? focused : FocusManager.GetFocusedElement(window);
            owner = window;
            owner.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(KeepDialogFocus), true);
        }

        Present(request);
    }

    private void Present(Request request, IInputElement? restoreFocus = null)
    {
        SetValue(ActiveDialogPropertyKey, request);
        Visibility = Visibility.Visible;

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (ActiveDialog != request || closing || !IsVisible)
                return;
            if (restoreFocus is UIElement { IsVisible: true, IsEnabled: true } element && IsAncestorOf(element)
                && Keyboard.Focus(element) == element)
                return;
            if (request.Content is UIElement editor && FieldTabNavigation.MoveFocus(editor, backwards: false, fromStart: true))
                return;
            (request.Type == TypeDialogEnum.Default ? DefaultButton : CancelButton).Focus();
        }, DispatcherPriority.Input);
    }

    private void KeepDialogFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var activeLayer = Topmost;
        if (IsVisible && ActiveDialog is not null && e.NewFocus is Visual target
            && PresentationSource.FromVisual(target) == PresentationSource.FromVisual(this)
            && target != activeLayer && !activeLayer.IsAncestorOf(target))
            e.Handled = true;
    }

    private void ReleaseDialogFocus()
    {
        owner?.RemoveHandler(Keyboard.PreviewGotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(KeepDialogFocus));
        owner = null;
    }

    private void Close(bool? result)
    {
        if (retainedChild is { } child)
        {
            child.Close(result);
            return;
        }

        if (ActiveDialog is not { } request || closing)
            return;

        if (result == true && request.Validation?.Invoke() is { Result: false } invalid)
        {
            request.Warning = invalid.Message;
            return;
        }

        closing = true;
        if (parents.TryPop(out var parent))
        {
            Present(parent.Request, parent.Focus);
            closing = false;
            request.Completion.TrySetResult(result);
            return;
        }

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
                ReleaseDialogFocus();
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
        retainedChild?.CancelPending();
        var request = ActiveDialog;
        closing = true;
        SetValue(ActiveDialogPropertyKey, null);
        ReleaseDialogFocus();
        previousFocus = null;
        Visibility = Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null);

        request?.Completion.TrySetResult(null);
        while (parents.TryPop(out var parent))
            parent.Request.Completion.TrySetResult(null);
        while (pending.TryDequeue(out var queued))
            queued.Completion.TrySetResult(null);
        closing = false;
    }

    private void Dialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close(ActiveDialog is { Type: TypeDialogEnum.Confirm, EscapeReturnsNull: false } ? false : null);
    }

    private void SuccessConfirmEvent(object sender, RoutedEventArgs e) => Close(true);
    private void CancelConfirmEvent(object sender, RoutedEventArgs e) => Close(false);
    private void DefaultEvent(object sender, RoutedEventArgs e) => Close(null);

    private void ButtonOutsideClickEvent(object sender, MouseButtonEventArgs e)
    {
        if (retainedChild is null && !border.IsMouseOver && ActiveDialog is { AllowMissClick: true } request)
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
            EditorIcon = options.EditorIcon;
            Background = options.Background;
            DefaultButton = new(options.DefaultButton.DefaultButtonText, options.DefaultButton.DefaultButtonForeground);
            ConfirmButton = new(options.SuccessButton.SuccessConfirmButtonText, options.SuccessButton.SuccessConfirmButtonForeground);
            CancelButton = new(options.CancelButton.CancelConfirmButtonText, options.CancelButton.CancelConfirmButtonForeground);
            AllowMissClick = options.AllowMissClick;
            EscapeReturnsNull = options.EscapeReturnsNull;
            Validation = validation;
        }

        public object Content { get; }
        public TypeDialogEnum Type { get; }
        public string? Title { get; }
        public Geometry? EditorIcon { get; }
        public bool HasEditorHeader => EditorIcon is not null;
        public SolidColorBrush Background { get; }
        public ButtonPresentation DefaultButton { get; }
        public ButtonPresentation ConfirmButton { get; }
        public ButtonPresentation CancelButton { get; }
        public string? Warning { get => warning; internal set => SetField(ref warning, value); }
        internal bool AllowMissClick { get; }
        internal bool EscapeReturnsNull { get; }
        internal Func<ValidatePopupDialog>? Validation { get; }
        internal TaskCompletionSource<bool?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed record ButtonPresentation(string Text, Brush Foreground);
}
