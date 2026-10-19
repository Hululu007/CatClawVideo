namespace CatClawVideo.Maui.Services;

/// <summary>插帧目标帧率（**功能已移除**，仅保留类型以保持编译）。</summary>
public enum InterpTarget { Fps60, Fps90, Fps120 }

/// <summary>插帧目标帧率的惰性桩：恒 60，不影响播放。</summary>
public static class InterpTargetPrefs
{
    public static InterpTarget Load() => InterpTarget.Fps60;
    public static void Save(InterpTarget v) { }
    public static int Fps(InterpTarget v) => 60;
    public static string Label(InterpTarget v) => "60fps";
    public static InterpTarget Next(InterpTarget v) => InterpTarget.Fps60;
}
