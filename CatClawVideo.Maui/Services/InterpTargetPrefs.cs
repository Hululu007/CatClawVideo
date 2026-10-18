namespace CatClawVideo.Maui.Services;

/// <summary>插帧强度 = 目标帧率。</summary>
public enum InterpTarget
{
    /// <summary>60fps（默认：24/30 → 60，兼容绝大多数片源与显示器）。</summary>
    Fps60,

    /// <summary>90fps。</summary>
    Fps90,

    /// <summary>120fps（高刷屏最顺；同时把 CPU/GPU 开销与插帧伪影风险一起抬高）。</summary>
    Fps120,
}

/// <summary>
/// 插帧目标帧率的持久化。用途：运动补偿喂 <c>minterpolate=fps=&lt;n&gt;</c>，
/// RIFE 喂 <c>rife.vpy</c> 的 <c>fps_num/fps_den</c>。
/// <para>注意：目标高于显示器刷新率没有意义（多出来的帧会被丢掉），所以别盲选 120 ——
/// 上面那行状态浮层显示的 <c>出 xx fps</c> 才是真实结果。</para>
/// </summary>
public static class InterpTargetPrefs
{
    const string Key = "interp_target";

    public static InterpTarget Load() =>
        Enum.TryParse<InterpTarget>(Preferences.Default.Get(Key, nameof(InterpTarget.Fps60)), out var v)
            ? v : InterpTarget.Fps60;

    public static void Save(InterpTarget v) => Preferences.Default.Set(Key, v.ToString());

    public static int Fps(InterpTarget v) => v switch
    {
        InterpTarget.Fps90 => 90,
        InterpTarget.Fps120 => 120,
        _ => 60,
    };

    public static string Label(InterpTarget v) => Fps(v) + "fps";

    public static InterpTarget Next(InterpTarget v) => v switch
    {
        InterpTarget.Fps60 => InterpTarget.Fps90,
        InterpTarget.Fps90 => InterpTarget.Fps120,
        _ => InterpTarget.Fps60,
    };
}
