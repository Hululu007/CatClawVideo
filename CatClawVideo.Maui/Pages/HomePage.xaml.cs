using CatClawVideo.Core.Interfaces;
using CatClawVideo.Core.Models;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;

namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 首页：MacCMS 真实源（分类 + 影片列表）。
/// 海报卡点击 → 观看页（携带源/影片参数拉真实详情与选集）。
///
/// <para><b>遥控器 / 键盘导航</b>：三层焦点 —— 切换源 → 分类 chips → 海报墙。
/// ↑↓ 在层间与层内移动，←→ 在分类 chips 内横向移动、在海报墙内按列移动，
/// OK 激活，Back 逐层退出（海报 → 分类 → 切换源 → 顶部 tab）。
/// 焦点态由本页显式驱动（列表项与 chip 都不是原生可聚焦控件）。</para>
/// </summary>
public partial class HomePage : ContentView, ITabView, IRemoteKeyHandler
{
    private readonly HomeViewModel _vm;

    // ─────────── 焦点分层 ───────────
    private const int LayerTopNav = 0;
    private const int LayerSwitchSite = 1;
    private const int LayerChips = 2;
    private const int LayerPosters = 3;
    private int _layer = LayerSwitchSite;

    private int _chipIndex;
    private int _posterIndex;
    private int _posterColumns = 6;

    // ── 主页推荐网格列数（与海报墙同一套 PosterLayoutHelper 自适应，焦点导航按列走）──
    private int _recColumns = 6;

    /// <summary>分类 chip 的焦点壳（与 Categories 一一对应，随集合变化重建）。</summary>
    private readonly List<Border> _chipShells = new();

    public HomePage(HomeViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = _vm;

        // 分类集合变化 / 选中项变化时刷新 chip 高亮
        _vm.Categories.CollectionChanged += (_, _) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                RebuildChipShells();
                UpdateChipStyles();
            });
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.SelectedCategoryId))
            {
                MainThread.BeginInvokeOnMainThread(UpdateChipStyles);
                MainThread.BeginInvokeOnMainThread(UpdateFilterUi);
                MainThread.BeginInvokeOnMainThread(ApplyModeVisual);   // 主页行流 / 分类网格 切换
            }
            if (e.PropertyName == nameof(HomeViewModel.FilterSummary))
                MainThread.BeginInvokeOnMainThread(UpdateFilterUi);
        };

        // 海报墙布局：与历史/收藏页一致，统一走 PosterLayoutHelper（固定卡片尺寸，列数自适应）
        PosterGrid.SizeChanged += (_, _) =>
        {
            ApplyPosterLayout();
            CaptureColumns();
        };

        // 主页推荐网格：与海报墙完全同一套布局（同 cap、同列数自适应）。
        // 主页模式下 PosterGrid 不可见（Size=0）不触发自己的 SizeChanged，由 HomeGrid 算。
        HomeGrid.SizeChanged += (_, _) =>
        {
#if WINDOWS
            PosterLayoutHelper.Apply(HomeGrid, HomeGrid.Width, HomeGrid.Height, cap: 252);
#else
            PosterLayoutHelper.Apply(HomeGrid, HomeGrid.Width, HomeGrid.Height);
#endif
            CaptureRecColumns();
        };

        // 列表数据变化后（首次加载/翻页）刷新列数并复位越界焦点
        _vm.Items.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnItemsChanged);

        // 主页推荐变化后复位越界的焦点索引（重建清空时 _posterIndex 可能越界）
        _vm.HomeRecItems.CollectionChanged += (_, _) => MainThread.BeginInvokeOnMainThread(OnHomeRecItemsChanged);

        // 应用初始模式视觉（默认主页：推荐网格可见，切换胶囊等 chrome 按模式显隐）
        ApplyModeVisual();

        StartColdStartOverlay();
    }

    // ═══════════ 冷启动遮罩 ═══════════
    //
    // ART guest（QEMU TCG）冷启动实测 41~46 秒（解 590MB initrd + ART 运行时 boot），
    // 期间海报墙全空、只有站点条一行小字——体感「白屏卡死」（2026-09-27 用户反馈）。
    // 遮罩分阶段显示真实进度；首页数据（分类）就绪即淡出进入主界面。
    // 轮询（400ms）而非事件接线：订阅/引擎/首页数据三个信号分属三层，轮询最省接线。

    private IDispatcherTimer? _coldStartTimer;
    private readonly DateTime _coldStartUtc = DateTime.UtcNow;

    /// <summary>本轮首页取数真的开跑过没有（IsHomeLoading 出现过 true）——
    /// 没有它就无法区分「还没开跑」与「已经失败」，后者必须立刻收遮罩。</summary>
    private bool _coldLoadStarted;

    /// <summary>取数已收工（IsHomeLoading 由 true 翻回 false）。配合分类仍为空 = 这轮失败了。</summary>
    private bool _coldLoadFinished;

    /// <summary>冷启动遮罩最长停留（秒）。留够「首选站预算 50s + 回退探测 45s」这条最坏路径，
    /// 又不至于把人困住；超时无条件收起——宁可露出错误提示让人操作。</summary>
    private const int ColdStartCapSeconds = 120;

    /// <summary>运行环境未就绪的原因（空串 = 就绪）。由自检结果写入：
    /// 没装运行环境 / 系统镜像缺件 / 爬虫桥缺件时，冷启动进度条**停住不动**，
    /// 免得用户看着百分比一直爬、以为"在加载"（2026-10-15 用户要求）。</summary>
    private string _envBlockedReason = "";

    /// <summary>桥上报的**真实**进度（-1 = 还没有真实里程碑）。每个百分点都对应一个 adb 实测节点：
    /// 设备出现 0.40 / sys.boot_completed=1 0.70 / 桥 APK 就绪 0.78 / forward 就位 0.85 / ping 通 0.95。</summary>
    private double _realPct = -1;
    private string _realStage = "";

    /// <summary>显示用的进度值（0~1）。每个阶段对它做「先快爬、再放缓」的两段式推进。</summary>
    private double _shownPct = -1;

    /// <summary>当前阶段是从哪个里程碑起的（阶段切换时重置，用于算"本阶段已经爬了多久"）。</summary>
    private double _segPct = 0.01;

    /// <summary>当前阶段的起始时刻（StartColdStartOverlay 里初始化）。</summary>
    private DateTime _segStartUtc;

    /// <summary>快爬段时长（秒）：起步 / 每阶段切换后先在这个时间内冲到位。</summary>
    private const double FastRiseSeconds = 3.5;

    /// <summary>放缓段的时间常数（秒）：快爬结束后按这个速度向"下一档 − 1%"渐近。</summary>
    private const double SlowTauSeconds = 25;

    /// <summary>桥会上报的里程碑档位（与 SpiderBridgeClient.Report 的取值一一对应）。
    /// 用来算"下一档" —— 逼近的上限就是它减 1%，所以进度条永远不会抢先宣布到达下一阶段。</summary>
    private static readonly double[] ProgressLadder =
    {
        0.05, 0.10, 0.12, 0.15, 0.40, 0.45, 0.50, 0.60, 0.70, 0.72, 0.78, 0.85, 0.95,
    };

    /// <summary>
    /// 一个阶段内的两段式进度（用户要的节奏）：
    /// <para>① 快爬段：<see cref="FastRiseSeconds"/> 秒内线性冲到本阶段 plateau = 起点 + 75% 余量；</para>
    /// <para>② 放缓段：之后按 <see cref="SlowTauSeconds"/> 的时间常数向 <paramref name="cap"/> 渐近，
    /// 而 cap 本身是"下一档 − 1%"，所以永远到不了下一档。</para>
    /// 观感：起步 1% → 3.5 秒冲到 ~12% → 放缓；下一阶段到了再冲一次，如此重复。
    /// </summary>
    private static double StageProgress(double fromPct, double cap, DateTime segStart)
    {
        var t = (DateTime.UtcNow - segStart).TotalSeconds;
        var plateau = fromPct + (cap - fromPct) * 0.75;
        return t < FastRiseSeconds
            ? fromPct + (plateau - fromPct) * (t / FastRiseSeconds)
            : plateau + (cap - plateau) * (1 - Math.Exp(-(t - FastRiseSeconds) / SlowTauSeconds));
    }

    /// <summary>比 <paramref name="cur"/> 大的最小档位；已经是最高档则返回它自己（不再爬）。</summary>
    private static double NextMilestone(double cur)
    {
        foreach (var v in ProgressLadder)
            if (v > cur + 1e-9) return v;
        return cur;
    }

    private void StartColdStartOverlay()
    {
        // ★ 仅 Windows 显示（2026-10-03，用户反馈「安卓端也弹启动画面」）：
        //   这个遮罩描述的是 Windows 独有的 QEMU/ART 引擎冷启动（解 391MB initrd + ART boot），
        //   在 Android 上既没有这段冷启动，百分比与「引擎/ART/QEMU」文案也全是假的。
        //   Android 端用进程内 DexSpiderRuntime，直接进主界面即可（数据加载已有骨架/转圈）。
        if (!OperatingSystem.IsWindows()) return;

        // ★ 必须在这里显式打开：XAML 里已改成 IsVisible="False"（免得 Android 端构造期闪一下），
        //   Windows 端由本方法负责显示。
        //   2026-10-04 用户反馈「启动画面给你改没了」—— 当时就漏了这一行。
        ColdStartOverlay.IsVisible = true;

        // 真实进度：外置 Android 桥在每个 adb 实测里程碑上报（设备出现 / 系统启动完成 /
        // 桥 APK 就绪 / forward 就位 / ping 通）。有它就按它走，没有才用起步爬升。
        CatClawVideo.Core.Providers.SpiderBridgeClient.Progress = (pct, stage) =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (pct > _realPct) { _realPct = pct; _realStage = stage; }   // 只前进不回退
            });

        _segStartUtc = _coldStartUtc;      // 第一阶段（起步）从现在开始计时

        _coldStartTimer = Dispatcher.CreateTimer();
        _coldStartTimer.Interval = TimeSpan.FromMilliseconds(400);
        _coldStartTimer.Tick += (_, _) => UpdateColdStartOverlay();
        _coldStartTimer.Start();
        UpdateColdStartOverlay();

        // 环境检测：与引擎预热并行跑（本地探测，毫秒级），结果贴在遮罩上
        _ = RunEnvCheckAsync();
    }

    // ═══════════ 启动环境检测（Windows）═══════════
    //
    // 模拟器冷启这 1 分多钟里用户只能干等，正好用来把「装完为什么起不来 / 慢得离谱」
    // 的根因摆出来：随包 Android 环境是否就位、WHPX 硬件加速是否可用、系统镜像与
    // AVB 参数是否成对、爬虫桥 APK 在不在、磁盘/内存够不够。
    // 全部本地探测，不联网（只有 -accel-check 启一次子进程，秒级返回）。

    /// <summary>后台跑一遍环境检测，就绪后在遮罩里逐条渲染（非 Windows 返回空表 → 面板不显示）。</summary>
    private async Task RunEnvCheckAsync() => await RefreshEnvChecksAsync();

    /// <summary>重新探测并刷新卡片（一键处置执行完后调用）。</summary>
    private async Task RefreshEnvChecksAsync()
    {
        try
        {
            var items = await Task.Run(Services.StartupEnvCheck.Run).ConfigureAwait(true);
            if (items.Count == 0) return;
            RenderEnvChecks(items);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[envcheck] 环境检测失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RenderEnvChecks(IReadOnlyList<Services.EnvCheckItem> items)
    {
        try
        {
            EnvCheckList.Clear();
            var bad = items.Count(i => i.Level is Services.EnvCheckLevel.Warn or Services.EnvCheckLevel.Fail);
            var failed = items.Count(i => i.Level is Services.EnvCheckLevel.Fail);

            // 汇总做成状态药丸（绿=全通过 / 琥珀=有告警 / 红=有失败），比一行灰字更醒目
            var (badgeText, tint) = bad == 0
                ? ($"{items.Count} 项全部通过", Color.FromArgb("#34C759"))
                : failed > 0
                    ? ($"{bad} 项异常", Color.FromArgb("#FF453A"))
                    : ($"{bad} 项需注意", Color.FromArgb("#FF9F0A"));
            EnvCheckSummary.Text = badgeText;
            EnvCheckSummary.TextColor = tint;
            EnvCheckBadge.BackgroundColor = tint.WithAlpha(0.16f);

            // 环境就绪情况 → 决定冷启动进度条要不要停（只看「模拟器能不能起来」的那几项）
            _envBlockedReason = EnvBlocker(items);
            // 同时作为**启动门禁**：没环境就不让进主界面（顶栏禁用 + 遮罩不淡出）
            Services.StartupGate.Set(_envBlockedReason);

            foreach (var it in items) EnvCheckList.Add(BuildEnvRow(it));
            RenderEnvActions(items);
            EnvCheckPanel.IsVisible = true;
            if (_coldStartTimer is not null) ApplyColdStartStatus();   // 门禁状态立刻反映到遮罩文案
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[envcheck] 渲染失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 环境是不是「卡在起点」：返回原因（空串 = 就绪）。
    /// <para>只看真正决定 jar 源能不能起来的三项 —— Android 运行环境（缺/没装完都算）、
    /// 系统镜像缺件、爬虫桥缺件。磁盘/内存这类告警**不**冻结进度（照常能跑）。</para>
    /// </summary>
    private static string EnvBlocker(IReadOnlyList<Services.EnvCheckItem> items)
    {
        foreach (var it in items)
        {
            var blocked = it.Title switch
            {
                // 未装（Fail）/ 没装完或待解压（Warn）都算没就绪；用本机已装 SDK（Info）算就绪
                "Android 运行环境" => it.Level is Services.EnvCheckLevel.Fail or Services.EnvCheckLevel.Warn,
                "系统镜像" or "爬虫桥程序" => it.Level == Services.EnvCheckLevel.Fail,
                _ => false,
            };
            if (blocked) return $"{it.Title}：{it.Detail}";
        }
        return "";
    }

    /// <summary>
    /// 一条检测行：圆点 + 名称（左栏）+ 值（右栏对齐）。异常项的值用状态色，
    /// 并在下方追加一行处置建议（缩进 + 同色）。
    /// </summary>
    private static View BuildEnvRow(Services.EnvCheckItem it)
    {
        var levelColor = it.Level switch
        {
            Services.EnvCheckLevel.Ok => Color.FromArgb("#34C759"),
            Services.EnvCheckLevel.Warn => Color.FromArgb("#FF9F0A"),
            Services.EnvCheckLevel.Fail => Color.FromArgb("#FF453A"),
            _ => Color.FromArgb("#8E8E93"),
        };
        var isProblem = it.Level is Services.EnvCheckLevel.Warn or Services.EnvCheckLevel.Fail;

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 9,
        };
        row.Add(new Microsoft.Maui.Controls.Shapes.Ellipse
        {
            WidthRequest = 7,
            HeightRequest = 7,
            Fill = new SolidColorBrush(levelColor),
            VerticalOptions = LayoutOptions.Center,
        }, 0, 0);

        var name = new Label { Text = it.Title, FontSize = 12.5, VerticalOptions = LayoutOptions.Center };
        name.SetDynamicResource(Label.TextColorProperty, "TextPrimaryColor");
        row.Add(name, 1, 0);

        var value = new Label
        {
            Text = it.Detail,
            FontSize = 12.5,
            HorizontalTextAlignment = TextAlignment.End,
            LineBreakMode = LineBreakMode.WordWrap,
            VerticalOptions = LayoutOptions.Center,
        };
        if (isProblem) value.TextColor = levelColor;
        else value.SetDynamicResource(Label.TextColorProperty, "TextSecondaryColor");
        row.Add(value, 2, 0);

        var stack = new VerticalStackLayout { Spacing = 3 };
        stack.Add(row);
        if (isProblem && it.Hint is { Length: > 0 } hint)
        {
            stack.Add(new Label
            {
                Text = "→ " + hint,
                FontSize = 11,
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(16, 0, 0, 0),
                TextColor = levelColor,
            });
        }
        return stack;
    }


    // ═══════════ 一键处置 ═══════════
    //
    // 检测到的问题当场给按钮，而不是只让用户读文字：虚拟化平台没开 → 提权 DISM 直接开；
    // 载荷/mpv 缺失 → 跑取件脚本或打开下载页；数据目录不可写 → 直接打开目录。
    // 执行完自动重跑检测刷新卡片。

    private void RenderEnvActions(IReadOnlyList<Services.EnvCheckItem> items)
    {
        EnvCheckActions.Clear();
        var actions = items.Where(i => i.Actions is { Count: > 0 })
            .SelectMany(i => i.Actions!)
            .GroupBy(a => a.Id)
            .Select(g => g.First())
            .ToList();
        foreach (var a in actions)
        {
            var btn = new Button
            {
                Text = a.Text,
                FontSize = 12,
                HeightRequest = 30,
                Padding = new Thickness(14, 0),
                CornerRadius = 9,
                BorderWidth = 1,
                BackgroundColor = Color.FromArgb("#9B7ED8").WithAlpha(0.14f),
                CommandParameter = a.Id,
            };
            btn.SetDynamicResource(Button.TextColorProperty, "PrimaryColor");
            btn.SetDynamicResource(Button.BorderColorProperty, "PrimaryColor");
            btn.Clicked += (s, _) => OnEnvActionClicked((s as Button)?.CommandParameter as string);
            EnvCheckActions.Add(btn);
        }
        EnvCheckActions.IsVisible = actions.Count > 0;
    }

    private void OnEnvActionClicked(string? id)
    {
        switch (id)
        {
            case "enable-whpx": _ = EnableWhpxAsync(); break;
            case "howto-whpx": _ = AlertAsync(WhpxHowTo); break;
            case "open-optional-features": OpenOptionalFeatures(); break;
            // 2026-10-12：原先的 fetch-assets / open-release-vm 两个处置已删 ——
            // 它们指向的是已退役的 QEMU ART guest 取件（CatClaw.Qemu 的 vm-assets 发布页），
            // 而新版自检（Services/StartupEnvCheck）不再产生这两个 id：运行环境现在随包，
            // 缺失只可能是安装不完整，正确处置是重装而不是去下别的包。
            // 2026-10-13：0.2.6 轻量包不再随带 AndroidSdk，运行环境改首运下载 —— 上面的注释
            // 「随包、缺失=安装不完整」自此失效；download-runtime 成为新的标准处置。
            case "open-datadir": OpenDataDir(); break;
            case "download-runtime": _ = DownloadRuntimeAsync(); break;
            // 2026-10-14：自动下载之外补两条手动路径 ——「手动下载」只把地址给到手（代理/墙/断流时
            // 用浏览器、迅雷、别的机器都行），「本地安装包安装」把用户下好的 zip 校验+解压+自检；
            // 「立即安装」是自检发现缓存里已有一份完整包时的直达按钮（不必再选文件）。
            case "manual-download": _ = ManualDownloadAsync(); break;
            case "install-runtime": _ = InstallRuntimeAsync(null); break;
            case "install-cached-runtime": _ = InstallRuntimeAsync(Services.RuntimeProvisioner.ZipPath); break;
        }
    }

    /// <summary>正在下载运行环境（防重入：面板上的按钮连点不叠加任务）。</summary>
    private bool _provisioningRuntime;

    /// <summary>
    /// 一键下载 Android 运行环境（模拟器 + 镜像，约 0.9GB）：
    /// 确认（含磁盘预检文案）→ 下载/解压（进度写进面板汇总行）→ 失败给可读原因并保留断点。
    /// </summary>
    private async Task DownloadRuntimeAsync()
    {
        if (_provisioningRuntime) return;
        _provisioningRuntime = true;
        try
        {
            var root = Services.RuntimeProvisioner.TargetRoot;
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            var free = drive.AvailableFreeSpace;
            var ok = await Shell.Current.DisplayAlertAsync(
                "下载 Android 运行环境",
                $"jar 爬虫源与网盘源需要一个 Android 运行环境：\n" +
                $"· 下载约 {Services.RuntimeProvisioner.AssetBytes / 1024 / 1024} MB（断点续传）\n" +
                $"· 解压后约 {Services.RuntimeProvisioner.UnpackedBytes / 1024 / 1024 / 1024:0.#} GB，" +
                (Services.RuntimeProvisioner.IsPortable
                    ? "落在**程序同目录**（随程序一起拷贝/删除）\n"
                    : "程序目录不可写，落在用户数据目录\n") +
                $"· 磁盘 {drive.Name} 可用 {free / 1024.0 / 1024 / 1024:0.#} GB（需约 3.6 GB）\n\n" +
                $"网页源 / MacCMS / JS 脚本源不受影响，可先跳过。现在开始下载吗？",
                "开始下载", "以后再说");
            if (!ok) return;

            await Task.Run(() => Services.RuntimeProvisioner.ProvisionAsync(
                RuntimeProgress("运行环境下载中…", showSpeed: true), RuntimeStage, CancellationToken.None))
                .ConfigureAwait(true);

            await RefreshEnvChecksAsync();
            await Shell.Current.DisplayAlertAsync("运行环境就绪",
                "Android 运行环境下载完成。首次启动模拟器约需 1 分钟（之后走快照会快很多）。", "好");
            await RestartBootstrapAsync();     // 装好就地把桥拉起来，不用重启程序
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[runtime] 下载失败：{ex.GetType().Name}: {ex.Message}");
            await RefreshEnvChecksAsync();
            await Shell.Current.DisplayAlertAsync("下载失败",
                ex.Message + "\n\n稍后可在本面板重试（支持从断点续传）。", "知道了");
        }
        finally
        {
            _provisioningRuntime = false;
        }
    }

    /// <summary>
    /// 手动下载：只把安装包地址交到用户手里，不自己起下载。
    /// <para>自动下载只有 GitHub Releases 直连一条路，代理/墙/断流任一环节不通就整条废掉；
    /// 用浏览器、迅雷、网盘客户端甚至别的机器下载都行 —— 拿到 zip 后由
    /// <see cref="InstallRuntimeAsync"/> 校验并解压，装出来的东西与自动下载完全一致。</para>
    /// </summary>
    private async Task ManualDownloadAsync()
    {
        var url = Services.RuntimeProvisioner.AssetUrl;
        const string open = "用浏览器打开直链";
        const string copy = "复制下载链接";
        const string mirror = "复制镜像链接（gh-proxy）";
        const string folder = "打开存放目录";

        var pick = await Shell.Current.DisplayActionSheetAsync("手动下载运行环境包", "取消", null,
            open, copy, mirror, folder);
        if (pick == mirror)
        {
            // 直连在「github.com 被 hosts 黑洞 / 墙内」时打不开，镜像只是换条路，文件相同（装的时候照样校验 SHA256）
            var m = Services.RuntimeProvisioner.MirrorUrls[0];
            try { await Clipboard.Default.SetTextAsync(m); } catch { }
            await AlertAsync("镜像链接已复制：\n\n" + m + "\n\n" + ManualDownloadNextStep);
            return;
        }
        if (pick == copy)
        {
            try { await Clipboard.Default.SetTextAsync(url); } catch { }
            await AlertAsync("下载链接已复制：\n\n" + url + "\n\n" + ManualDownloadNextStep);
            return;
        }
        if (pick == folder)
        {
            OpenRuntimeDir();
            await AlertAsync(ManualDownloadNextStep);
            return;
        }
        if (pick != open) return;

        try { await Launcher.OpenAsync(new Uri(url)); }
        catch (Exception ex)
        {
            DiagLog.Write("[runtime] 打开下载直链失败：" + ex.Message);
            await AlertAsync("没能调起浏览器。可以手动复制这个地址：\n\n" + url);
            return;
        }
        await AlertAsync(ManualDownloadNextStep);
    }

    /// <summary>手动下载之后的下一步提示（三个入口共用，免得文案各处漂移）。</summary>
    private const string ManualDownloadNextStep =
        "下载完成后回到本面板，点「本地安装包安装」选中该 zip —— 客户端会校验 SHA256、" +
        "解压到程序同目录（程序目录不可写时为用户数据目录）并自检必需件，与自动下载装出来的完全一致。\n\n" +
        "提示：网页源 / MacCMS / JS 脚本源不需要这个包，可以先跳过。";

    /// <summary>
    /// 手动安装 Android 运行环境：选包（或直接用缓存里那份）→ 确认（体积/磁盘/会清掉旧运行环境）
    /// → 非官方原件再确认一次 → 解压 + 必需件自检。
    /// </summary>
    /// <param name="preSelectedZip">已知的包路径（自检发现缓存里已有一份完整包时直接传，省掉选文件）；
    /// 传 null 则弹文件选择器。</param>
    private async Task InstallRuntimeAsync(string? preSelectedZip)
    {
        if (_provisioningRuntime) return;
        _provisioningRuntime = true;
        try
        {
            var zip = preSelectedZip;
            if (zip is null)
            {
                var picked = await FilePicker.PickAsync(new PickOptions
                {
                    PickerTitle = "选择 Android 运行环境包（zip）",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.WinUI] = new[] { ".zip" },
                        [DevicePlatform.Android] = new[] { "*/*" },
                    }),
                });
                if (picked is null) return;                        // 用户取消
                zip = File.Exists(picked.FullPath)
                    ? picked.FullPath
                    : await CopyPickedToCacheAsync(picked);         // 极少数拿不到真实路径的情况
                if (zip is null) return;
            }

            if (!File.Exists(zip))
            {
                await AlertAsync("找不到这个安装包：\n" + zip);
                return;
            }

            var sizeMb = new FileInfo(zip).Length / 1024 / 1024;
            var drive = new DriveInfo(Path.GetPathRoot(Services.RuntimeProvisioner.TargetRoot)!);
            var free = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            if (!await Shell.Current.DisplayAlertAsync("安装 Android 运行环境",
                    $"安装包：{Path.GetFileName(zip)}（{sizeMb} MB）\n" +
                    $"解压到：{Services.RuntimeProvisioner.TargetRoot}\n" +
                    $"磁盘 {drive.Name} 可用 {free:0.#} GB（解压需约 2.6 GB，另留模拟器运行余量）\n\n" +
                    "安装会先清掉已有的 Android 运行环境（不影响 AVD 数据、订阅与其它设置）。现在开始吗？",
                    "开始安装", "取消"))
                return;

            // 官方原件（大小 + SHA256）直接装；对不上再问一次 —— 从网盘/镜像/迅雷下的很可能就是同一个
            // 文件（下载器改了名字罢了），但也确实可能被动过，让用户自己决定比默默接受或默默拒绝都合适。
            var official = await Task.Run(() =>
                Services.RuntimeProvisioner.IsOfficialPackageAsync(zip, CancellationToken.None));
            if (!official && !await Shell.Current.DisplayAlertAsync("安装包与官方发布不一致",
                    "所选文件的 SHA256 对不上官方发布包（可能来自第三方镜像、被修改过，或下载不完整）。\n\n" +
                    "继续安装等于信任这个文件；来源不确定的话，建议先在官方发布页重新下载。",
                    "继续安装", "取消"))
                return;

            await Task.Run(() => Services.RuntimeProvisioner.InstallFromZipAsync(
                zip, RuntimeProgress("运行环境安装中…"), RuntimeStage, CancellationToken.None))
                .ConfigureAwait(true);

            await RefreshEnvChecksAsync();
            await RestartBootstrapAsync();     // 装好就地把桥拉起来，不用重启程序

            // 缓存里那份 zip（自己下的）用完就没价值了，938MB 白占地方 —— 问一句删不删；
            // 用户从别处选的包不动（那是别人的文件）。
            var cached = string.Equals(zip, Services.RuntimeProvisioner.ZipPath, StringComparison.OrdinalIgnoreCase);
            const string done = "Android 运行环境安装完成。首次启动模拟器约需 1 分钟（之后走快照会快很多）。";
            if (cached)
            {
                if (await Shell.Current.DisplayAlertAsync("运行环境就绪",
                        done + $"\n\n本地那份安装包（{sizeMb} MB）已不再需要，现在删除吗？", "删除安装包", "保留"))
                    Services.RuntimeProvisioner.CleanupDownload();
            }
            else
            {
                await AlertAsync(done);
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[runtime] 手动安装失败：{ex.GetType().Name}: {ex.Message}");
            await RefreshEnvChecksAsync();
            await AlertAsync("安装失败：" + ex.Message +
                "\n\n常见原因：安装包不完整（重新下载）、磁盘空间不足、或模拟器/应用正占着要覆盖的文件。");
        }
        finally
        {
            _provisioningRuntime = false;
        }
    }

    /// <summary>
    /// 运行环境刚装好：**就地把「统一引导」再跑一遍**（起模拟器 → 装桥 → 连通），
    /// 期间重新拉起冷启动遮罩显示真实进度，桥就绪后重载首页数据。
    /// <para>启动时那次引导早已失败退场（环境当时不存在），没人再拉一次的话程序就停在
    /// 「空首页 + 订阅站点均拉取失败」，非重启不可 —— 这是 2026-10-15 用户实测反馈的缺口。</para>
    /// </summary>
    private async Task RestartBootstrapAsync()
    {
        if (!Services.RuntimeBootstrapper.Available) return;
        try
        {
            _realPct = -1; _shownPct = -1; _segPct = 0.01; _segStartUtc = DateTime.UtcNow;
            _coldLoadStarted = false; _coldLoadFinished = false;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ColdStartOverlay.IsVisible = true;          // fade-out 会把它关掉，这里重新上屏
                ColdStartOverlay.Opacity = 1;
                ColdStartPct.Text = "";
                ColdStartStatus.Text = "正在启动 Android 运行环境…";
                ColdStartProgress.Progress = 0.03;
                if (_coldStartTimer is null)
                {
                    _coldStartTimer = Dispatcher.CreateTimer();
                    _coldStartTimer.Interval = TimeSpan.FromMilliseconds(400);
                    _coldStartTimer.Tick += (_, _) => UpdateColdStartOverlay();
                }
                _coldStartTimer.Start();
            });

            await Services.RuntimeBootstrapper.RunAsync().ConfigureAwait(true);   // 起模拟器 + 装桥（约 1 分钟）
            // 桥就绪 → 重新取首页数据；分类上屏后遮罩自己跳 100% 淡出（既有逻辑）
            await MainThread.InvokeOnMainThreadAsync(async () => await _vm.LoadHomeAsync());
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[runtime] 装好后重跑引导失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private long _rtLastBytes;
    private long _rtLastTicks;
    private double _rtLastMbps;
    private volatile bool _rtUiPending;

    /// <summary>
    /// 不走 <see cref="Progress{T}"/>：它会把回调**转回创建它的线程（UI）**，主线程被 jar 站点
    /// 预加载压住时算出来的是过期样本（用户看到"0.5 MB/s"、实际 8.6 MB/s，2026-10-15 实测）。
    /// 这里改成普通 IProgress：速度与文案在**上报线程**上算好，再合并投递一帧到 UI。
    /// </summary>
    private sealed class RtProgress(HomePage page, string prefix, bool showSpeed) : IProgress<double>
    {
        public void Report(double p) => page.OnRuntimeProgress(prefix, showSpeed, p);
    }

    internal void OnRuntimeProgress(string prefix, bool showSpeed, double p)
    {
        var text = $"{prefix} {p * 100:0}%";
        if (showSpeed)
        {
            var bytes = (long)(p / 0.9 * Services.RuntimeProvisioner.AssetBytes);
            var now = Environment.TickCount64;
            if (_rtLastTicks == 0) { _rtLastTicks = now; _rtLastBytes = bytes; }
            else if (now - _rtLastTicks >= 600)
            {
                _rtLastMbps = (bytes - _rtLastBytes) / 1048576.0 / ((now - _rtLastTicks) / 1000.0);
                _rtLastBytes = bytes;
                _rtLastTicks = now;
            }
            text += $"（{_rtLastMbps:0.0} MB/s · 源：{Services.RuntimeProvisioner.LastUsedSource}）";
        }

        if (_rtUiPending) return;          // UI 没跟上时只保留最新一帧，别排队
        _rtUiPending = true;
        var shown = text;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _rtUiPending = false;
            EnvCheckSummary.Text = shown;
            EnvCheckSummary.TextColor = Color.FromArgb("#0A84FF");
            EnvCheckBadge.BackgroundColor = Color.FromArgb("#0A84FF").WithAlpha(0.16f);
        });
    }

    /// <summary>下载/安装运行环境的进度写进自检卡片的汇总行（蓝色药丸 + 百分比）。
    /// <paramref name="showSpeed"/>=true 时额外显示实测速度与当前下载源（下载段占进度 0~0.9）。
    /// <para>括号里那截**恒定显示**（沿用上次读数）：否则"算得出/算不出"两帧宽度不同，整行一闪一闪。</para></summary>
    private IProgress<double> RuntimeProgress(string prefix, bool showSpeed = false) =>
        new RtProgress(this, prefix, showSpeed);

    /// <summary>阶段文案：落日志，**同时显示在自检卡片的进度药丸上**。
    /// <para>连接、判定回环、换源这些阶段没有字节流动，进度回调不会触发 —— 只写日志的话
    /// 用户看到的就是"点了下载没反应"（2026-10-15 实测反馈），所以这里必须上界面。</para></summary>
    private void RuntimeStage(string stage)
    {
        DiagLog.Write($"[runtime] {stage}");
        MainThread.BeginInvokeOnMainThread(() =>
        {
            EnvCheckSummary.Text = stage;
            EnvCheckSummary.TextColor = Color.FromArgb("#0A84FF");
            EnvCheckBadge.BackgroundColor = Color.FromArgb("#0A84FF").WithAlpha(0.16f);
        });
    }

    /// <summary>
    /// 极少数情况下 FilePicker 给不出本地真实路径（云盘占位符、非本地盘）。
    /// 这时把内容拷到运行环境缓存目录再装 —— 只有这种情况才多花一次 0.9GB 拷贝。
    /// </summary>
    private async Task<string?> CopyPickedToCacheAsync(FileResult picked)
    {
        try
        {
            var dst = Services.RuntimeProvisioner.ZipPath;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            EnvCheckSummary.Text = "正在读取所选安装包…";
            await using (var src = await picked.OpenReadAsync())
            await using (var fs = File.Create(dst))
                await src.CopyToAsync(fs);
            return dst;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[runtime] 复制所选安装包失败：{ex.GetType().Name}: {ex.Message}");
            await AlertAsync("读取所选文件失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>打开运行环境缓存目录（把这当成「下载到哪」的落点，手动下载的 zip 放这儿也便于管理）。</summary>
    private void OpenRuntimeDir()
    {
        var dir = Path.GetDirectoryName(Services.RuntimeProvisioner.ZipPath)!;
        try
        {
            Directory.CreateDirectory(dir);
#if WINDOWS
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
#else
            _ = Launcher.OpenAsync(new Uri(dir));
#endif
        }
        catch (Exception ex) { DiagLog.Write("[runtime] 打开运行环境目录失败：" + ex.Message); }
    }

    private static Task AlertAsync(string message) =>
        Shell.Current?.DisplayAlertAsync("启动自检", message, "好") ?? Task.CompletedTask;

    /// <summary>
    /// WHPX 未启用的完整处置步骤（点「详细步骤」弹出）。
    /// 三种根因的解法不同，所以先说怎么判断，再给逐条操作与排障要点。
    /// </summary>
    private const string WhpxHowTo =
        "按顺序来（多数机器到第 ④ 步就好了）：\n\n" +
        "① 确认 CPU 虚拟化在 BIOS/UEFI 里是开的（Intel VT-x / AMD-V / SVM Mode）。\n" +
        "   这里被关掉的话，后面怎么弄都没用。\n\n" +
        "② 开启可选功能（需要「虚拟机监控程序平台」，建议把「虚拟机平台」一起开）：\n" +
        "   · 一键：点本卡片上的「一键启用虚拟化」——会弹 UAC，自动执行下面两条命令。\n" +
        "   · 手动：Win+R 输入 optionalfeatures，勾选「虚拟机监控程序平台」「虚拟机平台」。\n" +
        "   · 管理员命令行：\n" +
        "     dism /online /enable-feature /featurename:HypervisorPlatform /all /norestart\n" +
        "     dism /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart\n\n" +
        "③ 确认 hypervisor 启动类型没被关过（管理员命令行；一键按钮也会做这一步）：\n" +
        "   bcdedit /set hypervisorlaunchtype auto\n\n" +
        "④ 重启电脑（功能开关不重启不生效），回来重开本应用看「硬件加速」是否变绿。\n\n" +
        "排障：\n" +
        "· 「任务管理器显示虚拟化已启用」「Hyper-V 全开」都不代表 WHPX 可用——WHPX 要的是\n" +
        "  WinHvPlatform.dll，它由「虚拟机监控程序平台」这个功能提供。\n" +
        "· 与 VMware/VirtualBox 老版本、部分反作弊驱动的虚拟化占用冲突时，也可能一直起不来。\n" +
        "· 不开 WHPX 时模拟器会回落到软件模拟：冷启从 1 分多钟变成十几分钟，且操作严重卡顿。\n" +
        "  可以这样跑，但体验很差，建议按上面几步开启后重启电脑。";

    /// <summary>提权启用「虚拟机监控程序平台」（WHPX 的宿主前置功能），重启后生效。</summary>
    private async Task EnableWhpxAsync()
    {
#if WINDOWS
        try
        {
            // 一条提权命令做三件事（用 & 串联，某步失败不影响其余，退出码取最后一条）：
            //   ① 开「虚拟机监控程序平台」  —— WHPX 的宿主前置（WinHvPlatform.dll 由它提供）
            //   ② 开「虚拟机平台」          —— 建议一起开（WSL2/沙箱等同样依赖）
            //   ③ hypervisorlaunchtype=auto —— 曾被 bcdedit 关过的机器，光开功能起不来
            var chain = "/c dism /Online /Enable-Feature /FeatureName:HypervisorPlatform /All /NoRestart"
                      + " & dism /Online /Enable-Feature /FeatureName:VirtualMachinePlatform /All /NoRestart"
                      + " & bcdedit /set hypervisorlaunchtype auto";
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", chain)
            { UseShellExecute = true, Verb = "runas" };
            var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                await AlertAsync("未能启动提权命令，请点「详细步骤」按命令行自行操作。");
                return;
            }
            await p.WaitForExitAsync();
            await AlertAsync(p.ExitCode == 0
                ? "已执行启用操作（虚拟机监控程序平台 + 虚拟机平台 + hypervisorlaunchtype=auto）。\n\n" +
                  "请重启电脑后生效——重启前引擎仍走软件模拟。重启后回来看本卡片「硬件加速」是否变绿；" +
                  "若仍不绿，点「详细步骤」按 BIOS(VT-x/AMD-V) 那几条排查。"
                : "启用命令返回 " + p.ExitCode + "（可能被 UAC/组策略拦下）。\n\n" +
                  "请点「详细步骤」按命令行逐条操作，或点「手动打开 Windows 功能」自行勾选。");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户取消了 UAC 授权：静默返回，不打扰
        }
        catch (Exception ex)
        {
            await AlertAsync("启用异常：" + ex.Message);
        }
#endif
        await RefreshEnvChecksAsync();
    }

    /// <summary>打开「启用或关闭 Windows 功能」对话框（DISM 失败或想手工操作时的兜底）。</summary>
    private void OpenOptionalFeatures()
    {
#if WINDOWS
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("optionalfeatures.exe") { UseShellExecute = true });
        }
        catch (Exception ex) { DiagLog.Write("[envcheck] 打开 Windows 功能失败：" + ex.Message); }
#endif
    }

    /// <summary>
    /// <summary>打开数据目录（排障用：看日志/镜像/缓存，或手动清理腾空间）。</summary>
    private void OpenDataDir()
    {
        var dir = CatClawVideo.Core.AppPaths.LocalRoot;
        try
        {
#if WINDOWS
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
#else
            _ = Launcher.OpenAsync(new Uri(dir));
#endif
        }
        catch (Exception ex) { DiagLog.Write("[envcheck] 打开数据目录失败：" + ex.Message); }
    }

    private void UpdateColdStartOverlay()
    {
        // 取数生命周期：true = 正在跑；由 true 翻回 false 而分类仍空 = 这轮已经失败收工
        if (_vm.IsHomeLoading) _coldLoadStarted = true;
        else if (_coldLoadStarted) _coldLoadFinished = true;

        // ★ 启动门禁（2026-10-15 用户要求「没环境就不让启动」）：运行环境没就绪时**不放行** ——
        //   不淡出遮罩、也不理会超时兜底，自检面板与下载入口一直留在眼前。
        //   装好后自检重跑 → _envBlockedReason 变空 → 这里放行，后续按正常流程淡出进主界面。
        if (_envBlockedReason.Length > 0)
        {
            MainThread.BeginInvokeOnMainThread(ApplyColdStartStatus);
            return;
        }

        // 数据就绪（分类已上屏）= 冷启动结束：停表并淡出进主界面
        if (_vm.Categories.Count > 0)
        {
            FinishColdStartOverlay(jumpTo100: true);
            return;
        }

        // ⚠ 兜底（2026-10-02 新增，用户反馈「启动卡在 97%」）：
        // 旧逻辑只认「分类 > 0」这一条出路，于是 ART guest 起不来 / 订阅站全挂时遮罩永不淡出，
        // HomeStatus（"订阅站点均拉取失败…"）被压在遮罩下面 —— 用户只看到一个不动的百分比。
        // 两条收摊条件：① 取数已失败收工（IsHomeLoading 翻回 false）；② 总时长超上限（兜底中的兜底，
        // 防「一直 true 不翻转」的假挂起）。
        if (_coldLoadFinished || (DateTime.UtcNow - _coldStartUtc).TotalSeconds > ColdStartCapSeconds)
        {
            FinishColdStartOverlay(jumpTo100: false);
            return;
        }

        MainThread.BeginInvokeOnMainThread(ApplyColdStartStatus);
    }

    /// <summary>停表并淡出遮罩。<paramref name="jumpTo100"/>=数据就绪（走 100% 庆祝帧）；
    /// false = 失败/超时（先把真实原因写在遮罩上停 0.9s 再淡出，让用户读得到）。</summary>
    private void FinishColdStartOverlay(bool jumpTo100)
    {
        _coldStartTimer?.Stop();
        _coldStartTimer = null;
        // 就绪 ≠ 立刻消失：先跳 100%（用户要求「一下子加载到 100% 再进程序」），
        // 停 ~0.5s 让最后一帧可见，再淡出。直接淡出会让数字永远停在 80 多。
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (jumpTo100)
            {
                ColdStartPct.Text = "100%";
                _ = ColdStartProgress.ProgressTo(1.0, 150, Easing.Linear);
                await Task.Delay(500);
            }
            else if (!string.IsNullOrEmpty(_vm.HomeStatus))
            {
                ColdStartStatus.Text = _vm.HomeStatus;
                ColdStartPct.Text = "";
                await Task.Delay(900);
            }
            await FadeOutColdStartOverlayAsync();
        });
    }

    private void ApplyColdStartStatus()
    {
        var siteCount = SiteRegistry.Playable.Count();

        // 运行环境没就绪：**进度条停住**（不推进也不回退），只把原因写到遮罩上。
        // 装好/处置完（自检重跑后 _envBlockedReason 变空）会自动接着走。
        if (_envBlockedReason.Length > 0)
        {
            ColdStartStatus.Text = "运行环境未就绪，进度已暂停 —— " + _envBlockedReason +
                                   "（点上方「启动自检」里的按钮处置后会自动继续）";
            return;
        }

        // ── 进度条 + 百分比：**真进度优先** ──
        // 2026-10-15（用户要求）：不再按时间编百分比。桥每到一个 adb 实测里程碑就上报一次，
        // 收到就按它走（只前进不回退）；还没收到任何里程碑时，从 3% 慢慢爬到 15% 就**停住等** ——
        // 于是「卡在哪一步」= 进度条停在哪个档，不会出现"时间到了就自己涨"的假象。
        // ── 每个阶段：先快爬、再放缓（用户 2026-10-15 指定的节奏）──
        //   起步 1% → 3.5s 冲到 ~12% → 放缓等下一阶段；下一阶段到了再冲一次，如此重复。
        //   上限始终是"下一档 − 1%"，所以快爬也不会越过还没发生的里程碑。
        double p;
        string stage;
        if (_realPct >= 0)
        {
            if (_realPct > _segPct)                      // 新的真里程碑 = 新阶段
            {
                _segPct = _realPct;
                _segStartUtc = DateTime.UtcNow;
            }
            var cap = Math.Max(_realPct, NextMilestone(_realPct) - 0.01);
            var target = StageProgress(_segPct, cap, _segStartUtc);
            if (target > _shownPct) _shownPct = target;  // 只前进
            p = Math.Max(_shownPct, _realPct) * 100;
            stage = _realStage;
        }
        else
        {
            // 还没有任何真实里程碑：1% 起步 → 3.5s 快爬到 ~11.5% → 放缓等「设备出现」那一档
            if (_shownPct < 0) _shownPct = 0.01;
            var target = StageProgress(0.01, 0.15, _coldStartUtc);
            if (target > _shownPct) _shownPct = target;
            p = _shownPct * 100;
            stage = siteCount == 0 ? "正在恢复订阅与站点…" : "正在准备 Android 运行环境…";
        }

        if (_vm.Categories.Count > 0) { p = 100; stage = "首页数据已就绪"; }
        p = Math.Round(p, 0, MidpointRounding.AwayFromZero);
        ColdStartPct.Text = $"{p:0}%";
        _ = ColdStartProgress.ProgressTo(p / 100.0, 380, Easing.Linear);
        ColdStartStatus.Text = stage;
    }

    private async Task FadeOutColdStartOverlayAsync()
    {
        if (!ColdStartOverlay.IsVisible) return;
        await ColdStartOverlay.FadeTo(0, 350, Easing.CubicOut);
        ColdStartOverlay.IsVisible = false;
    }

    public Task OnTabShownAsync()
    {
        ApplyPosterLayout();
        CaptureColumns();
        // 初始层 = 顶栏：本页不抢焦点，交给顶栏「首页」tab
        // （MainPage.ShowTab 紧接着会调 FocusTopNav，这里保持不亮即可，避免闪一下切换源环）。
        _layer = LayerTopNav;
        RenderFocus();
        RemoteKeyRouter.Push(this);
        // 设置里切了「主页推荐内容」/ 换站后：回首页 tab 时行流按需重建
        _ = _vm.RefreshHomeRecIfStaleAsync();
        return _vm.LoadHomeCommand.ExecuteAsync(null);
    }

    // ═══════════════════════ 布局 ═══════════════════════

    /// <summary>海报墙布局：与历史/收藏页同一套 PosterLayoutHelper（Android 卡高 182 / Windows 252，宽 2:3，列数自适应）</summary>
    private void ApplyPosterLayout()
    {
#if WINDOWS
        PosterLayoutHelper.Apply(PosterGrid, PosterGrid.Width, PosterGrid.Height, cap: 252);
#else
        PosterLayoutHelper.Apply(PosterGrid, PosterGrid.Width, PosterGrid.Height);
#endif
    }

    /// <summary>记录当前列数（方向键上下移动需要按列换算索引）。</summary>
    private void CaptureColumns()
    {
        if (PosterGrid.ItemsLayout is GridItemsLayout g && g.Span > 0)
            _posterColumns = g.Span;
    }

    private void OnItemsChanged()
    {
        CaptureColumns();
        if (_posterIndex >= _vm.Items.Count) _posterIndex = Math.Max(0, _vm.Items.Count - 1);
        RenderFocus();
    }

    // ═══════════════════════ 分类 chips ═══════════════════════

    /// <summary>
    /// 刷新分类 chip 引用：焦点外壳已由 XAML 模板静态声明（外层 Border=壳，内层 Border=chip），
    /// 集合变化后直接遍历建立索引即可。**禁止在此重排/重挂视图**——旧版运行时包壳
    /// （把 chip 从 BindableLayout 摘出再塞进新建 Border）会重挂原生视图，
    /// Android 上切站点后 TapGestureRecognizer 整体失效（点击分类无反应，真机复现）。
    /// </summary>
    // ═══════════════════ 分类筛选（对位 TVBox GridFilterDialog）═══════════════════

    /// <summary>
    /// 刷新「筛选」入口：站点没返回 filters 就整块隐藏；有条件时把已选项直接写在按钮上。
    /// <para>两级动作表（选组 → 选值）而不是自绘弹层：手机与桌面都能用、不需要新的焦点层，
    /// 且筛选本来就是低频操作。要改成 TVBox 那种一行一组的弹层面板时，只需要换这里的交互壳。</para>
    /// </summary>
    void UpdateFilterUi()
    {
        var groups = _vm.CurrentFilters;
        // 筛选是「分类」的附属能力：主页行流没有当前分类，整块隐藏（网格视图才有）
        FilterShell.IsVisible = !_vm.IsHomeMode && groups.Count > 0;
        var summary = _vm.FilterSummary;
        FilterLabel.Text = string.IsNullOrEmpty(summary) ? "筛选" : summary;
    }

    /// <summary>本页是挂在 Shell 页里的 ContentView，动作表要走宿主 Page。</summary>
    Page? HostPage()
    {
        VisualElement? v = this;
        while (v is not null and not Page) v = v.Parent as VisualElement;
        return v as Page;
    }

    private async void OnFilterTapped(object? sender, TappedEventArgs e)
    {
        var groups = _vm.CurrentFilters;
        var host = HostPage();
        if (groups.Count == 0 || host is null) return;

        var names = groups.Select(g => g.Name).Append("清除全部筛选").ToArray();
        var pick = await host.DisplayActionSheetAsync("按哪一项筛选？", "取消", null, names);
        if (pick is null || pick == "取消") return;

        if (pick == "清除全部筛选")
        {
            await _vm.ClearFiltersAsync();
            return;
        }
        var group = groups.Find(g => g.Name == pick);
        if (group is null) return;

        var choices = group.Values.Select(v => v.Display).ToArray();
        var value = await host.DisplayActionSheetAsync($"{group.Name} · 选一个", "取消", "不限", choices);
        if (value is null || value == "取消") return;

        if (value == "不限") await _vm.SetFilterAsync(group.Key, null);
        else await _vm.SetFilterAsync(group.Key, Array.Find(group.Values.ToArray(), v => v.Display == value));
    }

    private void RebuildChipShells()
    {
        _ = Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(30), () =>
        {
            try
            {
                _chipShells.Clear();
                foreach (var child in CategoryChipHost.Children)
                {
                    if (child is Border shell && shell.Content is Border)
                        _chipShells.Add(shell);
                }

                if (_chipIndex >= _chipShells.Count) _chipIndex = Math.Max(0, _chipShells.Count - 1);
                UpdateChipStyles();
            }
            catch { }
        });
    }

    /// <summary>选中分类 chip 用主题色实底 + 白字；焦点另加描边（与选中态独立，可并存）。</summary>
    private void UpdateChipStyles()
    {
        var res = Application.Current?.Resources;
        var active = res?["PrimaryColor"] as Color ?? Colors.Purple;
        var inactive = res?["ChipInactiveColor"] as Color ?? Colors.Gray;
        var activeText = Colors.White;
        var inactiveText = res?["TextSecondaryColor"] as Color ?? Colors.Gray;

        for (int i = 0; i < _chipShells.Count; i++)
        {
            bool focused = _layer == LayerChips && i == _chipIndex;
            _chipShells[i].Stroke = focused ? (res?["PrimaryColor"] as Color ?? Colors.Purple) : Colors.Transparent;
            _chipShells[i].Scale = focused ? 1.06 : 1.0;
        }

        foreach (var shell in _chipShells)
        {
            if (shell.Content is not Border chip) continue;
            bool on = (chip.BindingContext as VodCategory)?.Id == _vm.SelectedCategoryId;
            chip.BackgroundColor = on ? active : inactive;
            if (chip.Content is Label label)
                label.TextColor = on ? activeText : inactiveText;
        }
    }

    // ═══════════════════════ 焦点渲染 ═══════════════════════

    private void RenderFocus()
    {
        // 切换源焦点环
        var primary = Application.Current?.Resources["PrimaryColor"] as Color ?? Colors.Purple;
        SwitchSiteShell.Stroke = _layer == LayerSwitchSite ? primary : Colors.Transparent;
        SwitchSiteShell.Scale = _layer == LayerSwitchSite ? 1.05 : 1.0;

        UpdateChipStyles();

        // 海报墙焦点：只点亮当前项（主页推荐网格与源分类网格同一套一维索引）
        var active = ActiveItems();
        for (int i = 0; i < active.Count; i++)
            active[i].IsFocused = _layer == LayerPosters && i == _posterIndex;
    }

    /// <summary>当前焦点导航作用的卡片集合（主页推荐网格 / 源分类网格，随视图切换）。</summary>
    private System.Collections.Generic.IReadOnlyList<VodItem> ActiveItems() =>
        _vm.IsHomeMode ? _vm.HomeRecItems : _vm.Items;

    /// <summary>当前视图的网格列数。</summary>
    private int ActiveColumns() => _vm.IsHomeMode ? _recColumns : _posterColumns;

    private void ClearPosterFocus()
    {
        foreach (var it in _vm.Items) it.IsFocused = false;
        foreach (var it in _vm.HomeRecItems) it.IsFocused = false;
    }

    private void FocusSwitchSite()
    {
        _layer = LayerSwitchSite;
        RenderFocus();
    }

    private void FocusChips(int index = 0)
    {
        if (_chipShells.Count == 0) { FocusSwitchSite(); return; }
        _layer = LayerChips;
        _chipIndex = Math.Clamp(index, 0, _chipShells.Count - 1);
        RenderFocus();
        ScrollToChip(_chipIndex);
    }

    /// <summary>
    /// 把焦点 chip 滚进可视区。分类行是横向 ScrollView，chip 数量随源变化（十几个很常见），
    /// 不滚的话焦点会跑出屏幕 —— 尤其是「切换源 ← 回最右 chip」这种一次跳到末项的移动。
    /// </summary>
    private void ScrollToChip(int index)
    {
        try
        {
            if (index >= 0 && index < _chipShells.Count)
                _ = CategoryScroll.ScrollToAsync(_chipShells[index], ScrollToPosition.MakeVisible, animated: false);
        }
        catch { }
    }

    private void FocusPosters(int index = 0)
    {
        var items = ActiveItems();
        if (items.Count == 0) { FocusChips(); return; }
        _posterIndex = Math.Clamp(index, 0, items.Count - 1);
        _layer = LayerPosters;
        RenderFocus();
        ScrollActivePoster(_posterIndex);
    }

    /// <summary>把焦点海报滚入可视区（否则遥控器移动时焦点会跑出屏幕）。</summary>
    private void ScrollActivePoster(int index)
    {
        try
        {
            CollectionView grid = _vm.IsHomeMode ? HomeGrid : PosterGrid;
            grid.ScrollTo(index, position: ScrollToPosition.MakeVisible, animate: false);
        }
        catch { }
    }

    // ═══════════════════════ IRemoteKeyHandler ═══════════════════════

    /// <summary>
    /// 被主壳层要求接管焦点（顶栏按 ↓）。
    ///
    /// <para>2026-09-19 用户建议：首页最常用的是挑内容，「切换源」是低频操作 ——
    /// 让 ↓ 一步就到内容区，省掉一次多余的按键。切换源改为从 chip 行两端进：
    /// 最左 chip <c>←</c>、最右 chip <c>→</c>（见 <see cref="TryMove"/>）。</para>
    ///
    /// <para>落点按视图分流：网格 → 记忆的分类 chip；主页行流 → 第一行（TVBox 首页就是直接逛内容）。</para>
    /// </summary>
    public void FocusContent()
    {
        if (_vm.IsHomeMode) FocusPosters(0);
        else FocusChips(Math.Max(0, _chipIndex));
    }

    /// <summary>
    /// 顶栏抢走焦点：本页**所有**焦点层一起熄灭（切换源环 / 分类 chip / 海报墙），
    /// 并把层标记为「顶栏」。
    ///
    /// <para>原先只清了海报墙 —— 因为当时进入这条路径的唯一方式是「从内容区按 ↑ 上顶栏」，
    /// 起点必然在内容层。现在**启动与切 tab 也由顶栏持有焦点**（见 <c>MainPage.ShowTab</c>），
    /// 而本页的初始层停在「切换源」上，只清海报就会留下「顶栏首页 + 首页切换源」双高亮
    /// （2026-09-19 用户截图）。</para>
    /// </summary>
    public void BlurContent()
    {
        _layer = LayerTopNav;
        ClearPosterFocus();
        RenderFocus();
    }

    public bool Handle(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Up:
            case RemoteKey.Down:
            case RemoteKey.Left:
            case RemoteKey.Right:
                return TryMove(key);

            case RemoteKey.Enter:
                return Activate();

            case RemoteKey.Back:
                return GoBack();
        }
        return false;
    }

    private bool TryMove(RemoteKey dir)
    {
        switch (_layer)
        {
            case LayerTopNav:
                return false;

            case LayerSwitchSite:
                if (dir == RemoteKey.Down)
                {
                    // ↓ 进内容区：网格 → 分类 chips；主页 → 行流第一行
                    if (_vm.IsHomeMode) FocusPosters(0);
                    else FocusChips(Math.Max(0, _chipIndex));
                    return true;
                }
                if (dir == RemoteKey.Up) { _layer = LayerTopNav; RenderFocus(); return false; }  // 交还顶栏
                // ← 退回分类行最右 chip（2026-09-19 用户反馈：从「切换源」按 ← 出不去）。
                // 与「最右 chip → 上到切换源」成对 —— 两边互为对方的出口。
                if (dir == RemoteKey.Left && !_vm.IsHomeMode && _chipShells.Count > 0)
                {
                    FocusChips(_chipShells.Count - 1);
                    return true;
                }
                return true;   // → 右侧无内容，吃掉

            case LayerChips:
                switch (dir)
                {
                    case RemoteKey.Left:
                        if (_chipIndex <= 0) { FocusSwitchSite(); return true; }
                        _chipIndex--;
                        RenderFocus();
                        return true;

                    case RemoteKey.Right:
                        if (_chipIndex < _chipShells.Count - 1)
                        {
                            _chipIndex++;
                            RenderFocus();
                        }
                        else
                        {
                            // 最右侧 chip 再往右 → 上到「切换源」（2026-09-19 用户建议）。
                            // 与 FocusContent 的「↓ 进分类」正好成对：切换源 ↓ 会回到刚才这个 chip。
                            FocusSwitchSite();
                        }
                        return true;

                    case RemoteKey.Up:
                        FocusSwitchSite();
                        return true;

                    case RemoteKey.Down:
                        ClearPosterFocus();
                        FocusPosters(0);
                        return true;
                }
                return true;

            case LayerPosters:
                {
                    // 主页推荐网格与源分类网格同一套一维导航（列数按当前视图取）
                    var items = ActiveItems();
                    int cols = Math.Max(1, ActiveColumns());
                    switch (dir)
                    {
                        case RemoteKey.Left:
                            if (_posterIndex % cols == 0) { FocusChips(_chipIndex); return true; }  // 行首 → 回 chips
                            _posterIndex--;
                            RenderFocus();
                            ScrollActivePoster(_posterIndex);
                            return true;

                        case RemoteKey.Right:
                            if (_posterIndex >= items.Count - 1) return true;
                            _posterIndex++;
                            RenderFocus();
                            ScrollActivePoster(_posterIndex);
                            return true;

                        case RemoteKey.Up:
                            if (_posterIndex - cols < 0) { ClearPosterFocus(); FocusChips(_chipIndex); return true; }
                            _posterIndex -= cols;
                            RenderFocus();
                            ScrollActivePoster(_posterIndex);
                            return true;

                        case RemoteKey.Down:
                            if (_posterIndex + cols > items.Count - 1) return true;   // 已到底
                            _posterIndex += cols;
                            RenderFocus();
                            ScrollActivePoster(_posterIndex);
                            return true;
                    }
                    return true;
                }
        }
        return false;
    }

    /// <summary>OK：切换源 → 弹源选择；分类 chip → 切分类；海报 → 进观看页。</summary>
    private bool Activate()
    {
        switch (_layer)
        {
            case LayerSwitchSite:
                OnSwitchSiteTapped(this, new TappedEventArgs(null));
                return true;

            case LayerChips:
                if (_chipIndex >= 0 && _chipIndex < _vm.Categories.Count)
                    _ = _vm.SelectCategoryAsync(_vm.Categories[_chipIndex]);
                return true;

            case LayerPosters:
                var act = ActiveItems();
                if (_posterIndex >= 0 && _posterIndex < act.Count)
                    OpenItem(act[_posterIndex]);
                return true;
        }
        return false;
    }

    /// <summary>Back：海报 → 分类（网格）/ 切换源（主页）→ 交还顶栏。</summary>
    private bool GoBack()
    {
        switch (_layer)
        {
            case LayerPosters:
                ClearPosterFocus();
                FocusChips(_chipIndex);
                return true;
            case LayerChips:
                FocusSwitchSite();
                return true;
            case LayerSwitchSite:
                _layer = LayerTopNav;
                RenderFocus();
                return false;   // 交还主壳层
        }
        return false;
    }

    // ═══════════════════════ 交互入口（鼠标 / 触屏 / OK 共用）═══════════════════════

    /// <summary>「切换源」点击 → 数据源选择弹窗（参考影视仓），选择后切换首页数据源</summary>
    /// <summary>首页右侧直播入口 → 直播间（未配置源时页面自身给出「配置直播源」引导）</summary>
    private async void OnLiveTapped(object? sender, TappedEventArgs e)
    {
        try { await Shell.Current.GoToAsync("live"); } catch { }
    }

    private async void OnSwitchSiteTapped(object? sender, TappedEventArgs e)
    {
        var sites = _vm.PlayableSites;
        if (sites.Count == 0)
        {
            _vm.HomeStatus = "暂无可用站点，请先在 设置 → 订阅源管理 添加订阅";
            return;
        }
        var dialog = new SitePickerDialogPage(sites, _vm.Site?.Key,
            site => MainThread.BeginInvokeOnMainThread(() => _ = _vm.SelectSiteCommand.ExecuteAsync(site)),
            _vm.FailedSites);
        await Shell.Current.Navigation.PushModalAsync(dialog);
    }

    // ═══════════════════════ 主页 / 分类视图分流 ═══════════════════════

    /// <summary>按视图（主页行流 / 分类网格）显隐内容区，筛选入口只在网格视图有意义。</summary>
    private void ApplyModeVisual()
    {
        var home = _vm.IsHomeMode;
        HomeRecHost.IsVisible = home;
        PosterGrid.IsVisible = !home;
        CategoryScroll.IsVisible = true;   // 分类 chips 常显（TVBox 的分类 tab 形态），首位固定「主页」
        UpdateFilterUi();
    }

    /// <summary>主页推荐集合变化后复位越界的焦点索引（重建清空时 _posterIndex 可能越界）。</summary>
    private void OnHomeRecItemsChanged()
    {
        var n = ActiveItems().Count;
        if (_posterIndex >= n) _posterIndex = Math.Max(0, n - 1);
        if (_layer == LayerPosters && _vm.IsHomeMode) RenderFocus();
    }

    /// <summary>记录主页推荐网格列数（方向键上下移动按列换算索引）。</summary>
    private void CaptureRecColumns()
    {
        if (HomeGrid.ItemsLayout is GridItemsLayout g && g.Span > 0)
            _recColumns = g.Span;
    }

    /// <summary>分类 chip 点击 → 拉取该分类影片</summary>
    private async void OnCategoryTapped(object? sender, TappedEventArgs e)
    {
        // 诊断：分类点击无反应问题——记录点击是否触发、绑定上下文是否解析成功
        if (sender is VisualElement ve)
            DiagLog.Write($"[cat-tap] 触发 sender={ve.GetType().Name} ctx={(ve.BindingContext?.GetType().Name ?? "null")}");
        else
            DiagLog.Write("[cat-tap] 触发 sender=null");
        if ((sender as VisualElement)?.BindingContext is not VodCategory cat)
        {
            DiagLog.Write("[cat-tap] 绑定上下文不是 VodCategory，忽略");
            return;
        }
        DiagLog.Write($"[cat-tap] 切换分类 id={cat.Id} name={cat.Name}");
        await _vm.SelectCategoryAsync(cat);
    }

    /// <summary>海报卡点击 → 观看页</summary>
    private void OnPosterTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as VisualElement)?.BindingContext is not VodItem item) return;
        OpenItem(item);
    }

    /// <summary>进入观看页（带源定位参数；type 必传——猫爪源等无 api 特征的源靠它路由）</summary>
    private void OpenItem(VodItem item)
    {
        // ⓪ 主页推荐行的特殊卡（TVBox 语义）：
        //    历史卡 → 直接回观看页续看（卡片上带着与 HistoryPage 同一套回跳参数）；
        //    豆瓣卡 → 跳搜索页跨源搜片名（豆瓣条目没有源，TVBox 是详情搜不到再快搜，我们一步到位）
        if (item.Tag == "history" && item.Action.Length > 0)
        {
            try { Shell.Current.GoToAsync(item.Action); } catch { }
            return;
        }
        if (item.Tag == "douban")
        {
            var q = Uri.EscapeDataString(item.Title);
            try { Shell.Current.GoToAsync($"search?q={q}"); } catch { }
            return;
        }

        if (_vm.Site is null) return;

        // TVBox GridFragment.onItemClick 的优先级照搬：action 先于 tag。
        // ① 带 action 的卡片是「操作入口」（网盘登录/清除 Cookie/排序…），交给爬虫的
        //    action(String) —— 只有这条路会弹 jar 的原生对话框与扫码二维码。
        //    （旧实现用「数字 id + csp_*Guard」猜，真机确认 8 张卡全带 action，按字段判更准。）
        if (item.Action.Length > 0)
        {
            _ = CatClawVideo.Maui.Services.SpiderUiHost.OpenDriveEntryAsync(_vm.Site, item);
            return;
        }

        // ② tag=folder/cover = 网盘里的一层目录：点它要用本条目 ID 当分类 ID 重新拉列表
        //    （TVBox changeView），喂给 detailContent/playerContent 会把目录路径当播放地址，
        //    表现为「播放失败：MalformedURLException: no protocol: /movies/137018/@folder」。
        //    主页行流里点目录卡要先切到该分类网格——列表变化发生在网格里，行流视图看不到。
        if (item.Tag is "folder" or "cover")
        {
            // SelectCategoryAsync 对普通分类会自动退场行流（HomeRows.Clear），网格视图接手展示
            _ = _vm.SelectCategoryAsync(new VodCategory { Id = item.Id, Name = item.Title });
            return;
        }

        var query = $"watch?title={Uri.EscapeDataString(item.Title)}" +
                    $"&sourceKey={Uri.EscapeDataString(item.SourceKey)}" +
                    $"&type={_vm.Site.Type}" +
                    $"&api={Uri.EscapeDataString(_vm.Site.Api)}" +
                    $"&itemId={Uri.EscapeDataString(item.Id)}" +
                    $"&year={Uri.EscapeDataString(item.Year ?? "")}" +
                    $"&remarks={Uri.EscapeDataString(item.Remarks ?? "")}" +
                    $"&desc={Uri.EscapeDataString(item.Description ?? "")}" +
                    // 封面透传：观看页不再拉详情，播放历史的海报靠它
                    $"&cover={Uri.EscapeDataString(item.Cover ?? "")}";
        Shell.Current.GoToAsync(query);
    }
}
