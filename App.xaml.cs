using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using WinCal.Core.Helpers;
using WinCal.Core.Services;
using WinCal.Views;

namespace WinCal;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private PopupWindow? _popup;
    private PopupWindow? _systemPopup;
    private SettingsWindow? _settingsWindow;
    private SystemCalendarInterceptor? _interceptor;
    private bool _hasShownSystemPanel;
    private bool _regionLookupPending;
    private IntPtr _regionLookupHost;
    private int _regionLookupVersion;
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 两个主进程不能同时保存、裁剪和恢复同一个 Shell 窗口。
        _instanceMutex = new Mutex(true, @"Local\WinCal.Main", out bool firstInstance);
        if (!firstInstance)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Unhandled exception: {args.Exception}");
            args.Handled = true;
        };

        // 初始化托盘图标
        _trayIcon = (TaskbarIcon)FindResource("TrayIcon")!;

        // 左键点击弹出日历
        _trayIcon.TrayLeftMouseDown += (s, args) => TogglePopup();

        // 动态生成带今日日期数字的图标
        _trayIcon.Icon = TrayIconGenerator.Generate(DateTime.Today.Day);

        // 更新托盘提示文本
        _trayIcon.ToolTipText = $"miniCal - {DateTime.Now:yyyy年M月d日 dddd}";

        // 应用保存的主题设置
        var settings = AppSettings.Load();
        ThemeHelper.ApplyTheme(settings.ThemeMode);

        // 动态创建右键菜单
        var menu = new ContextMenu();

        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (s, args) => OpenSettings();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (s, args) => Shutdown();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;

        // 将恢复守护的进程启动移出任务栏点击路径；此时尚不读取或修改系统面板。
        NativeCalendarRegionLease.WarmGuardian();

        // 保留原生通知，精确定位并覆盖同一面板中的日历区域。
        bool interceptorOk = false;
        try
        {
            _interceptor = new SystemCalendarInterceptor(Dispatcher);
            _interceptor.Start(ShowPopup);
            interceptorOk = _interceptor.IsHookInstalled;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Interceptor failed: {ex.Message}");
        }

        // 应用托盘图标可见性设置。
        // 安全兜底：隐藏图标后唯一入口是「点击任务栏时钟」，依赖拦截器；
        // 若拦截器未成功挂钩，强制显示图标，否则用户将无法唤出面板也无法进入设置。
        ApplyTrayIconVisibility(settings.ShowTrayIcon, interceptorOk);
        QueueSystemPopupWarmup();
    }

    private PopupWindow GetSystemPopup()
    {
        if (_systemPopup is not null) return _systemPopup;
        var popup = new PopupWindow(prepareSystemOverlay: true);
        popup.CalendarOverlayUnavailable += (_, _) =>
        {
            SystemCalendarPlacementCache.Shared.Invalidate(popup.SystemPanelWindow);
            _hasShownSystemPanel = false;
            ApplyTrayIconVisibility(AppSettings.Load().ShowTrayIcon,
                _interceptor?.IsHookInstalled ?? false);
        };
        _systemPopup = popup;
        return popup;
    }

    private void QueueSystemPopupWarmup()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (Dispatcher.HasShutdownStarted || _regionLookupPending) return;
            try { GetSystemPopup().WarmSystemOverlay(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WinCal overlay warmup: {ex.Message}"); }
        }));
    }

    /// <summary>
    /// 根据设置显示或隐藏托盘图标。
    /// </summary>
    /// <param name="show">用户设置的期望可见性</param>
    /// <param name="interceptorAvailable">拦截器是否可用（决定隐藏后是否还有入口）</param>
    private void ApplyTrayIconVisibility(bool show, bool interceptorAvailable)
    {
        if (_trayIcon == null) return;

        if (!show && !interceptorAvailable)
        {
            // 没有任何替代入口，拒绝隐藏并提示用户
            _trayIcon.Visibility = Visibility.Visible;
            System.Diagnostics.Debug.WriteLine(
                "WinCal: Tray icon forced visible — interceptor unavailable, hiding would leave no entry point.");

            MessageBox.Show(
                "已设置隐藏托盘图标，但系统日历拦截未能启动，隐藏后将无法唤出面板。\n\n" +
                "已临时显示托盘图标。",
                "miniCal", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 安装钩子并不代表当前 Windows 的窗口一定能被识别。
        // 首次成功联动前保留托盘，避免隐藏图标后没有任何可用入口。
        bool canHide = interceptorAvailable && _hasShownSystemPanel;
        _trayIcon.Visibility = show || !canHide ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 供设置窗口调用：保存设置后立即应用托盘图标可见性，无需重启。
    /// </summary>
    public static void RefreshTrayIconVisibility()
    {
        if (Current is not App app) return;

        var settings = AppSettings.Load();
        bool interceptorOk = app._interceptor?.IsHookInstalled ?? false;
        app.ApplyTrayIconVisibility(settings.ShowTrayIcon, interceptorOk);
        // Settings may change the data source; recreate the hidden overlay with fresh services.
        app._regionLookupVersion++;
        app._regionLookupPending = false;
        SystemCalendarPlacementCache.Shared.Invalidate();
        var previous = app._systemPopup;
        app._systemPopup = null;
        previous?.Close();
        if (app._popup == previous) app._popup = null;
        app.QueueSystemPopupWarmup();
    }

    /// <summary>
    /// 显示设置窗口（独立顶层窗口，单例模式）
    /// 可从右键菜单或齿轮按钮调用
    /// </summary>
    public static void ShowSettings()
    {
        try
        {
            var app = (App)Current;
            if (app._settingsWindow != null && app._settingsWindow.IsVisible)
            {
                // 已打开则激活
                app._settingsWindow.Activate();
                return;
            }

            app._settingsWindow = new SettingsWindow();
            app._settingsWindow.Closed += (_, _) => app._settingsWindow = null;
            app._settingsWindow.Show();
            app._settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "wincal_error.log");
            System.IO.File.WriteAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n--- InnerException ---\n{ex.InnerException}");
            MessageBox.Show($"错误已写入桌面 minical_error.log", "miniCal 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSettings()
    {
        // 使用 Dispatcher 延迟打开，避免与右键菜单的弹出窗口冲突
        Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Diagnostics.Debug.WriteLine("WinCal: OpenSettings dispatcher callback executing");
            ShowSettings();
        }));
    }

    /// <summary>
    /// 保留原生通知，用不激活的 miniCal 覆盖原生日历卡片。
    /// </summary>
    private async void ShowPopup(IntPtr systemPanelWindow)
    {
        int requestVersion = 0;
        PopupWindow? candidate = null;
        try
        {
            if (!SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
                return;

            if (_popup != null && _popup.IsVisible && _popup.SystemPanelWindow == systemPanelWindow)
                return;

            if (_regionLookupPending && _regionLookupHost == systemPanelWindow)
                return;

            _regionLookupPending = true;
            _regionLookupHost = systemPanelWindow;
            requestVersion = ++_regionLookupVersion;
            var timing = System.Diagnostics.Stopwatch.StartNew();
            bool reusedPlacement = SystemCalendarPlacementCache.Shared.TryGetPlacement(systemPanelWindow, out var cachedBounds);
            OverlayTiming.Write($"OPEN {requestVersion} cache={reusedPlacement} ms={timing.ElapsedMilliseconds}");
            var boundsQuery = reusedPlacement
                ? Task.FromResult<Rect?>(cachedBounds)
                : SystemCalendarRegionLocator.FindAsync(systemPanelWindow);
            var preparedPopup = GetSystemPopup();
            preparedPopup.PrepareSystemOverlay(systemPanelWindow);
            candidate = preparedPopup;
            var bounds = await boundsQuery;
            // SHOW/UNCLOAK 也可能早于卡片滑入动画结束；连续两次相同才操作其显示区域。
            Rect? previousBounds = null;
            for (int attempt = 0; !reusedPlacement && attempt < 5 && (bounds is null || bounds != previousBounds); attempt++)
            {
                previousBounds = bounds;
                await Task.Delay(100);
                if (requestVersion != _regionLookupVersion ||
                    !SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
                    return;
                bounds = await SystemCalendarRegionLocator.FindAsync(systemPanelWindow);
            }
            if (requestVersion != _regionLookupVersion ||
                !SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
                return;

            if (bounds is null || (!reusedPlacement && bounds != previousBounds))
            {
                SystemCalendarPlacementCache.Shared.Invalidate(systemPanelWindow);
                _hasShownSystemPanel = false;
                ApplyTrayIconVisibility(AppSettings.Load().ShowTrayIcon,
                    _interceptor?.IsHookInstalled ?? false);
                return;
            }

            OverlayTiming.Write($"OPEN {requestVersion} bounds_ready ms={timing.ElapsedMilliseconds}");

            if (_popup is not null && _popup != preparedPopup) _popup.Close();
            var popup = preparedPopup;
            candidate = popup;
            _popup = popup;
            popup.SetSystemCalendarBounds(bounds.Value);
            // 先定位隐藏的自家窗口，避免首帧出现在原生日历左侧。
            popup.UpdatePosition();
            popup.Show();
            popup.UpdatePosition();
            popup.StartFocusTracking();

            // Remember the native shape before opening the calendar hole. Only the first
            // verified layout uses UIA; later opens can reuse it without waiting for animation.
            if (!reusedPlacement)
                SystemCalendarPlacementCache.Shared.Remember(systemPanelWindow, bounds.Value);
            bool enabled = await popup.EnableCalendarReplacementAsync(reusedPlacement);
            OverlayTiming.Write($"OPEN {requestVersion} enabled={enabled} cache={reusedPlacement} ms={timing.ElapsedMilliseconds}");
            if (requestVersion != _regionLookupVersion) return;
            if (popup.IsSystemPanelDismissing) return;
            if (!popup.IsVisible ||
                !SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
            {
                // Closing during preparation is a normal cancellation, not a bad placement.
                popup.Hide();
                return;
            }
            if (!enabled)
            {
                popup.Hide();
                SystemCalendarPlacementCache.Shared.Invalidate(systemPanelWindow);
                _hasShownSystemPanel = false;
                ApplyTrayIconVisibility(AppSettings.Load().ShowTrayIcon,
                    _interceptor?.IsHookInstalled ?? false);
                if (reusedPlacement && SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (requestVersion == _regionLookupVersion &&
                            SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
                            ShowPopup(systemPanelWindow);
                    }));
                return;
            }
            if (SystemCalendarInterceptor.IsSystemPanelVisible(systemPanelWindow))
            {
                _hasShownSystemPanel = true;
                ApplyTrayIconVisibility(AppSettings.Load().ShowTrayIcon,
                    _interceptor?.IsHookInstalled ?? false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: ShowPopup error: {ex}");
            if (requestVersion == _regionLookupVersion)
            {
                SystemCalendarPlacementCache.Shared.Invalidate(systemPanelWindow);
                candidate?.Close();
                if (_popup == candidate) _popup = null;
                if (_systemPopup == candidate) _systemPopup = null;
                _hasShownSystemPanel = false;
                ApplyTrayIconVisibility(AppSettings.Load().ShowTrayIcon,
                    _interceptor?.IsHookInstalled ?? false);
            }
        }
        finally
        {
            if (requestVersion != 0 && requestVersion == _regionLookupVersion)
                _regionLookupPending = false;
        }
    }

    /// <summary>
    /// 切换日历面板显示/隐藏。用于托盘图标点击。
    /// </summary>
    private void TogglePopup()
    {
        // 托盘是用户的显式操作，取消任何尚未完成的原生面板显示请求。
        _regionLookupVersion++;
        _regionLookupPending = false;
        try
        {
            if (_popup == null || !_popup.IsVisible)
            {
                if (_popup == _systemPopup) _popup?.Hide();
                else _popup?.Close();
                _popup = new PopupWindow();
                _popup.Show();
                _popup.UpdatePosition();
                _popup.Activate();
                _popup.StartFocusTracking();
            }
            else
            {
                _popup.Hide();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: TogglePopup error: {ex}");
            _popup = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _regionLookupVersion++;
        NativeCalendarRegionLease.StopWarmGuardian();
        _popup?.Close();
        if (_systemPopup != _popup) _systemPopup?.Close();
        _interceptor?.Dispose();
        _trayIcon?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        base.OnExit(e);
    }
}
