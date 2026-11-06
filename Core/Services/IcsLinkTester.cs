using System.Net;
using System.Net.Http;
using Ical.Net;

namespace WinCal.Core.Services;

/// <summary>
/// 检查 ICS 订阅链接能否访问、解析，以及指定年份是否有事件。
/// 只发起读取请求，不修改订阅设置或日历缓存。
/// </summary>
public static class IcsLinkTester
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public record Result(bool IsValid, bool HasEvents, string ResolvedUrl, string Message);

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "miniCal/1.0 (Calendar Client)");
        return client;
    }

    public static async Task<Result> TestAsync(string url, int year)
    {
        if (year < 1 || year >= 9999)
            return new Result(false, false, url, "测试年份请输入 1 到 9998 之间的整数。");

        var usesYearPlaceholder = url.Contains(
            YearlyIcsCalendarService.YearPlaceholder, StringComparison.OrdinalIgnoreCase);
        var resolvedUrl = url.Trim().Replace(
            YearlyIcsCalendarService.YearPlaceholder,
            year.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new Result(false, false, resolvedUrl, "请输入有效的 HTTP/HTTPS 订阅链接。");
        }

        try
        {
            using var response = await HttpClient.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? usesYearPlaceholder ? $"{year} 年的订阅文件不存在。" : "订阅文件不存在。"
                    : "服务器未返回成功响应。";
                return new Result(false, false, resolvedUrl,
                    $"HTTP {(int)response.StatusCode}：{detail}");
            }

            var content = await response.Content.ReadAsStringAsync();
            if (!content.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) ||
                !content.Contains("END:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                return new Result(false, false, resolvedUrl, "服务器响应成功，但内容不是 ICS 日历文件。");
            }

            // 解析和事件展开可能耗时，在后台执行以免阻塞设置窗口。
            var (eventCount, hasEvents) = await Task.Run(() =>
            {
                var calendar = Calendar.Load(content)
                    ?? throw new FormatException("ICS 日历文件解析失败。");
                var yearStart = new DateTime(year, 1, 1);
                var hasEvents = calendar.GetOccurrences(yearStart, yearStart.AddYears(1)).Any();
                return (calendar.Events.Count, hasEvents);
            });
            var message = hasEvents
                ? $"链接有效，ICS 解析成功；{year} 年有事件（文件包含 {eventCount} 条事件定义）。"
                : $"链接和 ICS 格式有效，但 {year} 年没有事件（文件包含 {eventCount} 条事件定义）。";
            return new Result(true, hasEvents, resolvedUrl, message);
        }
        catch (TaskCanceledException)
        {
            return new Result(false, false, resolvedUrl, "请求超时（15 秒）。");
        }
        catch (HttpRequestException ex)
        {
            return new Result(false, false, resolvedUrl, $"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return new Result(false, false, resolvedUrl, $"ICS 日历文件解析失败：{ex.Message}");
        }
    }
}
