# WinminiCal — Windows 任务栏日历

> 一款轻量、优雅的 Windows 11 任务栏日历工具，灵感来自 macOS 平台的 Itsycal，支持显示事件列表

## 截图

![miniCal](https://github.com/ha0719/WinCal/blob/main/screenshot.jpeg "软件预览")


## 功能特性

- 🗓️ **系统托盘日历** — 点击任务栏时间区域即可弹出月历面板
- 🔁 **系统日历替换** — 自动拦截 Windows 原生日历弹窗，替换为 WinCal 面板
- 📅 **农历显示** — 每个日期格子下方显示农历日期
- 📌 **事件展示** — 有事件的日期显示彩色圆点，面板下方列出近期日程
- 🔍 **事件详情** — 鼠标悬停事件项，左侧弹出详情（标题、时间、地点、描述）
- 🌐 **ICS 订阅** — 支持远程 .ics 日历订阅（Google Calendar、Outlook 等），可用 `{year}` 占位订阅按年份分文件的日历
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
| SetWinEventHook | 系统日历窗口拦截 |

## 项目结构

```
WinCal/
├── App.xaml / App.xaml.cs          # 应用入口，托盘图标，拦截器
├── WinCal.csproj                   # 项目配置
│
├── Core/                           # 核心层
│   ├── Models/                     #   数据模型（CalendarEvent, CalendarDay）
│   ├── Services/                   #   日历服务（ICS、按年份 ICS、聚合、模拟、设置持久化）
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

设置文件位于 `%LOCALAPPDATA%\miniCal\settings.json`，ICS 缓存位于 `%LOCALAPPDATA%\WinCal\cache\`。

设置界面支持：

- 界面主题（深色 / 浅色 / 跟随系统）
- 字体大小（5 档）
- 开机自启动
- 日历数据源（系统日历 / ICS 订阅 / 两者都用），ICS 支持多个订阅与 `{year}` 年份占位
- 周起始日（周日 / 周一）
- 近期事件天数（1 / 3 / 7 天）
- ICS 刷新频率（10 / 30 / 60 / 120 分钟）

> 说明：`WindowsCalendarService`（读取系统邮箱日历）需要 Windows SDK，当前在 `WinCal.csproj` 中被
> `<Compile Remove>` 排除，因此「系统日历」数据源实际回落到 `MockCalendarService` 显示模拟数据。
> 要接入真实日历数据，请使用 ICS 订阅。
>
> 农历始终显示（暂无开关）。`settings.json` 中的 `ShowHeaderDateTime`、`TimeFormat` 已定义但尚未接入界面。

## 开发文档

- [产品与开发方案](WinCal_产品与开发方案.md)
- [开发进度记录](PROGRESS.md)

## License

MIT
