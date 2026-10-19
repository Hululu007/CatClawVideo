namespace CatClawVideo.Maui.Services;

/// <summary>超分档位（**功能已移除**，仅保留类型以保持编译）。</summary>
public enum EnhanceMode { Off, Builtin, Shaders }

/// <summary>
/// 超分辨率：2026-10-19 用户决定**整线移除**（实测收益不稳定、还占 GPU/带宽）。
/// <see cref="Load"/> 恒返回 <see cref="EnhanceMode.Off"/>，后端永远不挂 shader、不换缩放器。
/// </summary>
public static class EnhancePrefs
{
    public static EnhanceMode Load() => EnhanceMode.Off;
    public static void Save(EnhanceMode mode) { }
    public static string Label(EnhanceMode mode) => "关";
    public static EnhanceMode Next(EnhanceMode mode) => EnhanceMode.Off;
    public static string Hint(EnhanceMode mode) => "超分辨率功能已移除。";
}
