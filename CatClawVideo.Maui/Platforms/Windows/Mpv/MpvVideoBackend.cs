using CatClawVideo.Maui.Controls;
using OpenTK.Graphics.OpenGL;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CatClawVideo.Maui.Platforms.Windows.Mpv;

/// <summary>
/// libmpv 播放后端（Windows）：杜比视界/HDR 片源的正确出路。
/// FFmpegInteropX 不解析 DV 的 RPU 动态元数据，P5 基础层按错误色彩空间直出
/// ⇒ 发灰泛紫；mpv（gpu 渲染器 + FFmpeg RPU 解析 + tone-mapping）能把 DV/HDR10
/// 正确压到 SDR 显示。渲染宿主见 <see cref="MpvRenderControl"/>（GL→D3D11 互操作，
/// 无 ANGLE）。本类实现 <see cref="IVideoPlayerImplementation"/>，由 Handler 在
/// 检测到 DV 片源时代管播放。
/// </summary>
public unsafe sealed class MpvVideoBackend : IVideoPlayerImplementation, IDisposable
{
    private readonly VideoPlayerView _view;
    private readonly MpvRenderControl _renderControl;

    private IntPtr _mpv;
    private IntPtr _renderCtx;
    private Thread? _eventPump;
    private volatile bool _pumpStop;
    private bool _renderCtxReady;

    private double _pendingSpeed = 1.0;
    private double _pendingVolume = -1;
    private IReadOnlyDictionary<string, string>? _headers;
    /// <summary>当前源是否杜比视界（决定 hwdec 软解/硬解，initialize 前定案）。</summary>
    private bool _dvSource;

    /// <summary>必须在 Initialize() 之前调用：DV 源强制软解（hwdec=no），RPU 才能到达 mpv 做 tone-map。</summary>
    public void SetDolbyVision(bool dv)
    {
        _dvSource = dv;
        if (_mpv != IntPtr.Zero)
            // auto（零拷贝：d3d11va 帧直接留在 GPU 上给 mpv 的 GPU 渲染器用）——
            // 2026-10-18 从 auto-copy 改过来：拷回内存每帧都要过一次 CPU/总线，4K 上是卡顿主因之一。
            // DV 仍必须 no（软解保 RPU，见上）。
            MpvLib.SetPropertyString(_mpv, "hwdec", dv ? "no" : "auto");
        BtLog($"[mpv] DV 源={dv} → hwdec={(dv ? "no（软解，保 RPU）" : "auto（零拷贝）")}");
    }

    public MpvVideoBackend(VideoPlayerView view, MpvRenderControl renderControl)
    {
        _view = view;
        _renderControl = renderControl;
    }

    /// <summary>初始化 mpv 实例 + 渲染上下文。失败抛异常（Handler 回落 FFmpeg 路径）。</summary>
    public void Initialize()
    {
        _mpv = MpvLib.mpv_create();
        if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败");

        // 关键选项：全部在 initialize 之前设置
        MpvLib.SetPropertyString(_mpv, "vo", "libmpv");
        // 默认硬解 copy 模式（解码帧拷回内存再进 GL，规避 GL-DX interop 硬解的驱动差异）。
        // ⚠ DV 源必须软解：d3d11va 等 hwaccel 的 HEVC 输出 GPU surface，**不携带 RPU**，
        //   mpv 拿不到 DV 元数据 → P5 的 IPT 色彩被当普通 YCbCr 解读 → 发绿发紫（12:56 实测）。
        //   是否 DV 由 Handler 探测后经 SetDolbyVision 下发（initialize 前必须定案）。
        MpvLib.SetPropertyString(_mpv, "hwdec", _dvSource ? "no" : "auto");   // 零拷贝，见 SetDolbyVision
        MpvLib.SetPropertyString(_mpv, "keep-open", "no");
        MpvLib.SetPropertyString(_mpv, "idle", "yes");
        MpvLib.SetPropertyString(_mpv, "force-window", "no");
        MpvLib.SetPropertyString(_mpv, "audio-display", "no");
        MpvLib.SetPropertyString(_mpv, "input-default-bindings", "no");
        MpvLib.SetPropertyString(_mpv, "osc", "no");
        // DV/HDR → SDR tone-map：默认 auto（HDR10 用 bt.2390，DV 走 RPU 指导），这里显式钉住
        MpvLib.SetPropertyString(_mpv, "tone-mapping", "bt.2446a");
        // 网络流：mpv 自带缓存（默认 demuxer-max-bytes 150MiB 已够），Range 直连可 seek
        MpvLib.SetPropertyString(_mpv, "cache", "yes");
        // 2026-10-19 实测："播放突然卡住"是**数据供不上**（mpv 日志 Still buffering 一路涨、
        //   End buffering waited 174.997813 secs、Enter buffering 100% → 0%），与解码/渲染无关
        //   （vf 为空、丢帧仅 2）。夸克代理链路吞吐不稳时，64MiB/10s 的缓存太浅，一抖就断流。
        //   调到 512MiB / 60s：源慢但稳定时能扛过抖动，代价是起播多等一会儿、多占点内存。
        // ⚠ 2026-10-19 回退：这两行（512MiB + 128MiB 回退缓存）是我上一轮加的，怀疑它让 mpv 在
        //   大文件（1.29GB）上"先攒很久才播 / 攒满前后行为异常"，实测症状是"读一小会儿就完全不再发请求"。
        //   回到保守值（mpv 默认 150MiB 的一半），先把变量收窄，再按代理逐连接日志定位真因。
        MpvLib.SetPropertyString(_mpv, "demuxer-max-bytes", "64MiB");
        MpvLib.SetPropertyString(_mpv, "demuxer-max-back-bytes", "16MiB");
        // ⚠ readahead-secs 决定"**预读够多少秒才开始播**"，别调大：
        //   2026-10-19 实测把它从 10 调到 60 后，夸克原画这条涓流线路永远凑不够 60 秒 ⇒
        //   进度条停在 0.x 秒、日志一路 Still buffering（[0.566667s] 卡住不动）。
        //   缓存容量靠 demuxer-max-bytes（上限）撑，预读时长保持 10 秒（够起播、也够缓冲抖动）。
        MpvLib.SetPropertyString(_mpv, "demuxer-readahead-secs", "10");
        MpvLib.SetPropertyString(_mpv, "cache-pause", "yes");
        // 网络超时与重试：跟播放场景对齐（长超时，别秒断）
        MpvLib.SetPropertyString(_mpv, "network-timeout", "30");
        MpvLib.SetPropertyString(_mpv, "stream-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=10");

        // DV 日志：详细日志由 wait_event 的 LOG_MESSAGE 事件透出（见 EventPump）

        // 插帧（Windows 独有，见 Services.InterpolationPrefs）：video-sync/interpolation 属初始化期选项，
        // 起播前定案；读完偏好即落地，默认档不改变任何原有行为。
        ApplyInterpolation();

        var rc = MpvLib.mpv_initialize(_mpv);
        if (rc < 0) throw new InvalidOperationException($"mpv_initialize 失败: {MpvErrorStr(rc)}");

        // 属性观察（事件泵据此推 View 层状态；位置由 View 层定时器轮询 GetPosition，不走事件）
        Observe("track-list", MpvLib.MpvFormatString);
        // 视频真实像素尺寸：夸克转码的 mp4 容器（tkhd）声明比例经常是错的（16:9），
        // mpv 会打「Using container aspect ratio」并按坏声明拉伸 → 21:9 铺满无黑边。
        // 就绪后强制 video-aspect-override=编码像素比，恢复标准 letterbox。
        Observe("video-params/dw", MpvLib.MpvFormatInt64);
        Observe("video-params/dh", MpvLib.MpvFormatInt64);

        // 渲染上下文：等 RenderControl 就绪（GL 上下文已 current）后再建
        _renderControl.Render += OnRenderFrame;

        // mpv 内部日志桥：这套 mpv-2.dll 构建没导出 mpv_request_log_level（入口点缺失，
        // 13:12 实测把整个 Initialize 炸了）——改用 log-file 写文件（独立于事件机制）。
        try
        {
            var mpvLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CatClawVideo.debug", "logs", "mpv.log");
            MpvLib.SetPropertyString(_mpv, "log-file", mpvLog);
            MpvLib.SetPropertyString(_mpv, "log-append", "yes");
        }
        catch { /* 日志文件失败不致命 */ }

        _pumpStop = false;
        _eventPump = new Thread(EventPump) { IsBackground = true, Name = "mpv-events" };
        _eventPump.Start();

        BtLog($"[mpv] 初始化完成（hwdec={(_dvSource ? "no" : "auto")}, tone-mapping=bt.2446a）");
    }

    private static string MpvErrorStr(int code) => $"code={code}";

    private void Observe(string name, int format)
    {
        var n = MpvLib.ToUtf8Z(name);
        fixed (byte* np = n)
            MpvLib.mpv_observe_property(_mpv, 0, np, format);
    }

    private static void BtLog(string msg) =>
        Maui.Services.BtFileLog.Write(msg);

    /// <summary>
    /// 插帧：把设置页选定的档位落地到 mpv（**Windows 独有**，安卓端 ExoPlayer 没有等价钩子）。
    /// <para>① 平滑运动 = <c>video-sync=display-resample + interpolation + tscale=oversample</c>：
    /// 时间维重采样出中间帧，开销极低。依赖渲染侧上报 swap 估出刷新率 ——
    /// <see cref="OnRenderFrame"/> 每次渲染后都调了 <c>mpv_render_context_report_swap</c>，这条链是通的。</para>
    /// <para>② 运动补偿 = 上面三条 + <c>vf=lavfi=[minterpolate=…mi_mode=mci]</c>：真光流插帧，CPU 重。</para>
    /// <para>③ 关 = 恢复 mpv 默认（<c>video-sync=audio</c>，画面按原帧率输出）。</para>
    /// </summary>
    private void ApplyInterpolation()
    {
        var mode = Services.InterpolationPrefs.Load();

        // 防呆：DV 源在上层已被强制软解（hwdec=no，为了保住 RPU 做 tone-map），
        // 再叠一层 CPU 光流插帧会把 5600 直接吃满、必然掉帧 —— 自动降级到「平滑运动」。
        if (mode == Services.InterpolationMode.MotionCompensated && _dvSource)
        {
            BtLog("[mpv] DV 源 + 运动补偿 → 自动降级为「平滑运动」（软解已吃 CPU，再插帧必掉帧）");
            mode = Services.InterpolationMode.Smooth;
        }

        switch (mode)
        {
            case Services.InterpolationMode.Smooth:
                MpvLib.SetPropertyString(_mpv, "video-sync", "display-resample");
                MpvLib.SetPropertyString(_mpv, "interpolation", "yes");
                MpvLib.SetPropertyString(_mpv, "tscale", "oversample");
                break;

            case Services.InterpolationMode.Rife:
                // 插帧/RIFE 已于 2026-10-19 整线移除（用户决定）：Load() 恒返回 Off，此分支不可达。
                // 保留空分支只为兼容历史存档里的旧枚举值，不做任何事。
                break;

            case Services.InterpolationMode.MotionCompensated:
                MpvLib.SetPropertyString(_mpv, "video-sync", "display-resample");
                MpvLib.SetPropertyString(_mpv, "interpolation", "yes");
                MpvLib.SetPropertyString(_mpv, "tscale", "oversample");
                // mi_mode=mci 光流插帧 + aobmc 自适应重叠块补偿 + vsbmc 变尺寸块匹配：
                // ffmpeg 里画质最好的一档；fps=60 是目标输出帧率（1080p 上 5600 跑得动）。
                var target = Services.InterpTargetPrefs.Fps(Services.InterpTargetPrefs.Load());
                MpvLib.SetPropertyString(_mpv, "vf",
                    $"lavfi=[minterpolate=fps={target}:mi_mode=mci:mc_mode=aobmc:vsbmc=1]");
                break;

            default:
                MpvLib.SetPropertyString(_mpv, "video-sync", "audio");
                MpvLib.SetPropertyString(_mpv, "interpolation", "no");
                break;
        }
        EffectiveInterpolation = mode;   // 实际生效档（重源保护可能已降级）——状态浮层读它

        // ★ 硬解方式要跟着插帧走（2026-10-18 实测：est-vf-fps=23、实际 24fps ⇒ minterpolate 等于直通）：
        //   minterpolate 是 **CPU 滤镜**，而 hwdec=auto 给的是 GPU 帧（d3d11va）——帧要先下载回内存，
        //   互操作开销把滤镜拖垮。插帧档统一改 auto-copy（解码后立刻拷回内存，滤镜拿到的是内存帧），
        //   关插帧时再用回零拷贝 auto。DV 源始终 no（保 RPU）。
        if (!_dvSource && _mpv != IntPtr.Zero)
        {
            var wantHwdec = mode == Services.InterpolationMode.Off ? "auto" : "auto-copy";
            MpvLib.SetPropertyString(_mpv, "hwdec", wantHwdec);
            BtLog($"[mpv] 插帧={mode} → hwdec={wantHwdec}（CPU 滤镜需要内存帧；关插帧时用零拷贝）");
        }
        BtLog($"[mpv] 插帧={Services.InterpolationPrefs.Label(mode)}（{mode}）");

        ApplyEnhance();
    }

    /// <summary>
    /// 超分（**Windows 独有**）：仅用 mpv 内建的 gpu 缩放链，零下载、零额外依赖。
    /// <para>mpv 默认 <c>--scale=bilinear</c> 偏软；这里换成 ewa_lanczossharp（Jinc 系，锐利且振铃可控）
    /// + mitchell 降采样 + correct-downscaling/linear-downscaling（在**线性光**里缩放，避免暗部糊）。
    /// 色度保持 bilinear：色度平面乱升只会放大色带、白花 GPU。</para>
    /// <para>下一步（用户已认可）：接 Anime4K / NVIDIA Image Scaling 的 GLSL shader（都是开源现成轮子），
    /// 走 <c>--glsl-shaders</c> —— 那才是真"超分"，这里先把零下载这档做出来。</para>
    /// </summary>
    /// <summary>拼 Anime4K shader 链（返回 null = 随包文件缺失）。mpv 的列表分隔符用 ':'，
    /// 路径里的反斜杠统一成正斜杠（mpv 在 Windows 上对两者都认，但正斜杠更保险）。</summary>
    private static string? BuildAnime4KChain()
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "shaders", "anime4k");
            // 档位由「超分强度」决定：S/M/L（随包的 Anime4K v4.0 五档都在，先暴露这三档）
            var tier = Services.EnhanceStrengthPrefs.Tier(Services.EnhanceStrengthPrefs.Load());
            string[] files =
            [
                "Anime4K_Clamp_Highlights.glsl",
                "Anime4K_AutoDownscalePre_x2.glsl",
                $"Anime4K_Restore_CNN_{tier}.glsl",
                $"Anime4K_Upscale_CNN_x2_{tier}.glsl",
            ];
            var parts = new List<string>();
            foreach (var f in files)
            {
                var p = System.IO.Path.Combine(dir, f);
                if (!System.IO.File.Exists(p)) return null;   // 缺一不可（链断了 mpv 会报错）
                parts.Add(p.Replace('\\', '/'));
            }
            // ⚠ 分隔符必须是 ',' —— 2026-10-18 真机自测踩过：按 Anime4K 文档的 ':' 拼出来，
            //   mpv 会把整串当成**一个文件名**（logs/mpv.log: "Cannot open file 'a.glsl:b.glsl:…'"），
            //   四个 shader 一个都没加载。'=' 逗号才是 mpv 字符串列表的正规分隔符。
            return string.Join(",", parts);
        }
        catch { return null; }
    }

    private void ApplyEnhance()
    {
        var mode = Services.EnhancePrefs.Load();
        switch (mode)
        {
            case Services.EnhanceMode.Shaders:
                // Anime4K v4.0（MIT）官方推荐链：Clamp_Highlights（防高光过曝）→ AutoDownscalePre_x2
                // （内容已足够高时先降采样，省掉无意义的 x2）→ Restore_CNN_M（修线条）→ Upscale_CNN_x2_M（x2 升采样）。
                // 缺文件（未随包/被删）时自动退回内建缩放，绝不把播放卡死。
                var chain = BuildAnime4KChain();
                if (chain is not null)
                {
                    // ⚠ glsl-shaders 是**列表型**属性：用 mpv_set_property_string 写会把整串当成
                    //   一个文件名（2026-10-18 真机自测连踩 ':' 与 ',' 两次，mpv.log 均报
                    //   "Cannot open file 'a.glsl,b.glsl,…'"）。必须走列表命令：先 clr 再逐个 append。
                    MpvLib.Command(_mpv, "glsl-shaders-clr");
                    foreach (var shaderFile in chain.Split(','))
                        if (shaderFile.Length > 0) MpvLib.Command(_mpv, "glsl-shaders-append", shaderFile);
                    MpvLib.SetPropertyString(_mpv, "scale", "ewa_lanczossharp");
                    MpvLib.SetPropertyString(_mpv, "dscale", "mitchell");
                    MpvLib.SetPropertyString(_mpv, "correct-downscaling", "yes");
                    EffectiveEnhance = Services.EnhanceMode.Shaders;
                    BtLog($"[mpv] 超分=Anime4K(M)（{chain}）");
                    break;
                }
                EffectiveEnhance = Services.EnhanceMode.Builtin;
                BtLog("[mpv] 超分=Anime4K 缺文件 → 退回内建缩放");
                goto case Services.EnhanceMode.Builtin;

            case Services.EnhanceMode.Builtin:
                EffectiveEnhance = Services.EnhanceMode.Builtin;
                MpvLib.SetPropertyString(_mpv, "scale", "ewa_lanczossharp");
                MpvLib.SetPropertyString(_mpv, "dscale", "mitchell");
                MpvLib.SetPropertyString(_mpv, "correct-downscaling", "yes");
                MpvLib.SetPropertyString(_mpv, "linear-downscaling", "yes");
                BtLog($"[mpv] 超分={Services.EnhancePrefs.Label(mode)}（scale=ewa_lanczossharp, dscale=mitchell, correct-downscaling=yes）");
                break;

            default:
                EffectiveEnhance = Services.EnhanceMode.Off;
                try { MpvLib.Command(_mpv, "glsl-shaders-clr"); } catch { }   // 关档也要清链，否则残留上一档
                BtLog("[mpv] 超分=关（用 mpv 默认缩放，已清 shader 链）");
                break;
        }
    }

    // ═══════════ 渲染 ═══════════

    private void OnRenderFrame(TimeSpan frameDelta)
    {
        if (_mpv == IntPtr.Zero) return;
        EnsureRenderContext();
        if (!_renderCtxReady || _renderControl.FrameBuffer is null) return;

        var fb = _renderControl.FrameBuffer;
        var fbo = new MpvLib.MpvOpenGLFbo
        {
            Fbo = fb.GLFrameBufferHandle,
            W = fb.BufferWidth,
            H = fb.BufferHeight,
            InternalFormat = 0,
        };
        var flip = 0;   // GL FBO 渲染：mpv 按 GL 惯例（左下原点）输出，不翻转（参考实现 flipY=0；=1 会画面倒置）

        // ⚠ 参数只给 [Fbo, FlipY, 终止符]——绝不能传 BlockForTargetTime 且 Data=null：
        //   该 param 的数据类型是 int*，mpv 内部直接解引用 ⇒ null ⇒ AV（12:43/12:52 两次闪退根因）。
        MpvLib.MpvRenderParam* pars = stackalloc MpvLib.MpvRenderParam[3];
        {
            pars[0] = new(MpvLib.RenderParamOpenGLFbo, &fbo);
            pars[1] = new(MpvLib.RenderParamFlipY, &flip);
            pars[2] = default; // 终止符 MPV_RENDER_PARAM_INVALID
            // ★ 真实上屏帧率（2026-10-18 用户质疑"插帧没用、显示在骗人"后加）：
            //   mpv_render_context_update 返回的 MPV_RENDER_UPDATE_FRAME(=1) 表示"确实有一帧新画面要画"；
            //   数它 = **真正送到屏幕上的不同帧数**（比 estimated-vf-fps 可信 —— 后者在这条链上实测恒等于源帧率）。
            //   必须在 render 之前调用，这正是该 API 的用法。
            // ⚠ 2026-10-19 实测移除：这里原来用 frameDelta 估刷新率喂 display-fps-override（为插帧的
            //   display-resample 服务）。实测它估出来在 71/74/78 之间乱跳 → mpv 日志刷
            //   "Assuming 71/74/78 FPS for display sync" → 每秒重做一次 display sync →
            //   表现为**进度条不走、画面冻住**（实际 1.1fps、drops 上升）。插帧移除后是 video-sync=audio，
            //   根本不需要 override，故整段去掉。
            // ★ 上报播放状态（2026-10-19 定位）：VideoPlayerView 只在状态=Playing/Buffering 时才启动
            //   **位置轮询定时器**，而 mpv 后端从来没上报过状态 ⇒ 进度条永远 0:00、继续观看/历史进度/
            //   弹幕同步全部失效（以前默认内核是 MF、会报状态，所以没暴露）。这里每 500ms 采样一次，
            //   只在状态变化时才抬事件，避免刷屏。
            var nowState = Environment.TickCount64;
            if (nowState - _lastStateMs >= 500)
            {
                _lastStateMs = nowState;
                var st = MpvLib.GetPropertyString(_mpv, "paused-for-cache") == "yes"
                    ? CatClawVideo.Maui.Controls.VideoPlayerState.Buffering
                    : MpvLib.GetPropertyString(_mpv, "paused") == "yes"
                        ? CatClawVideo.Maui.Controls.VideoPlayerState.Paused
                        : CatClawVideo.Maui.Controls.VideoPlayerState.Playing;
                if (st != _lastReportedState)
                {
                    _lastReportedState = st;
                    var view = _view;
                    DispatchOnUi(() => view.RaiseStateChanged(st));
                }
            }

            var upFlags = MpvLib.mpv_render_context_update(_renderCtx);
            if ((upFlags & 1UL) != 0) TickFpsWindow();

            var rc = MpvLib.mpv_render_context_render(_renderCtx, pars);
            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                BtLog($"[mpv] 首帧渲染调用完成 rc={rc} fbo={fb.GLFrameBufferHandle} {fb.BufferWidth}x{fb.BufferHeight}");
            }
            else if (rc < 0 && _lastRenderRc >= 0)
            {
                BtLog($"[mpv] 渲染开始报错 rc={rc}（此前正常）");
            }
            _lastRenderRc = rc;
        }
        MpvLib.mpv_render_context_report_swap(_renderCtx);
    }

    private bool _firstFrameLogged;
    private int _lastRenderRc;

    private double _lastDisplayHzGuess;
    private int _hzStableTicks;

    private long _lastStateMs;
    private CatClawVideo.Maui.Controls.VideoPlayerState _lastReportedState;

    private long _fpsWindowStartMs = Environment.TickCount64;
    private long _fpsWindowFrames;
    private double _achievedFps;

    /// <summary>真实上屏帧率（约 2 秒滚动一次）：数的是 mpv 报告"有新帧要画"的次数，
    /// 也就是真正显示出来的不同帧 —— 插帧有没有生效，看它就够了。</summary>
    public double AchievedFps => _achievedFps;

    /// <summary>把实测到的显示刷新率喂给 mpv（vo=libmpv 时它查不到，display-fps=0 会让
    /// display-resample 失效）。外面用渲染循环的 frameDelta 测出来调它。</summary>
    public void SetDisplayFpsOverride(double hz)
    {
        if (_mpv == IntPtr.Zero || hz < 20 || hz > 240) return;
        try
        {
            MpvLib.SetPropertyString(_mpv, "display-fps-override",
                hz.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            BtLog($"[mpv] display-fps-override={hz:0.##}（vo=libmpv 查不到刷新率，按实测喂给它）");
        }
        catch { }
    }

    /// <summary>诊断用：把 mpv 侧跟"插帧到底有没有生效"有关的原始值一次性读出来。</summary>
    public string DescribeFilters()
    {
        if (_mpv == IntPtr.Zero) return "(no mpv)";
        try
        {
            string Rd(string p)
            {
                var v = MpvLib.GetPropertyString(_mpv, p);
                return v is null ? "-" : (v.Length > 90 ? v[..90] + "…" : v);
            }
            double RdD(string p) => MpvLib.GetPropertyDouble(_mpv, p);
            // 2026-10-19 用户质疑"48Mbps 还卡"后补：把**入流速率与缓存余量**也打出来 ——
            //   这才是判"到底谁慢"的唯一硬指标（raw-input-rate 单位是 bytes/s）。
            var inBps = RdD("demuxer-cache-state/raw-input-rate");
            return $"pos={RdD("time-pos"):0.0}s cache={RdD("demuxer-cache-duration"):0.0}s waiting={Rd("paused-for-cache")} "
                 + $"in={inBps / 1024.0:0}KB/s "
                 + $"vf=\"{Rd("vf")}\" est-vf-fps={RdD("estimated-vf-fps"):0.##} display-fps={RdD("display-fps"):0.##} "
                 + $"drops={RdD("frame-drop-count"):0} mistimed={RdD("mistimed-frame-count"):0} "
                 + $"decoder-drops={RdD("decoder-frame-drop-count"):0} video-sync={Rd("video-sync")} interp={Rd("interpolation")}";
        }
        catch (Exception ex) { return "(err " + ex.Message + ")"; }
    }

    private void TickFpsWindow()
    {
        var now = Environment.TickCount64;
        _fpsWindowFrames++;
        var elapsed = now - _fpsWindowStartMs;
        if (elapsed >= 2000)
        {
            _achievedFps = _fpsWindowFrames * 1000.0 / elapsed;
            _fpsWindowFrames = 0;
            _fpsWindowStartMs = now;
        }
    }

    /// <summary>在 GL 上下文当前线程上创建 render context（RenderControl.Ready 已 MakeCurrent）。</summary>
    private void EnsureRenderContext()
    {
        if (_renderCtxReady) return;

        var apiType = MpvLib.ToUtf8Z(MpvLib.RenderApiTypeOpenGL);
        MpvLib.MpvOpenGLInitParams initParams = default;
        initParams.GetProcAddress = &GetProcAddressThunk;
        MpvLib.MpvRenderParam* pars = stackalloc MpvLib.MpvRenderParam[3];
        fixed (byte* api = apiType)
        {
            pars[0] = new(MpvLib.RenderParamApiType, api);
            pars[1] = new(MpvLib.RenderParamOpenGLInitParams, &initParams);
            pars[2] = default;
            IntPtr ctx;
            var rc = MpvLib.mpv_render_context_create(&ctx, _mpv, pars);
            if (rc < 0)
            {
                BtLog($"[mpv] render_context_create 失败: {rc}");
                _renderCtxReady = false;
                return;
            }
            _renderCtx = ctx;
        }
        _renderCtxReady = true;
        Current = this;   // 左上角状态浮层据此读真实统计
        BtLog("[mpv] 渲染上下文就绪（OpenGL → D3D11 互操作）");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void* GetProcAddressThunk(void* ctx, byte* name) =>
        (void*)MpvRenderContext.GetProcAddress(MpvLib.FromUtf8(name));

    // ═══════════ 事件泵 ═══════════

    private void EventPump()
    {
        while (!_pumpStop && _mpv != IntPtr.Zero)
        {
            var ev = MpvLib.mpv_wait_event(_mpv, 0.5);
            if (ev == IntPtr.Zero) continue;
            var e = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvLib.MpvEvent>(ev);
            switch (e.EventId)
            {
                case MpvLib.EventShutdown:
                    return;
                case MpvLib.EventLogMessage:
                    // [prefix] level: text —— mpv 内部日志（GL 初始化错误、fallback、AV 前的最后遗言）
                    var log = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvLib.MpvEventLogMessage>((IntPtr)e.Data);
                    var prefix = MpvLib.FromUtf8(log.Prefix);
                    var level = MpvLib.FromUtf8(log.Level);
                    var text = MpvLib.FromUtf8(log.Text).TrimEnd();
                    BtLog($"[mpv:{prefix}/{level}] {text}");
                    break;
                case MpvLib.EventFileLoaded:
                    // 媒体就绪：View 层据此触发 MediaOpened（含 ShouldAutoPlay 的自动 Play）。
                    // 事件泵在后台线程，View/WinUI 访问必须回 UI 线程。
                    DispatchOnUi(() => _view.RaiseMediaOpened());
                    FixAspectOverride();
                    break;
                case MpvLib.EventPropertyChange:
                    FixAspectOverride();
                    break;
                case MpvLib.EventEndFile:
                    var endFile = System.Runtime.InteropServices.Marshal.PtrToStructure<MpvLib.MpvEventEndFile>((IntPtr)e.Data);
                    var reason = endFile.Reason;
                    if (reason == MpvLib.EndFileReasonEof)
                        DispatchOnUi(() => _view.RaiseMediaEnded());
                    else if (reason == MpvLib.EndFileReasonError)
                        DispatchOnUi(() => _view.RaiseMediaFailed("播放失败: mpv 解码/网络错误"));
                    break;
            }
        }
    }

    /// <summary>当前比例档（AspectFit/Original 需要「按编码像素比例 letterbox」）。</summary>
    private VideoAspect _aspectMode = VideoAspect.AspectFit;

    /// <summary>
    /// 强制 video-aspect-override = 编码像素比例（dw/dh）。
    /// 夸克转码容器声明的比例常是错的（mpv: "Using container aspect ratio" → 21:9 被拉成 16:9
    /// 铺满无黑边）；读 video-params 的真实像素尺寸覆写，恢复标准上下黑边。幂等：值同不写。
    /// </summary>
    /// <summary>本条源**实际生效**的插帧档（可能被重源保护改过）。
    /// 状态浮层显示它而不是设置里的档位，避免"设置写运动补偿、实际跑平滑运动"这种不一致
    /// （2026-10-18 自测：4K 源上浮层显示运动补偿，而保护已降级）。</summary>
    public Services.InterpolationMode EffectiveInterpolation { get; private set; } = Services.InterpolationMode.Off;

    /// <summary>本条源**实际生效**的超分档（重源保护会把它降成内建缩放）。浮层据此显示。</summary>
    public Services.EnhanceMode EffectiveEnhance { get; private set; } = Services.EnhanceMode.Off;

    /// <summary>真出画标志：只有**真正解出视频帧**时 mpv 的 <c>video-params/dw|dh</c> 才有值。
    /// <para>2026-10-18 用它做 mpv 兜底的判据：夸克代理流上 mpv 会「成功打开」——
    /// 日志 <c>audio=playing, video=eof</c>、首帧渲染 rc=0 —— 但画面**全黑**（rc=0 只说明我们的
    /// render 调用成功，不说明有帧）。同一条流旧内核 MF 能正常出画，所以上层靠这个标志在
    /// 6 秒内没出画时回落 MF。</para></summary>
    public bool HasVideoFrame { get; private set; }

    /// <summary>mpv 是否在等数据（paused-for-cache=yes）。给"卡死自动换线"看门狗判活用。</summary>
    public bool IsWaitingForCache =>
        _mpv != IntPtr.Zero && MpvLib.GetPropertyString(_mpv, "paused-for-cache") == "yes";

    /// <summary>demuxer 缓存里还有多少秒数据。≈0 且 IsWaitingForCache ⇒ 这条流真的没数据（不是慢）。</summary>
    public double CacheSeconds =>
        _mpv == IntPtr.Zero ? 0 : MpvLib.GetPropertyDouble(_mpv, "demuxer-cache-duration");

    /// <summary>当前活动实例（左上角状态浮层读它取真实数值）。Dispose 时置空。</summary>
    public static MpvVideoBackend? Current { get; private set; }

    /// <summary>供上层的状态浮层判断：这个实例是不是**当前真正在用**的那个内核。
    /// 之前浮层直接读 Current，切到 MF 兼容内核后还显示旧 mpv 的陈旧数值（2026-10-18 自测发现）。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// 真实播放统计：源分辨率/源帧率、滤镜后实际帧率、渲染面（输出）分辨率。
    /// <para>为什么用这些属性：<c>video-params/w|h</c> 是解码出来的真实像素尺寸；
    /// <c>container-fps</c> 是容器的源帧率；<c>estimated-vf-fps</c> 是**过完滤镜链后**的实际帧率
    /// （插帧开着时它会真的升到 60，而不是我们"打算插到多少"）；<c>osd-dimensions</c> 是真正显示出来的
    /// 尺寸（超分开着时就是放大后的表面）。没出画时返回 null，浮层显示占位。</para>
    /// </summary>
    public (double SrcW, double SrcH, double SrcFps, double OutFps, double OutW, double OutH)? ReadStats()
    {
        if (_mpv == IntPtr.Zero) return null;
        try
        {
            var sw = MpvLib.GetPropertyDouble(_mpv, "video-params/w");
            var sh = MpvLib.GetPropertyDouble(_mpv, "video-params/h");
            if (sw <= 0 || sh <= 0) return null;
            return (sw, sh,
                MpvLib.GetPropertyDouble(_mpv, "container-fps"),
                MpvLib.GetPropertyDouble(_mpv, "estimated-vf-fps"),
                MpvLib.GetPropertyDouble(_mpv, "osd-dimensions/w"),
                MpvLib.GetPropertyDouble(_mpv, "osd-dimensions/h"));
        }
        catch { return null; }
    }

    private void FixAspectOverride()
    {
        if (_mpv == IntPtr.Zero) return;
        try
        {
            var dw = MpvLib.GetPropertyDouble(_mpv, "video-params/dw");
            var dh = MpvLib.GetPropertyDouble(_mpv, "video-params/dh");
            if (dw > 0 && dh > 0)
            {
                HasVideoFrame = true;   // ★ 出画判据（必须在下面的比例档早退之前）
                ApplyHeavySourceGuard(dw, dh);   // ★ 4K 保护（见下）
            }
            if (_aspectMode is not (VideoAspect.AspectFit or VideoAspect.Original)) return;
            if (dw <= 0 || dh <= 0) return;
            var wantValue = dw / dh;
            var want = $"{(long)dw}/{(long)dh}";
            var cur = MpvLib.GetPropertyString(_mpv, "video-aspect-override");
            // ⚠ 必须按**数值**比较：mpv 会把自己设过的比例规范化成小数（"1920/1080" → "1.777778"），
            //   字符串比较永远不相等 ⇒ 每次属性变化都再设一次 ⇒ 设置本身又触发属性变化 ⇒ **自己触发自己**，
            //   表现就是 mpv 日志里同一秒内反复 "Set property: video-aspect-override"，
            //   紧接着 "Audio/Video desynchronisation detected! ... too slow" —— 播放卡顿的真凶。
            //   （这段逻辑早就在，但以前 mpv 只服务 DV 片源，今天 mpv 成了默认内核才被放大到每个片源。
            //     2026-10-18 实测。）
            if (TryParseAspect(cur, out var curValue) && Math.Abs(curValue - wantValue) < 0.002) return;
            MpvLib.SetPropertyString(_mpv, "video-aspect-override", want);
            BtLog($"[mpv] 强制按编码像素比例 letterbox: {want}（忽略容器声明 {cur ?? "无"}）");
        }
        catch { }
    }

    private bool _heavyGuardApplied;

    /// <summary>
    /// 4K 保护（2026-10-18 自测实测发现）：源是 4K（3840×2160）时，
    /// ① <c>minterpolate</c> 的 mci 运动补偿**根本跑不动** —— 左上角状态浮层实测显示
    ///    「源 24fps · 3840×2160 / 出 24fps」，即插帧一帧都没插上，白白占着 CPU；
    /// ② Anime4K 的 Upscale_CNN_x2 更是白烧 GPU —— 4K 源在 2560 宽的屏上本来就是**降采样**，
    ///    先放大 2 倍再缩回去，纯浪费。
    /// 所以在**第一帧到达、知道真实尺寸之后**（这里）做一次性降级：重插帧 → 平滑运动，
    /// 并清掉 shader 链（内建缩放继续有效）。用户的选择不写回设置，只对这条源生效。
    /// </summary>
    private void ApplyHeavySourceGuard(double dw, double dh)
    {
        if (_heavyGuardApplied || _mpv == IntPtr.Zero) return;
        _heavyGuardApplied = true;
        try
        {
            var bigSource = dh > 1200 || dw > 2000;      // 超过 1080p 就算重源
            if (!bigSource) return;

            var mode = Services.InterpolationPrefs.Load();
            if (mode == Services.InterpolationMode.MotionCompensated)
            {
                MpvLib.SetPropertyString(_mpv, "vf", "");
                MpvLib.SetPropertyString(_mpv, "video-sync", "display-resample");
                MpvLib.SetPropertyString(_mpv, "interpolation", "yes");
                MpvLib.SetPropertyString(_mpv, "tscale", "oversample");
                EffectiveInterpolation = Services.InterpolationMode.Smooth;   // 浮层据实显示，别报设置值
                BtLog($"[mpv] ⚠ 重源保护：{(long)dw}×{(long)dh} 上 minterpolate 跑不动（实测出帧率不涨）→ 本条源改用平滑运动");
            }

            if (Services.EnhancePrefs.Load() == Services.EnhanceMode.Shaders)
            {
                MpvLib.Command(_mpv, "glsl-shaders-clr");
                MpvLib.SetPropertyString(_mpv, "scale", "ewa_lanczossharp");
                MpvLib.SetPropertyString(_mpv, "dscale", "mitchell");
                MpvLib.SetPropertyString(_mpv, "correct-downscaling", "yes");
                EffectiveEnhance = Services.EnhanceMode.Builtin;   // 浮层据实显示，别报设置值
                BtLog($"[mpv] ⚠ 重源保护：{(long)dw}×{(long)dh} 是降采样场景，Anime4K 的 x2 升采样无意义 → 本条源改用内建缩放");
            }
        }
        catch { }
    }

    /// <summary>mpv 的比例值可能是 "16/9" 也可能是规范化后的 "1.777778"，两种都要认。</summary>
    private static bool TryParseAspect(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var slash = s.IndexOf('/');
        try
        {
            if (slash > 0)
            {
                var num = double.Parse(s[..slash], System.Globalization.CultureInfo.InvariantCulture);
                var den = double.Parse(s[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                if (den <= 0) return false;
                value = num / den;
                return value > 0;
            }
            return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value) && value > 0;
        }
        catch { return false; }
    }

    private void DispatchOnUi(Action action)
    {
        try
        {
            var dispatcher = _view.Dispatcher;
            if (dispatcher is null) { action(); return; }
            dispatcher.Dispatch(() => { try { action(); } catch { } });
        }
        catch { try { action(); } catch { } }
    }

    // ═══════════ IVideoPlayerImplementation ═══════════

    void IVideoPlayerImplementation.SetSource(string? url, IReadOnlyDictionary<string, string>? headers)
    {
        if (_mpv == IntPtr.Zero) return;
        _headers = headers;

        if (string.IsNullOrEmpty(url))
        {
            MpvLib.Command(_mpv, "stop");
            return;
        }

        // 防盗链头（TVBox 源 Referer/UA）→ mpv http 选项
        if (headers is not null)
        {
            var pairs = new List<string>();
            foreach (var kv in headers)
                pairs.Add($"{kv.Key}: {kv.Value}");
            MpvLib.SetPropertyString(_mpv, "http-header-fields", string.Join(",", pairs));
        }

        HasVideoFrame = false;   // 新源：复位出画标志（上层的起播看门狗按它判定）
        var rc = MpvLib.Command(_mpv, "loadfile", url, "replace");
        BtLog($"[mpv] loadfile rc={rc}: {url}");
        if (rc >= 0 && Math.Abs(_pendingSpeed - 1.0) > 0.001)
            MpvLib.SetPropertyString(_mpv, "speed", _pendingSpeed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (rc >= 0 && _pendingVolume >= 0)
            MpvLib.SetPropertyString(_mpv, "volume", (_pendingVolume * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.Play() { if (_mpv != IntPtr.Zero) MpvLib.SetPropertyString(_mpv, "pause", "no"); }
    void IVideoPlayerImplementation.Pause() { if (_mpv != IntPtr.Zero) MpvLib.SetPropertyString(_mpv, "pause", "yes"); }
    void IVideoPlayerImplementation.Stop() { if (_mpv != IntPtr.Zero) MpvLib.Command(_mpv, "stop"); }

    void IVideoPlayerImplementation.Seek(TimeSpan position)
    {
        if (_mpv == IntPtr.Zero) return;
        MpvLib.Command(_mpv, "seek", position.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "absolute");
    }

    void IVideoPlayerImplementation.SetVolume(double volume)
    {
        _pendingVolume = volume;
        if (_mpv != IntPtr.Zero)
            MpvLib.SetPropertyString(_mpv, "volume", Math.Clamp(volume * 100, 0, 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.SetAspect(VideoAspect aspect)
    {
        if (_mpv == IntPtr.Zero) return;
        _aspectMode = aspect;
        if (aspect is not (VideoAspect.AspectFit or VideoAspect.Original))
        {
            // Fill/AspectFill 档：清掉像素比覆写（这两档本来就要覆盖/裁切）
            try { MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no"); } catch { }
        }
        switch (aspect)
        {
            case VideoAspect.Original:
                // 「原始比例」= 保持源画幅且**绝不超出可视区**：
                // downscale-only = 视频大于窗口时自动缩到完整可见，小于窗口时保持 1:1 不放大。
                // ⚠ 绝不能用 unscaled=yes（1:1 直出）：4K 源在 1080p 窗口会直接超出屏幕（14:24 教训）。
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "downscale-only");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no");
                MpvLib.SetPropertyString(_mpv, "panscan", "0");
                break;
            case VideoAspect.AspectFit:
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "no");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no");
                MpvLib.SetPropertyString(_mpv, "panscan", "0");
                break;
            case VideoAspect.AspectFill:
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "no");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "no");
                MpvLib.SetPropertyString(_mpv, "panscan", "1");
                break;
            case VideoAspect.Fill:
                MpvLib.SetPropertyString(_mpv, "video-unscaled", "no");
                MpvLib.SetPropertyString(_mpv, "video-aspect-override", "display");
                MpvLib.SetPropertyString(_mpv, "panscan", "0");
                break;
        }
    }

    void IVideoPlayerImplementation.KeepScreenOn(bool keep) { /* Windows 无意义 */ }

    void IVideoPlayerImplementation.SetSpeed(double speed)
    {
        _pendingSpeed = speed;
        if (_mpv != IntPtr.Zero)
            MpvLib.SetPropertyString(_mpv, "speed", Math.Clamp(speed, 0.1, 4.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.SetExternalSubtitle(string? path, string? mime, double offsetSeconds)
    {
        if (_mpv == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(path))
        {
            MpvLib.Command(_mpv, "sub-remove");
            return;
        }
        var rc = MpvLib.Command(_mpv, "sub-add", path, "auto", "CatClawExt");
        if (rc < 0) BtLog($"[mpv] sub-add 失败 rc={rc}（外挂字幕加载失败）");
        MpvLib.SetPropertyString(_mpv, "sub-delay", (-offsetSeconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    void IVideoPlayerImplementation.SetDecoderMode(VideoDecoderMode mode)
    {
        if (_mpv == IntPtr.Zero) return;
        MpvLib.SetPropertyString(_mpv, "hwdec", mode switch
        {
            VideoDecoderMode.Hardware => "auto",
            VideoDecoderMode.Software => "no",
            _ => "auto-copy",
        });
    }

    IReadOnlyList<VideoTrackInfo> IVideoPlayerImplementation.GetTracks(VideoTrackKind kind)
    {
        if (_mpv == IntPtr.Zero) return Array.Empty<VideoTrackInfo>();
        var json = MpvLib.GetPropertyString(_mpv, "track-list");
        if (string.IsNullOrEmpty(json)) return Array.Empty<VideoTrackInfo>();
        try
        {
            var arr = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var kindStr = kind switch { VideoTrackKind.Audio => "audio", VideoTrackKind.Subtitle => "sub", _ => "video" };
            var list = new List<VideoTrackInfo>();
            foreach (var t in arr.EnumerateArray())
            {
                if (!t.TryGetProperty("type", out var tp) || tp.GetString() != kindStr) continue;
                var id = t.TryGetProperty("id", out var idv) ? idv.GetInt64().ToString() : "?";
                var title = t.TryGetProperty("title", out var tv) ? tv.GetString() : null;
                var lang = t.TryGetProperty("lang", out var lv) ? lv.GetString() : null;
                var codec = t.TryGetProperty("codec", out var cv) ? cv.GetString() : null;
                var selected = t.TryGetProperty("selected", out var sv) && sv.GetBoolean();
                var display = title ?? lang ?? (codec != null ? $"{kindStr} {id} ({codec})" : $"轨道 {id}");
                list.Add(new VideoTrackInfo(id, display, selected));
            }
            return list;
        }
        catch { return Array.Empty<VideoTrackInfo>(); }
    }

    void IVideoPlayerImplementation.SelectTrack(VideoTrackKind kind, string? id)
    {
        if (_mpv == IntPtr.Zero) return;
        var prop = kind switch { VideoTrackKind.Audio => "aid", VideoTrackKind.Subtitle => "sid", _ => "vid" };
        MpvLib.SetPropertyString(_mpv, prop, string.IsNullOrEmpty(id) ? "no" : id);
    }

    TimeSpan IVideoPlayerImplementation.GetPosition() =>
        _mpv == IntPtr.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, MpvLib.GetPropertyDouble(_mpv, "time-pos")));

    TimeSpan IVideoPlayerImplementation.GetDuration() =>
        _mpv == IntPtr.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, MpvLib.GetPropertyDouble(_mpv, "duration")));

    TimeSpan IVideoPlayerImplementation.GetBufferedPosition() =>
        _mpv == IntPtr.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(0, MpvLib.GetPropertyDouble(_mpv, "demuxer-cache-time")));

    bool IVideoPlayerImplementation.IsBufferedPositionReliable => _mpv != IntPtr.Zero;

    bool IVideoPlayerImplementation.IsWaitingForData =>
        _mpv != IntPtr.Zero && MpvLib.GetPropertyDouble(_mpv, "paused-for-cache") > 0.5;

    public void Dispose()
    {
        IsDisposed = true;
        if (ReferenceEquals(Current, this)) Current = null;   // 别把作废实例留给状态浮层
        DisposeCore();
    }

    private void DisposeCore()
    {
        BtLog($"[mpv] Dispose 开始（renderCtx={_renderCtx != IntPtr.Zero} mpv={_mpv != IntPtr.Zero}）");
        _pumpStop = true;
        _renderControl.Render -= OnRenderFrame;
        if (_renderCtx != IntPtr.Zero)
        {
            // 文档要求：render_context_free 必须在创建它的线程、GL 上下文 current 时调用
            //（本类生命周期全部在 UI 线程，共享 GL 上下文也常驻 current 于 UI 线程）
            try
            {
                MpvLib.mpv_render_context_free(_renderCtx);
                BtLog("[mpv] render context 已释放");
            }
            catch (Exception ex)
            {
                BtLog($"[mpv] render context 释放异常: {ex.GetType().Name}");
            }
            _renderCtx = IntPtr.Zero;
        }
        if (_mpv != IntPtr.Zero)
        {
            MpvLib.mpv_wakeup(_mpv);
            if (_eventPump is { } pump && !pump.Join(TimeSpan.FromSeconds(3)))
                BtLog("[mpv] 警告：事件泵 3s 未退出，仍继续销毁");
            MpvLib.mpv_terminate_destroy(_mpv);
            _mpv = IntPtr.Zero;
            BtLog("[mpv] 实例已销毁");
        }
    }
}
