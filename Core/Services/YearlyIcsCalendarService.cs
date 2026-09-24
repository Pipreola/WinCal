using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WinCal.Core.Models;
using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using WinCalEvent = WinCal.Core.Models.CalendarEvent;

namespace WinCal.Core.Services;

/// <summary>
/// 按年份分文件的 ICS 订阅服务。
///
/// URL 中用 {year} 占位，运行时替换为需要的年份，例如
/// https://cdn.jsdelivr.net/npm/chinese-days/dist/years/{year}.ics
///
/// 这类订阅每年一个文件，未来年份通常还没发布（请求返回 404），
/// 因此每个年份都有独立的兜底链路：
///   内存 → 磁盘缓存 → 按年份 URL → 聚合 URL（如 holidays.ics）→ 放弃该年份
/// 任何一步失败都不会抛给调用方，也不会删除已有的磁盘缓存。
/// </summary>
public class YearlyIcsCalendarService : ICalendarService
{
    /// <summary>URL 模板中代表年份的占位符</summary>
    public const string YearPlaceholder = "{year}";

    /// <summary>某年份确认缺失后，等待多久再重试（避免每次开面板都打一次 404）</summary>
    private static readonly TimeSpan MissingRetryInterval = TimeSpan.FromHours(12);

    private readonly string _urlTemplate;
    private readonly string? _aggregateUrl;
    private readonly int _refreshMinutes;
    private readonly string _calendarName;
    private readonly string _color;
    private readonly string _cacheDir;
    private readonly HttpClient _httpClient;

    private readonly Dictionary<int, YearEntry> _years = new();
    private readonly YearEntry _aggregate = new();

    /// <summary>单个年份（或聚合文件）的缓存状态</summary>
    private sealed class YearEntry
    {
        /// <summary>已加载的日历数据；null 表示这一年暂时没有任何可用数据</summary>
        public IcalCalendar? Calendar;

        /// <summary>当前持有数据的抓取时间，用于判断是否过期</summary>
        public DateTime DataTimestamp = DateTime.MinValue;

        /// <summary>最近一次确认该年份缺失（404）的时间；MinValue 表示未确认缺失</summary>
        public DateTime MissingSince = DateTime.MinValue;

        /// <summary>是否有后台刷新正在进行</summary>
        public bool Refreshing;
    }

    public YearlyIcsCalendarService(
        string urlTemplate,
        int refreshMinutes = 30,
        string calendarName = "ICS 订阅",
        string color = "#0078D4",
        string? aggregateUrl = null)
    {
        _urlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
        _refreshMinutes = refreshMinutes;
        _calendarName = calendarName;
        _color = color;
        // 未显式指定兜底链接时，尝试从模板推断
        _aggregateUrl = aggregateUrl ?? TryDeriveAggregateUrl(urlTemplate);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "miniCal/1.0 (Calendar Client)");

        // 与 IcsCalendarService 共用缓存目录和命名规则，按解析后的实际 URL 分文件
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCal", "cache");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// 从按年份模板推断聚合文件链接（chinese-days 的目录结构：
    /// dist/years/{year}.ics 旁边有一个覆盖多年的 dist/holidays.ics）。
    /// 结构不匹配时返回 null，即不启用聚合兜底。
    /// </summary>
    private static string? TryDeriveAggregateUrl(string urlTemplate)
    {
        const string marker = "/years/" + YearPlaceholder + ".ics";
        var idx = urlTemplate.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        return urlTemplate.Substring(0, idx) + "/holidays.ics";
    }

    /// <summary>把模板中的 {year} 替换为具体年份</summary>
    private string ResolveUrl(int year) => _urlTemplate.Replace(
        YearPlaceholder,
        year.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StringComparison.OrdinalIgnoreCase);

    public async Task<List<WinCalEvent>> GetEventsAsync(DateTime start, DateTime end)
    {
        var events = new List<WinCalEvent>();
        if (end < start)
            return events;

        // 查询范围可能跨年（月历网格前后各留 7 天，1 月和 12 月必然跨年），逐年取数据
        for (int year = start.Year; year <= end.Year; year++)
        {
            IcalCalendar? calendar;
            try
            {
                calendar = await EnsureYearAsync(year);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: Yearly ICS {year} load failed: {ex.Message}");
                continue;
            }

            if (calendar == null)
                continue;

            try
            {
                foreach (var occurrence in calendar.GetOccurrences(start, end))
                {
                    var calEvent = ConvertOccurrence(occurrence);
                    if (calEvent != null)
                        events.Add(calEvent);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: Yearly ICS {year} expand failed: {ex.Message}");
            }
        }

        // 去重：某年份走聚合兜底时，聚合文件里也包含其他年份的事件，
        // 会和按年份文件的结果重叠。这里只合并完全相同的事件（起止时间 + 标题），
        // 不做标题前缀合并，避免误删同一节日的「休」和「班」两条。
        return events
            .GroupBy(e => (e.StartTime, e.EndTime, e.Title))
            .Select(g => g.First())
            .OrderBy(e => e.StartTime)
            .ToList();
    }

    public async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrWhiteSpace(_urlTemplate))
            return false;

        try
        {
            return await EnsureYearAsync(DateTime.Today.Year) != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<CalendarAccountInfo>> GetCalendarAccountsAsync()
    {
        var available = await IsAvailableAsync();
        return new List<CalendarAccountInfo>
        {
            new("ICS 订阅", _calendarName, "ics-yearly", _color, available)
        };
    }

    public async Task ForceRefreshAsync()
    {
        // 强制刷新：清掉缺失标记（可能新年份刚发布），重新抓取已接触过的年份 + 当前年份
        List<int> years;
        lock (_years)
        {
            foreach (var entry in _years.Values)
            {
                entry.MissingSince = DateTime.MinValue;
                entry.Refreshing = false;
            }
            years = _years.Keys.ToList();
        }

        _aggregate.MissingSince = DateTime.MinValue;
        _aggregate.Refreshing = false;

        var currentYear = DateTime.Today.Year;
        if (!years.Contains(currentYear))
            years.Add(currentYear);

        foreach (var year in years)
        {
            try
            {
                await RefreshYearFromNetworkAsync(year, GetEntry(year));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: Yearly ICS {year} force refresh failed: {ex.Message}");
            }
        }
    }

    public void OpenSystemCalendarApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ResolveUrl(DateTime.Today.Year)) { UseShellExecute = true });
        }
        catch { }
    }

    private YearEntry GetEntry(int year)
    {
        lock (_years)
        {
            if (!_years.TryGetValue(year, out var entry))
            {
                entry = new YearEntry();
                _years[year] = entry;
            }
            return entry;
        }
    }

    private bool IsFresh(YearEntry entry)
        => entry.Calendar != null && (DateTime.Now - entry.DataTimestamp).TotalMinutes < _refreshMinutes;

    /// <summary>缺失标记是否已过冷却期，可以再试一次网络</summary>
    private static bool MayAttemptNetwork(YearEntry entry)
        => entry.MissingSince == DateTime.MinValue
           || DateTime.Now - entry.MissingSince >= MissingRetryInterval;

    /// <summary>
    /// 确保指定年份的数据可用。优先级：内存 → 磁盘 → 网络。
    /// 已有数据但过期时后台刷新，不阻塞 UI；完全没有数据时才同步等待网络。
    /// </summary>
    private async Task<IcalCalendar?> EnsureYearAsync(int year)
    {
        var entry = GetEntry(year);

        // 1. 内存缓存仍新鲜
        if (IsFresh(entry))
            return entry.Calendar;

        // 2. 内存为空 → 试磁盘
        if (entry.Calendar == null)
            await TryLoadFromDiskAsync(year, entry);

        // 3. 有可用数据（内存旧数据或刚从磁盘载入）：
        // 仅在过期时后台刷新，先把手上的数据交出去，不阻塞 UI
        if (entry.Calendar != null)
        {
            if (!IsFresh(entry) && !entry.Refreshing && MayAttemptNetwork(entry))
                _ = RefreshYearFromNetworkAsync(year, entry);
            return entry.Calendar;
        }

        // 4. 什么都没有 → 同步抓一次（仅首次，或该年份刚进入视图时）
        if (!entry.Refreshing && MayAttemptNetwork(entry))
            await RefreshYearFromNetworkAsync(year, entry);

        return entry.Calendar;
    }

    /// <summary>从磁盘缓存加载某年份数据（毫秒级）</summary>
    private async Task TryLoadFromDiskAsync(int year, YearEntry entry)
    {
        // 按年份文件本身的缓存
        if (await TryLoadFileAsync(CachePathFor(ResolveUrl(year)), entry))
            return;

        // 按年份文件没缓存过，试聚合文件的缓存（该年份可能本来就走兜底）
        if (_aggregateUrl != null)
            await TryLoadFileAsync(CachePathFor(_aggregateUrl), entry);
    }

    private async Task<bool> TryLoadFileAsync(string path, YearEntry entry)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            var content = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(content))
                return false;

            var calendar = ParseIcsContent(content);
            if (calendar == null)
                return false;

            entry.Calendar = calendar;
            entry.DataTimestamp = File.GetLastWriteTime(path);
            Debug.WriteLine($"WinCal: Loaded ICS from disk cache {Path.GetFileName(path)} " +
                            $"({calendar.Events.Count} events, cached at {entry.DataTimestamp:yyyy-MM-dd HH:mm})");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Failed to load disk cache {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 抓取某年份数据；按年份 URL 不存在时退到聚合 URL。
    /// 全部失败也不抛出，已有的旧数据（内存 / 磁盘）保持不动。
    /// </summary>
    private async Task RefreshYearFromNetworkAsync(int year, YearEntry entry)
    {
        entry.Refreshing = true;
        try
        {
            var url = ResolveUrl(year);
            var (content, notFound) = await TryDownloadAsync(url);

            if (content != null)
            {
                var calendar = ParseIcsContent(content);
                if (calendar != null)
                {
                    entry.Calendar = calendar;
                    entry.DataTimestamp = DateTime.Now;
                    entry.MissingSince = DateTime.MinValue;
                    await TryWriteDiskCacheAsync(CachePathFor(url), content);
                    Debug.WriteLine($"WinCal: Yearly ICS {year} refreshed ({calendar.Events.Count} events)");
                    return;
                }

                Debug.WriteLine($"WinCal: Yearly ICS {year} downloaded but unparsable, keeping previous data");
                return;
            }

            if (notFound)
            {
                // 该年份尚未发布（或已下架）：记录缺失时间，进入冷却，转聚合兜底
                entry.MissingSince = DateTime.Now;
                Debug.WriteLine($"WinCal: Yearly ICS {year} not published (404), trying aggregate fallback");
                await TryAggregateFallbackAsync(year, entry);
                return;
            }

            // 网络错误（超时 / DNS / 5xx）：不算缺失，下次还会重试
            Debug.WriteLine($"WinCal: Yearly ICS {year} fetch failed, keeping previous data");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Yearly ICS {year} refresh error: {ex.Message}");
        }
        finally
        {
            entry.Refreshing = false;
        }
    }

    /// <summary>
    /// 聚合文件兜底：按年份文件缺失时，用覆盖多年的 holidays.ics 顶上。
    /// 聚合文件自身也缓存在内存和磁盘，多个缺失年份共用。
    /// </summary>
    private async Task TryAggregateFallbackAsync(int year, YearEntry entry)
    {
        if (_aggregateUrl == null)
            return;

        // 聚合数据已在内存且新鲜 → 直接复用
        if (!IsFresh(_aggregate))
        {
            if (_aggregate.Calendar == null)
                await TryLoadFileAsync(CachePathFor(_aggregateUrl), _aggregate);

            if (!IsFresh(_aggregate) && MayAttemptNetwork(_aggregate))
            {
                var (content, notFound) = await TryDownloadAsync(_aggregateUrl);
                if (content != null)
                {
                    var calendar = ParseIcsContent(content);
                    if (calendar != null)
                    {
                        _aggregate.Calendar = calendar;
                        _aggregate.DataTimestamp = DateTime.Now;
                        _aggregate.MissingSince = DateTime.MinValue;
                        await TryWriteDiskCacheAsync(CachePathFor(_aggregateUrl), content);
                        Debug.WriteLine($"WinCal: Aggregate ICS refreshed ({calendar.Events.Count} events)");
                    }
                }
                else if (notFound)
                {
                    _aggregate.MissingSince = DateTime.Now;
                }
            }
        }

        if (_aggregate.Calendar == null)
        {
            Debug.WriteLine($"WinCal: No data available for {year} (per-year missing, aggregate unavailable)");
            return;
        }

        // 把聚合数据作为该年份的数据源。聚合文件里包含其他年份的事件，
        // GetEventsAsync 的去重会处理与按年份文件的重叠部分。
        entry.Calendar = _aggregate.Calendar;
        entry.DataTimestamp = _aggregate.DataTimestamp;
        Debug.WriteLine($"WinCal: Year {year} served from aggregate fallback");
    }

    /// <summary>
    /// 下载 URL 内容。返回 (内容, 是否 404)。
    /// 内容为 null 且 notFound 为 false 表示网络层错误，应保留旧数据并稍后重试。
    /// </summary>
    private async Task<(string? Content, bool NotFound)> TryDownloadAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);

            if (response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Gone)
            {
                return (null, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"WinCal: ICS request {url} returned {(int)response.StatusCode}");
                return (null, false);
            }

            var content = await response.Content.ReadAsStringAsync();

            // CDN 对缺失文件有时回 200 + 纯文本说明，用内容特征再确认一次
            if (string.IsNullOrWhiteSpace(content) || !content.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"WinCal: ICS request {url} returned non-calendar body");
                return (null, true);
            }

            return (content, false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: ICS download {url} failed: {ex.Message}");
            return (null, false);
        }
    }

    private async Task TryWriteDiskCacheAsync(string path, string content)
    {
        try
        {
            await File.WriteAllTextAsync(path, content);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Failed to write disk cache {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>缓存文件路径：URL 的 SHA-256 十六进制前 16 位（与 IcsCalendarService 一致）</summary>
    private string CachePathFor(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        return Path.Combine(_cacheDir, $"{hash}.ics");
    }

    private IcalCalendar? ParseIcsContent(string icsContent)
    {
        try
        {
            return IcalCalendar.Load(icsContent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Failed to parse ICS calendar: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 将一次 Occurrence 转为 WinCal 事件模型。
    /// 与 IcsCalendarService.ConvertOccurrence 保持一致，包括单日全天事件
    /// （DTEND == DTSTART）修正为次日，避免月历格子匹配不到。
    /// </summary>
    private WinCalEvent? ConvertOccurrence(Occurrence occurrence)
    {
        if (occurrence.Source == null)
            return null;

        if (occurrence.Source is not IcalEvent icalEvent)
            return null;

        var period = occurrence.Period;
        if (period?.StartTime == null)
            return null;

        var startTime = period.StartTime.AsSystemLocal;
        var isAllDay = icalEvent.IsAllDay;

        DateTime endTime;
        if (period.EndTime != null)
        {
            endTime = period.EndTime.AsSystemLocal;
        }
        else if (period.Duration != default)
        {
            endTime = startTime + period.Duration;
        }
        else
        {
            endTime = isAllDay ? startTime.AddDays(1) : startTime.AddHours(1);
        }

        if (isAllDay && endTime.Date <= startTime.Date)
            endTime = startTime.AddDays(1);

        var title = !string.IsNullOrWhiteSpace(icalEvent.Summary)
            ? icalEvent.Summary
            : "(无标题)";

        return new WinCalEvent(
            Title: title,
            StartTime: startTime,
            EndTime: endTime,
            IsAllDay: isAllDay,
            CalendarName: _calendarName,
            Color: _color,
            Location: icalEvent.Location ?? "",
            Description: icalEvent.Description ?? ""
        );
    }
}
