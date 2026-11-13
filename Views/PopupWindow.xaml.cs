using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinCal.Core.Helpers;
using WinCal.Core.Models;
using WinCal.Core.Services;
using WinCal.ViewModels;
using WinCal.Views.Controls;

namespace WinCal.Views;

public partial class PopupWindow : Window
{
    private readonly CalendarViewModel _calendarViewModel;
    private readonly AppSettings _settings;
    private IntPtr _systemPanelWindow;
    private readonly bool _systemOverlayMode;
    private bool _settingsApplied;
    private int _overlaySession;
    private Rect? _calendarOverlayBounds;
    private bool _updatingPosition;
    private bool _regionRefreshPending;
    private DateTime _lastRegionRefresh;
    private NativeCalendarRegionLease? _calendarRegionLease;
    private CancellationTokenSource? _replacementPreparation;
    private EventDetailWindow? _detailWindow;
    private readonly SystemPanelDismissalState _dismissalState = new();
    private readonly TranslateTransform _dismissalTransform = new();
    private SystemPanelLifecycleObserver? _systemPanelObserver;
    private Task _regionReleaseTask = Task.CompletedTask;

    public IntPtr SystemPanelWindow => _systemPanelWindow;
    public bool IsSystemPanelDismissing => _dismissalState.IsDismissing;
    public event EventHandler? CalendarOverlayUnavailable;

    public PopupWindow(IntPtr systemPanelWindow = default, Rect? calendarOverlayBounds = null,
        bool prepareSystemOverlay = false)
    {
        _systemPanelWindow = systemPanelWindow;
        _systemOverlayMode = prepareSystemOverlay || systemPanelWindow != IntPtr.Zero;
        _calendarOverlayBounds = calendarOverlayBounds;
        // 加载设置
        _settings = AppSettings.Load();

        // 初始化 ViewModel
        _calendarViewModel = new CalendarViewModel();

        InitializeComponent();

        if (_systemOverlayMode)
        {
            WindowActivationHelper.PreventActivation(this);
            RootBorder.RenderTransform = _dismissalTransform;
            // 覆盖原生日历卡片时使用精确尺寸，事件列表占用月历下方的剩余空间。
            SizeToContent = SizeToContent.Manual;
            CalendarEventsRow.Height = new GridLength(1, GridUnitType.Star);
            EventListHeightRow.Height = new GridLength(1, GridUnitType.Star);
        }

        // 设置 DataContext
        DataContext = _calendarViewModel;

        // 绑定事件列表
        EventListControl.ItemsSource = _calendarViewModel.UpcomingEvents;

        // 订阅事件列表变化以更新空状态提示
        _calendarViewModel.UpcomingEvents.CollectionChanged += (_, _) => UpdateNoEventsVisibility();

        Loaded += OnLoaded;

        // 窗口尺寸变化时重新定位（异步加载事件后窗口变高）
        SizeChanged += OnSizeChanged;

        // 在 PopupWindow 级别捕获鼠标事件，显示事件详情
        PreviewMouseMove += OnPreviewMouseMove;

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                ReleaseCalendarRegion();
                CloseDetailWindow();
                StopFocusTracking();
            }
        };
    }

    /// <summary>Build the hidden HWND and date cells without showing or modifying the system panel.</summary>
    public void WarmSystemOverlay()
    {
        if (!_systemOverlayMode || IsVisible) return;
        if (!_settingsApplied)
        {
            ApplySettings();
            _settingsApplied = true;
        }
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        RootBorder.Measure(new Size(Width, MaxHeight));
        RootBorder.Arrange(new Rect(RootBorder.DesiredSize));
        RootBorder.UpdateLayout();
    }

    public void PrepareSystemOverlay(IntPtr host)
    {
        if (!_systemOverlayMode || IsVisible) throw new InvalidOperationException("Overlay is already visible.");
        ReleaseCalendarRegion();
        _systemPanelWindow = host;
        _calendarOverlayBounds = null;
        _calendarViewModel.NavigateToToday();
    }

    public void SetSystemCalendarBounds(Rect bounds)
    {
        _calendarOverlayBounds = bounds;
        UpdatePosition();
    }

    /// <summary>
    /// Shell 使用高于普通 Topmost 的窗口层级。准备独立恢复守护后，仅让出原生日历区域。
    /// </summary>
    public async Task<bool> EnableCalendarReplacementAsync(bool reuseCachedPlacement = false)
    {
        if (_systemPanelWindow == IntPtr.Zero || _calendarOverlayBounds is not Rect bounds || !IsVisible)
            return false;

        int session = _overlaySession;
        IntPtr host = _systemPanelWindow;
        // A rapid reopen waits for the previous DISARM without blocking the dispatcher.
        // A late, cancelled preparation may also add a release while this await is pending.
        Task release;
        do
        {
            release = _regionReleaseTask;
            await release;
        } while (release != _regionReleaseTask);
        if (session != _overlaySession || host != _systemPanelWindow || !IsVisible ||
            _dismissalState.IsDismissing || !SystemCalendarInterceptor.IsSystemPanelVisible(host))
            return false;

        using var preparation = new CancellationTokenSource();
        _replacementPreparation = preparation;
        NativeCalendarRegionLease? lease;
        try
        {
            lease = await NativeCalendarRegionLease.TryCreateAsync(host, bounds, preparation.Token, reuseCachedPlacement);
        }
        finally
        {
            if (_replacementPreparation == preparation) _replacementPreparation = null;
        }
        if (lease is null) return false;
        if (session != _overlaySession || host != _systemPanelWindow ||
            !IsVisible || _dismissalState.IsDismissing || !SystemCalendarInterceptor.IsSystemPanelVisible(host))
        {
            TrackRegionRelease(lease);
            return false;
        }

        _calendarRegionLease = lease;
        // 守护启动期间卡片位置可能改变，最后再按当前已验证的位置同步。
        UpdatePosition();
        return IsVisible && _calendarRegionLease?.IsHealthy == true;
    }

    private void ReleaseCalendarRegion()
    {
        _overlaySession++;
        _regionRefreshPending = false;
        _lastRegionRefresh = default;
        _replacementPreparation?.Cancel();
        var lease = _calendarRegionLease;
        _calendarRegionLease = null;
        if (lease is not null) TrackRegionRelease(lease);
    }

    private void TrackRegionRelease(NativeCalendarRegionLease lease)
    {
        Task release = lease.DisposeAsync();
        _regionReleaseTask = _regionReleaseTask.IsCompleted
            ? release : Task.WhenAll(_regionReleaseTask, release);
    }

    private void CalendarReplacementFailed()
    {
        SystemCalendarPlacementCache.Shared.Invalidate(_systemPanelWindow);
        Hide();
        CalendarOverlayUnavailable?.Invoke(this, EventArgs.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_settingsApplied)
        {
            ApplySettings();
            _settingsApplied = true;
        }
        UpdateNoEventsVisibility();
    }

    /// <summary>
    /// 窗口尺寸变化后重新定位，确保不超出屏幕底部
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePosition();
    }

    public void UpdatePosition()
    {
        if (_updatingPosition || _dismissalState.IsDismissing) return;
        _updatingPosition = true;
        try
        {
            if (_systemOverlayMode)
            {
                if (_calendarOverlayBounds is Rect bounds)
                {
                    WindowPositionHelper.PositionOverSystemCalendar(this, bounds);
                    if (_calendarRegionLease is not null && !_calendarRegionLease.Update(bounds))
                        CalendarReplacementFailed();
                }
            }
            else
                WindowPositionHelper.PositionNearTaskbar(this);

            if (_detailWindow?.IsVisible == true)
                _detailWindow.UpdatePosition(this);
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    /// <summary>
    /// 应用用户设置到界面
    /// </summary>
    private void ApplySettings()
    {
        // #1 主题
        ThemeHelper.ApplyTheme(_settings.ThemeMode);

        // #2 字体大小偏移
        ApplyFontSizeOffset();

        // #6 周起始日
        _calendarViewModel.WeekStartDay = _settings.WeekStartDay == Core.Services.WeekStartDay.Monday ? 1 : 0;
        Calendar.UpdateWeekHeaders(_calendarViewModel.WeekStartDay);
        _ = _calendarViewModel.RefreshDataAsync();

    }

    /// <summary>
    /// 应用字体大小偏移（基于基准字号 + offset）
    /// </summary>
    private void ApplyFontSizeOffset()
    {
        var offset = _settings.FontSizeOffset;
        if (offset == 0) return;

        // 用 ScaleTransform 缩放整个面板（因为 XAML 中大量硬编码 FontSize）
        // offset 范围 -2~+2，每级缩放 6%
        var scale = 1.0 + offset * 0.06;
        RootBorder.LayoutTransform = new ScaleTransform(scale, scale);
    }

    // Win32 API 用于焦点检测
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    // 焦点丢失检测
    private DispatcherTimer? _focusTimer;
    private IntPtr _initialForegroundWindow; // 弹出时的前台窗口

    /// <summary>
    /// 启动焦点丢失检测
    /// 策略：记录弹出时的前台窗口，只有当前台窗口变为其他窗口时才关闭
    /// </summary>
    public void StartFocusTracking()
    {
        StopFocusTracking();

        // 记录当前前台窗口（通常是桌面/任务栏/ShellExperienceHost）
        _initialForegroundWindow = GetForegroundWindow();
        // A cached placement can be displayed while Shell is still animating. Observe the
        // settled layout in the background rather than moving to an intermediate UIA rectangle.
        _lastRegionRefresh = DateTime.UtcNow;

        _focusTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(_systemOverlayMode ? 32 : 200)
        };
        _focusTimer.Tick += OnFocusCheckTick;
        _focusTimer.Start();
        if (_systemPanelWindow != IntPtr.Zero)
        {
            _systemPanelObserver = new SystemPanelLifecycleObserver(
                _systemPanelWindow, Dispatcher, () =>
                {
                    if (IsVisible) CheckSystemPanelDismissal();
                });
            CheckSystemPanelDismissal();
        }
    }

    private void StopFocusTracking()
    {
        _systemPanelObserver?.Dispose();
        _systemPanelObserver = null;
        _dismissalState.Reset();
        CancelDismissalAnimation();
        if (_focusTimer != null)
        {
            _focusTimer.Stop();
            _focusTimer.Tick -= OnFocusCheckTick;
            _focusTimer = null;
        }
    }

    private bool CheckSystemPanelDismissal()
    {
        if (!IsVisible || _systemPanelWindow == IntPtr.Zero) return true;
        IntPtr foreground = GetForegroundWindow();
        bool nativeForeground = foreground == _systemPanelWindow ||
            (foreground != IntPtr.Zero && GetWindow(foreground, GW_OWNER) == _systemPanelWindow);
        var action = _dismissalState.Observe(
            SystemCalendarInterceptor.IsSystemPanelVisible(_systemPanelWindow),
            nativeForeground, foreground != IntPtr.Zero, SystemParameters.ClientAreaAnimation);
        switch (action)
        {
            case SystemPanelDismissalAction.BeginAnimation:
                BeginDismissalAnimation();
                break;
            case SystemPanelDismissalAction.CancelAnimation:
                CancelDismissalAnimation();
                break;
            case SystemPanelDismissalAction.Hide:
                OverlayTiming.Write("CLOSE native_hidden_or_motion_disabled");
                Hide();
                return true;
        }
        return _dismissalState.IsDismissing;
    }

    private void BeginDismissalAnimation()
    {
        int generation = _dismissalState.Generation;
        CloseDetailWindow();
        RootBorder.IsHitTestVisible = false;
        OverlayTiming.Write($"CLOSE animation_begin generation={generation}");
        // Animate our content inside the fixed, verified calendar rectangle. Neither the
        // notification region nor the native region is changed on animation frames.
        var animation = new DoubleAnimation(0, Math.Max(ActualWidth, Width),
            TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            if (!IsVisible || generation != _dismissalState.Generation) return;
            // A foreground notification may be queued behind the animation callback.
            if (CheckSystemPanelDismissal() && _dismissalState.Complete(generation))
            {
                OverlayTiming.Write($"CLOSE animation_complete generation={generation}");
                // Keep the now-transparent HWND and calendar hole until Shell hides. This
                // prevents its own calendar from flashing during the remainder of the slide,
                // and lets a rapid foreground return cancel dismissal without reopening.
            }
        };
        _dismissalTransform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void CancelDismissalAnimation()
    {
        _dismissalTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _dismissalTransform.X = 0;
        RootBorder.IsHitTestVisible = true;
    }

    private async void OnFocusCheckTick(object? sender, EventArgs e)
    {
        if (_systemPanelWindow != IntPtr.Zero)
        {
            if (CheckSystemPanelDismissal()) return;
            if (_calendarRegionLease is not null && !_calendarRegionLease.IsHealthy)
            {
                CalendarReplacementFailed();
                return;
            }
            // 覆盖模式保留通知中心的焦点和显示生命周期。
            if (!SystemCalendarInterceptor.IsSystemPanelVisible(_systemPanelWindow))
            {
                Hide();
                return;
            }

            // Closing animation can remain visible briefly after focus returns to the clock.
            // Its partial UIA bounds must not overwrite a verified placement.
            if (GetForegroundWindow() != _systemPanelWindow) return;

            if (_regionRefreshPending || DateTime.UtcNow - _lastRegionRefresh < TimeSpan.FromMilliseconds(500))
                return;

            _regionRefreshPending = true;
            _lastRegionRefresh = DateTime.UtcNow;
            int session = _overlaySession;
            IntPtr host = _systemPanelWindow;
            try
            {
                var bounds = await SystemCalendarRegionLocator.FindAsync(host);
                if (session != _overlaySession || host != _systemPanelWindow ||
                    !IsVisible || _dismissalState.IsDismissing || !SystemCalendarInterceptor.IsSystemPanelVisible(host) ||
                    GetForegroundWindow() != host)
                    return;

                if (bounds != _calendarOverlayBounds)
                {
                    // Recheck a changed/absent card after a frame-sized interval. A moving
                    // rectangle belongs to Shell animation, not a new persistent layout.
                    await Task.Delay(50);
                    if (session != _overlaySession || host != _systemPanelWindow ||
                        !IsVisible || _dismissalState.IsDismissing || !SystemCalendarInterceptor.IsSystemPanelVisible(host) ||
                        GetForegroundWindow() != host)
                        return;
                    var confirmedBounds = await SystemCalendarRegionLocator.FindAsync(host);
                    if (session != _overlaySession || host != _systemPanelWindow ||
                        !IsVisible || _dismissalState.IsDismissing || !SystemCalendarInterceptor.IsSystemPanelVisible(host) ||
                        GetForegroundWindow() != host || confirmedBounds != bounds)
                        return;
                }

                if (bounds is null)
                {
                    OverlayTiming.Write("BACKGROUND bounds_null");
                    // 无法确认边界时立即让出系统界面，禁止猜测坐标遮挡通知。
                    CalendarReplacementFailed();
                    return;
                }

                if (_calendarOverlayBounds != bounds)
                {
                    OverlayTiming.Write($"BACKGROUND bounds_changed old={_calendarOverlayBounds} new={bounds}");
                    SystemCalendarPlacementCache.Shared.Invalidate(host);
                }
                _calendarOverlayBounds = bounds;
                UpdatePosition();
            }
            finally
            {
                if (session == _overlaySession) _regionRefreshPending = false;
            }
            return;
        }

        var foreground = GetForegroundWindow();
        var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // 前台是我们自己 → 正常，不做任何操作
        if (foreground == myHwnd)
            return;

        // 前台是我们的子窗口（EventDetail）→ 正常
        var owner = GetWindow(foreground, GW_OWNER);
        if (owner == myHwnd)
            return;

        // 前台仍然是原来的窗口（桌面/任务栏）→ 用户还没点击其他地方，保持打开
        if (foreground == _initialForegroundWindow)
            return;

        // 前台变成了一个新的窗口 → 用户切换了焦点，关闭弹窗
        CloseDetailWindow();
        Hide();
        StopFocusTracking();
    }

    /// <summary>
    /// 失焦自动关闭（同时关闭详情窗口）
    /// 作为定时器检测的补充
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_systemOverlayMode)
            return;
        CloseDetailWindow();
        Hide();
        StopFocusTracking();
    }

    /// <summary>
    /// 鼠标移动时检测是否悬停在 EventItem 上
    /// </summary>
    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dismissalState.IsDismissing) return;
        var pos = e.GetPosition(this);
        var hitResult = VisualTreeHelper.HitTest(this, pos);
        if (hitResult == null)
        {
            CloseDetailWindow();
            return;
        }

        // 忽略齿轮按钮区域
        if (IsMouseOverSettingsButton(hitResult.VisualHit))
        {
            CloseDetailWindow();
            return;
        }

        var eventItem = FindAncestor<EventItem>(hitResult.VisualHit);
        if (eventItem?.EventData is CalendarEvent evt)
        {
            ShowDetailWindow(evt);
        }
        else
        {
            CloseDetailWindow();
        }
    }

    /// <summary>
    /// 判断鼠标是否在齿轮按钮上
    /// </summary>
    private bool IsMouseOverSettingsButton(DependencyObject visualHit)
    {
        var btn = FindAncestor<Button>(visualHit);
        return btn == SettingsButton;
    }

    /// <summary>
    /// 显示事件详情窗口（在主面板左侧）
    /// </summary>
    private void ShowDetailWindow(CalendarEvent evt)
    {
        if (_detailWindow == null)
        {
            _detailWindow = new EventDetailWindow();
            _detailWindow.ShowActivated = false; // 不抢焦点
        }

        _detailWindow.ShowEvent(evt, this);
    }

    /// <summary>
    /// 关闭详情窗口
    /// </summary>
    private void CloseDetailWindow()
    {
        if (_detailWindow != null)
        {
            _detailWindow.Hide();
        }
    }

    /// <summary>
    /// 隐藏时同时关闭详情
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        ReleaseCalendarRegion();
        StopFocusTracking();
        _detailWindow?.Close();
        _detailWindow = null;
        base.OnClosed(e);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T ancestor)
                return ancestor;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    /// 更新事件列表区域的可见性（有事件显示列表，无事件显示占位文案）
    /// </summary>
    private void UpdateNoEventsVisibility()
    {
        var hasEvents = _calendarViewModel.UpcomingEvents.Count > 0;
        EventScrollViewer.Visibility = hasEvents ? Visibility.Visible : Visibility.Collapsed;
        NoEventsText.Visibility = hasEvents ? Visibility.Collapsed : Visibility.Visible;
        EndMarker.Visibility = hasEvents ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 点击刷新按钮强制刷新日历数据
    /// </summary>
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            await _calendarViewModel.ForceRefreshAsync();
            UpdateNoEventsVisibility();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 点击齿轮图标打开设置窗口（模态）
    /// </summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        CloseDetailWindow();
        Hide(); // 先关闭弹窗，再打开独立设置窗口
        App.ShowSettings();
    }
}
