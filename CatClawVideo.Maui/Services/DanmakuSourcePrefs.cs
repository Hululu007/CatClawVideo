namespace CatClawVideo.Maui.Services;

/// <summary>
/// 弹幕源设置（2026-10-19 目标 P3）。
/// <para>默认**关闭**（地址为空）：只有用户显式填了"弹弹play 兼容"服务器地址才启用。
/// 支持三种填法：①自建 misaka_danmu_server / danmu_api；②官方 api.dandanplay.net + 自己申请的
/// AppId/AppSecret（官方免注册不可用，实测 403）；③只填地址不带凭据（自建服务通常不需要）。</para>
/// <para>环境变量 <c>CATCLAW_DANDANPLAY_URL</c> / <c>_APPID</c> / <c>_SECRET</c> 优先级最高，
/// 便于自动化测试与不想改设置的高级用户。</para>
/// </summary>
public static class DanmakuSourcePrefs
{
    private const string KeyUrl = "danmaku.ddp.url";
    private const string KeyAppId = "danmaku.ddp.appid";
    private const string KeySecret = "danmaku.ddp.secret";

    /// <summary>服务器根地址（空 = 关闭弹弹play 源）。</summary>
    public static string Url
    {
        get
        {
            var env = CatClawVideo.Core.Services.DandanplayDanmaku.EnvUrl;
            return env.Length > 0 ? env : Preferences.Default.Get(KeyUrl, "");
        }
        set => Preferences.Default.Set(KeyUrl, value?.Trim() ?? "");
    }

    public static string AppId
    {
        get
        {
            var env = CatClawVideo.Core.Services.DandanplayDanmaku.EnvAppId;
            return env.Length > 0 ? env : Preferences.Default.Get(KeyAppId, "");
        }
        set => Preferences.Default.Set(KeyAppId, value?.Trim() ?? "");
    }

    public static string Secret
    {
        get
        {
            var env = CatClawVideo.Core.Services.DandanplayDanmaku.EnvSecret;
            return env.Length > 0 ? env : Preferences.Default.Get(KeySecret, "");
        }
        set => Preferences.Default.Set(KeySecret, value?.Trim() ?? "");
    }

    public static string Label => Url.Length == 0 ? "关（用站点自带）" : Url;
}
