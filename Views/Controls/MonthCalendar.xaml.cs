using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCal.ViewModels;

namespace WinCal.Views.Controls;

public partial class MonthCalendar : UserControl
{
    public MonthCalendar()
    {
        InitializeComponent();

        PrevButton.Click += (s, e) =>
        {
            if (DataContext is CalendarViewModel vm)
                vm.NavigatePreviousMonth();
        };

        NextButton.Click += (s, e) =>
        {
            if (DataContext is CalendarViewModel vm)
                vm.NavigateNextMonth();
        };

        TodayButton.Click += (s, e) =>
        {
            if (DataContext is CalendarViewModel vm)
                vm.NavigateToToday();
        };

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;

        // 使用 PreviewMouseLeftButtonUp 在 MonthCalendar 级别捕获点击
        // 这比在单个 DayCell 上绑定事件更可靠
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;

        // 滚轮翻月：只绑在 MonthCalendar 上，不影响下方事件列表的滚动
        MouseWheel += OnMouseWheel;
    }

    /// <summary>
    /// 滚轮切换月份：上滚看上一月，下滚看下一月。
    /// 高精度触控板会产生大量小增量事件，累加到一个整格（120）才翻一页，
    /// 否则轻轻一划会连翻数月。
    /// </summary>
    private int _wheelAccumulator;
    private const int WheelStep = 120; // 一个标准滚轮刻度

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not CalendarViewModel vm) return;
        if (e.Delta == 0) return;

        // 反向滚动时先清零，避免上下来回滚时残留的增量导致迟滞
        if (_wheelAccumulator != 0 && Math.Sign(e.Delta) != Math.Sign(_wheelAccumulator))
            _wheelAccumulator = 0;

        _wheelAccumulator += e.Delta;

        while (Math.Abs(_wheelAccumulator) >= WheelStep)
        {
            if (_wheelAccumulator > 0)
            {
                vm.NavigatePreviousMonth();   // 上滚 → 上一月
                _wheelAccumulator -= WheelStep;
            }
            else
            {
                vm.NavigateNextMonth();       // 下滚 → 下一月
                _wheelAccumulator += WheelStep;
            }
        }

        // 标记已处理，防止事件冒泡到外层 ScrollViewer 造成面板一起滚动
        e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is CalendarViewModel vm)
        {
            CalendarGrid.ItemsSource = vm.CalendarDays;
            UpdateWeekHeaders(vm.WeekStartDay);
            vm.SelectDate(DateTime.Today);
            HookCollectionChanged(vm);
            ScheduleSyncSelection(vm);
        }
    }

    private CalendarViewModel? _hookedViewModel;

    /// <summary>
    /// 监听网格重建：月份切换 / 数据刷新会重建 CalendarDays，
    /// 新生成的 DayCell 需要重新应用选中高亮。
    /// </summary>
    private void HookCollectionChanged(CalendarViewModel vm)
    {
        if (ReferenceEquals(_hookedViewModel, vm)) return;

        if (_hookedViewModel != null)
            _hookedViewModel.CalendarDays.CollectionChanged -= OnCalendarDaysChanged;

        _hookedViewModel = vm;
        vm.CalendarDays.CollectionChanged += OnCalendarDaysChanged;
    }

    private void OnCalendarDaysChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_hookedViewModel != null)
            ScheduleSyncSelection(_hookedViewModel);
    }

    /// <summary>
    /// 延到布局完成后再同步高亮：此刻 ItemsControl 才生成好新的 DayCell 容器
    /// </summary>
    private void ScheduleSyncSelection(CalendarViewModel vm)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SyncSelection(vm);
            RefreshHoverUnderMouse();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 网格重建后刷新鼠标下方格子的 hover 状态。
    /// 翻月会销毁并重建全部 42 个 DayCell，被移除的旧实例收不到 MouseLeave，
    /// 新实例的 hover 默认为 false —— 结果是鼠标明明停在格子上却没有高亮，
    /// 必须挪动鼠标才恢复。这里用命中测试主动补上。
    /// </summary>
    private void RefreshHoverUnderMouse()
    {
        if (!IsMouseOver) return;

        var pos = Mouse.GetPosition(this);
        var hit = VisualTreeHelper.HitTest(this, pos);
        var cell = hit == null ? null : FindAncestor<DayCell>(hit.VisualHit);
        cell?.SetHovered(true);
    }

    /// <summary>
    /// 更新星期标题行（支持周一起始或周日起始）
    /// </summary>
    public void UpdateWeekHeaders(int weekStartDay)
    {
        // 周一: 一二三四五六日, 周日: 日一二三四五六
        string[] monStart = { "一", "二", "三", "四", "五", "六", "日" };
        string[] sunStart = { "日", "一", "二", "三", "四", "五", "六" };
        var headers = weekStartDay == 1 ? monStart : sunStart;

        var textBlocks = new[] { H0, H1, H2, H3, H4, H5, H6 };
        for (int i = 0; i < 7; i++)
        {
            textBlocks[i].Text = headers[i];
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is CalendarViewModel vm)
        {
            CalendarGrid.ItemsSource = vm.CalendarDays;
            HookCollectionChanged(vm);
            ScheduleSyncSelection(vm);
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 从点击位置向上查找 DayCell
        var hitResult = VisualTreeHelper.HitTest(this, e.GetPosition(this));
        if (hitResult == null) return;

        var dayCell = FindAncestor<DayCell>(hitResult.VisualHit);
        if (dayCell?.DayData is { } dayData && DataContext is CalendarViewModel vm)
        {
            // 更新 ViewModel（选中高亮由 SyncSelection 依据 SelectedDate 统一刷新，
            // 这样月份切换重建格子后高亮也不会丢）
            vm.SelectDate(dayData.Date);
            SyncSelection(vm);
        }
    }

    /// <summary>
    /// 依据 ViewModel 的 SelectedDate 刷新所有 DayCell 的选中态。
    /// 网格重建（月份切换 / 数据刷新）后也需要调用，否则高亮会丢失。
    /// </summary>
    private void SyncSelection(CalendarViewModel vm)
    {
        ApplySelection(CalendarGrid, vm.SelectedDate.Date);
    }

    private static void ApplySelection(DependencyObject parent, DateTime selectedDate)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DayCell cell)
            {
                cell.IsSelected = cell.DayData?.Date.Date == selectedDate;
            }
            else
            {
                ApplySelection(child, selectedDate);
            }
        }
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

}
