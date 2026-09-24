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
    private SettingsWindow? _settingsWindow;
    private SystemCalendarInterceptor? _interceptor;

    protected override void OnStartup(StartupEventArgs e)
    {
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

        // 启动系统日历拦截器：点击任务栏时钟时替换为我们的面板
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

        _trayIcon.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
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
    /// 显示日历面板（不切换，始终显示）。用于拦截器回调。
    /// </summary>
    private void ShowPopup()
    {
        try
        {
            if (_popup != null && _popup.IsVisible)
            {
                // 已显示则只激活，不重新创建
                _popup.Activate();
                return;
            }

            _popup?.Close();
            _popup = new PopupWindow();
            _popup.Show();
            WindowPositionHelper.PositionNearTaskbar(_popup);
            _popup.Activate();

            // 延迟启动焦点跟踪定时器，给窗口时间获取焦点
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _popup?.StartFocusTracking();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: ShowPopup error: {ex}");
            _popup = null;
        }
    }

    /// <summary>
    /// 切换日历面板显示/隐藏。用于托盘图标点击。
    /// </summary>
    private void TogglePopup()
    {
        try
        {
            if (_popup == null || !_popup.IsVisible)
            {
                _popup?.Close();
                _popup = new PopupWindow();
                _popup.Show();
                WindowPositionHelper.PositionNearTaskbar(_popup);
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
        _interceptor?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
