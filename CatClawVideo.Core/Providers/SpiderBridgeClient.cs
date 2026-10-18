using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// Guard 加固源的**外部 Android 桥**客户端（桌面/Windows）。
///
/// <para><b>为什么要它</b>：Guard 壳（<c>csp_*Guard</c>）的原生解密器是 <b>arm64 ELF</b>，
/// 只有命名空间被 native bridge（libndk_translation）接管的进程才装载得起来。Android 的
/// native bridge 按 app 命名空间开启，开启条件是 <c>primaryCpuAbi</c> 为 ARM ABI ——
/// 主程序带 <c>lib/x86_64</c>（.NET 运行时/迅雷都在里面），装成 arm64 等于让整个运行时走翻译。
/// 因此壳那部分被拆到独立的 arm64-only APK（<c>com.catclaw.bridge</c>）里跑，桌面经
/// <c>adb forward</c> 用**与 guest 桥完全相同的协议**（换行分隔 JSON + id 回显）驱动它。</para>
///
/// <para>桥不可用时所有方法返回 null，调用方回落到本来的 QEMU/ART guest 路径 —— 行为不劣化。</para>
/// </summary>
public static class SpiderBridgeClient
{
    /// <summary>模拟器内 bridge 的监听端口（bridge APK 固定 18600）。</summary>
    private const int GuestPort = 18600;

    /// <summary>宿主侧映射端口。
    /// <para><b>为什么不是 18600</b>：Windows 主程序自己的 QEMU ART guest 也把 guest 的 18600
    /// hostfwd 到宿主 18600（<c>[srv] 桥监听 :18600</c>），两者会抢同一个宿主端口
    /// （实测 <c>cannot bind listener 10048</c>）。故宿主侧改用 18700 → guest 18600。
    /// 用 CATCLAW_BRIDGE_HOST_PORT 覆盖。</para></summary>
    private static readonly int HostPort = int.TryParse(
        Environment.GetEnvironmentVariable("CATCLAW_BRIDGE_HOST_PORT"), out var hp) ? hp : 18700;

    /// <summary>adb 串号（空 = 自动取第一台 emulator）。用 CATCLAW_ANDROID_SERIAL 覆盖。</summary>
    public static string? Serial { get; set; } =
        Environment.GetEnvironmentVariable("CATCLAW_ANDROID_SERIAL");

    /// <summary>要拉起的 AVD 名（空 = 不自动启动模拟器）。用 CATCLAW_ANDROID_AVD 覆盖。</summary>
    public static string? Avd { get; set; } =
        Environment.GetEnvironmentVariable("CATCLAW_ANDROID_AVD");

    /// <summary>
    /// 真正要用的 AVD 名：显式指定（<see cref="Avd"/>，来自 CATCLAW_ANDROID_AVD）优先；
    /// 否则用**就地运行环境**配套的 <see cref="BundledAvdName"/>。
    /// <para>刻意在启动模拟器的那一刻才算，而不是进程启动时算死 —— 运行环境可能是启动之后
    /// 才下载安装好的（2026-10-15 实测：启动时算成开发用的 catclaw_gapi36，装好环境后
    /// 模拟器 "Unknown AVD name" 直接退出，adb 永远认不到设备）。</para>
    /// </summary>
    private static string? ResolveAvd(bool inPlaceSdk)
    {
        if (Avd is { Length: > 0 }) return Avd;      // 显式覆盖（开发用）
        return inPlaceSdk ? BundledAvdName : null;
    }

    /// <summary>
    /// 启动模拟器时是否**显示它自己的窗口**（Android 图形界面）。
    /// <para>默认 false = 带 <c>-no-window</c> 后台跑（播放器场景没人要看它）；
    /// 打开后启动模拟器会弹出一个独立的 Android 窗口，可以直接在里面操作（排查/试用 jar 源时有用）。
    /// 由界面层（设置页）写入并持久化；**改动在下次启动模拟器时生效**（模拟器只能整只重启）。</para>
    /// </summary>
    public static bool ShowEmulatorWindow { get; set; }

    public static Action<string>? Log;

    /// <summary>
    /// 真实进度上报（0~1 + 阶段文案）。冷启动遮罩订阅它来驱动进度条 —— 每个百分比都对应一个
    /// **用 adb 真实观测到的里程碑**（设备出现 / sys.boot_completed=1 / 桥 APK 就绪 /
    /// forward 就位 / ping 通），不再按时间编。没等到下一个里程碑时进度就停在当前档，
    /// 「卡在哪一步」一眼可见（2026-10-15 用户要求：要真进度条）。
    /// </summary>
    public static Action<double, string>? Progress;

    /// <summary>上报一个真实里程碑（同时写日志，两边对得上）。</summary>
    private static void Report(double pct, string stage)
    {
        Log?.Invoke($"[bridge] {stage}");
        try { Progress?.Invoke(pct, stage); } catch { }
    }

    /// <summary>宿主在「桥不可用 → 回落 QEMU/ART」这类分支里也能推进真实进度。</summary>
    public static void ReportStage(double pct, string stage) => Report(pct, stage);

    // ─────────── 模拟器生命周期（2026-10-15）───────────
    //
    // 背景（实测日志 18:26~18:30）：adb 认不到设备时，每个等桥的调用方都会各自
    // 「没设备 → 拉起模拟器」，同一秒里能连拉 4 次，每次都是 3~4GB 的 qemu；而这些
    // 进程没人回收 —— 关掉程序后它们照样挂着（用户实测：「关程序不关虚拟机」）。
    // 这里补三件事：① 只允许有一个「我们拉起的」模拟器；② 启动前清掉上一轮的僵尸；
    // ③ 退出程序时回收（adb emu kill → 进程树兜底）。

    /// <summary>本次使用的模拟器控制台端口（adb 端口 = 它 + 1）。
    /// <para>默认 5562，但**不再写死**：Windows 上 Hyper-V/WSL/Docker 会动态保留一批 TCP 端口段
    /// （2026-10-15 本机实测落在 5508~5607），落在保留段里的 bind 直接失败 —— 而模拟器对此
    /// <b>不报错</b>：guest 照常起来，只是 console/adb 端口根本没开，于是 adb 永远看不到设备、
    /// 桥永远连不上。启动前用 <see cref="PickEmulatorPort"/> 挑一个真能绑上的口。</para></summary>
    public static int EmulatorPort { get; private set; } = 5562;

    /// <summary>本进程拉起的模拟器进程（null = 没拉起过）。</summary>
    private static Process? _emulator;
    private static readonly object _emuLock = new();

    /// <summary>本程序拉起的模拟器是否还在跑（设置页状态显示用）。</summary>
    public static bool EmulatorRunning
    {
        get { lock (_emuLock) return _emulator is { HasExited: false }; }
    }

    /// <summary>设置页「Android 虚拟机」行的状态文案（纯状态读取，不探测、不阻塞）。</summary>
    public static string StatusText()
    {
        if (EmulatorRunning) return _ready ? "运行中 · 桥已就绪" : "启动中…";
        return _ready ? "运行中（外部启动）" : "未运行";
    }

    /// <summary>模拟器自身输出落盘路径。「起了但 adb 认不到」这类问题，只有它自己的日志说得清。</summary>
    private static string EmulatorLogPath => Path.Combine(AppPaths.DataRoot, "logs", "emulator.log");

    /// <summary>宿主 SpiderProxyServer 的端口。壳用 <c>SpiderApi.getAddress()</c>（硬编码
    /// <c>http://127.0.0.1:&lt;port&gt;</c>）拼「云盘配置」数据端点；桥连上后会把它下发到壳，
    /// 并把该端口 adb forward 进模拟器 —— 这样壳里的 127.0.0.1:port 就打到宿主 proxy。</summary>
    public static int HostProxyPort { get; set; }

    // ─────────── 并发模型（2026-10-12）───────────
    //
    // 背景：宿主侧本来是并发的（搜索页 SearchConcurrency=16、预加载并发发起），但这里一把
    //   全局 Gate(1,1) 把**整趟往返**锁死（load + 发请求 + 等响应），加上桥内那个单线程读
    //   循环，两层合起来把并发压成 1 路。实测预加载 240s 只装成 14 个站。
    //
    // 现在拆成三把各司其职：
    //   · WriteGate   —— 只护「往 socket 写一行」。多线程并发写同一条 NetworkStream 会让
    //     JSON 行交错（宿主按 id 也拆不开）。**必须在 Pending 注册之后、写之前取**。
    //   · LoadGate    —— 只护 load。转译层「同刻只容一个 load」是硬约束（并发 load 抢 so
    //     句柄会把桥撞崩）；桥内也加了 LOAD_LOCK，这里是第二道。
    //   · _connLock   —— 护 Connect()/Forget() 对 _sock/_ns/_rlen/_rpos 的读写。
    //   call 本身**不持任何全局锁** —— 同站串行由桥内的 SITE_LOCKS 保证。
    //
    // ★ CATCLAW_BRIDGE_SERIAL=1 ⇒ 退回「全串行」（call 也走 Gate）。并发模型改动影响面
    //   大，出问题时应当能不改代码立刻退回已知可用状态。

    /// <summary>全串行回退开关（排障用）：设 CATCLAW_BRIDGE_SERIAL=1。</summary>
    private static readonly bool ForceSerial =
        Environment.GetEnvironmentVariable("CATCLAW_BRIDGE_SERIAL") == "1";

    /// <summary>往 socket 写一行的串行闸门（防响应字节交错）。</summary>
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    /// <summary>load 闸门：转译层同刻只容一个 load（桥内 LOAD_LOCK 之外的第二道）。</summary>
    private static readonly SemaphoreSlim LoadGate = new(1, 1);

    /// <summary>迅雷 op 专用闸门（与 load/call 互不阻塞）。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>连接建立/销毁的互斥（用 lock 而非信号量：Connect 内部会调 Forget，需可重入）。</summary>
    private static readonly object _connLock = new();
    private static readonly SemaphoreSlim ReadyGate = new(1, 1);
    private static Socket? _sock;
    private static NetworkStream? _ns;
    private static readonly byte[] Rbuf = new byte[64 * 1024];
    private static int _rlen, _rpos;
    private static int _nextId = 1;
    /// <summary>已装载站点。并发后从多线程读写 ⇒ 必须是并发容器（原来是 HashSet，靠外层 Gate 保活）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Loaded =
        new(StringComparer.Ordinal);

    /// <summary>桩上行的 UI 事件（ui-dialog / ui-dismiss / ui-toast）——与应答共用一条连接，
    /// 按 <c>ev</c> 字段识别。宿主 MAUI 层订阅渲染（同 JavaSpiderRuntime.UiEvent 的用法）。</summary>
    public static Action<JsonObject>? UiEvent;

    /// <summary>已发出、等应答的请求：key = id。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> Pending = new();
    private static Task? _reader;
    private static string? _adb;
    private static bool _ready;

    /// <summary>所有 csp_ jar 源都走桥。
    /// <para>⚠ 不能只看类名是否以 Guard 结尾（曾经这么写，结果整批源掉回 QEMU/不可用，实测）：
    /// 加固方案不止一种 ——</para>
    /// <list type="bullet">
    /// <item>「ftyguard」壳（饭太硬那套）：入口类名带 Guard 后缀、assets 里是加密的 .guard；</item>
    /// <item>「FishGuard」壳（摸鱼儿那套）：classes.dex 是明文、类名不带后缀
    ///   （声明 csp_Bili 就是类 Bili），但 assets 里同样是 arm64 的 FishGuard-v8.so。</item>
    /// </list>
    /// <para>两者都必须在本 APK 这种 bridged 命名空间里装载（arm64 .so），
    /// 所以判据放宽成「type=3 且 api 是 csp_」。桥 bindShell 找不到 Init/DexNative 不致命，
    /// 会继续按声明类名装载 —— 明文 jar 照样能跑。</para></summary>
    public static bool ShouldRoute(VodSiteInfo site) =>
        site.Type == 3
        && !string.IsNullOrEmpty(site.Api)
        && site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase);

    // ─────────────── adb / 模拟器 引导 ───────────────

    // ─────────── 随包 Android 环境（0.2.5 起安装包自带）───────────
    //
    // 安装包内布局（与 Android SDK 同构，模拟器据此自动推断 SDK 根）：
    //   {安装目录}\AndroidSdk\emulator\emulator.exe
    //   {安装目录}\AndroidSdk\platform-tools\adb.exe
    //   {安装目录}\AndroidSdk\system-images\android-36\google_apis_playstore\x86_64\
    //
    // 为什么按这个布局：emulator.exe 的 SDK 根 = 自己的上一级目录，AVD 里那句
    // image.sysdir.1（相对路径）就是相对它解析的 —— 于是整包自洽，不依赖用户在
    // 机器上装过 Android SDK，也不用改注册表/环境变量（2026-10-12 预检实测）。

    /// <summary>随包 Android SDK 根；未随包（开发机/轻量包）返回 null，回落本机已装的 SDK。</summary>
    private static string? BundledSdkRoot()
    {
        var d = Path.Combine(AppContext.BaseDirectory, "AndroidSdk");
        return Directory.Exists(Path.Combine(d, "emulator")) ? d : null;
    }

    /// <summary>就地安装的 Android SDK 根（<see cref="CatClawVideo.Core.AppPaths.RuntimeRoot"/> 下的
    /// <c>AndroidSdk</c>，正常就是程序同目录那份）；未就绪返回 null。
    /// <para>2026-10-15 用户要求：运行环境只认程序同目录这一处，不再回退机器上装的 Android SDK
    /// （那套会让程序悄悄跑起 Android Studio 的模拟器）。</para></summary>
    public static string? ProvisionedSdkRoot()
    {
        var d = Path.Combine(CatClawVideo.Core.AppPaths.RuntimeRoot, "AndroidSdk");
        return File.Exists(Path.Combine(d, "emulator", "emulator.exe")) ? d : null;
    }

    /// <summary>实际生效的 Android SDK 根：随包优先，其次首运下载的，null = 都没有（需要引导下载）。</summary>
    public static string? EffectiveSdkRoot() => BundledSdkRoot() ?? ProvisionedSdkRoot();

    /// <summary>随包镜像对应的 AVD 名。刻意与开发用的 catclaw_gapi36 区分开，
    /// 免得装了发行版的用户机器上两个 AVD 撞名、互相覆盖 userdata。</summary>
    public const string BundledAvdName = "catclaw_play36";

    /// <summary>随包环境的 AVD 家目录：%LOCALAPPDATA%\CatClawVideo\avd。
    /// <para><b>为什么不能放安装目录</b>：模拟器要在 AVD 目录里生成 ram.img / userdata-qemu.img /
    /// cache.img 等（冷启后约 3.2GB），而 Program Files 下普通用户不可写。</para></summary>
    private static string BundledAvdHome() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CatClawVideo", "avd");

    /// <summary>给模拟器分配的 vCPU 数 = 宿主**逻辑处理器数的一半**。
    ///
    /// <para><b>为什么取一半</b>：模拟器只是宿主上的一个进程，宿主同时还要跑应用自身、
    /// FFmpeg 解码与界面渲染。把逻辑核全给客机，vCPU 线程会和宿主线程互相抢核，反而更慢。
    /// 取一半后：本机 12 逻辑核 ⇒ 6（正好等于物理核数）；20 线程的机器 ⇒ 10。</para>
    ///
    /// <para><b>下限 2</b>：Android 在单核上不具可用性，且模拟器默认值就是 2。
    /// <b>上限 16</b>：hw.cpu.ncore 在 emulator\lib\hardware-properties.ini 里没有声明 max，
    /// 但 vCPU 超过宿主物理核只有害无益，故设一个保守上限。</para>
    ///
    /// <para>⚠ 命令行 -cores 会**覆盖** AVD 里的 hw.cpu.ncore，实际生效的是启动参数那一处；
    /// 两处都写成同一个值只为 AVD 定义本身可读、不至于自相矛盾。</para></summary>
    private static int GuestCoreCount() =>
        Math.Clamp(Environment.ProcessorCount / 2, 2, 16);

    /// <summary>首次运行时把随包镜像登记成一个 AVD（就是两份文本 ini）。
    /// <para><b>为什么不调 avdmanager</b>：那要另带 cmdline-tools（~150MB）和 JDK；
    /// 而 AVD 定义本身只有 &lt;name&gt;.ini 和 &lt;name&gt;.avd\config.ini 两个文件，
    /// 模拟器对缺失的键一律取默认值。</para></summary>
    private static bool EnsureBundledAvd()
    {
        try
        {
            var avdDir = Path.Combine(BundledAvdHome(), BundledAvdName + ".avd");
            var iniFile = Path.Combine(BundledAvdHome(), BundledAvdName + ".ini");
            var cfgFile = Path.Combine(avdDir, "config.ini");
            if (File.Exists(iniFile) && File.Exists(cfgFile)) return true;

            Directory.CreateDirectory(avdDir);
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(iniFile, string.Join("\r\n", new[]
            {
                "avd.ini.encoding=UTF-8",
                "path=" + avdDir,
                "path.rel=avd\\" + BundledAvdName + ".avd",
                "target=android-36",
            }) + "\r\n", utf8);

            // image.sysdir.1 必须是**相对**路径（相对模拟器推断出的 SDK 根）。
            // tag.id 要与镜像目录名 google_apis_playstore 一致，否则模拟器拒绝启动。
            File.WriteAllText(cfgFile, string.Join("\r\n", new[]
            {
                "PlayStore.enabled = no",
                "abi.type = x86_64",
                "avd.ini.encoding = UTF-8",
                "disk.cachePartition = yes",
                "disk.cachePartition.size = 66MB",
                "disk.dataPartition.size = 6442450944",
                "disk.systemPartition.size = 0",
                "disk.vendorPartition.size = 0",
                "fastboot.forceColdBoot = no",
                "fastboot.forceFastBoot = yes",
                "hw.accelerometer = yes",
                "hw.audioInput = no",
                "hw.audioOutput = no",
                "hw.battery = yes",
                "hw.camera.back = none",
                "hw.camera.front = none",
                "hw.cpu.arch = x86_64",
                "hw.cpu.ncore = " + GuestCoreCount(),
                "hw.device.manufacturer = Google",
                "hw.device.name = pixel_5",
                "hw.gps = yes",
                "hw.gpu.enabled = yes",
                "hw.gpu.mode = swiftshader_indirect",
                "hw.gsmModem = yes",
                "hw.keyboard = yes",
                "hw.lcd.density = 440",
                "hw.lcd.depth = 32",
                "hw.lcd.height = 2340",
                "hw.lcd.width = 1080",
                "hw.mainKeys = no",
                "hw.ramSize = 3072M",
                "hw.sdCard = no",
                "hw.sensors.light = yes",
                "hw.sensors.orientation = yes",
                "hw.sensors.proximity = yes",
                "image.sysdir.1 = system-images\\android-36\\google_apis_playstore\\x86_64\\",
                "tag.display = Google APIs",
                "tag.id = google_apis_playstore",
                "vm.heapSize = 256M",
            }) + "\r\n", utf8);

            Log?.Invoke("[bridge] 运行环境首次运行：已登记 AVD " + BundledAvdName + " → " + BundledAvdHome());
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke("[bridge] 登记随包 AVD 失败：" + ex.Message);
            return false;
        }
    }

    private static string? FindAdb()
    {
        if (_adb is not null) return _adb;
        // 随包 adb 排第一：发行版不依赖用户机器上装过 Android SDK。
        var bundledSdk = BundledSdkRoot();
        // 只认「程序同目录」那份（随包 = 就地安装，两者现在同一处）。2026-10-15 用户要求：
        // 不再自动去用机器上装的 Android SDK（%LOCALAPPDATA%\Android\Sdk / ANDROID_HOME /
        // ANDROID_SDK_ROOT / PATH），免得它在用户机器上悄悄跑起 Android Studio 的模拟器。
        // 想临时指到别处，用显式的 CATCLAW_ANDROID_SDK（开发用，默认不设）。
        var cands = new List<string?>
        {
            Environment.GetEnvironmentVariable("CATCLAW_ANDROID_SDK") is { Length: > 0 } ov
                ? Path.Combine(ov, "platform-tools", "adb.exe") : null,
            bundledSdk is not null ? Path.Combine(bundledSdk, "platform-tools", "adb.exe") : null,
            ProvisionedSdkRoot() is { } pv ? Path.Combine(pv, "platform-tools", "adb.exe") : null,
        };
        foreach (var c in cands)
            if (c is not null && File.Exists(c)) return _adb = c;
        // PATH 兜底
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in path.Split(Path.PathSeparator))
        {
            try { var f = Path.Combine(d.Trim(), "adb.exe"); if (File.Exists(f)) return _adb = f; } catch { }
        }
        return null;
    }

    private static (int Code, string Out) Run(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "(超时)"); }
            return (p.ExitCode, (so.Result + se.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary>
    /// 只拉起一个模拟器：已经在启动中就等它（不再重复 spawn），否则先清僵尸再起。
    /// <para>「重复 spawn」是本轮实测最贵的 bug：adb 认不到设备时，每个调用方都会各自拉起一个，
    /// 一秒 4 个、每个 3~4GB，而且都没人回收。</para>
    /// <para><paramref name="avdName"/> 由 <see cref="ResolveAvd"/> 在**启动这一刻**算出
    /// （不用进程启动时的 <see cref="Avd"/>，那时运行环境可能还没装好）。</para>
    /// </summary>
    private static bool StartEmulatorOnce(string adb, string emu, string? bundled, string avdName)
    {
        lock (_emuLock)
        {
            if (_emulator is { HasExited: false })
            {
                Report(0.12, "模拟器已在启动中，等它起来（不重复拉起）…");
                return true;
            }

            CleanupStaleEmulator(adb);

            // 让模拟器注册到一个**刚起**的 adb 服务：服务后起时注册包容易丢，设备就一直不出现。
            // ★★ 模拟器启动时会向 127.0.0.1:5037 的 adb 服务**注册自己**，服务不在就只有一行
            //    "Unable to connect to adb daemon on port: 5037"，之后 adb devices 永远是空。
            //    所以这里只 start-server（幂等），**绝不能 kill-server** ——
            //    2026-10-15 实测：这里原本写的是 kill-server，把本来能成的一次冷启直接判死
            //    （emulator.log 第 43 行就是那条 Unable to connect）。
            Run(adb, "start-server", 30000);

            EmulatorPort = PickEmulatorPort();
            Report(0.10, "正在启动 Android 模拟器…");
            var avdInOurHome = File.Exists(Path.Combine(BundledAvdHome(), avdName + ".ini"));
            Log?.Invoke($"[bridge] 启动模拟器 {avdName} …（端口 {EmulatorPort}/{EmulatorPort + 1}；" +
                        $"SDK 根：{bundled ?? "本机 SDK"}；AVD 家目录：{(avdInOurHome ? BundledAvdHome() : "默认（本机 .android\avd）")}）");
            try
            {
                // 显示画面（设置页开关）→ 去掉 -no-window，模拟器会弹出自己的 Android 窗口。
                // 其余参数不变：无音频、不存快照、swiftshader 软渲染（虚拟机上也能出画面）。
                var windowArg = ShowEmulatorWindow ? " " : " -no-window";
                var psi = new ProcessStartInfo(emu,
                    $"-avd {avdName}{windowArg} -no-audio -no-boot-anim -no-snapshot -gpu swiftshader_indirect -memory 4096 -cores {GuestCoreCount()} -port {EmulatorPort} -no-metrics")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                if (bundled is not null)
                {
                    // AVD 家目录指向 %LOCALAPPDATA%（安装目录只读）；SDK 根显式给出做双保险。
                    // ⚠ 只在 AVD 确实登记在我们家目录时才设 ANDROID_AVD_HOME：显式覆盖的 AVD
                    //   （CATCLAW_ANDROID_AVD）通常在本机 %USERPROFILE%\.android\avd 下，硬指到
                    //   我们这儿会让模拟器 "Unknown AVD name" 直接退出（2026-10-15 实测）。
                    if (avdInOurHome) psi.Environment["ANDROID_AVD_HOME"] = BundledAvdHome();
                    psi.Environment["ANDROID_SDK_ROOT"] = bundled;
                    psi.Environment["ANDROID_HOME"] = bundled;
                }
                var p = Process.Start(psi);
                if (p is null) { Log?.Invoke("[bridge] 模拟器进程未启动"); return false; }
                _emulator = p;
                _ = Task.Run(() => PumpEmulatorLog(p));
                return true;
            }
            catch (Exception ex) { Log?.Invoke("[bridge] 模拟器启动失败: " + ex.Message); return false; }
        }
    }

#if WINDOWS
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
#endif

        /// <summary>
        /// 截一张模拟器画面（<c>adb exec-out screencap -p</c>）存到 <paramref name="outPath"/>，
        /// 返回文件路径（没连上/失败返回 null）。
        /// <para>这是「**临时看一眼模拟器状态**」最省事的做法（2026-10-15 用户要求）：
        /// 不需要模拟器带窗口、也不用重启它 —— 无窗口后台跑着照样能截；想连续看就多点几次。</para>
        /// </summary>
        public static async Task<string?> CaptureScreenshotAsync(string outPath, CancellationToken ct = default)
        {
            if (Serial is not { Length: > 0 } serial) return null;
            var adb = FindAdb();
            if (adb is null) return null;

            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(adb, $"-s {serial} exec-out screencap -p")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi);
                    if (p is null) return null;
                    var dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // ⚠ 必须 exec-out（不是 shell shell）：shell 会把 PNG 里的 \n 翻成 \r\n，图就废了
                    using (var fs = File.Create(outPath)) p.StandardOutput.BaseStream.CopyTo(fs);
                    p.WaitForExit(20000);
                    return new FileInfo(outPath).Length > 1024 ? outPath : null;
                }
                catch (Exception ex) { Log?.Invoke("[bridge] 截图失败：" + ex.Message); return null; }
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 带窗口启动时，等窗口出来就把它拉到前台。
        /// <para>不拉的话它会开在猫爪窗口后面 —— 用户看着就是"没弹窗"（2026-10-15 实测反馈）。
        /// 模拟器窗口属于 <c>emulator.exe</c>（Qt 那层），所以按进程名找 MainWindowHandle。</para>
        /// </summary>
        private static async Task BringEmulatorWindowToFrontAsync()
        {
#if WINDOWS
            try
            {
                for (var i = 0; i < 40; i++)          // 最多等 ~60 秒（冷启期间窗口才出现）
                {
                    await Task.Delay(1500).ConfigureAwait(false);
                    foreach (var pr in System.Diagnostics.Process.GetProcessesByName("emulator"))
                    {
                        try
                        {
                            if (pr.MainWindowHandle == IntPtr.Zero) continue;
                            SetForegroundWindow(pr.MainWindowHandle);
                            Log?.Invoke("[bridge] 已把模拟器窗口切到前台（" + pr.MainWindowTitle + "）");
                            return;
                        }
                        catch { }
                        finally { pr.Dispose(); }
                    }
                }
                Log?.Invoke("[bridge] 没等到模拟器窗口（可能仍在冷启；它一般会自己出现在任务栏）");
            }
            catch (Exception ex) { Log?.Invoke("[bridge] 切前台失败：" + ex.Message); }
#else
            await Task.CompletedTask;
#endif
        }

    /// <summary>
    /// 关程序时回收模拟器：先请它自己优雅退出（<c>adb emu kill</c> 会把 userdata 落盘），
    /// 超时再杀进程树。只动 <see cref="EmulatorPort"/> 上那个（我们的专用口），
    /// 用户自己在 Android Studio 里开的模拟器不受影响。
    /// </summary>
    public static void ShutdownEmulator()
    {
        Process? p;
        lock (_emuLock) { p = _emulator; _emulator = null; }
        // 没有本进程句柄时（比如接的是上一轮留下的实例），只有串号确实指向我们这个端口才动它 ——
        // 用户自己在 Android Studio 里开的模拟器（5554 那类）绝不能被我顺手关掉。
        var mine = Serial is { Length: > 0 } s
                   && (s == $"emulator-{EmulatorPort}" || s == $"127.0.0.1:{EmulatorPort + 1}");
        if (p is null && !mine) return;

        Log?.Invoke("[bridge] 关闭模拟器…");
        try
        {
            var adb = FindAdb();
            if (adb is not null)
            {
                Run(adb, $"-s emulator-{EmulatorPort} emu kill", 10000);
                Run(adb, $"-s 127.0.0.1:{EmulatorPort + 1} emu kill", 10000);   // 走了 adb connect 兜底的情况
            }
        }
        catch { }
        try
        {
            if (p is { HasExited: false })
            {
                if (!p.WaitForExit(10000)) p.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) { Log?.Invoke("[bridge] 关闭模拟器失败：" + ex.Message); }

        // 状态归零：模拟器没了，桥的连接/就绪标记也必须跟着失效，
        // 否则设置页会显示「运行中（外部启动）」，而且下次 EnsureReady 会以为还连着。
        _ready = false;
        Serial = null;
        try { Forget(); } catch { }
    }

    /// <summary>
    /// 起新模拟器前清掉上一轮的僵尸：端口上没设备却还占着 AVD 锁/端口的实例，
    /// 会让新实例直接报「too many emulator instances」或被顶掉（实测 2026-10-15）。
    /// </summary>
    private static void CleanupStaleEmulator(string adb)
    {
        try
        {
            var (_, dev) = Run(adb, "devices", 15000);
            if (dev.Contains($"emulator-{EmulatorPort}", StringComparison.Ordinal)) return;   // 有活设备，别乱动

            Run(adb, $"-s emulator-{EmulatorPort} emu kill", 8000);   // 没设备也应答不了，纯兜底
            foreach (var f in Directory.EnumerateFiles(BundledAvdHome(), "*.lock", SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 挑一个「真的能绑上」的控制台端口（console 与 console+1 都要能绑）。
    /// <para>从 5562 往上逐个试偶数口 —— 试绑本身就能同时排掉「被别人占了」和
    /// 「落在 Windows 保留段里」两种失败（后者返回 WSAEACCES）。</para>
    /// </summary>
    private static int PickEmulatorPort()
    {
        var want = EmulatorPort;
        for (var p = want; p <= 5760; p += 2)
        {
            if (!CanBind(p) || !CanBind(p + 1)) continue;
            if (p != want) Log?.Invoke($"[bridge] 端口 {want} 绑不上（多半落在系统保留段）→ 改用 {p}");
            return p;
        }
        return want;   // 全试不出来就退回默认值，让模拟器自己去报错
    }

    /// <summary>本机能不能绑这个 TCP 端口（绑一下就放，不占着）。</summary>
    private static bool CanBind(int port)
    {
        try
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    /// <summary>模拟器自己的 stdout/stderr 落盘（不带它，「起了却连不上」只能靠猜）。</summary>
    private static void PumpEmulatorLog(Process p)
    {
        try
        {
            var dir = Path.GetDirectoryName(EmulatorLogPath)!;
            Directory.CreateDirectory(dir);
            var fi = new FileInfo(EmulatorLogPath);
            if (fi.Exists && fi.Length > 8L * 1024 * 1024) fi.Delete();   // 简单封顶，避免长跑无限增长
            var w = new StreamWriter(EmulatorLogPath, append: true) { AutoFlush = true };
            w.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动模拟器 pid={p.Id} =====");
            p.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) { try { w.WriteLine(e.Data); } catch { } } };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) { try { w.WriteLine(e.Data); } catch { } } };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch { }
    }

    /// <summary>确保「模拟器在跑 + bridge 服务在跑 + adb forward 就位」。幂等、失败返回 false。</summary>
    public static async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (_ready) return true;
        await ReadyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready) return true;
            Report(0.05, "正在准备 Android 运行环境…");
            var adb = FindAdb();
            if (adb is null) { Log?.Invoke("[bridge] 未找到 adb.exe（设 CATCLAW_ANDROID_SDK 或装 Android SDK）"); return false; }

            string serial = Serial ?? "";
            if (serial.Length == 0)
            {
                var (_, dev) = Run(adb, "devices", 15000);
                serial = dev.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => (l.StartsWith("emulator-") || l.StartsWith($"127.0.0.1:{EmulatorPort + 1}"))
                                && l.EndsWith("\tdevice"))
                    .Select(l => l.Split('\t')[0])
                    .FirstOrDefault() ?? "";
                // 就地运行环境存在 ⇒ 用配套的 catclaw_play36（此刻才判定，见 ResolveAvd）
                var avd = ResolveAvd(EffectiveSdkRoot() is not null);
                if (serial.Length == 0 && avd is { Length: > 0 })
                {
                    // 没设备但有 AVD：拉起模拟器（冷启 ~70s）
                    // SDK 根优先级：随包 {安装目录}\AndroidSdk → 首运下载 %LOCALAPPDATA%\CatClawVideo\AndroidSdk
                    // → 由 adb 路径上溯。emulator.exe 的 SDK 根 = 自己的上一级，AVD 里相对的
                    // image.sysdir.1 自然指到对应镜像（随包/下载两处布局同构）。
                    var bundled = EffectiveSdkRoot();
                    var emu = bundled is not null
                        ? Path.Combine(bundled, "emulator", "emulator.exe")
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(adb)!, "..", "emulator", "emulator.exe"));
                    if (bundled is not null) EnsureBundledAvd();
                    if (File.Exists(emu) && StartEmulatorOnce(adb, emu, bundled, avd))
                    {
                        Report(0.15, "等待模拟器出现（adb 还没认到设备）…");
                        for (var i = 0; i < 60 && serial.Length == 0; i++)
                        {
                            await Task.Delay(5000, ct).ConfigureAwait(false);
                            // ⚠ 这里**不能** kill-server：模拟器只在启动那一小会儿注册自己，中途杀掉服务，
                            //   设备就算本来认到了也会被摘掉且再也回不来。只把服务「拉起来」（幂等），
                            //   实在认不到就直连模拟器自己的 adb 端口兜底。
                            if (i is 1 or 5)
                            {
                                Run(adb, "start-server", 30000);
                                Log?.Invoke("[bridge] 刷新 adb 服务连接…");
                            }
                            if (i == 11)
                            {
                                var (_, co) = Run(adb, $"connect 127.0.0.1:{EmulatorPort + 1}", 15000);
                                Log?.Invoke($"[bridge] 仍未认到设备 → adb connect 127.0.0.1:{EmulatorPort + 1}：{co.Trim()}");
                            }
                            var (_, d2) = Run(adb, "devices", 15000);
                            serial = d2.Split('\n').Select(l => l.Trim())
                                .Where(l => (l.StartsWith("emulator-") || l.StartsWith($"127.0.0.1:{EmulatorPort + 1}"))
                                            && l.Contains("device"))
                                .Select(l => l.Split('\t')[0]).FirstOrDefault() ?? "";
                        }
                    }
                }
            }
            if (serial.Length == 0) { Log?.Invoke("[bridge] 没有可用的 emulator 设备"); return false; }
            Serial = serial;
            Report(0.40, $"模拟器已连接（{serial}）");
            // 串号里的端口就是我们在用的控制台口（可能是上一轮实例留下的）——同步过来，
            // 退出回收 / 清理僵尸才找得准。
            if (serial.StartsWith("emulator-", StringComparison.Ordinal)
                && int.TryParse(serial["emulator-".Length..], out var usedPort) && usedPort > 0)
            {
                EmulatorPort = usedPort;
            }

            // ★★ 0.2.5：等系统真正就绪再往下走。
            //   为什么必须等：adb devices 在**开机动画还没走完**时就会把设备报成 device，
            //   而这之后紧接着就是 pm install —— 撞上尚未起来的 PackageManager 时，
            //   adb 只回一句 "Performing Streamed Install" 就没了下文，桥 APK 静默没装上
            //   （端口转发照做，最终表现为「连 127.0.0.1:18700 被拒」）。
            //   开发机上模拟器通常早就开着，所以这个竞态一直没暴露；而发行版首次运行
            //   必然是冷启（用户机器上还没有 userdata），**必现**。
            //   （2026-10-12 实测：发布版第一次跑，随包环境冷启 76s，APK 安装就在这一步失败的。）
            var booted = false;
            for (var w = 0; w < 80; w++)
            {
                var (_, bc) = Run(adb, $"-s {serial} shell getprop sys.boot_completed", 15000);
                if (bc.Trim() == "1") { booted = true; break; }
                if (w == 0) Report(0.45, "等待模拟器系统启动完成（冷启约 60~90s）…");
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            Report(booted ? 0.70 : 0.60, booted ? "模拟器系统已启动完成" : "模拟器系统迟迟未就绪，继续尝试");

            // ★★ 首次使用（新 AVD / 换镜像后 userdata 是空的）必须先把桥 APK 装进去：
            //    换到 google_apis_playstore 镜像重建 AVD 后实测「连接 127.0.0.1:18700 被拒」——
            //    服务根本没在跑。发行版更不能指望用户手动 adb install。
            try
            {
                var listed = Run(adb, $"-s {serial} shell pm list packages com.catclaw.bridge", 20000);
                if (!listed.Out.Contains("com.catclaw.bridge", StringComparison.Ordinal))
                {
                    var apk = Path.Combine(AppContext.BaseDirectory, "BridgeApk", "catclaw-bridge.apk");
                    if (File.Exists(apk))
                    {
                        Report(0.72, "正在安装爬虫桥程序…");
                        Log?.Invoke("[bridge] 桥 APK 未安装 → 自动安装（" + apk + "）");
                        // ★ -g：安装时一次授予该 APK 声明的**全部运行时权限**。
                        //   桥声明了 POST_NOTIFICATIONS（前台服务通知用），不加 -g 时
                        //   发行版首装会弹一次系统权限对话框 —— 而这一步发生在启动流程里
                        //   （用户还没看到界面），弹窗体验很差。加了 -g 静默通过。
                        //   （2026-10-13 在 TVBox 上验证过同一套做法的效果。）
                        var ins = Run(adb, $"-s {serial} install -g -r \"{apk}\"", 180000);
                        Log?.Invoke(ins.Out.Contains("Success", StringComparison.OrdinalIgnoreCase)
                            ? "[bridge] 桥 APK 安装成功"
                            : "[bridge] 桥 APK 安装失败：" + ins.Out.Trim());
                    }
                    else
                    {
                        Log?.Invoke("[bridge] 桥 APK 未随包（BridgeApk\\catclaw-bridge.apk 缺失），爬虫链路不可用");
                    }
                }
            }
            catch (Exception ex) { Log?.Invoke("[bridge] 桥 APK 安装检查异常：" + ex.Message); }
            Report(0.78, "爬虫桥程序已就绪");

            // bridge 服务（前台服务，幂等）
            Run(adb, $"-s {serial} shell am start -n com.catclaw.bridge/.MainActivity", 30000);
            await Task.Delay(4000, ct).ConfigureAwait(false);

            // 只映射桥协议本身（宿主 HostPort → guest 18600）。
            // ⚠ 不要映射 6678 / 9978..9999：那是 Windows 侧 QEMU guest 已经在用的宿主端口，
            //   重复 forward 会 bind 失败（进而可能覆盖掉 QEMU 的映射）。
            Run(adb, $"-s {serial} forward --remove tcp:{HostPort}", 8000);
            var fr = Run(adb, $"-s {serial} forward tcp:{HostPort} tcp:{GuestPort}", 8000);
            if (fr.Code != 0) Log?.Invoke($"[bridge] forward tcp:{HostPort} 失败：{fr.Out}");
            else Log?.Invoke($"[bridge] forward tcp:{HostPort} → guest {GuestPort}");

            Report(0.85, "桥服务已启动、端口映射就位（开始 ping 验证）");
            // ★★ 必须**真 ping 通**才算就绪。只做 am start + adb forward 会在桥服务还没监听时
            //    就宣告成功，调用方随后的 thunder init / load 失败 → 引导回落到 QEMU ART guest
            //    （实测 2026-10-11：guest 照样起了 2.9GB，用户看到「说好的切断呢」）。
            //    模拟器冷启动 + 桥前台服务起来最坏 ~70s，这里给 25×3s = 75s。
            // ⚠ 窗口必须盖住**模拟器冷启动**（实测 60~90s，慢的时候更久）：
            //   窗口太短会在冷启时判定「桥不可用」→ 引导回落到 QEMU guest（2026-10-11 实测：
            //   kill 模拟器重启后 75s 窗口不够，QEMU 又被拉起来 2.6GB）。
            for (var attempt = 0; attempt < 60; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                // ★ 0.2.5：每 10 次尝试重设一次端口转发。
                //   冷启期间 adbd 随系统起来会重连，设备一重连，adb forward 的映射就被清空
                //   （实测第一次跑发行版：forward --list 变空 ⇒ 之后连续 42 次连接全是
                //   「目标计算机积极拒绝」，直到外部手工补上转发才通）。
                //   设备稳定后这一步是幂等的空操作，没有副作用。
                if (attempt > 0 && attempt % 10 == 0)
                {
                    Run(adb, $"-s {serial} forward --remove tcp:{HostPort}", 8000);
                    Run(adb, $"-s {serial} forward tcp:{HostPort} tcp:{GuestPort}", 8000);
                    Log?.Invoke($"[bridge] 重设端口转发 tcp:{HostPort}（第 {attempt} 次尝试之后）");
                }
                if (Connect())
                {
                    try
                    {
                        var pr = await RpcAsync(new JsonObject
                        {
                            ["id"] = Interlocked.Increment(ref _nextId),
                            ["op"] = "ping",
                        }, CancellationToken.None, 6000).ConfigureAwait(false);
                        if (pr?["ok"]?.GetValue<bool>() == true)
                        {
                            Report(0.95, $"爬虫桥已连接（第 {attempt + 1} 次 ping 通）");
                            _ready = true;
                            break;
                        }
                    }
                    catch { Forget(); }
                }
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            if (!_ready)
            {
                Report(0.85, "桥服务没起来（ping 始终失败）→ 桥判定不可用");
                return false;
            }
            if (HostProxyPort > 0 && Connect())
            {
                await RpcAsync(new JsonObject { ["id"] = Interlocked.Increment(ref _nextId), ["op"] = "setProxyPort", ["port"] = HostProxyPort },
                    CancellationToken.None, 20_000).ConfigureAwait(false);
                await EnsureReverseForwardedAsync(HostProxyPort, ct).ConfigureAwait(false);
                Log?.Invoke($"[bridge] 已下发 proxy 端口 {HostProxyPort} 并 reverse 进模拟器");
            }
            return true;
        }
        catch (Exception ex) { Log?.Invoke("[bridge] 引导失败: " + ex.Message); return false; }
        finally { ReadyGate.Release(); }
    }

    // ─────────────── 协议 ───────────────

    private static void Forget()
    {
        // ★ 并发后 _sock/_ns/_rlen/_rpos 会被多线程读写（原来靠外层 Gate 保活），
        //   这里用可重入的 lock —— Connect() 内部会调 Forget()。
        lock (_connLock)
        {
            try { _ns?.Dispose(); } catch { }
            try { _sock?.Dispose(); } catch { }
            _ns = null; _sock = null; _rlen = _rpos = 0;
        }
        Loaded.Clear();
        foreach (var kv in Pending) kv.Value.TrySetException(new IOException("bridge 连接已重置"));
        Pending.Clear();
    }

    private static bool Connect()
    {
        // lock 而非信号量：Forget() 会在锁内被调用，必须可重入。
        // 持锁期间做的是本机回环 connect（毫秒级），不构成阻塞风险。
        lock (_connLock)
        {
            if (_sock is { Connected: true } && _ns is not null) return true;
            Forget();
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                s.Connect("127.0.0.1", HostPort);
                _sock = s; _ns = new NetworkStream(s, ownsSocket: false);
                _reader = Task.Run(ReadLoopAsync);
                return true;
            }
            catch (Exception ex) { Log?.Invoke($"[bridge] 连接 127.0.0.1:{HostPort} 失败：{ex.Message}"); Forget(); return false; }
        }
    }

    /// <summary>读一行（UTF-8 按字节累积，避免多字节字符被逐字节截断）。</summary>
    private static async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var acc = new List<byte>(1024);
        while (true)
        {
            if (_rpos >= _rlen)
            {
                _rlen = await _ns!.ReadAsync(Rbuf.AsMemory(), ct).ConfigureAwait(false);
                _rpos = 0;
                if (_rlen <= 0) throw new IOException("bridge EOF");
            }
            while (_rpos < _rlen)
            {
                var b = Rbuf[_rpos++];
                if (b == (byte)'\n') return Encoding.UTF8.GetString(acc.ToArray());
                acc.Add(b);
            }
        }
    }

    /// <summary>读循环：按 <c>ev</c> 分流事件、按 <c>id</c> 分流应答。
    /// <para>⚠ 必须分流：桩弹对话框时上行的 ui-dialog 会**插在** action 的应答之间
    /// （实测：call action → 事件先到、应答后到）。原来「读下一行当应答」会把事件当应答吞掉，
    /// 表现为「有 ui-dialog 桥侧日志但宿主没反应」。</para></summary>
    private static async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line.Length == 0) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(line); } catch { continue; }
                if (node is not JsonObject o) continue;
                if (o["ev"] is not null)
                {
                    // 桥内聚合搜索的结果流（ev=search / search-done）走独立回调，
                    // 不混进 UI 事件（否则会被 SpiderUiHost 当成对话框处理）。
                    var evName = o["ev"] is JsonValue evv && evv.TryGetValue<string>(out var evs) ? evs : "";
                    try
                    {
                        if (evName is "search" or "search-done") SearchEvent?.Invoke(o);
                        else UiEvent?.Invoke(o);
                    }
                    catch (Exception ex) { Log?.Invoke("[bridge] 事件处理异常(" + evName + "): " + ex.Message); }
                    continue;
                }
                if (o["id"] is JsonValue idv && idv.TryGetValue<int>(out var id) && Pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(o);
            }
        }
        catch (Exception ex) { Log?.Invoke("[bridge] 读循环结束: " + ex.Message); Forget(); }
    }

    /// <summary>发一条请求并等它的应答（事件行由读循环旁路处理）。</summary>
    /// <summary>桥是否已就绪（ping 通过）。</summary>
    public static bool Ready => _ready;

    /// <summary>
    /// 桥 APK 掉了就自己把它拉回来。桌面端本来就管着模拟器、手里有 adb，没理由让用户自己去点重试。
    /// <para>2026-10-18 自测发现：半小时内桥崩了两次（Android 的 LMK 会杀掉后台的桥），用户看到的是
    /// 「线路加载失败：桥会话已断开（主桥刚崩溃，正在重建）」，而应用只是记了条日志就放弃 ——
    /// 接着落进已退役的本机 guest 分支，报出更误导的错。</para>
    /// <para>做法：<c>adb shell monkey</c> 拉起 launcher activity（比 am start 稳，不需要知道 activity 名），
    /// 然后等 <see cref="Ready"/> 恢复，最多约 15 秒；返回是否恢复。</para>
    /// </summary>
    public static async Task<bool> ReviveBridgeAppAsync(CancellationToken ct = default)
    {
        try
        {
            var adb = FindAdb();
            if (adb is null) { Log?.Invoke("[bridge] 自愈：找不到 adb，放弃"); return false; }
            var serial = Serial;
            var args = (string.IsNullOrWhiteSpace(serial) ? string.Empty : $"-s {serial} ")
                     + "shell monkey -p com.catclaw.bridge -c android.intent.category.LAUNCHER 1";
            var (code, outp) = Run(adb, args, 30000);
            var tail = outp.Length > 120 ? outp[^120..] : outp;
            Log?.Invoke($"[bridge] 自愈：拉起桥 APK rc={code} {tail.Replace('\n', ' ')}");
            for (var i = 0; i < 15 && !Ready; i++)
                await Task.Delay(1000, ct).ConfigureAwait(false);
            Log?.Invoke(Ready ? "[bridge] 自愈成功：桥已恢复" : "[bridge] 自愈失败：桥仍不可达");
            return Ready;
        }
        catch (Exception ex) { Log?.Invoke("[bridge] 自愈异常: " + ex.Message); return false; }
    }

    /// <summary>桥内聚合搜索的结果流（<c>ev=search</c> 单站结果、<c>ev=search-done</c> 收尾）。</summary>
    public static Action<JsonObject>? SearchEvent;

    /// <summary>启动**桥内聚合搜索**（对齐 TVBox：在爬虫所在进程里并发遍历站点，结果流式回推）。
    ///
    /// <para><b>为什么必须放桥里</b>：97 个站逐站走宿主↔桥往返，光协议往返 + 宿主侧 8 路闸门
    /// 就要几十秒（实测「63s 预算只跑完 37/97 个站」）；桥内 24 路并发实测 **14 站 7.6s 全跑完**
    /// （各站耗时合计是总耗时的 3.3 倍 ⇒ 真并行）。op 立即返回，结果经 <see cref="SearchEvent"/> 流式回推。</para>
    /// </summary>
    public static async Task<bool> StartBatchSearchAsync(
        string kw,
        IEnumerable<(string Site, string ClassName, string? Ext, string Jar)> sites,
        int concurrency = 24,
        CancellationToken ct = default)
    {
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return false;
        var arr = new JsonArray();
        foreach (var s in sites)
        {
            arr.Add(new JsonObject
            {
                ["site"] = s.Site,
                ["className"] = s.ClassName,
                ["ext"] = s.Ext ?? "",
                ["jar"] = s.Jar,
            });
        }
        if (arr.Count == 0) return false;
        try
        {
            var resp = await RpcAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["op"] = "searchBatch",
                ["kw"] = kw,
                ["sites"] = arr,
                ["concurrency"] = concurrency,
            }, ct, 30_000).ConfigureAwait(false);
            var ok = resp?["ok"]?.GetValue<bool>() == true;
            Log?.Invoke(ok
                ? $"[bridge] 桥内聚合搜索已启动（{arr.Count} 站，并发 {concurrency}）"
                : $"[bridge] 桥内聚合搜索启动失败：{resp?["error"]}");
            return ok;
        }
        catch (Exception ex) { Log?.Invoke("[bridge] 桥内聚合搜索异常：" + ex.Message); return false; }
    }

    /// <summary>取消在飞的桥内聚合搜索（离开搜索页 / 发起新搜索时调）。
    /// 不取消的话桥内 24 路会继续跑，跟详情/播放抢站方 ⇒「播放加载半天」。</summary>
    public static async Task CancelBatchSearchAsync()
    {
        try
        {
            if (!_ready || _ns is null) return;
            await RpcAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["op"] = "searchCancel",
            }, CancellationToken.None, 5000).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>桥内 /proxy HTTP 服务的**宿主侧**基址（形如 http://127.0.0.1:18611）。
    /// <para>网盘取流用：jar 的 playerContent 回 <c>http://127.0.0.1:&lt;宿主代理口&gt;/proxy?do=proxy&amp;key=…</c>，
    /// 宿主 SpiderProxyServer 收到后要转给 jar 自己的 <c>Proxy.proxy(Map)</c>。QEMU 时代这步走
    /// guest 隧道（<c>QemuArtGuest.ProxyBase</c>）；换成外置 Android 桥后由桥内 ProxyServer 供
    /// （设备侧 18601，adb forward 到宿主 18611）。</para></summary>
    public static string? ProxyBase { get; private set; }

    /// <summary>桥内代理服务的设备侧端口（ProxyServer.PORT）。</summary>
    public const int DeviceProxyPort = 18601;
    /// <summary>宿主侧映射端口。</summary>
    public const int HostProxyTunnelPort = 18611;

    /// <summary>确保桥内 /proxy 服务可达（adb forward + ping 一次），返回宿主侧基址；失败 null。</summary>
    public static async Task<string?> EnsureProxyBaseAsync(CancellationToken ct = default)
    {
        if (ProxyBase is not null) return ProxyBase;
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return null;
        var adb = FindAdb();
        if (adb is null || Serial is null) return null;
        Run(adb, $"-s {Serial} forward --remove tcp:{HostProxyTunnelPort}", 8000);
        var fr = Run(adb, $"-s {Serial} forward tcp:{HostProxyTunnelPort} tcp:{DeviceProxyPort}", 8000);
        if (fr.Code != 0)
        {
            Log?.Invoke($"[bridge] proxy forward 失败：{fr.Out}");
            return null;
        }
        Log?.Invoke($"[bridge] proxy tunnel tcp:{HostProxyTunnelPort} → 桥内 {DeviceProxyPort}");
        ProxyBase = $"http://127.0.0.1:{HostProxyTunnelPort}";
        return ProxyBase;
    }

    /// <summary>桥内 GoProxy（pvideo）流服务的设备侧端口。
    /// <para>fish 壳的 <c>playerContent</c> 会把「夸克原画/VIP」这类直连流构造成
    /// <c>http://127.0.0.1:5266/fishplay/go/…</c>（同端口的 <c>fishdanmu</c> 弹幕亦然）——
    /// pvideo 只绑**设备内**回环，宿主播放器直连自己的 5266 必死
    /// （FFmpeg <c>Connection to tcp://127.0.0.1:5266 failed: -138</c> → 回落原生 MF
    /// → 播放器「源不受支持（视频编码或容器格式不兼容）」）。</para></summary>
    public const int DeviceGoProxyPort = 5266;
    /// <summary>宿主侧映射端口（adb forward tcp:18621 → 桥内 5266）。</summary>
    public const int HostGoProxyTunnelPort = 18621;

    private static string? _goProxyBase;

    /// <summary>确保桥内 GoProxy（5266）流服务可达（adb forward），返回宿主侧基址；失败 null。
    /// <para>与 <see cref="EnsureProxyBaseAsync"/> 同理：QEMU 时代这条隧道由 guest 内 init 的
    /// <c>tcpfwd</c>（25266 → 5266）+ 宿主 hostfwd 供；换成外置 Android 桥后必须由 adb 补上。</para></summary>
    public static async Task<string?> EnsureGoProxyBaseAsync(CancellationToken ct = default)
    {
        if (_goProxyBase is not null) return _goProxyBase;
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return null;
        var adb = FindAdb();
        if (adb is null || Serial is null) return null;
        Run(adb, $"-s {Serial} forward --remove tcp:{HostGoProxyTunnelPort}", 8000);
        var fr = Run(adb, $"-s {Serial} forward tcp:{HostGoProxyTunnelPort} tcp:{DeviceGoProxyPort}", 8000);
        if (fr.Code != 0)
        {
            Log?.Invoke($"[bridge] GoProxy forward 失败：{fr.Out}");
            return null;
        }
        Log?.Invoke($"[bridge] GoProxy tunnel tcp:{HostGoProxyTunnelPort} → 桥内 {DeviceGoProxyPort}");
        return _goProxyBase = $"http://127.0.0.1:{HostGoProxyTunnelPort}";
    }

    /// <summary>Guard 加解密（op = "guard-decrypt" | "guard-encrypt"）：FishConfig 的扫码链路
    /// 探测宿主 10.0.2.2:18481 → GuardSvcLoop → 这里 → 桥内壳的 Rc.KJ/encrypt。
    /// 外置 Android 桥取代 QEMU 后，这条服务由桥供（此前由 QEMU guest 的 bridge op 供）。</summary>
    public static async Task<string?> GuardCryptoAsync(string op, string data, CancellationToken ct = default)
    {
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return null;
        var resp = await RpcAsync(new JsonObject
        {
            ["id"] = Interlocked.Increment(ref _nextId),
            ["op"] = op,
            ["data"] = data,
        }, ct, 30_000).ConfigureAwait(false);
        if (resp?["ok"]?.GetValue<bool>() != true) return null;
        var r = resp["result"];
        if (r is null || r.GetValueKind() == System.Text.Json.JsonValueKind.Null) return null;
        return r.GetValue<string>();
    }

    private static async Task<JsonNode?> RpcAsync(JsonObject req, CancellationToken ct, int timeoutMs = 120_000)
    {
        var id = req["id"]!.GetValue<int>();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // ★ 注册必须在写之前：并发下应答可能刚写完就到达并被读循环派发，
        //   若那时 Pending 里还没有这个 id，应答会被当成「未匹配」丢掉。
        Pending[id] = tcs;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(req.ToJsonString() + "\n");
            // ★ 写串行：多线程并发写同一条 NetworkStream 会让 JSON 行交错，
            //   宿主按 id 也拆不开。取本地引用是因为 _ns 可能被 Forget() 置空。
            await WriteGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ns = _ns ?? throw new IOException("bridge 未连接");
                await ns.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                await ns.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { WriteGate.Release(); }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task.ConfigureAwait(false);
        }
        finally { Pending.TryRemove(id, out _); }
    }

    /// <summary>调桥上的迅雷引擎（op=thunder）。返回 result 原文；桥不可达/失败返回 null。</summary>
    public static async Task<string?> ThunderAsync(string method, IDictionary<string, object?>? args, CancellationToken ct)
    {
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return null;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Connect()) return null;
            var req = new JsonObject { ["id"] = Interlocked.Increment(ref _nextId), ["op"] = "thunder", ["method"] = method };
            if (args is not null)
                foreach (var kv in args)
                    req[kv.Key] = kv.Value switch
                    {
                        null => null,
                        string str => JsonValue.Create(str),
                        int i => JsonValue.Create(i),
                        long l => JsonValue.Create(l),
                        bool b => JsonValue.Create(b),
                        _ => JsonValue.Create(kv.Value.ToString()),
                    };
            var resp = await RpcAsync(req, ct, 600_000).ConfigureAwait(false);
            if (resp?["ok"]?.GetValue<bool>() != true)
            {
                Log?.Invoke($"[bridge] thunder.{method} 失败：{resp?["error"]}");
                return null;
            }
            return resp["result"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) { Log?.Invoke($"[bridge] thunder.{method} 异常：{ex.Message}"); Forget(); return null; }
        finally { Gate.Release(); }
    }

    /// <summary>把**宿主的**端口反向映射进模拟器（device → host）。
    /// <para>壳在模拟器里用 <c>http://127.0.0.1:&lt;port&gt;</c> 访问宿主 SpiderProxyServer，
    /// 所以必须用 <c>adb reverse</c>。⚠ 不能用 <c>adb forward</c>：宿主的 proxy 自己就监听在该端口上，
    /// forward 要绑同一个宿主端口 → <c>cannot bind listener 10048</c>（实测 2026-10-11）。</para></summary>
    public static async Task<bool> EnsureReverseForwardedAsync(int port, CancellationToken ct)
    {
        if (_reverseForwarded.Contains(port)) return true;
        var adb = FindAdb();
        if (adb is null || string.IsNullOrEmpty(Serial)) return false;
        await Task.Run(() => Run(adb, $"-s {Serial} reverse --remove tcp:{port}", 8000), ct).ConfigureAwait(false);
        var (code, o) = await Task.Run(() => Run(adb, $"-s {Serial} reverse tcp:{port} tcp:{port}", 8000), ct).ConfigureAwait(false);
        if (code != 0) { Log?.Invoke($"[bridge] reverse tcp:{port} 失败：{o}"); return false; }
        _reverseForwarded.Add(port);
        Log?.Invoke($"[bridge] reverse tcp:{port}（模拟器内 127.0.0.1:{port} → 宿主 proxy）");
        return true;
    }

    private static readonly HashSet<int> _reverseForwarded = new();

    /// <summary>把模拟器内的一个回环端口映射到宿主（迅雷的流服务会随机选端口）。</summary>
    public static async Task<bool> EnsurePortForwardedAsync(int port, CancellationToken ct)
    {
        if (_forwarded.Contains(port)) return true;
        var adb = FindAdb();
        if (adb is null || string.IsNullOrEmpty(Serial)) return false;
        var (code, _) = await Task.Run(() => Run(adb, $"-s {Serial} forward --remove tcp:{port}", 8000), ct).ConfigureAwait(false);
        var (c2, o2) = await Task.Run(() => Run(adb, $"-s {Serial} forward tcp:{port} tcp:{port}", 8000), ct).ConfigureAwait(false);
        if (c2 != 0) { Log?.Invoke($"[bridge] forward tcp:{port} 失败：{o2}"); return false; }
        _forwarded.Add(port);
        Log?.Invoke($"[bridge] forward tcp:{port} → 模拟器内 {port}（迅雷流）");
        return true;
    }

    private static readonly HashSet<int> _forwarded = new();

    /// <summary>回传用户在宿主对话框里的选择（which：≥0 列表项，-1/-2/-3 肯定/否定/中性）。</summary>
    public static async Task SendUiResultAsync(int seq, int which)
    {
        // ★ 2026-10-12：去掉外层 Gate。这条只是把用户在宿主对话框里的选择回传，
        //   很短且与爬虫调用无关，没有理由跟 97 个站的搜索排同一把锁。
        //   写串行由 RpcAsync 内部的 WriteGate 保证。
        try
        {
            if (!Connect()) return;
            await RpcAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["op"] = "ui-result",
                ["seq"] = seq,
                ["which"] = which,
            }, CancellationToken.None, 30_000).ConfigureAwait(false);
        }
        catch (Exception ex) { Log?.Invoke($"[bridge] ui-result({seq},{which}) 发送失败: {ex.Message}"); }
    }

    /// <summary>把桌面桥的参数形态规整成 bridge APK 那边的形态（见各处注释）。</summary>
    private static string[] NormalizeArgs(string method, JsonArray args)
    {
        // ⚠ 必须用 GetValue<string>() 解包，**不能**用 ToJsonString()：
        //   JsonValue.ToJsonString() 对字符串会带上外层引号（"abc" → "\"abc\""），
        //   于是 spider 收到的是「带引号的 id」——实测 6V.detailContent 拿到
        //   id="\"/dianshiju/guoju/29777.html\"" 后返回 246B 空壳（vod_name 全空），
        //   ParsePlaySources 解析出 0 条线路 → 播放源退化成兜底（Url=vod_id）→
        //   playerContent(详情页路径) → 垃圾地址 → 「不是完整网址」。
        //   （2026-10-11 定位：只有走了 normalize 的 homeContent/categoryContent/playerContent
        //     正常，走 default 分支的 detailContent 全坏 —— 就是这一行的锅。）
        var a = args.Select(x => x is JsonValue v && v.TryGetValue<string>(out var sv)
            ? sv
            : (x?.ToJsonString() ?? "")).ToList();
        switch (method)
        {
            case "homeContent":
                return new[] { "true" };
            case "categoryContent":
                // 桌面：[tid, pg, {筛选}] → bridge：[tid, pg, filter, {筛选}]
                {
                    var tid = args.Count > 0 ? Json(args, 0) : "";
                    var pg = args.Count > 1 ? Json(args, 1) : "";
                    var map = args.Count > 2 && args[2] is JsonObject o ? o : new JsonObject();
                    var has = map.Count > 0;
                    return new[] { tid, pg, has ? "true" : "false", has ? map.ToJsonString() : "" };
                }
            case "searchContent":
                // 桌面：[key, pg] → bridge：[key, false, pg]（3 参搜不到时会自动裁到 2 参）
                return new[] { Json(args, 0), "false", Json(args, 1) };
            case "playerContent":
                // 桌面：[flag, id] → bridge：[flag, id, ""]（第 3 参是 List，空串 → 空表）
                return new[] { Json(args, 0), Json(args, 1), "" };
            default:
                return a.ToArray();
        }
        static string Json(JsonArray a, int i) =>
            i < a.Count ? (a[i] is JsonValue v && v.TryGetValue<string>(out var s) ? s : a[i]!.ToJsonString().Trim('"')) : "";
    }

    /// <summary>调桥。返回 null = 桥不可用/未路由 ⇒ 调用方走本地（QEMU/ART guest）路径。</summary>
    /// <summary>只做**装载**（桥内 load op），不发 call。供启动后台预加载用：
    /// <para>对齐 TVBox 的 jar 站点预热 —— 冷 load 实测 0.8~1.4s/站，97 个站不预热的话
    /// **每次搜索都要现场付这笔钱**（实测「63s 预算只跑完 37/97 个站」）。预热后搜索期
    /// load 降到 0.05s，只剩 jar 自己的网络耗时。</para></summary>
    public static async Task<bool> PreloadSiteAsync(VodSiteInfo site, CancellationToken ct)
    {
        if (!ShouldRoute(site)) return false;
        if (Loaded.ContainsKey(site.Key)) return true;
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return false;
        await LoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Loaded.ContainsKey(site.Key)) return true;
            if (!Connect()) return false;
            var jar = (site.Jar ?? "").Split(";md5;")[0];
            if (string.IsNullOrWhiteSpace(jar)) return false;
            var lr = await RpcAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["op"] = "load",
                ["site"] = site.Key,
                ["className"] = site.Api,
                ["ext"] = site.Ext ?? "",
                ["jars"] = new JsonArray(jar),
            }, ct).ConfigureAwait(false);
            if (lr?["ok"]?.GetValue<bool>() != true)
            {
                Log?.Invoke($"[bridge] 预加载 {site.Key} 失败：{lr?["error"]}");
                return false;
            }
            Loaded.TryAdd(site.Key, 0);
            Log?.Invoke($"[bridge] 预加载 {site.Key} 已装载（{site.Api}）");
            return true;
        }
        catch (Exception ex) { Log?.Invoke($"[bridge] 预加载 {site.Key} 异常：{ex.Message}"); return false; }
        finally { LoadGate.Release(); }
    }

    public static async Task<string?> TryCallAsync(VodSiteInfo site, string method, JsonArray args, CancellationToken ct)
    {
        if (!ShouldRoute(site))
        {
            // 诊断：凡是 csp_ 源但没被路由的，留一行说明为什么（Type/Api 形态不匹配）
            if (site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase))
                Log?.Invoke($"[bridge] 未路由 {site.Key}: api={site.Api} type={site.Type} " +
                            $"(需 type=3 且 api 以 Guard 结尾)");
            return null;
        }
        if (!await EnsureReadyAsync(ct).ConfigureAwait(false)) return null;

        // ★ 2026-10-12 拆锁：原来一把 Gate 把 load + call + 等响应整段锁死，97 个站全排一条队。
        //   现在 load 段走 LoadGate（转译层同刻只容一个 load），call 段**不持任何全局锁** ——
        //   同站串行由桥内 SITE_LOCKS 保证，跨站并行正是要拿到的并发度。
        //   CATCLAW_BRIDGE_SERIAL=1 时退回整段串行（排障回退）。
        var serial = ForceSerial;
        if (serial) await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Connect()) return null;

            if (!Loaded.ContainsKey(site.Key))
            {
                var jar = (site.Jar ?? "").Split(";md5;")[0];
                if (string.IsNullOrWhiteSpace(jar)) { Log?.Invoke($"[bridge] {site.Key} 无 jar 地址"); return null; }
                await LoadGate.WaitAsync(ct).ConfigureAwait(false);
                JsonNode? lr;
                try
                {
                    // 双检：排队等 LoadGate 期间，同站可能已被别的线程装载好（并发下这是常态，
                    // 97 个站的预加载与用户请求会同时命中同一站）。
                    lr = Loaded.ContainsKey(site.Key) ? null : await RpcAsync(new JsonObject
                    {
                        ["id"] = Interlocked.Increment(ref _nextId),
                        ["op"] = "load",
                        ["site"] = site.Key,
                        ["className"] = site.Api,
                        ["ext"] = site.Ext ?? "",
                        ["jars"] = new JsonArray(jar),
                    }, ct).ConfigureAwait(false);
                }
                finally { LoadGate.Release(); }
                if (lr is not null)
                {
                    if (lr["ok"]?.GetValue<bool>() != true)
                    {
                        Log?.Invoke($"[bridge] {site.Key} load 失败：{lr["error"]}");
                        return null;
                    }
                    Loaded.TryAdd(site.Key, 0);
                    Log?.Invoke($"[bridge] {site.Key} 已装载（{site.Api}）");
                }
            }

            var argv = NormalizeArgs(method, args);
            var ja = new JsonArray();
            foreach (var s in argv) ja.Add(s);
            var cr = await RpcAsync(new JsonObject
            {
                ["id"] = Interlocked.Increment(ref _nextId),
                ["op"] = "call",
                ["site"] = site.Key,
                ["method"] = method,
                ["args"] = ja,
            }, ct).ConfigureAwait(false);
            if (cr?["ok"]?.GetValue<bool>() != true)
            {
                Log?.Invoke($"[bridge] {site.Key}.{method} 失败：{cr?["error"]}");
                return null;
            }
            return cr["result"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[bridge] {site.Key}.{method} 异常：{ex.Message}");
            Forget();
            return null;
        }
        finally { if (serial) Gate.Release(); }
    }
}
