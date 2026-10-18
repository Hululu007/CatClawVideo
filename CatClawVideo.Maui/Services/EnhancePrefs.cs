namespace CatClawVideo.Maui.Services;

/// <summary>超分辨率档位（**Windows 独有**，走 mpv 的 gpu 缩放链）。</summary>
public enum EnhanceMode
{
    /// <summary>关（mpv 默认缩放）。</summary>
    Off,

    /// <summary>内建缩放：ewa_lanczossharp 升采样 + mitchell 降采样 + correct-downscaling。
    /// 零下载、几乎不花钱，比 mpv 默认的 bilinear 明显锐利，但不是 AI 超分。</summary>
    Builtin,

    /// <summary>GLSL shader 超分：**Anime4K v4.0（MIT）** 随包 132KB，链 =
    /// Clamp_Highlights → AutoDownscalePre_x2 → Restore_CNN_M → Upscale_CNN_x2_M。
    /// 成熟开源轮子，我们只做目录管理 + 拼 <c>glsl-shaders</c>，不自己写算法。
    /// 缺文件时自动退回 <see cref="Builtin"/>，不会把播放卡死。</summary>
    Shaders,
}

/// <summary>
/// 超分档位的持久化。为什么只有 Windows：这些能力全挂在 mpv 的渲染链上
/// （<c>--scale/--dscale/--glsl-shaders</c>），安卓端 ExoPlayer 没有等价钩子。
/// </summary>
public static class EnhancePrefs
{
    const string Key = "video_enhance";

    public static EnhanceMode Load() =>
        Enum.TryParse<EnhanceMode>(Preferences.Default.Get(Key, nameof(EnhanceMode.Off)), out var m)
            ? m : EnhanceMode.Off;

    public static void Save(EnhanceMode mode) => Preferences.Default.Set(Key, mode.ToString());

    public static string Label(EnhanceMode mode) => mode switch
    {
        EnhanceMode.Builtin => "内建缩放",
        EnhanceMode.Shaders => "Anime4K",
        _ => "关",
    };

    public static EnhanceMode Next(EnhanceMode mode) => mode switch
    {
        EnhanceMode.Off => EnhanceMode.Builtin,
        EnhanceMode.Builtin => EnhanceMode.Shaders,
        _ => EnhanceMode.Off,
    };

    public static string Hint(EnhanceMode mode) => mode switch
    {
        EnhanceMode.Builtin =>
            "已开启「内建缩放」：升采样用 ewa_lanczossharp、降采样 mitchell，并开启 correct-downscaling。" +
            "\n\n零下载、几乎不增加开销，比默认缩放锐利。【重新起播生效】",
        EnhanceMode.Shaders =>
            "已开启「Anime4K」超分（v4.0，官方推荐链：Clamp_Highlights → AutoDownscalePre_x2 → " +
            "Restore_CNN_M → Upscale_CNN_x2_M）。\n\n动画片源效果最明显（线条更干净、边缘更锐）；" +
            "真人片源也能用，但更推荐「内建缩放」。\n\n【重新起播生效】；若显卡吃力/掉帧就退回「内建缩放」。",
        _ => "已关闭超分（用 mpv 默认缩放）。【重新起播生效】",
    };
}
