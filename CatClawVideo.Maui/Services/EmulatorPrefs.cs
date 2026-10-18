namespace CatClawVideo.Maui.Services;

/// <summary>
/// Android 模拟器（Windows 端 jar/网盘源的宿主）相关偏好。
/// <para><b>常驻</b>：应用退出时默认会 <c>ShutdownEmulator()</c>，下次启动就得**冷启动**模拟器
/// （自检里那 ~1 分钟）。开发和测试时这很浪费时间，所以给一个开关：打开后退出不杀模拟器，
/// 下次启动直接复用（已有 <c>EmulatorRunning</c> 复用判断）。代价是它一直占着 ~2.5GB 内存。</para>
/// </summary>
public static class EmulatorPrefs
{
    const string Key = "emulator_keep_alive";

    /// <summary>模拟器常驻（默认关：普通用户关掉应用就应释放内存）。</summary>
    public static bool KeepAlive
    {
        get => Preferences.Default.Get(Key, false);
        set => Preferences.Default.Set(Key, value);
    }

    public static string Label(bool on) => on ? "常驻（退出不关）" : "随应用关闭";

    /// <summary>供设置页通用选择器枚举档位（回环）。</summary>
    public static bool Next(bool on) => !on;
}
