namespace CatClawVideo.Maui.Services;

/// <summary>超分辨率档位（**Windows 独有**，走 mpv 的 gpu 缩放链）。</summary>
public enum EnhanceMode
{
    /// <summary>关（mpv 默认缩放）。</summary>
    Off,

    /// <summary>内建缩放：ewa_lanczossharp 升采样 + mitchell 降采样 + correct-downscaling。
    /// 零下载、几乎不花钱，比 mpv 默认的 bilinear 明显锐利，但不是 AI 超分。</summary>
    Builtin,

    /// <summary>GLSL shader 包（Anime4K / NIS 等）—— 下一步接入：随包或按需下载 glsl 目录，
    /// 走 <c>--glsl-shaders</c>。这两个都是成熟开源轮子（Anime4K 动画向、NIS 真人向），
    /// 我们只写目录管理与档位 UI，不自己写算法。</summary>
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
        EnhanceMode.Shaders => "shader（未接入）",
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
            "「shader 超分」还没接入（下一步：随包/下载 Anime4K·NIS 的 glsl，挂 --glsl-shaders）。" +
            "\n\n现在先按「内建缩放」生效。",
        _ => "已关闭超分（用 mpv 默认缩放）。【重新起播生效】",
    };
}
