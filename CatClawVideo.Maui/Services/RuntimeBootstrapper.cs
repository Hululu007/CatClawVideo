namespace CatClawVideo.Maui.Services;

/// <summary>
/// 「统一引导」的可重入入口：启动时由 <c>MauiProgram</c> 把那段引导（起外置 Android 桥 →
/// 桥就绪 → jar/网盘源可用）注册进来，运行中还能再拉一次。
/// <para>为什么需要：引导是启动时**一次性**跑的，环境没装好时它失败退场；等用户下载安装完
/// 运行环境，如果没人再拉一次，程序就停在「空首页 + 订阅站点均拉取失败」，非重启不可
/// （2026-10-15 用户实测反馈）。现在装好即自动再拉一次，遮罩也重走一遍。</para>
/// </summary>
public static class RuntimeBootstrapper
{
    /// <summary>引导实现（由 MauiProgram 在启动装配时赋值）。</summary>
    public static Func<Task>? Run { get; set; }

    /// <summary>是否已经能重跑（没注册就什么都不做）。</summary>
    public static bool Available => Run is not null;

    /// <summary>跑一次引导（并发调用是安全的：桥内部有 ReadyGate 串行化）。</summary>
    public static Task RunAsync() => Run?.Invoke() ?? Task.CompletedTask;
}
