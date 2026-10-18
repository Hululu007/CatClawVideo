namespace CatClawVideo.Maui.Services;

/// <summary>插帧档位（**Windows 独有**）。</summary>
public enum InterpolationMode
{
    /// <summary>关（mpv 默认：video-sync=audio）。</summary>
    Off,

    /// <summary>平滑运动：<c>--video-sync=display-resample --interpolation=yes --tscale=oversample</c>。
    /// 帧率对齐显示器、由时间维重采样补出中间帧；开销极低（GPU 侧几乎免费），
    /// 消除 24fps 在 60/120Hz 上的抖动 —— 但**不是运动补偿**，快速横摇的大位移会看出残影/不连贯。</summary>
    Smooth,

    /// <summary>真运动补偿：<c>--vf=lavfi=[minterpolate=…mi_mode=mci:mc_mode=aobmc:vsbmc=1]</c>。
    /// ffmpeg 的块匹配光流插帧，零额外依赖（随包的 mpv-2.dll 自带 lavfi）。
    /// <para>实测（2026-10-18，Ryzen 5 5600 + RTX 4060，随包 mpv-2.dll 直接调 API / vo=null）：
    /// 1080p 31.5fps 源 → <c>estimated-vf-fps=60.00</c>，CPU 约 **1.0 个核**；
    /// 比我最初估的 3~5 核低得多 ⇒ 1080p 完全有余量，4K 才有压力。</para></summary>
    MotionCompensated,
}

/// <summary>
/// 插帧档位的持久化：设置页写、播放页起播时读。
/// <para>为什么只有 Windows：我们的 Windows 播放器是 libmpv，插帧就是两条现成的 mpv 路径
/// （时间维重采样 / lavfi minterpolate）；安卓端是 ExoPlayer(Media3)，只有 GlEffect 那层
/// shader 钩子，**没有**等价的时间维滤镜链，AI 插帧更是没有现实路径。</para>
/// </summary>
public static class InterpolationPrefs
{
    const string Key = "video_interp";

    public static InterpolationMode Load() =>
        Enum.TryParse<InterpolationMode>(Preferences.Default.Get(Key, nameof(InterpolationMode.Off)), out var m)
            ? m : InterpolationMode.Off;

    public static void Save(InterpolationMode mode) => Preferences.Default.Set(Key, mode.ToString());

    public static string Label(InterpolationMode mode) => mode switch
    {
        InterpolationMode.Smooth => "平滑运动",
        InterpolationMode.MotionCompensated => "运动补偿（重）",
        _ => "关",
    };

    /// <summary>点一下轮一档：关 → 平滑运动 → 运动补偿 → 关。</summary>
    public static InterpolationMode Next(InterpolationMode mode) => mode switch
    {
        InterpolationMode.Off => InterpolationMode.Smooth,
        InterpolationMode.Smooth => InterpolationMode.MotionCompensated,
        _ => InterpolationMode.Off,
    };

    /// <summary>切换后给用户的说明（重新起播生效）。</summary>
    public static string Hint(InterpolationMode mode) => mode switch
    {
        InterpolationMode.Smooth =>
            "已开启「平滑运动」：帧率对齐屏幕刷新率，24fps 的抖动会明显减少，开销很小。\n\n" +
            "【重新起播生效】下次点开视频时应用。若觉得画面发虚或音画不同步，换回「关」。",
        InterpolationMode.MotionCompensated =>
            "已开启「运动补偿」：ffmpeg minterpolate 真插帧（块匹配光流），24/30fps 插到 60fps，快速横摇也连贯。\n\n" +
            "【代价实测很低】1080p 30→60 约 1 个 CPU 核（Ryzen 5 5600 实测），4K 片源才会吃紧。\n\n" +
            "【重新起播生效】卡顿就换回「关」或「平滑运动」。",
        _ => "已关闭插帧（mpv 默认：音画同步优先，画面按原帧率输出）。\n\n【重新起播生效】。",
    };
}
