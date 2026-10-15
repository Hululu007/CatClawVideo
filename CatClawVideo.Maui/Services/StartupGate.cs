namespace CatClawVideo.Maui.Services;

/// <summary>
/// 启动门禁：**Android 运行环境没就绪就不让进主界面**（2026-10-15 用户要求：
/// 「没环境就不让启动」）。
/// <para>写入方：首页启动自检（<c>HomePage.RenderEnvChecks</c>）—— 运行环境未安装 /
/// 没装完 / 系统镜像或爬虫桥缺件时写入原因（空串 = 放行）。</para>
/// <para>消费方：主页面顶栏（tab 与搜索入口置灰禁用、把人按回首页）+ 首页冷启动遮罩
/// （不再淡出，把自检面板与下载按钮留在眼前）。装好后自检重跑会自动解禁。</para>
/// </summary>
public static class StartupGate
{
    private static string _reason = "";

    /// <summary>当前阻塞原因（空串 = 放行）。</summary>
    public static string BlockReason => _reason;

    /// <summary>是否处于阻塞状态。</summary>
    public static bool IsBlocked => _reason.Length > 0;

    /// <summary>阻塞状态变化（参数 = 新的阻塞原因）。</summary>
    public static event Action<string>? Changed;

    /// <summary>设置阻塞原因（同值不重复通知）。</summary>
    public static void Set(string? reason)
    {
        reason ??= "";
        if (reason == _reason) return;
        _reason = reason;
        try { Changed?.Invoke(reason); } catch { }
    }
}
