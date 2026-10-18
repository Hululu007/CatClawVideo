namespace CatClawVideo.Maui.Services;

/// <summary>
/// Windows 播放内核选择（**Windows 独有**）。
/// <para>历史：libmpv 一直只给杜比视界片源用（RPU tone-map 必须），普通片源走 FFmpegInteropX/MF。
/// 但 mpv 独有的能力（插帧、以后的 Anime4K/NIS 超分、gpu-next 缩放）只有走 mpv 才生效 ——
/// 2026-10-18 用户要求把默认内核换成 mpv。</para>
/// <para>保留开关的原因：个别流（异常封装/加密代理流）可能 mpv 吃不下而 MF 能放，
/// 出问题时一键切回「兼容模式」即可，不用改代码重编。</para>
/// </summary>
public static class PlayerBackendPrefs
{
    const string Key = "player_backend_mpv";

    /// <summary>true = 优先 libmpv（默认）；false = 强制 FFmpegInteropX/MF（兼容模式）。</summary>
    public static bool PreferMpv() => Preferences.Default.Get(Key, true);

    public static void Save(bool preferMpv) => Preferences.Default.Set(Key, preferMpv);

    public static string Label(bool preferMpv) => preferMpv ? "mpv（默认）" : "MF 兼容";

    /// <summary>供设置页的通用选择器枚举档位（回环）。</summary>
    public static bool Next(bool preferMpv) => !preferMpv;

    public static string Hint(bool preferMpv) => preferMpv
        ? "已切到 mpv 内核：【重新起播生效】。\n\n" +
          "好处：插帧可用，HDR/杜比视界 tone-map 一致，后续超分（Anime4K/NIS）也在这一侧。\n\n" +
          "若某个片源卡顿/黑屏/没声音，先把「插帧」关掉试试；仍然不行就切回「MF 兼容」。"
        : "已切到 MF 兼容内核（FFmpegInteropX/MediaFoundation）：最稳，但【插帧不可用】（它依赖 mpv）。\n\n" +
          "【重新起播生效】。";
}
