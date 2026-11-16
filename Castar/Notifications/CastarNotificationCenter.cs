using Castar.Api;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Castar.Notifications;

internal sealed class CastarNotificationCenter : IDisposable
{
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan GroupingWindow = TimeSpan.FromSeconds(3);

    private readonly Window mainWindow;
    private readonly Action<string, Guid?, DateTime?> openEvent;
    private readonly Func<Guid, Task<byte[]?>> loadAvatar;
    private readonly List<Notice> visible = [];
    private readonly List<Notice> pending = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer expiration;
    private NotificationToastWindow? window;
    private NotificationCorner corner = NotificationCorner.BottomRight;
    private int maxVisible = 3;
    private TimeSpan? pausedAt;
    private bool layoutPending;
    private bool disposed;

    internal CastarNotificationCenter(Window mainWindow, Action<string, Guid?, DateTime?> openEvent,
        Func<Guid, Task<byte[]?>> loadAvatar)
    {
        this.mainWindow = mainWindow;
        this.openEvent = openEvent;
        this.loadAvatar = loadAvatar;
        expiration = new DispatcherTimer(DispatcherPriority.Background, mainWindow.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        expiration.Tick += Expiration_Tick;
        mainWindow.LocationChanged += MainWindow_LayoutChanged;
        mainWindow.StateChanged += MainWindow_LayoutChanged;
        mainWindow.DpiChanged += Window_DpiChanged;
        mainWindow.Closed += MainWindow_Closed;
        SystemParameters.StaticPropertyChanged += SystemParameters_PropertyChanged;
    }

    internal void Configure(NotificationCorner corner, int maxVisible)
    {
        if (disposed)
            return;

        this.corner = Enum.IsDefined(corner) ? corner : NotificationCorner.BottomRight;
        this.maxVisible = Math.Clamp(maxVisible, 1, 5);
        Arrange();
    }

    internal void Publish(BudgetEventDto change)
    {
        if (disposed || !change.Created || change.Kind is not (BudgetEvents.Purchase or BudgetEvents.Income))
            return;

        var now = clock.Elapsed;
        var group = visible.Concat(pending).Where(notice => notice.Kind == change.Kind && now - notice.LastArrival <= GroupingWindow)
            .MaxBy(notice => notice.LastArrival);
        if (group is null)
        {
            group = new Notice(change.Kind);
            pending.Add(group);
        }

        group.Append(Math.Max(1, change.CreatedCount), change.EntityId, change.AddedOn, change.Preview, now);
        if (visible.Contains(group))
            group.Start(now);
        Arrange();
    }

    internal void Clear()
    {
        visible.Clear();
        pending.Clear();
        expiration.Stop();
        pausedAt = null;
        if (window is { } toast)
        {
            toast.Hide();
            toast.ShowNotices([], false);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        mainWindow.LocationChanged -= MainWindow_LayoutChanged;
        mainWindow.StateChanged -= MainWindow_LayoutChanged;
        mainWindow.DpiChanged -= Window_DpiChanged;
        mainWindow.Closed -= MainWindow_Closed;
        SystemParameters.StaticPropertyChanged -= SystemParameters_PropertyChanged;
        expiration.Tick -= Expiration_Tick;
        Clear();
        window?.Close();
        window = null;
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => Dispose();

    private void MainWindow_LayoutChanged(object? sender, EventArgs e) => ScheduleArrange();

    private void Window_DpiChanged(object sender, DpiChangedEventArgs e) => ScheduleArrange();

    private void SystemParameters_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            ScheduleArrange();
    }

    private void ScheduleArrange()
    {
        if (!mainWindow.Dispatcher.CheckAccess())
        {
            _ = mainWindow.Dispatcher.InvokeAsync(ScheduleArrange);
            return;
        }
        if (disposed || layoutPending || window?.IsVisible != true)
            return;

        layoutPending = true;
        _ = mainWindow.Dispatcher.InvokeAsync(() =>
        {
            layoutPending = false;
            if (!disposed && window?.IsVisible == true)
                Arrange();
        }, DispatcherPriority.Loaded);
    }

    private NotificationToastWindow CreateWindow()
    {
        var toast = new NotificationToastWindow();
        toast.SourceInitialized += (_, _) => NotificationPlacement.ProtectFocus(toast);
        toast.DpiChanged += Window_DpiChanged;
        toast.MouseEnter += Toast_MouseEnter;
        toast.MouseLeave += Toast_MouseLeave;
        toast.OpenRequested += Open;
        toast.DismissRequested += Dismiss;
        toast.DismissAllRequested += Clear;
        return toast;
    }

    private void Arrange()
    {
        var count = visible.Count + pending.Count;
        if (count == 0)
        {
            Clear();
            return;
        }

        using var dpi = new NotificationPlacement.PerMonitorScope();
        var toast = window ??= CreateWindow();
        var handle = new WindowInteropHelper(toast).EnsureHandle();
        var area = NotificationPlacement.FindWorkArea(mainWindow, handle);
        var canDismissAll = count > 1;
        var footer = canDismissAll ? NotificationToastWindow.FooterHeight : 0;
        var verticalSpace = area.HeightDip - NotificationPlacement.MarginDip * 2 - footer;
        var capacity = Math.Clamp((int)(verticalSpace / NotificationToastWindow.CardSlotHeight), 1, maxVisible);
        FillVisible(capacity);

        var top = corner is NotificationCorner.TopLeft or NotificationCorner.TopRight;
        var cards = visible.ToArray();
        if (top)
            Array.Reverse(cards);
        toast.ShowNotices(cards, canDismissAll);
        toast.Width = Math.Clamp(area.WidthDip - NotificationPlacement.MarginDip * 2, 1, NotificationToastWindow.PreferredWidth);
        NotificationPlacement.Position(handle, area, corner, toast.Width, toast.Height);
        if (!toast.IsVisible)
        {
            toast.Show();
            NotificationPlacement.Position(handle, area, corner, toast.Width, toast.Height);
        }
        expiration.Start();
        foreach (var notice in visible)
            if (notice.BeginAvatarLoad() is { } userId)
                _ = LoadAvatarAsync(notice, userId);
    }

    private async Task LoadAvatarAsync(Notice notice, Guid userId)
    {
        try
        {
            var image = await loadAvatar(userId);
            if (!disposed && (visible.Contains(notice) || pending.Contains(notice)))
                notice.SetAvatar(userId, image);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ArgumentException)
        {
            // Keep the initials when the image cannot be loaded.
        }
    }

    private void FillVisible(int capacity)
    {
        if (visible.Count > capacity)
        {
            var overflow = visible.GetRange(capacity, visible.Count - capacity);
            visible.RemoveRange(capacity, overflow.Count);
            pending.InsertRange(0, overflow);
        }

        var now = clock.Elapsed;
        while (visible.Count < capacity && pending.Count > 0)
        {
            var next = pending[0];
            pending.RemoveAt(0);
            next.Start(now);
            visible.Add(next);
        }
    }

    private void Toast_MouseEnter(object sender, MouseEventArgs e) => pausedAt ??= clock.Elapsed;

    private void Toast_MouseLeave(object sender, MouseEventArgs e)
    {
        if (pausedAt is not { } paused)
            return;

        var now = clock.Elapsed;
        foreach (var notice in visible)
            notice.ExtendAfterPause(paused, now);
        pausedAt = null;
    }

    private void Expiration_Tick(object? sender, EventArgs e)
    {
        if (pausedAt.HasValue)
            return;

        var now = clock.Elapsed;
        if (visible.RemoveAll(notice => notice.ExpiresAt <= now) > 0)
            Arrange();
    }

    private void Dismiss(Notice notice)
    {
        if (visible.Remove(notice))
            Arrange();
    }

    private void Open(Notice notice)
    {
        if (!visible.Remove(notice))
            return;

        Arrange();
        openEvent(notice.Kind, notice.EntityId, notice.AddedOn);
    }

    internal sealed class Notice(string kind) : INotifyPropertyChanged
    {
        public string Kind { get; } = kind;
        public BudgetOperationPreviewDto? Preview { get; private set; }
        public string Title => Preview is { } preview
            ? BudgetNotificationText.Title(preview) : BudgetNotificationText.Format(Kind, Count, null);
        public string Details => Preview is { } preview ? BudgetNotificationText.Details(preview) : "";
        public string GroupLabel => Preview is not null
            ? BudgetNotificationText.Format(Kind, Count, null) : "";
        public string AccessibleText => BudgetNotificationText.Format(Kind, Count, Preview);
        public byte[]? Avatar { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        private Guid? avatarRequestedFor;
        private int Count { get; set; }
        internal Guid? EntityId { get; private set; }
        internal DateTime? AddedOn { get; private set; }
        internal TimeSpan LastArrival { get; private set; }
        private TimeSpan ShownAt { get; set; }
        internal TimeSpan ExpiresAt { get; private set; }

        internal void Append(int count, Guid? entityId, DateTime? addedOn, BudgetOperationPreviewDto? preview, TimeSpan now)
        {
            Count = (int)Math.Min(int.MaxValue, (long)Count + count);
            EntityId = entityId == Guid.Empty ? null : entityId;
            AddedOn = addedOn;
            if (preview?.UserId != Preview?.UserId || preview?.HasAvatar != Preview?.HasAvatar)
            {
                avatarRequestedFor = null;
                Avatar = null;
            }
            Preview = preview;
            LastArrival = now;
        }

        internal Guid? BeginAvatarLoad()
        {
            if (Preview is not { HasAvatar: true } preview || preview.UserId == avatarRequestedFor)
                return null;
            avatarRequestedFor = preview.UserId;
            return preview.UserId;
        }

        internal void SetAvatar(Guid userId, byte[]? image)
        {
            if (Preview is not { HasAvatar: true } preview || preview.UserId != userId)
                return;
            Avatar = image;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));
        }

        internal void Start(TimeSpan now)
        {
            ShownAt = now;
            ExpiresAt = now + DisplayDuration;
        }

        internal void ExtendAfterPause(TimeSpan paused, TimeSpan now) =>
            ExpiresAt += now - (ShownAt > paused ? ShownAt : paused);
    }
}
