using System.Windows;
using System.Windows.Interop;
using WinCal.Core.Helpers;
using WinCal.Core.Models;
using WinCal.ViewModels;

namespace WinCal.Views;

public partial class EventDetailWindow : Window
{
    public EventDetailWindow()
    {
        InitializeComponent();
        WindowActivationHelper.PreventActivation(this);
    }

    /// <summary>
    /// 显示指定事件的详情，并定位到主面板左侧
    /// </summary>
    public void ShowEvent(CalendarEvent evt, Window mainPanel)
    {
        DetailTitle.Text = evt.Title;
        DetailTime.Text = EventListViewModel.FormatEventTime(evt);

        // 地点
        if (!string.IsNullOrEmpty(evt.Location))
        {
            LocationPanel.Visibility = Visibility.Visible;
            DetailLocation.Text = evt.Location;
        }
        else
        {
            LocationPanel.Visibility = Visibility.Collapsed;
        }

        // 日历来源
        if (!string.IsNullOrEmpty(evt.CalendarName))
        {
            CalendarPanel.Visibility = Visibility.Visible;
            DetailCalendar.Text = evt.CalendarName;
        }
        else
        {
            CalendarPanel.Visibility = Visibility.Collapsed;
        }

        // 描述
        if (!string.IsNullOrEmpty(evt.Description))
        {
            DescSeparator.Visibility = Visibility.Visible;
            DetailDescription.Visibility = Visibility.Visible;
            DetailDescription.Text = evt.Description;
        }
        else
        {
            DescSeparator.Visibility = Visibility.Collapsed;
            DetailDescription.Visibility = Visibility.Collapsed;
        }

        // 先 Show 以获取实际尺寸
        Show();

        // 定位到主面板左侧，垂直居中对齐
        UpdatePosition(mainPanel);
    }

    /// <summary>
    /// 更新位置到主面板左侧，底部对齐
    /// </summary>
    public void UpdatePosition(Window mainPanel)
    {
        if (!IsVisible || mainPanel == null) return;

        // 使用主面板所在显示器的工作区，并在左侧空间不足时尝试右侧。
        WindowPositionHelper.PositionAlongsideSystemPanel(
            this, new WindowInteropHelper(mainPanel).Handle);
    }
}
