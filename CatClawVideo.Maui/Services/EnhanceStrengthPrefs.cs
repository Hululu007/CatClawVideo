namespace CatClawVideo.Maui.Services;

/// <summary>超分强度（决定 Anime4K 用哪一档 CNN 模型，越大越锐也越吃 GPU）。</summary>
public enum EnhanceStrength
{
    /// <summary>轻：Restore_CNN_S + Upscale_CNN_x2_S（约 35 KB 权重，最省）。</summary>
    Light,

    /// <summary>中：M 档（默认，画质/开销平衡，1080p→4K 在 4060 上无压力）。</summary>
    Medium,

    /// <summary>强：L 档（更锐，GPU 开销明显上升；4K 或高刷屏建议先试中档）。</summary>
    Strong,
}

/// <summary>超分强度的持久化。随包的 Anime4K v4.0 里 S/M/L/UL/VL 五档都在，
/// 这里先暴露 S/M/L 三档（UL/VL 权重更大，留给以后按需开）。</summary>
public static class EnhanceStrengthPrefs
{
    const string Key = "enhance_strength";

    public static EnhanceStrength Load() =>
        Enum.TryParse<EnhanceStrength>(Preferences.Default.Get(Key, nameof(EnhanceStrength.Medium)), out var v)
            ? v : EnhanceStrength.Medium;

    public static void Save(EnhanceStrength v) => Preferences.Default.Set(Key, v.ToString());

    public static string Label(EnhanceStrength v) => v switch
    {
        EnhanceStrength.Light => "轻（S）",
        EnhanceStrength.Strong => "强（L）",
        _ => "中（M）",
    };

    /// <summary>对应 Anime4K 文件里的档位字母。</summary>
    public static string Tier(EnhanceStrength v) => v switch
    {
        EnhanceStrength.Light => "S",
        EnhanceStrength.Strong => "L",
        _ => "M",
    };

    public static EnhanceStrength Next(EnhanceStrength v) => v switch
    {
        EnhanceStrength.Light => EnhanceStrength.Medium,
        EnhanceStrength.Medium => EnhanceStrength.Strong,
        _ => EnhanceStrength.Light,
    };
}
