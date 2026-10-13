using System.Net;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

internal static class BiliHttp
{
    // Keep this identical for token requests and WebSocket authentication.
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static HttpClient CreateClient() => new(new HttpClientHandler
    {
        // All cookies are explicitly scoped to each run. Do not retain a login
        // Set-Cookie when the user switches to anonymous reading.
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = TimeSpan.FromSeconds(15) };

    public static string CookieHeader(CookieOptions cookies)
    {
        var fields = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(cookies.Sessdata)) fields.Add($"SESSDATA={cookies.Sessdata}");
        if (!string.IsNullOrWhiteSpace(cookies.Buvid3)) fields.Add($"buvid3={cookies.Buvid3}");
        if (!string.IsNullOrWhiteSpace(cookies.BiliJct)) fields.Add($"bili_jct={cookies.BiliJct}");
        return string.Join("; ", fields);
    }

    public static void SetHeaders(HttpRequestMessage request, CookieOptions cookies)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Referrer = new Uri("https://live.bilibili.com/");
        var header = CookieHeader(cookies);
        if (header.Length > 0) request.Headers.Add("Cookie", header);
    }

    public static JsonElement ReadData(JsonElement root)
    {
        if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number))
            throw new InvalidDataException("B站接口返回格式异常");
        if (number != 0)
        {
            var message = root.TryGetProperty("message", out var text) ? text.GetString() : "请求失败";
            throw new BiliApiException(number, $"B站接口返回 {number}：{message}");
        }
        return root.GetProperty("data");
    }
}

internal sealed class BiliApiException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

internal sealed class BiliAuthenticationException(string message) : Exception(message);
