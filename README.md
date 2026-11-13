# WinminiCal — Windows 任务栏日历

> 一款轻量、优雅的 Windows 11 任务栏日历工具，灵感来自 macOS 平台的 Itsycal，支持显示事件列表

## 截图

![miniCal](https://github.com/ha0719/WinCal/blob/main/screenshot.jpeg "软件预览")


## 功能特性

- 🗓️ **系统托盘日历** — 点击任务栏时间区域即可弹出月历面板
- 🔁 **原生通知兼容** — 点击任务栏时钟时保留上方 Windows 通知，用 WinCal 覆盖下方原生日历
- 📅 **农历显示** — 每个日期格子下方显示农历日期
- 📌 **事件展示** — 有事件的日期显示彩色圆点，面板下方列出近期日程
- 🔍 **事件详情** — 鼠标悬停事件项，左侧弹出详情（标题、时间、地点、描述）
- 🌐 **ICS 订阅** — 支持远程 .ics 日历订阅（Google Calendar、Outlook 等），可用 `{year}` 占位订阅按年份分文件的日历
- 🔎 **链接测试** — 在设置中测试新输入或已添加的 ICS 链接；`{year}` 链接可指定测试年份
- 🎨 **主题跟随** — 深色 / 浅色主题自动跟随系统
- ⚙️ **丰富设置** — 周起始日、近期事件天数、字体大小、刷新频率等

订阅日历可以参考：https://yangh9.github.io/ChinaCalendar/

### 中国节假日订阅

推荐 [chinese-days](https://github.com/vsme/chinese-days)，含法定假日与调休上班日：

```
https://cdn.jsdelivr.net/npm/chinese-days/dist/years/{year}.ics
```

`{year}` 会在运行时替换为需要的年份。这类订阅每年一个文件，未来年份通常尚未发布（请求返回 404），
因此每个年份都有独立兜底链路：**内存 → 磁盘缓存 → 按年份文件 → 聚合文件（`holidays.ics`）→ 跳过该年份**。
任何一环失败都不会中断面板加载，也不会清掉已有缓存。

也可以直接订阅覆盖多年的聚合文件（不带 `{year}`，走普通 ICS 通道）：

```
https://cdn.jsdelivr.net/npm/chinese-days/dist/holidays.ics
```

> 事件标题形如 `元旦(休)`、`春节(班)`，`(休)` 为放假、`(班)` 为调休上班。
> 兜底只能延缓失效，无法造出数据：若上游尚未发布某年份，且聚合文件也不覆盖该年，则该年无节假日显示。

## 技术栈

| 技术 | 说明 |
|------|------|
| C# 12 + .NET 8 | 主开发语言和运行时 |
| WPF | UI 框架，矢量渲染 + 自定义控件 |
| MVVM 架构 | 手写 `INotifyPropertyChanged`（已引入 CommunityToolkit.Mvvm 但尚未使用） |
| Hardcodet.NotifyIcon.Wpf | 系统托盘图标 |
| Ical.Net | ICS 日历文件解析 |
| SetWinEventHook | 观察系统面板显示，联动 WinCal 月历 |

## 项目结构

```
WinCal/
├── App.xaml / App.xaml.cs          # 应用入口，托盘图标，拦截器
├── WinCal.csproj                   # 项目配置
│
├── Core/                           # 核心层
│   ├── Models/                     #   数据模型（CalendarEvent, CalendarDay）
│   ├── Services/                   #   日历服务（ICS、按年份 ICS、聚合、空服务、设置持久化）
│   └── Helpers/                    #   工具类（定位、主题、农历、拦截器等）
│
├── ViewModels/                     # ViewModel 层
│   ├── CalendarViewModel.cs        #   日历主逻辑
│   └── EventListViewModel.cs       #   事件列表逻辑
│
├── Views/                          # 视图层
│   ├── PopupWindow.xaml            #   弹出日历主窗口
│   ├── SettingsWindow.xaml         #   设置窗口
│   ├── EventDetailWindow.xaml      #   事件详情浮层
│   ├── Controls/                   #   自定义控件（月历、日期格子、事件项）
│   └── Themes/                     #   主题资源（Light.xaml, Dark.xaml）
│
├── publish.bat                     # 一键发布脚本
├── build.bat                       # 开发构建脚本（暂停查看输出）
└── build_nopause.bat               # 开发构建脚本（不暂停）
```

## 构建与运行

### 前置要求

- .NET 8 SDK（仅装运行时无法构建；`winget install Microsoft.DotNet.SDK.8`，
  用 `dotnet --list-sdks` 确认列出 8.x）
- Windows 10 1903+ 或 Windows 11
- Visual Studio 2022 或 VS Code + C# Dev Kit

### 开发调试

```bat
build.bat
```

### 发布单文件

```bat
publish.bat
```

产物：`dist/WinCal.exe`（单文件，约 50-70 MB，无需安装 .NET 运行时）

## 配置

### 保留原生通知，替换日历区域

点击任务栏时钟时，WinCal 通过 UI Automation 定位系统面板中的日历卡片，
以相同位置和大小覆盖原生日历，保留上方原生通知。鼠标操作 WinCal 不主动切换前台焦点。
关闭原生面板时，联动的 WinCal 和事件详情也会收起。
收起开始时，WinCal 使用约 120ms 的向右退出动画；原生窗口提前隐藏时立即结束。
快速重开会取消退出并复位，系统关闭界面动画时直接收起。原生动画没有公开逐帧进度，
两者的运动曲线可能略有差异。
托盘图标仍可单独打开 WinCal。
系统面板已经打开时再启动 WinCal，也会自动接入当前面板。

Windows 的系统面板层级高于普通置顶窗口。WinCal 临时从原生窗口的显示区域中
裁去已确认的日历卡片，露出同位置的自定义日历，上方通知区域继续由 Windows 处理。
收起面板或退出应用时恢复原显示区域；启动后会预热一个同名恢复守护进程，
在 WinCal 主进程异常退出时尝试恢复原状。不会修改系统日历控件或保存系统设置。
日历窗口也会提前在隐藏状态下完成创建和布局，后续打开复用窗口；
正常收起后恢复守护继续待命，后续打开复用同一个进程和连接。
首次打开时等待布局稳定并确认卡片边界，随后缓存位置。
同一系统窗口、屏幕、缩放及原生区域形状未变化时，后续打开直接复用位置，
不再等待 UI Automation 查询和布局稳定；显示后在后台检查布局变化。
缓存仅保存在当前进程内，布局变化或保存设置后重新确认。
打开和关闭动画中的临时区域变化不清除已验证的位置；显示前仍确认当前原生形状可恢复。
当前依赖 Windows 11 的 `CalendarCenterGrid` / `NotificationCenterGrid` 标识；
无法确认日历区域、日历已折叠、恢复守护未就绪或系统更新改变控件结构时，
保留原生界面并使用托盘入口，禁止猜测坐标遮挡通知。
若设置隐藏托盘图标，应用会先保留图标，直到本次启动后首次成功联动，保证始终有设置入口。

### 设置与缓存

设置文件位于 `%LOCALAPPDATA%\miniCal\settings.json`，ICS 缓存位于 `%LOCALAPPDATA%\WinCal\cache\`。

设置界面支持：

- 界面主题（深色 / 浅色 / 跟随系统）
- 字体大小（5 档）
- 开机自启动
- 日历数据源（系统日历 / ICS 订阅 / 两者都用），ICS 支持多个订阅与 `{year}` 年份占位
- 周起始日（周日 / 周一）
- 近期事件天数（1 / 3 / 7 天）
- ICS 刷新频率（10 / 30 / 60 / 120 分钟）
- ICS 链接测试（检查实际请求地址、ICS 格式和指定年份是否有事件）

测试 `{year}` 链接时，在「测试年份」输入年份，再点击新链接或已添加订阅旁的「测试」。测试只检查展开后的年份链接；应用运行时的聚合文件兜底不计入测试结果。

> 说明：`WindowsCalendarService`（读取系统邮箱日历）需要 Windows SDK，当前在 `WinCal.csproj` 中被
> `<Compile Remove>` 排除，因此选择「系统日历」时不会加载事件；选择「两者都用」时仅显示 ICS 事件。
> 要显示真实日历事件，请使用 ICS 订阅。
>
> 农历始终显示（暂无开关）。`settings.json` 中的 `ShowHeaderDateTime`、`TimeFormat` 已定义但尚未接入界面。

## 开发文档

通知兼容的布局与窗口样式回归检查（无需 NuGet 测试框架）：

```powershell
dotnet run --project tests/NotificationCompatibility.Tests/NotificationCompatibility.Tests.csproj
```

在解锁的桌面上启动待测 WinCal，再执行实际遮挡与鼠标点击验收：

```powershell
dotnet run --project tests/LiveNotificationOverlayCheck/LiveNotificationOverlayCheck.csproj
```

验收检查日历与通知各五点的真实命中窗口、鼠标点击与滚轮翻月、通知标题点击、通知区域滚轮及关闭联动，
并恢复原面板开关、通知滚动位置与鼠标位置。加 `--rapid-toggle` 可检查五组快速开关与恢复。
这些桌面交互检查需在目标 Windows 版本上运行；程序不会读取通知内容或点击具体通知。

- [产品与开发方案](WinCal_产品与开发方案.md)
- [开发进度记录](PROGRESS.md)

## License

MIT
