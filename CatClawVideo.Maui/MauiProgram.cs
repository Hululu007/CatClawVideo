using System.Text;
using CatClawVideo.Maui.Services;
using CatClawVideo.Maui.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CatClawVideo.Maui;

public static class MauiProgram
{
    /// <summary>全局服务定位器（平台层/页面无构造注入时的入口）</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
#if WINDOWS
            // 树渲染控件（jar View 树 → 原生 WebView2）：用正规 Handler 让框架管尺寸/可见性。
            .ConfigureMauiHandlers(h => h.AddHandler<Platforms.Windows.TreeWebView2,
                Platforms.Windows.TreeWebView2Handler>())
#endif
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(handlers =>
            {
                // 跨平台视频播放器（Android: Media3 ExoPlayer / Windows: WinUI MediaPlayer）
#if ANDROID
                handlers.AddHandler(typeof(Controls.VideoPlayerView),
                    typeof(Platforms.Android.VideoPlayerViewHandler));
#endif
#if WINDOWS
                handlers.AddHandler(typeof(Controls.VideoPlayerView),
                    typeof(Platforms.Windows.VideoPlayerViewHandler));
                // 远程投屏画面（CATCLAW/1 客户端）：只有 Windows 头做了帧解码/上屏
                handlers.AddHandler(typeof(Controls.RemoteView),
                    typeof(Platforms.Windows.RemoteViewHandler));
#endif
            });

        var services = builder.Services;

        // 虚拟机套件（独立仓库 CatClaw.Qemu）的路径注入：套件不认识本应用的 AppPaths，
        // 这里把 Debug/Release 隔离后的两个根目录交给它（QEMU 控制台日志、guest 数据盘、
        // 块设备镜像、流缓存都落这两处）。
        CatClaw.Qemu.QemuPaths.Configure(CatClawVideo.Core.AppPaths.DataRoot, CatClawVideo.Core.AppPaths.LocalRoot);

        // ═══════════════════════════════════════════════════
        // Database（单连接单例，初始化放后台不阻塞首帧）
        // ═══════════════════════════════════════════════════
        // Debug/Release 数据隔离（见 Core.AppPaths）：首次从旧的共享位置（MAUI AppDataDirectory）拷一份 → 订阅/历史不丢
        CatClawVideo.Core.AppPaths.SeedFile("catclawvideo.db", FileSystem.AppDataDirectory);
        var dbPath = CatClawVideo.Core.AppPaths.Of("catclawvideo.db");
        var db = new VideoDatabase(dbPath);
        // 片名首字母索引：注入数据库，之后每次列表上屏（CoverResolver.Attach）都会被动积累。
        // 用完全限定名：本方法参数名 services 会遮蔽 Services 命名空间。
        CatClawVideo.Maui.Services.SearchIndex.Initialize(db);
        _ = Task.Run(async () =>
        {
            try { await db.EnsureInitializedAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"数据库初始化失败: {ex.Message}"); }
        });
        services.AddSingleton(db);

        // ═══════════════════════════════════════════════════
        // Platform / Infrastructure services
        // ═══════════════════════════════════════════════════
        services.AddSingleton<IThemeService, Services.ThemeService>();
        services.AddSingleton<VideoPlaybackManager>();

        // ═══════════════════════════════════════════════════
        // 影视源提供者：MacCMS JSON 直连 + TVBox spider 爬虫运行时 + 聚合路由
        // ═══════════════════════════════════════════════════
        // GBK 等代码页：部分采集站/网盘源响应为 GBK，须在首次 GetEncoding 前注册（最前！）
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        services.AddSingleton<ISubscriptionManager, CatClawVideo.Core.Providers.TvBoxSubscriptionManager>();

        // ═══════════════════════════════════════════════════
        // 直播（TVBox LivePlayActivity 移植）：源加载 + EPG 均为进程内单例
        //   —— 直播间与直播源配置页共用同一份偏好与已解析频道，换页不重复拉源
        // ═══════════════════════════════════════════════════
        services.AddSingleton<CatClawVideo.Core.Live.LiveSourceService>();
        services.AddSingleton<CatClawVideo.Core.Live.EpgService>();

        // ═══════════════════════════════════════════════════
        // 网络媒体（猫爪音乐 WebDAV 能力移植）：连接仓库 + WebDAV 客户端 + 本地流代理
        //   —— 代理把带 Basic Auth / OpenList 重定向的远程文件变成 127.0.0.1 普通 HTTP 流，
        //      双平台播放器统一按本地地址消费（Windows 播放器不支持自定义请求头）
        // ═══════════════════════════════════════════════════
        services.AddSingleton<CatClawVideo.Core.Network.WebDavProfileStore>();
        services.AddSingleton<CatClawVideo.Core.Network.WebDavService>();
        services.AddSingleton<CatClawVideo.Core.Network.WebDavStreamProxy>();

        // spider 运行时：JS（drpy2，Jint 纯托管，双端可用）+ jar/dex（Android DexClassLoader，仅 Android）
        services.AddSingleton<CatClawVideo.Core.Interfaces.IJsRuntimeService, CatClawVideo.Core.Services.JsRuntimeService>();
        // ★★ 尽早宣告「外置 Android 桥接管 jar 源」（2026-10-11）：必须在**任何**后台
        //    预加载/预热之前 —— JavaSpiderRuntime 的 jar 预加载会逐站 EnsureBridgeAsync，
        //    它只看本类桥状态、不知道外置桥存在，晚一步就把 QEMU ART guest 拉起来了。
        //    外置桥随后若证明不可用且允许 QEMU，会把它复位 false 走回退。
        CatClawVideo.Core.Providers.JavaSpiderRuntime.AttachExternalBridge();

        var jsRuntime = new CatClawVideo.Core.Providers.DrpyJsSpiderRuntime(
            new CatClawVideo.Core.Services.JsRuntimeService(),
            cacheDir: CatClawVideo.Core.AppPaths.LocalSub("drpy2"),
            log: m => System.Diagnostics.Debug.WriteLine(m));
        // 磁力下载引擎（Windows 下方赋值；Android 恒 null → 磁力下载任务提示不支持）
        CatClaw.Qemu.QemuGuestEngine? magnetDownloadEngine = null;

#if !ANDROID

        // ★ 2026-10-15 用户决定：全走「外置 Android 桥 + 随包模拟器」方案，本机 QEMU 整线退役。
        //   迅雷/磁力本来就已经搬进桥里（com.catclaw.bridge：libxl_thunder_sdk.so 是 arm64，
        //   只有模拟器能加载），QEMU 那条只是"桥不可用"时的回退 —— 现在连回退一起删掉：
        //   桥不可用就是不可用（自检里明确显示运行环境未安装 + 下载入口），不再偷偷起 1.6GB 的 VM。
        //   磁盘缓存上限（设置页那个"磁力缓存"档位）随之失去消费方，配置项保留但不再生效。
        var cacheGbPref = Preferences.Default.Get("stream_cache_gb", (int)CatClaw.Qemu.StreamCachePrefs.DefaultGb);
        CatClaw.Qemu.StreamCachePrefs.CapGb = cacheGbPref;
        BtFileLog.Write($"[缓存] 上限 {CatClaw.Qemu.StreamCachePrefs.CapGb}GB（设置页可调；QEMU 退役后暂无消费方）");

        // 磁力播放链路：① 桥内迅雷（首选）→ ② 迅雷网盘 API（兜底）→ 全部失败回落内置 BT
        var bridgeThunder = new CatClawVideo.Core.Providers.BridgeThunderEngine(BtFileLog.Write);
        CatClawVideo.Core.Interfaces.MagnetEngines.Thunder = new CatClawVideo.Core.Providers.ChainedMagnetEngine(
            bridgeThunder,
            new CatClawVideo.Core.Providers.ThunderPanEngine());

        // （统一引导已移到 jarRuntime 声明之后 —— 它要用到 jarRuntime）
#endif

        // TVBox 系爬虫（ProxyOrigin 等）会把播放地址拼成 http://127.0.0.1:<port>/proxy?...
        // 端口来自爬虫自己的 drivePort()：在 6677–6999 逐端口探测 GET /proxy?do=ck。
        // 宿主不提供该服务 → 端口缓存非法 → 地址端口为空 → .NET 抛
        // 「Invalid URI: Invalid port specified.」→ 播放页「播放失败：加载失败」。
        // 必须早于任何播放地址解析，故放启动最前；监听失败不影响其他能力（Start 返回 false 只记日志）。
        var spiderProxy = new CatClawVideo.Core.Services.SpiderProxyServer { Log = BtFileLog.Write };
        // /cache?do=get|set|del —— jar/JS 爬虫的跨启动 KV（对位 TVBox CacheRequestProcess + Hawk）。
        // 不接的话爬虫的 set 静默丢失，表现为「登录/配置存了，重启又没了」。
        spiderProxy.CacheStore = CatClawVideo.Core.Services.SpiderLocalStore
            .For(CatClawVideo.Core.AppPaths.LocalSub("spider-kv"));
        // m3u8 去广告：默认关（与 TVBox HawkConfig.M3U8_PURIFY 一致），设置页可开
        CatClawVideo.Core.Services.M3u8Purifier.Enabled = Preferences.Default.Get("m3u8_purify", false);
        CatClawVideo.Maui.Services.HistoryCap.Load();   // 播放历史条数上限（Data 层不读 Preferences，靠这里回填）
        // DoH：Core 不碰 Preferences，所以这里回填选择并在变化时存回去
        CatClawVideo.Core.Services.Doh.Selector = CatClawVideo.Maui.Services.DohPrefs.Load();
        CatClawVideo.Core.Services.Doh.Changed = () => CatClawVideo.Maui.Services.DohPrefs.Save(
            CatClawVideo.Core.Services.Doh.Selector);
        // 局域网遥控（对位 TVBox RemoteServer）：token 持久化 —— 换一次就要在手机上重抄一次太难用。
        var rcToken = Preferences.Default.Get("rc_token", "");
        if (rcToken.Length == 0)
        {
            rcToken = Guid.NewGuid().ToString("N")[..16];
            Preferences.Default.Set("rc_token", rcToken);
        }
        CatClawVideo.Core.Services.RemoteControlHub.Token = rcToken;
        try { CatClawVideo.Core.Services.RemoteControlHub.DeviceName = "猫爪影视 · " + DeviceInfo.Current.Name; }
        catch { }
        CatClawVideo.Core.Services.GoLiveProxy.Log = m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); };
        // 订阅 rules 里的正则是任意用户串，编译失败的要能被看见（作废该组，不影响其余判定）
        CatClawVideo.Core.Providers.TvBoxParseEngine.Log = m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); };
        CatClawVideo.Core.Providers.TvBoxConfigStore.Log = m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); };
        spiderProxy.Start();
        services.AddSingleton(spiderProxy);

#if ANDROID
        // proxy 端口上报给 Java 侧 SpiderApi.hostProxyPort（Guard 系网盘源用
        // spiderApi.getAddress/getPort 拼「云盘配置」页 URL，返回空则配置入口失效）
        Platforms.Android.TvBoxCompatBridge.SetProxyPort(spiderProxy.Port);
#endif

        // TVBox 社区 JS Spider 运行时（Jint）：csp_ + .js spider 包的 type=3 站点（含网盘聚合源）。
        // proxyPort 用懒访问器——此处 spiderProxy 已构造，端口在首请求时才真正读取。
        var tvboxJsRuntime = new CatClawVideo.Core.Providers.TvBoxJsSpiderRuntime(
            new CatClawVideo.Core.Services.JsRuntimeService(),
            cacheDir: CatClawVideo.Core.AppPaths.LocalSub("tvbox-js"),
            proxyPort: () => spiderProxy.Port,
            log: m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); });
#if ANDROID
        var jarRuntime = new Platforms.Android.DexSpiderRuntime(
            Path.Combine(FileSystem.CacheDirectory, "spider"),
            // ⚠ 必须同时进 DiagLog（= logcat 的 CatClawDiag）：

  // Release 版只写 Debug.WriteLine 的话，jar 下载/initApi/spider 异常在真机上一概看不到
            // —— 2026-10-17 排查「设置中心站点打不开」时踩到：logcat 里一条 [dex] 都没有。
            m => { System.Diagnostics.Debug.WriteLine(m); DiagLog.Write(m); },
            // Guard 系网盘源弹「云盘配置」对话框需要前台 Activity（Alert 需要 Activity token）
            currentActivity: () => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity);

        // 荐片（csp_Jianpian）宿主侧 P2P：
        //  ① libp2p.so + com.p2p.P2PClass 起本地 httpd（实测端口 8087+）；
        //  ② 在 spider 扫描的 9978…9999 整段起反代指向该 httpd；
        //  ③ /proxy?do=… 回调爬虫自己的 proxy(Map) 生成响应（对齐 TVBox ApiConfig.proxyLocal）。
        // 必须在我们解析播放地址前就绪，否则 spider 的 adjustPort 握手失败、拼出空端口地址。
        // 启动会阻塞到 httpd 就绪，故放后台线程预热；兜底 await 在 SpiderVodProvider 里。
        var jpP2p = new Platforms.Android.JianpianP2P(
            Path.Combine(FileSystem.CacheDirectory, "p2p"), BtFileLog.Write)
        {
            ProxyHandler = (q, ct) => jarRuntime.ProxyAsync(new Dictionary<string, string>(q), ct),
        };
        CatClawVideo.Core.Interfaces.JpP2PSupport.Current = jpP2p;
        _ = Task.Run(async () => { try { await jpP2p.EnsureReadyAsync(); } catch { } });

        // 迅雷下载引擎（磁力优先）：libxl_thunder_sdk.so + libxl_stat.so + thunder.jar。
        // 起不来（ABI 不符 / appKey 失效 / 服务端变更）不影响任何现有能力 —— 所有调用点都会回落内置 BT。
        var thunder = new Platforms.Android.ThunderP2P(
            Path.Combine(FileSystem.CacheDirectory, "thunder"), BtFileLog.Write);
        CatClawVideo.Core.Interfaces.MagnetEngines.Thunder = thunder;
        _ = Task.Run(async () => { try { await thunder.EnsureReadyAsync(); } catch { } });

#else
        // 桌面桥（2026-09-29 JRE 全退役）：JavaBridge 目录只用于部署完整性检查，
        // 桥唯一跑在 ART guest 里（guest 里真 ART，不需要宿主任何 Java）。
        // ⚠ 不能因缺 JavaBridge 换 NullSpiderRuntime——那会把 ART 桥一起顶掉（jar 源全灭）。
        // Guard 加固源的外置 Android 桥（com.catclaw.bridge，arm64-only + libndk_translation）。
        // 见 SpiderBridgeClient 注释：壳解密器是 arm64 ELF，必须在命名空间被 native bridge
        // 接管的进程里装载，故拆到独立 APK，桌面经 adb forward 用同一套协议驱动。
        CatClawVideo.Core.Providers.SpiderBridgeClient.Log = msg =>
        {
            System.Diagnostics.Debug.WriteLine(msg);
            DiagLog.Write(msg);
        };
        // 宿主 proxy 端口下发给桥（Guard 系网盘源靠 SpiderApi.getAddress/getPort 拼数据端点）
        try { CatClawVideo.Core.Providers.SpiderBridgeClient.HostProxyPort = spiderProxy.Port; } catch { }
        // AVD 名**不在这里定死**（2026-10-15 实测踩坑）：这里算的是"进程启动那一刻"的运行环境，
        // 而运行环境完全可能是启动之后才下载安装好的 —— 那时 Avd 已被写成开发用的 catclaw_gapi36，
        // 它又不在本机 AVD 家目录里（那儿登记的是 catclaw_play36）⇒ 模拟器报
        // "Unknown AVD name [catclaw_gapi36]" 当场退出，adb 永远认不到设备。
        // 现在这里只保留**显式覆盖**（CATCLAW_ANDROID_AVD，开发用）；默认名改由桥在真正启动
        // 模拟器的那一刻按当时的运行环境判定（见 SpiderBridgeClient.ResolveAvd）。
        CatClawVideo.Core.Providers.SpiderBridgeClient.Avd ??=
            Environment.GetEnvironmentVariable("CATCLAW_ANDROID_AVD");
        // 「显示模拟器画面」偏好（设置页可切）：true 时启动模拟器不加 -no-window，弹出 Android 窗口
        CatClawVideo.Core.Providers.SpiderBridgeClient.ShowEmulatorWindow =
            Preferences.Default.Get("emu_show_window", false);

        var bridgeDir = CatClawVideo.Core.Providers.JavaSpiderRuntime.FindBridgeDir();
        CatClawVideo.Core.Interfaces.ISpiderRuntime jarRuntime = bridgeDir != null
            ? new CatClawVideo.Core.Providers.JavaSpiderRuntime(bridgeDir, m =>
            {
                // Windows 桌面没有控制台，Debug.WriteLine 不挂调试器就抓不到 →
                // 同时落 %APPDATA%\CatClawVideo\home-debug.log，排障 Guard 解壳/桥加载要看这段
                System.Diagnostics.Debug.WriteLine(m);
                DiagLog.Write(m);
            },
            // 桥起来后下发 proxy 端口：Guard 系网盘源（MDrive 等）靠 SpiderApi.getPort()
            // 拼「云盘配置」数据端点（对齐 Android 的 TvBoxCompatBridge.SetProxyPort）
            proxyPort: () => spiderProxy.Port)
            : new CatClawVideo.Core.Providers.NullSpiderRuntime("jvm-dex");

        // ★★ 统一引导（2026-10-11）：**先桥，后 QEMU**，只此一条路径。
        //   为什么必须合并成一条：原来「迅雷 VM 预热」延迟 3s、「爬虫 VM 预热」延迟 6s，
        //   而外置 Android 桥要 ~5s 才好 —— 3s 那次先跑，看到桥还没好就回落 QEMU，
        //   guest 照样被拉起来（实测 22:19:17 起了 2.6GB，用户看到的正是它）。
        //   合并后：**桥可用 ⇒ 两个预热一律不做**，QEMU ART guest 一次都不会被拉起。
        //
        // ★ 2026-10-15：包成具名本地函数并注册到 RuntimeBootstrapper —— 运行环境**装好之后**
        //   首页会再拉一次（否则引导早已失败退场，装好也只能重启程序才生效）。
        async Task BootstrapAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                for (var i = 0; i < 30; i++)   // 等订阅解析出站点（最多 30s）
                {
                    if (CatClawVideo.Core.Models.SiteRegistry.Sites.Count > 0) break;
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }

                // ① 先试外置 Android 桥。EnsureReadyAsync 自带「没设备就按 CATCLAW_ANDROID_AVD
                //    （默认 catclaw_gapi36）拉起模拟器 → 起桥服务 → adb forward/reverse」。
                if (await CatClawVideo.Core.Providers.SpiderBridgeClient.EnsureReadyAsync().ConfigureAwait(false)
                    && await bridgeThunder.EnsureReadyAsync().ConfigureAwait(false))
                {
                    CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable = true;
                    BtFileLog.Write("[桥] 外置 Android 桥就绪（爬虫 + 迅雷都在桥里）→ 不启动 QEMU ART guest（省 ~2.5GB）");
                    return;
                }

                // ② 桥不可用：**不再回落 QEMU**（2026-10-15：QEMU 整线退役，也不再随包）。
                //    jar / 网盘源此时明确不可用，冷启动进度停在「运行环境未就绪」这一档，
                //    自检里会显示「Android 运行环境：未找到」并给出下载入口 —— 不藏着掖着。
                BtFileLog.Write("[桥] 外置 Android 桥不可用；本机 QEMU 已退役 → 不起回退（jar/网盘源暂不可用）");
                CatClawVideo.Core.Providers.SpiderBridgeClient.ReportStage(
                    0.45, "Android 运行环境未就绪：jar/网盘源暂不可用（可在启动自检里下载）");
            }
            catch (Exception ex) { BtFileLog.Write($"[桥] 引导异常：{ex.Message}"); }
        }

        CatClawVideo.Maui.Services.RuntimeBootstrapper.Run = BootstrapAsync;
        _ = Task.Run(BootstrapAsync);

        // 桥爬虫 UI 事件 → MAUI 对话框/二维码（对齐 TVBox：点「登入自己网盘」弹
        // 「已登录+启用中」列表 + 扫码登录，而非回落网页）
        if (jarRuntime is CatClawVideo.Core.Providers.JavaSpiderRuntime desktopJar)
        {
            CatClawVideo.Maui.Services.SpiderUiHost.Attach(desktopJar);

            // ★ 外置 Android 桥接管时，Guard 解密服务（18481）也必须供上：
            //   jar 的扫码链路探测 10.0.2.2:18481。此前它只在 QEMU guest 握手时启动，
            //   切断 QEMU 后没人供 ⇒ 扫码链路拿不到解密（2026-10-11）。
            CatClawVideo.Core.Providers.JavaSpiderRuntime.ExternalBridgeAttached =
                () => desktopJar.StartGuardSvcIfNeeded(CancellationToken.None);
            if (CatClawVideo.Core.Providers.JavaSpiderRuntime.ExternalBridgeOwnsSpiders)
                desktopJar.StartGuardSvcIfNeeded(CancellationToken.None);

            // ★ 迅雷引擎合并（QEMU ART VM 租用）已随 QEMU 整线退役删除（2026-10-15）：
            //   那段的前提是"随包 QemuGuest\x86guest\art_initrd_x64.gz 存在"，现在不再随包，
            //   迅雷完全由外置桥内那套承担（BridgeThunderEngine），不需要宿主侧再租 VM。
            _ = desktopJar;
        }

#endif
        // spider 型直播源（TVBox LivePlayActivity 的 liveContent 链）：Core.Live 不依赖爬虫层，
        // 由这里在运行时都造好之后把取列表的口子注进去。
        CatClawVideo.Core.Live.LiveSpiderBridge.Fetcher = async (entry, ct) =>
        {
            var target = entry.Url.Length > 0 ? entry.Url : entry.Api;
            if (target.Length == 0) return null;
            var isJs = target.Contains(".js", StringComparison.OrdinalIgnoreCase);
            var site = new CatClawVideo.Core.Models.VodSiteInfo
            {
                Key = "live#" + (entry.Name.Length > 0 ? entry.Name : target),
                Name = entry.Name,
                Api = target,
                Type = 3,
                Jar = entry.Jar.Length > 0 ? entry.Jar : null,
                SubscriptionName = "live",
                SpiderKind = isJs ? CatClawVideo.Core.Models.VodSpiderKind.Script
                                  : CatClawVideo.Core.Models.VodSpiderKind.Jar,
            };
            var rt = isJs ? (object)tvboxJsRuntime : jarRuntime;
            if (rt is not CatClawVideo.Core.Interfaces.ISpiderLiveRuntime live) return null;
            var txt = await live.LiveContentAsync(site, target, ct);
            // 运行时把「无结果」规范成 "{}"，这里当空处理让上层给出「未返回频道列表」
            return string.IsNullOrWhiteSpace(txt) || txt.Length < 3 ? "" : txt;
        };

        CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable = jsRuntime.IsSupported || tvboxJsRuntime.IsSupported;
        CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable = jarRuntime.IsSupported;

        // ★ jar 桥预热：ART guest VM 冷启动（5~11s）此前发生在第一次 jar 站点调用上——
        //   首选站点是 jar 源时就整段叠进「首页首载/切站」等待里。订阅就绪且存在 jar 源时，
        //   启动后台把桥拉起来（延迟 6s 避让首屏 UI 与迅雷 VM 预热；幂等、失败无害）。
        //   ⚠ 桌面专属：引用 JavaSpiderRuntime.WarmUpAsync（ART guest 桥的预热）；
        //   Android 的 DexSpiderRuntime 是进程内 Dex 桥、无 VM 冷启动，无需预热
        //   （is 模式检查对 Android 的 var 具体类型是 CS8121 编译错误）。
        // （爬虫 VM 预热已并入上面的「统一引导」——两处分开写会因时序互相打架，
        //   导致桥还没好就先起了 QEMU guest。这里不再单独预热。）

        // js2Proxy 回环代理路由：按 siteKey 查站点 → 按 SpiderKind 分派 JS/Java 爬虫运行时
        spiderProxy.JsProxyHandler = (query, ct) =>
        {
            var key = query.GetValueOrDefault("siteKey");
            var site = key is null ? null : CatClawVideo.Core.Models.SiteRegistry.Find(key);
            if (site is not null)
            {
                CatClawVideo.Core.Interfaces.ISpiderRuntime? rt = site.SpiderKind switch
                {
                    CatClawVideo.Core.Models.VodSpiderKind.Jar => jarRuntime,
                    CatClawVideo.Core.Models.VodSpiderKind.Script => tvboxJsRuntime,
                    _ => null,
                };
                return rt is CatClawVideo.Core.Interfaces.ISpiderProxyRuntime pr
                    ? pr.ProxyAsync(query, ct)
                    : Task.FromResult<(int Status, string Mime, byte[]? Body)?>(null);
            }

            // spider 自发的 proxy 请求（如 Guard 系网盘源 detailContent 内部的 do=config）
            // 不携带 siteKey → 依次尝试各支持回调的运行时（jar → js），谁接住用谁；
            // 都不接则回 null（调用方 502）。
            foreach (var rt in new CatClawVideo.Core.Interfaces.ISpiderRuntime[] { jarRuntime, tvboxJsRuntime })
            {
                if (rt is CatClawVideo.Core.Interfaces.ISpiderProxyRuntime pr)
                    return pr.ProxyAsync(query, ct);
            }
            return Task.FromResult<(int Status, string Mime, byte[]? Body)?>(null);
        };

        // 流式中继（2026-10-02）：网盘取流 do=proxy&key=… 是 883MB~2.35GB 的连续流，
        // 走上面的 byte[] 回调等于整部片子进宿主内存（必 OOM，且要等下完才起播）。
        // jar 隧道这条实现了流式（ResponseHeadersRead），这里只把请求交给它；
        // 返回 null（无流式实现 / 桥不可用）时服务端自动落回 byte[] 老路，行为不变。
        // 第二参传原始请求头：壳的 Range 约定走 query（见 ProxyStreamAsync 注释）。
        spiderProxy.JsProxyStreamHandler = (query, requestHeaders, ct) =>
        {
#if !ANDROID
            // ⚠ 这个 is 模式只能在桌面上用：Android 的 jarRuntime 静态类型是 DexSpiderRuntime
            //   （与 JavaSpiderRuntime 无继承关系，Roslyn 直接 CS8121 拒绝），而流式实现
            //   ProxyStreamAsync 只挂在 JavaSpiderRuntime 上。Android 保持原有 byte[] 回落路径。
            if (jarRuntime is CatClawVideo.Core.Providers.JavaSpiderRuntime streaming)
                return streaming.ProxyStreamAsync(query, requestHeaders, ct);
#endif
            return Task.FromResult<(int Status, string Mime, Stream Body, IReadOnlyDictionary<string, string>? Headers)?>(null);
        };

        // 「源看不到」类问题的第一现场：订阅解析完/站点集合一变就记一行
        // （可播 = type1 MacCMS + 运行时就绪的 spider 源；jar 桥可用性单独打印）
        CatClawVideo.Core.Models.SiteRegistry.Changed += () =>
            DiagLog.Write($"[源] 站点合计={CatClawVideo.Core.Models.SiteRegistry.Sites.Count} " +
                          $"可播={CatClawVideo.Core.Models.SiteRegistry.Playable.Count()} " +
                          $"jar桥={CatClawVideo.Core.Models.SiteRegistry.JarSpiderAvailable} " +
                          $"js={CatClawVideo.Core.Models.SiteRegistry.JsSpiderAvailable} " +
                          $"桥目录={CatClawVideo.Core.Providers.JavaSpiderRuntime.FindBridgeDir() ?? "(未找到)"}");


        // 下载管理器：HTTP 直链 + 磁力下载（Windows 注入迅雷引擎；Android 磁力暂不支持）
        services.AddSingleton(sp => new DownloadManager(magnetDownloadEngine));

        // 平台嗅探器：Android WebView 拦截 / Windows WebView2 拦截（TVBox parse=1 页面解析）
#if ANDROID
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new Platforms.Android.AndroidWebSniffer();
#elif WINDOWS
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new Platforms.Windows.WindowsWebSniffer();
#else
        CatClawVideo.Core.Interfaces.IWebSniffer sniffer = new CatClawVideo.Core.Providers.NullWebSniffer();
#endif

        var vodProvider = new CatClawVideo.Core.Providers.CompositeVodSourceProvider(            new IVodSourceProvider[]
            {
                new CatClawVideo.Core.Providers.CatClawSourceProvider(),
                new CatClawVideo.Core.Providers.MacCmsJsonProvider(sniffer),
                new CatClawVideo.Core.Providers.SpiderVodProvider(jsRuntime, jarRuntime, sniffer, log: BtFileLog.Write,
                    tvboxJsRuntime: tvboxJsRuntime),
            });
        services.AddSingleton<IVodSourceProvider>(vodProvider);

        // /rc/search 的处理器：跨源搜一遍再回 json（对位 TVBox /action?do=search）。
        // 限制站点数是因为这是给电脑浏览器用的，等 40 个站不如先出前几个站的结果。
        CatClawVideo.Core.Services.RemoteControlHub.SearchHandler = async (text, ct) =>
        {
            var hits = new List<Dictionary<string, string>>();
            foreach (var site in CatClawVideo.Core.Models.SiteRegistry.Playable
                         .Where(x => x.Searchable).Take(8).ToList())
            {
                try
                {
                    var items = await vodProvider.SearchAsync(site, text, ct).ConfigureAwait(false);
                    foreach (var it in items.Take(15))
                        hits.Add(new Dictionary<string, string>
                        {
                            ["site"] = site.Name,
                            ["siteName"] = site.Name,
                            ["sourceKey"] = site.Key,
                            ["name"] = it.Title,
                            ["id"] = it.Id,
                            ["cover"] = it.Cover ?? "",
                        });
                }
                catch { /* 单站失败不该拖垮整个搜索 */ }
            }
            return System.Text.Json.JsonSerializer.Serialize(hits);
        };

        // ═══════════════════════════════════════════════════
        // 封面获取与兜底
        //   源封面取不到（防盗链 / CDN 失效 / JS 盾 / 站点资源损坏，如毒舌电影）时按序兜底：
        //     ① 跨源检索：用**用户自己订阅的可搜索源**按片名找同名片封面
        //        （实测一次站内搜索即拿到详情链接+封面，无第三方限流）
        //     ② 豆瓣海报（会限流，作为补充）
        //     ③ 返回 null → 界面显示本地渲染的占位海报（绝不空白）
        //   内置磁盘缓存 + 并发上限 + 超时 + 失败负缓存 + 主机熔断，避免慢站把列表拖死。
        // ═══════════════════════════════════════════════════
        services.AddSingleton(new CatClawVideo.Core.Services.CoverImageService(
            CatClawVideo.Core.AppPaths.CacheRoot,
            DiagLog.Write,
            crossSourceCover: async (title, ct) =>
            {
                // 只打「声明了站内搜索接口」的站：没有搜索接口的站会退化成扫分类页（一次十几个请求），
                // 不适合做封面兜底。命中要求标题归一后一致，避免借到同名的别的片子。
                var target = CatClawVideo.Core.Services.CoverImageService.NormalizeTitle(title);
                if (target.Length < 2) return null;

                foreach (var site in SiteRegistry.Playable)
                {
                    if (!site.DeclaredSearch) continue;
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var items = await vodProvider.SearchAsync(site, title, ct);
                        var hit = items.FirstOrDefault(i =>
                            CatClawVideo.Core.Services.CoverImageService.NormalizeTitle(i.Title) == target &&
                            !string.IsNullOrWhiteSpace(i.Cover));
                        if (hit?.Cover is { Length: > 0 } cover)
                        {
                            DiagLog.Write($"[cover] 跨源命中「{title}」← {site.Name}");
                            return cover;
                        }
                    }
                    catch
                    {
                        // 单站失败继续下一站
                    }
                }
                return null;
            }));

        // ═══════════════════════════════════════════════════
        // ViewModels
        // ═══════════════════════════════════════════════════
        services.AddSingleton<CatClawVideo.Core.Services.IUpdateService, UpdateService>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<VideoPlayerViewModel>();
        services.AddTransient<DownloadsViewModel>();
        services.AddTransient<AboutViewModel>();

        // ═══════════════════════════════════════════════════
        // Pages / Shell
        // ═══════════════════════════════════════════════════
        services.AddSingleton<AppShell>();
        services.AddSingleton<Pages.MainPage>();
        services.AddTransient<Pages.HomePage>();
        services.AddTransient<Pages.FavoritesPage>();
        services.AddTransient<Pages.SettingsPage>();
        services.AddTransient<Pages.DownloadDetailPage>();
        services.AddTransient<Pages.VideoPlayerPage>();
        services.AddTransient<Pages.WatchPage>();
        services.AddTransient<Pages.SearchPage>();
        services.AddTransient<Pages.SourceConfigPage>();
        services.AddTransient<Pages.HistoryPage>();
        services.AddTransient<Pages.LocalMediaPage>();
        services.AddTransient<Pages.AboutPage>();
        services.AddTransient<Pages.DiagnosticLogPage>();
        services.AddTransient<Pages.LivePage>();
        services.AddTransient<Pages.LiveSourcePage>();
        services.AddTransient<Pages.RemotePage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();

        // Core 层日志 → DiagLog（Android 上同时进 logcat，tag=CatClawDiag）
        CatClawVideo.Core.Providers.CatClawLog.Sink = DiagLog.Write;

        // 诊断日志服务：构造即注册进 Core.Log 门面（设置页开关控制，默认关闭、关闭时零开销）。
        // 必须在 builder.Build() 之后构造 —— 它读 Preferences 与 AppPaths，依赖已初始化的平台层。
        _ = new Services.DiagnosticLog();
        Services = app.Services;

        // ═══════════════════════════════════════════════════
        // 启动后台恢复订阅源：解析已保存订阅 → 填充 SiteRegistry
        // （失败静默不阻塞首帧；多订阅取第一个成功者）
        // ═══════════════════════════════════════════════════
        // ★ 启动先把**上次解析成功的站点缓存**同步灌进注册表（毫秒级、离线可用）：
        //   否则首屏渲染时异步恢复还没回来 → 每次启动都闪「还没有可用的源 + 扫码配对」引导页
        //   （2026-09-16 用户实测：以为数据没持久化）。联网刷新在下面后台照常进行并覆盖缓存。
        try
        {
            var cachedSites = CatClawVideo.Core.Models.SiteCache.Load();
            if (cachedSites is not null)
            {
                SiteRegistry.Replace(cachedSites);
                DiagLog.Write($"[启动] 站点缓存命中 {cachedSites.Count} 个（后台联网刷新中）");
            }
        }
        catch { }

        _ = Task.Run(async () =>
        {
            try
            {
                var database = app.Services.GetRequiredService<VideoDatabase>();
                var subscriptionManager = app.Services.GetRequiredService<ISubscriptionManager>();
                // 单选启用（2026-09-26 用户定案）：一次只用一个订阅源，行内开关切换。
                               var subs = await database.GetAllSubscriptionsAsync();
                // 全停是合法状态（用户主动停用）：尊重之，站点表空 → 首页回引导态
                var active = subs.FirstOrDefault(s => s.Enabled);
                if (active is null)
                {
                    SiteRegistry.Replace([]);
                    DiagLog.Write("[启动] 没有启用中的订阅源（可在订阅管理页启用），站点列表为空");
                    return;
                }
                var sites = await subscriptionManager.LoadSubscriptionAsync(active.SourceUrl);
                SiteRegistry.Replace(sites);
                CatClawVideo.Core.Models.SiteCache.Save(sites);   // 供下次启动秒读
                DiagLog.Write($"[启动] 订阅恢复成功: {active.Name}（启用源）→ {sites.Count} 站点");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[启动] 订阅恢复异常: {ex.Message}");
            }
        });

        return app;
    }

}

