using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed record AccountVerification(AccountState State, BrowserAccount? Account = null);
public sealed class BiliAccountService : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    public async Task<AccountVerification> VerifyAsync(CookieOptions cookies, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            BiliHttp.SetHeaders(request, cookies);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return new(AccountState.Unavailable);
            if (response.Content.Headers.ContentLength > 256 * 1024) return new(AccountState.Unavailable);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            { if (buffer.Length + count > 256 * 1024) return new(AccountState.Unavailable); buffer.Write(bytes, 0, count); }
            using var document = JsonDocument.Parse(buffer.ToArray()); var root = document.RootElement;
            if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var number)) return new(AccountState.Unavailable);
            if (number == -101) return new(AccountState.Expired);
            if (number != 0 || !root.TryGetProperty("data", out var data)) return new(AccountState.Unavailable);
            if (!data.TryGetProperty("isLogin", out var login)) return new(AccountState.Unavailable);
            if (login.ValueKind == JsonValueKind.False) return new(AccountState.Expired);
            if (login.ValueKind != JsonValueKind.True || !data.TryGetProperty("mid", out var mid) || !mid.TryGetInt64(out var uid) || uid <= 0) return new(AccountState.Unavailable);
            return new(AccountState.LoggedIn, new(uid, Text(data, "uname", 80), Text(data, "face", 2048)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(AccountState.Unavailable); }
    }
    private static string Text(JsonElement data, string key, int max)
    { var text = data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; return text.Length > max ? text[..max] : text; }
    public void Dispose() => _http.Dispose();
}
