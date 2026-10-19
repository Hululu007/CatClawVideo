namespace CatClawVideo.Maui.Services;

/// <summary>超分强度（**功能已移除**，仅保留类型以保持编译）。</summary>
public enum EnhanceStrength { Light, Medium, Strong }

public static class EnhanceStrengthPrefs
{
    public static EnhanceStrength Load() => EnhanceStrength.Medium;
    public static void Save(EnhanceStrength v) { }
    public static string Label(EnhanceStrength v) => "中（M）";
    public static string Tier(EnhanceStrength v) => "M";
    public static EnhanceStrength Next(EnhanceStrength v) => EnhanceStrength.Medium;
}
