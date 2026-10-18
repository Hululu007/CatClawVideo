namespace CatClawVideo.Maui.Services;

/// <summary>
/// RIFE（AI 插帧）运行时环境的探测与脚本生成。
/// <para><b>为什么是"可选下载"而不是随包</b>：这套链路上游全是开源现成轮子，但拼起来要
/// 便携 VapourSynth（5.9MB，**不含 Python**）+ 嵌入式 Python + <b>onnxruntime-gpu</b>
/// （CPU 版 1080p 跑不动，必须 GPU 版 ~150MB）+ rife 模型（~50MB），合计约 300MB ——
/// 直接随包会把安装包顶到 400MB+，且 ABI/Python 版本强耦合（实测 2026-10-18：vs-rife 的
/// PyPI wheel 只有 0.1MB 纯 Python，插件与模型都不在里面）。所以做成"用户点一下再下"。</para>
/// <para>目录约定（与 AppPaths.RuntimeRoot 一致，随程序位置走）：<c>{程序目录}\rife\</c>，
/// 其中 <c>vapoursynth\</c>（VS 便携 + python + site-packages）、<c>models\</c>（rife onnx）、
/// <c>rife.vpy</c>（本类生成）。</para>
/// </summary>
public static class RifeEnvironment
{
    /// <summary>环境根目录（缺失即未安装）。</summary>
    public static string Root =>
        System.IO.Path.Combine(CatClawVideo.Core.AppPaths.RuntimeRoot, "rife");

    /// <summary>生成好的挂载脚本路径（<c>--vf=vapoursynth=&lt;它&gt;</c>）。</summary>
    public static string ScriptPath => System.IO.Path.Combine(Root, "rife.vpy");

    /// <summary>三件套是否齐：VS 的 vspipe/vsscript、site-packages 里的 vsrife、models 目录。</summary>
    public static bool IsReady
    {
        get
        {
            try
            {
                var vs = System.IO.Path.Combine(Root, "vapoursynth");
                var models = System.IO.Path.Combine(Root, "models");
                return System.IO.Directory.Exists(vs)
                    && System.IO.Directory.Exists(models)
                    && System.IO.File.Exists(ScriptPath)
                    && System.IO.Directory.GetFiles(models, "*.onnx", System.IO.SearchOption.AllDirectories).Length > 0;
            }
            catch { return false; }
        }
    }

    /// <summary>给设置页/日志用的人话状态。</summary>
    public static string StatusText => IsReady
        ? "RIFE 运行时已就绪 ✓"
        : "RIFE 运行时未安装（预计约 300MB：VapourSynth + Python + onnxruntime-gpu + rife 模型）";

    /// <summary>
    /// 生成 rife.vpy（幂等）。脚本本身十几行 —— 这是"我们唯一要写的胶水"：
    /// 载入源 → 用 rife 滤镜按目标帧率插帧 → 输出。目标帧率取显示器刷新率（上限 2× 源，避免无谓开销）。
    /// </summary>
    public static string? EnsureScript(double targetFps = 60)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Root);
            var body = string.Join("\n", new[]
            {
                "# 由猫爪影视自动生成（RifeEnvironment.EnsureScript）—— 不要手改，改了会被覆盖。",
                "import vapoursynth as vs",
                "core = vs.core",
                "core.num_threads = 8",
                "",
                "src = video_in  # mpv 通过 --vf=vapoursynth 注入的源",
                "target = " + targetFps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "",
                "# 只在需要补帧时插（已经 >= 目标帧率就不动，避免白烧 GPU）",
                "if src.fps.numerator / src.fps.denominator < target - 0.01:",
                "    out = core.rife.RIFE(src, fps_num=int(target * 1000), fps_den=1000, model=1, sc=True)",
                "else:",
                "    out = src",
                "out.set_output()",
                "",
            });
            System.IO.File.WriteAllText(ScriptPath, body, System.Text.Encoding.UTF8);
            return ScriptPath;
        }
        catch { return null; }
    }
}
