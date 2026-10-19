namespace CatClawVideo.Maui.Services;

/// <summary>插帧档位（**功能已移除**，仅保留类型以保持编译）。</summary>
public enum InterpolationMode { Off, Smooth, MotionCompensated, Rife }

/// <summary>
/// 插帧：2026-10-19 用户决定**整线移除**（实测 minterpolate 无法稳定达标，反而吃满 CPU 拖慢播放）。
/// 现在 <see cref="Load"/> 恒返回 <see cref="InterpolationMode.Off"/>，任何路径都不会再启用插帧；
/// 后端 <c>ApplyInterpolation</c> 因此永远设成"关 + 不插帧"。等以后真要做得住再做，不再拿目标冒充结果。
/// </summary>
public static class InterpolationPrefs
{
    public static InterpolationMode Load() => InterpolationMode.Off;
    public static void Save(InterpolationMode mode) { /* 已停用：不再持久化 */ }
    public static string Label(InterpolationMode mode) => "关";
    public static InterpolationMode Next(InterpolationMode mode) => InterpolationMode.Off;
    public static string Hint(InterpolationMode mode) => "插帧功能已移除。";
}
