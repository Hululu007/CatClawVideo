using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 弹弹play 兼容弹幕源（2026-10-19 目标 P3）。
///
/// <para><b>为什么是"可配置服务器"而不是直连官方</b>：官方 <c>api.dandanplay.net</c> **免注册不可用** ——
/// 实测 <c>/api/v2/search/episodes</c> 与 <c>/api/v2/comment/1</c> 都返回 <c>HTTP 403</c>（需要 AppId 签名）。
/// 所以默认关闭，设置里可填：①自建 <c>misaka_danmu_server</c> / <c>danmu_api</c> 这类"弹弹play 兼容"地址；
/// ②官方地址 + 自己申请的 AppId/AppSecret（本类按官方规范做 HMAC-SHA256 签名）。</para>
///
/// <para><b>与现有链路的关系</b>：拿到的 JSON 里 <c>p</c> 字段与 bilibili XML 的 <c>p</c> 完全同构
/// （<c>时间,模式,字号,颜色,时间戳,池,用户,行号</c>），所以这里**只做 JSON→XML 的转写**，
/// 然后交给既有的 <see cref="DanmuParser.LoadAsync"/>（它支持内联 XML）—— 渲染、密度、字号、轨道动画
/// 全部复用，不新增一条渲染路径。失败一律返回空表，调用方回落到站点自带弹幕。</para>
/// </summary>
public static class DandanplayDanmaku
{
    /// <summary>环境变量覆盖（便于自动化测试与高级用户）：CATCLAW_DANDANPLAY_URL / _APPID / _SECRET。</summary>
    public static string EnvUrl => Environment.GetEnvironmentVariable("CATCLAW_DANDANPLAY_URL")?.Trim() ?? "";
    public static string EnvAppId => Environment.GetEnvironmentVariable("CATCLAW_DANDANPLAY_APPID")?.Trim() ?? "";
    public static string EnvSecret => Environment.GetEnvironmentVariable("CATCLAW_DANDANPLAY_SECRET")?.Trim() ?? "";

    /// <summary>按文件名+大小匹配剧集并取回弹幕。任何一步失败 → 空表（调用方回落站点自带弹幕）。</summary>
    public static async Task<List<DanmuCue>> LoadAsync(
        string baseUrl, string? appId, string? appSecret,
        string fileName, long fileLength, HttpClient http, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(fileName)) return [];
        try
        {
            var root = baseUrl.TrimEnd('/');

            // ① 匹配剧集：POST /api/v2/match {fileName,fileSize} → matches[0].episodeId
            var body = new JsonObject { ["fileName"] = fileName, ["fileSize"] = fileLength };
            using var mreq = Build(HttpMethod.Post, root + "/api/v2/match", appId, appSecret, body.ToJsonString());
            using var mres = await http.SendAsync(mreq, ct).ConfigureAwait(false);
            if (!mres.IsSuccessStatusCode) return [];
            var mid = 0L;
            try
            {
                var mjson = JsonNode.Parse(await mres.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                mid = mjson?["matches"]?.AsArray().FirstOrDefault()?["episodeId"]?.GetValue<long>() ?? 0;
            }
            catch { return []; }
            if (mid <= 0) return [];

            // ② 取弹幕：GET /api/v2/comment/{id}?format=json
            using var creq = Build(HttpMethod.Get, root + $"/api/v2/comment/{mid}?format=json&withRelated=true", appId, appSecret, null);
            using var cres = await http.SendAsync(creq, ct).ConfigureAwait(false);
            if (!cres.IsSuccessStatusCode) return [];

            // ③ JSON → bilibili XML → 复用既有解析器
            var xml = ToBilibiliXml(await cres.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return xml.Length == 0 ? [] : await DanmuParser.LoadAsync(xml, null, ct).ConfigureAwait(false);
        }
        catch { return []; }
    }

    /// <summary>弹弹play 的 <c>comments[].p</c> / <c>m</c> 与 bilibili <c>&lt;d p="…"&gt;text&lt;/d&gt;</c> 同构，直接转写。</summary>
    internal static string ToBilibiliXml(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var arr = node?["comments"]?.AsArray();
            if (arr is null || arr.Count == 0) return "";
            var sb = new StringBuilder("<i>");
            foreach (var c in arr)
            {
                var p = c?["p"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(p)) continue;
                var text = c?["m"]?.GetValue<string>() ?? "";
                sb.Append("<d p=\"").Append(Escape(p)).Append("\">").Append(Escape(text)).Append("</d>");
            }
            sb.Append("</i>");
            return sb.ToString();
        }
        catch { return ""; }
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>按官方规范构造请求；给了 appId/secret 就带 X-AppId + X-Signature（HMAC-SHA256，base64）。</summary>
    private static HttpRequestMessage Build(HttpMethod method, string url, string? appId, string? secret, string? jsonBody)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(appId))
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var sig = Convert.ToBase64String(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret ?? ""), Encoding.UTF8.GetBytes(appId + ts + url)));
            req.Headers.TryAddWithoutValidation("X-AppId", appId);
            req.Headers.TryAddWithoutValidation("X-Timestamp", ts);
            req.Headers.TryAddWithoutValidation("X-Signature", sig);
        }
        return req;
    }
}
