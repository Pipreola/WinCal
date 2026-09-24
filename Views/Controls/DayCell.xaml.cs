using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCal.Core.Models;

namespace WinCal.Views.Controls;

public partial class DayCell : UserControl
{
    private bool _isHovered;

    public DayCell()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 绑定的日历日数据
    /// </summary>
    public static readonly DependencyProperty DayDataProperty =
        DependencyProperty.Register(
            nameof(DayData), typeof(CalendarDay), typeof(DayCell),
            new PropertyMetadata(null, OnDayDataChanged));

    public CalendarDay? DayData
    {
        get => (CalendarDay?)GetValue(DayDataProperty);
        set => SetValue(DayDataProperty, value);
    }

    /// <summary>
    /// 是否被选中
    /// </summary>
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected), typeof(bool), typeof(DayCell),
            new PropertyMetadata(false, OnIsSelectedChanged));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>
    /// 日期被点击时触发的事件
    /// </summary>
    public event RoutedEventHandler? DayClicked;

    private static void OnDayDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cell = (DayCell)d;
        if (e.NewValue is not CalendarDay day) return;

        cell.DayText.Text = day.Date.Day.ToString();

        // 副标题：节日 / 节气 / 农历（由 LunarCalendarHelper 按优先级决定）
        cell.LunarText.Text = day.LunarDate;
        cell.LunarText.Visibility = string.IsNullOrEmpty(day.LunarDate)
            ? Visibility.Collapsed
            : Visibility.Visible;

        cell.UpdateHolidayBadge(day);
        cell.UpdateEventDots(day);
        cell.UpdateVisualState();
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((DayCell)d).UpdateVisualState();
    }

    /// <summary>
    /// 右上角「休」/「班」角标
    /// </summary>
    private void UpdateHolidayBadge(CalendarDay day)
    {
        switch (day.HolidayKind)
        {
            case HolidayKind.Holiday:
                HolidayBadge.Visibility = Visibility.Visible;
                HolidayBadgeText.Text = "休";
                HolidayBadge.Background = Brush("HolidayBadgeBrush");
                break;

            case HolidayKind.Workday:
                HolidayBadge.Visibility = Visibility.Visible;
                HolidayBadgeText.Text = "班";
                HolidayBadge.Background = Brush("WorkdayBadgeBrush");
                break;

            default:
                HolidayBadge.Visibility = Visibility.Collapsed;
                break;
        }

        // 非本月的角标降低不透明度，避免抢视觉焦点
        HolidayBadge.Opacity = day.IsCurrentMonth ? 1.0 : 0.45;
    }

    /// <summary>
    /// 事件圆点：按颜色去重，最多 3 个
    /// </summary>
    private void UpdateEventDots(CalendarDay day)
    {
        var dots = new[] { Dot1, Dot2, Dot3 };

        if (!day.HasEvents || day.Events.Count == 0)
        {
            EventDots.Visibility = Visibility.Collapsed;
            foreach (var dot in dots)
                dot.Visibility = Visibility.Collapsed;
            return;
        }

        var distinctColors = day.Events
            .Select(e => e.Color)
            .Distinct()
            .Take(3)
            .ToList();

        EventDots.Visibility = Visibility.Visible;
        for (int i = 0; i < dots.Length; i++)
        {
            if (i >= distinctColors.Count)
            {
                dots[i].Visibility = Visibility.Collapsed;
                continue;
            }

            dots[i].Visibility = Visibility.Visible;
            try
            {
                var brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(distinctColors[i]));
                brush.Freeze();
                dots[i].Fill = brush;
            }
            catch
            {
                dots[i].Fill = Brush("EventDotBrush");
            }
        }

        EventDots.Opacity = day.IsCurrentMonth ? 1.0 : 0.45;
    }

    /// <summary>
    /// 统一计算背景与文字颜色。
    /// 背景优先级：今日（实心方块）&gt; 选中（描边方块）&gt; hover（低饱和底色）&gt; 无
    /// 三种状态集中在一处处理，避免各自改 Foreground 互相覆盖。
    /// </summary>
    private void UpdateVisualState()
    {
        var day = DayData;
        if (day == null) return;

        bool isToday = day.IsToday;
        bool isSelected = IsSelected;

        // 背景
        if (isToday)
        {
            RootBorder.Background = Brush("TodayAccentBrush");
            RootBorder.BorderThickness = new Thickness(0);
            RootBorder.BorderBrush = null;
        }
        else if (isSelected)
        {
            RootBorder.Background = _isHovered
                ? Brush("HoverBrush")
                : Brushes.Transparent;
            RootBorder.BorderThickness = new Thickness(1.5);
            // 描边画在面板底色上，用 AccentOnSurface 而非填充用的 TodayAccent
            RootBorder.BorderBrush = Brush("AccentOnSurfaceBrush");
        }
        else if (_isHovered)
        {
            RootBorder.Background = Brush("HoverBrush");
            RootBorder.BorderThickness = new Thickness(0);
            RootBorder.BorderBrush = null;
        }
        else
        {
            RootBorder.Background = Brushes.Transparent;
            RootBorder.BorderThickness = new Thickness(0);
            RootBorder.BorderBrush = null;
        }

        // 文字颜色
        if (isToday)
        {
            DayText.Foreground = Brush("TextOnAccentBrush");
            LunarText.Foreground = Brush("TextOnAccentBrush");
            DayText.FontWeight = FontWeights.Bold;
            LunarText.Opacity = 0.85;
        }
        else
        {
            DayText.Foreground = isSelected
                // 文字画在面板底色上，用 AccentOnSurface 保证深色主题下足够亮
                ? Brush("AccentOnSurfaceBrush")
                : day.IsCurrentMonth
                    ? Brush("TextPrimaryBrush")
                    : Brush("TextDisabledBrush");

            LunarText.Foreground = day.IsCurrentMonth
                ? Brush("TextSecondaryBrush")
                : Brush("TextDisabledBrush");

            DayText.FontWeight = FontWeights.Normal;
            LunarText.Opacity = 1.0;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetHovered(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(false);
    }

    /// <summary>
    /// 外部设置 hover 状态。
    /// 供网格重建后（如滚轮翻月）补齐鼠标下方格子的高亮：
    /// 新建的实例不会收到 MouseEnter，需要主动同步。
    /// </summary>
    public void SetHovered(bool hovered)
    {
        if (_isHovered == hovered) return;
        _isHovered = hovered;
        UpdateVisualState();
    }

    private void OnDayClick(object sender, MouseButtonEventArgs e)
    {
        DayClicked?.Invoke(this, new RoutedEventArgs { Source = this });
    }

    private static Brush? Brush(string key)
        => Application.Current?.TryFindResource(key) as Brush;
}
