using System.Diagnostics;
using System.Text;

namespace CatClawVideo.Maui.Services;

/// <summary>环境检测项的严重级别。</summary>
public enum EnvCheckLevel { Ok, Warn, Fail, Info }

/// <summary>
/// 一个可点的处置动作。只声明语义 id 与按钮文案，具体怎么执行由宿主 UI 决定
/// （这样检测服务不依赖 MAUI，也能在无 UI 场景下复用）。
/// </summary>
public sealed record EnvAction(string Id, string Text);

/// <summary>一条环境检测结果。<paramref name="Hint"/> 是文字建议，<paramref name="Actions"/> 是可点的一键处置。</summary>
public sealed record EnvCheckItem(string Title, EnvCheckLevel Level, string Detail, string? Hint = null,
    IReadOnlyList<EnvAction>? Actions = null);

/// <summary>
/// 启动环境检测（Windows）：把「Android 运行环境是否就位」与「宿主侧条件」合成一份清单，
/// 由首页冷启动画面在模拟器冷启期间展示 —— 排查「爬虫源全灭 / 引擎慢得离谱 / 装完起不来」
/// 这类问题，用户第一眼就能看到根因与处置办法，不用去翻日志。
///
/// <para><b>2026-10-12 改版</b>：原先探的是 QEMU ART guest 套件（QemuGuest\ 内核+根镜像+WHPX）。
/// 那条线已彻底退役，改由**随包 Android 模拟器**（{安装目录}\AndroidSdk\）承担；随包镜像
/// 也已精简重签（Android 16 / API 36）。本文件随之改为探测模拟器环境，文案也一并改口，
/// 否则启动页会显示「x86_64 QEMU + BIOS」「Android 13 内核 + 根镜像」这类早已不存在的说法。</para>
///
/// <para>全部为本地探测（文件/注册表/一次 -accel-check 子进程），无网络；毫秒~秒级；
/// 任何一项抛异常都只影响该项（降级成 Info），不会阻断启动。</para>
/// </summary>
public static class StartupEnvCheck
{
    /// <summary>随包镜像的固定落点（与 .iss 的打包目录、SpiderBridgeClient 的 AVD 定义三处一致）。</summary>
    private const string ImageRel = @"system-images\android-36\google_apis_playstore\x86_64";

    /// <summary>磁盘剩余空间的告警阈值（模拟器 AVD 冷启后约 3.2GB + 流缓存默认上限 10GB）。</summary>
    private const long DiskWarnBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>物理内存告警阈值（模拟器默认 3GB + 宿主自身）。</summary>
    private const ulong MemoryWarnBytes = 4UL * 1024 * 1024 * 1024;

    /// <summary>执行全部检测。非 Windows 平台返回空表（调用方据此隐藏面板）。</summary>
    public static IReadOnlyList<EnvCheckItem> Run()
    {
        var items = new List<EnvCheckItem>();
#if WINDOWS
        var baseDir = AppContext.BaseDirectory;

        // ① Android 运行环境：随包优先，其次本机已装的 SDK（开发机）
        var sdkRoot = ProbeRuntime(items, baseDir);

        // ② 硬件加速：x86_64 客机在 Windows 上走 WHPX。直接问模拟器自己最准
        //    （emulator -accel-check），比查注册表/可选功能少一堆误判。
        items.Add(ProbeAccel(sdkRoot));

        // ③ 系统镜像：版本/ABI + 「镜像与 AVB 参数成对」校验
        items.Add(ProbeImage(sdkRoot, baseDir));

        // ④ 桥程序（arm64-only APK，装进模拟器里跑 jar 爬虫与 Guard 解密）
        items.Add(ProbeBridgeApk(baseDir));

        // ⑤ 磁盘剩余空间
        try
        {
            var root = Path.GetPathRoot(CatClawVideo.Core.AppPaths.LocalRoot);
            var free = root is null ? 0 : new DriveInfo(root).AvailableFreeSpace;
            var gb = free / 1024.0 / 1024 / 1024;
            items.Add(free >= DiskWarnBytes
                ? new EnvCheckItem("磁盘空间", EnvCheckLevel.Ok, $"{gb:0.#} GB 可用")
                : new EnvCheckItem("磁盘空间", EnvCheckLevel.Warn, $"{gb:0.#} GB 可用",
                    "建议 ≥ 5 GB：模拟器首次冷启要在数据目录展开约 3.2GB（ram.img/userdata），" +
                    "流缓存默认上限另有 10GB（设置页可调小）",
                    [new EnvAction("open-datadir", "打开数据目录")]));
        }
        catch { }

        // ⑥ 物理内存
        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var gb = total / 1024.0 / 1024 / 1024;
            items.Add(total >= (long)MemoryWarnBytes
                ? new EnvCheckItem("内存", EnvCheckLevel.Ok, $"{gb:0.#} GB")
                : new EnvCheckItem("内存", EnvCheckLevel.Warn, $"{gb:0.#} GB",
                    "建议 ≥ 4 GB：模拟器默认占 3GB，余量不足时宿主频繁换页、播放卡顿"));
        }
        catch { }

        // ⑦ 系统与运行时（信息项：报障时先看这一行）
        try
        {
            items.Add(new EnvCheckItem("系统", EnvCheckLevel.Info,
                $"{WindowsVersionText()} · " +
                $"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · .NET {Environment.Version.Major}"));
        }
        catch { }
#endif
        return items;
    }

#if WINDOWS
    /// <summary>找可用的 Android SDK 根：优先随包，其次本机。找不到返回 null 并记一条 Fail。</summary>
    private static string? ProbeRuntime(List<EnvCheckItem> items, string baseDir)
    {
        try
        {
            var bundled = Path.Combine(baseDir, "AndroidSdk");
            if (File.Exists(Path.Combine(bundled, "emulator", "emulator.exe"))
                && File.Exists(Path.Combine(bundled, "platform-tools", "adb.exe")))
            {
                items.Add(new EnvCheckItem("Android 运行环境", EnvCheckLevel.Ok, "随包（开箱即用）"));
                return bundled;
            }

            var home = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
            if (File.Exists(Path.Combine(home, "emulator", "emulator.exe")))
            {
                items.Add(new EnvCheckItem("Android 运行环境", EnvCheckLevel.Info, "本机已装的 Android SDK",
                    "本安装包自带运行环境；这里用的是机器上已装的那套（模拟器 + AVD 需自行准备）"));
                return home;
            }

            items.Add(new EnvCheckItem("Android 运行环境", EnvCheckLevel.Fail, "未找到",
                "安装包应当自带「安装目录\\AndroidSdk\\」（模拟器 + 系统镜像 + adb）。" +
                "缺失说明安装不完整，建议重新运行安装程序"));
        }
        catch (Exception ex)
        {
            items.Add(new EnvCheckItem("Android 运行环境", EnvCheckLevel.Info, "探测失败：" + ex.GetType().Name));
        }
        return null;
    }

    /// <summary>
    /// 问模拟器自己：<c>emulator -accel-check</c>。输出形如
    /// <code>accel:\n0\nWHPX(10.0.26200) is installed and usable.\naccel</code>
    /// 其中第二行是状态码（0 = 可用），第三行是给用户看的说明。
    /// </summary>
    private static EnvCheckItem ProbeAccel(string? sdkRoot)
    {
        if (sdkRoot is null)
            return new EnvCheckItem("硬件加速", EnvCheckLevel.Info, "未探测（无运行环境）");
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(sdkRoot, "emulator", "emulator.exe"), "-accel-check")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return new EnvCheckItem("硬件加速", EnvCheckLevel.Info, "未探测（进程未启动）");
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(true); } catch { }
                return new EnvCheckItem("硬件加速", EnvCheckLevel.Info, "探测超时");
            }
            var lines = (so.Result + "\n" + se.Result)
                .Split('\n').Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.Equals("accel", StringComparison.OrdinalIgnoreCase)
                                     && !l.Equals("accel:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var code = lines.FirstOrDefault(l => l.Length == 1 && char.IsDigit(l[0]));
            var msg = lines.LastOrDefault() ?? "";
            if (code == "0")
                return new EnvCheckItem("硬件加速", EnvCheckLevel.Ok, msg.Length > 0 ? msg : "已启用");

            var actions = new List<EnvAction>
            {
                new("enable-whpx", "一键启用虚拟化"),
                new("howto-whpx", "详细步骤"),
                new("open-optional-features", "手动打开 Windows 功能"),
            };
            return new EnvCheckItem("硬件加速", EnvCheckLevel.Warn, msg.Length > 0 ? msg : "不可用",
                "模拟器需要 WHPX（Windows 虚拟机监控程序平台）。未启用时 x86_64 客机会回落到软件模拟，" +
                "冷启动由 1 分多钟变成十几分钟且操作严重卡顿，建议按提示开启后重启电脑", actions);
        }
        catch (Exception ex)
        {
            return new EnvCheckItem("硬件加速", EnvCheckLevel.Info, "探测失败：" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 系统镜像：从 <c>source.properties</c> 读出真实型号，并校验
    /// <c>system.img</c> 与 <c>VerifiedBootParams.textproto</c> **成对**。
    ///
    /// <para><b>为什么必须成对</b>：随包的 system.img 是精简并重签过的（vbmeta 摘要随之改变），
    /// 而 textproto 里带着配套的 <c>androidboot.vbmeta.{size,hash_alg,digest}</c>。
    /// 只换其中一个时，init 会报 <c>vbmeta digest error isn't allowed</c> 直接起不来 ——
    /// 这是「装完打不开」最隐蔽的一种，值得在启动页就点出来。</para>
    /// </summary>
    private static EnvCheckItem ProbeImage(string? sdkRoot, string baseDir)
    {
        try
        {
            var dir = Path.Combine(sdkRoot ?? Path.Combine(baseDir, "AndroidSdk"), ImageRel);
            var img = Path.Combine(dir, "system.img");
            var tp = Path.Combine(dir, "VerifiedBootParams.textproto");
            if (!File.Exists(img))
                return new EnvCheckItem("系统镜像", EnvCheckLevel.Fail, "未找到",
                    sdkRoot is null ? "没有可用的 Android 运行环境" : ("缺少 " + img));

            var api = ""; var abi = ""; var tag = "";
            var props = Path.Combine(dir, "source.properties");
            if (File.Exists(props))
            {
                foreach (var line in File.ReadAllLines(props))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var k = line[..i].Trim(); var v = line[(i + 1)..].Trim();
                    switch (k)
                    {
                        case "AndroidVersion.ApiLevel": api = v; break;
                        case "SystemImage.Abi": abi = v; break;
                        case "SystemImage.TagDisplay": tag = v; break;
                    }
                }
            }

            var name = new StringBuilder();
            name.Append(string.IsNullOrEmpty(api) ? "Android" : AndroidNameOf(api));
            if (!string.IsNullOrEmpty(api)) name.Append($"（API {api}）");
            if (!string.IsNullOrEmpty(abi)) name.Append(" · ").Append(abi);
            if (!string.IsNullOrEmpty(tag)) name.Append(" · ").Append(tag);
            name.Append(" · ").Append(FileSizeText(new FileInfo(img).Length));

            if (!File.Exists(tp))
                return new EnvCheckItem("系统镜像", EnvCheckLevel.Fail, name.ToString(),
                    "缺少 VerifiedBootParams.textproto —— 它带着与 system.img 配套的 AVB 校验参数，" +
                    "两者必须同时存在，否则客机启动时报 vbmeta 摘要错误。建议重新安装");

            return new EnvCheckItem("系统镜像", EnvCheckLevel.Ok, name.ToString());
        }
        catch (Exception ex)
        {
            return new EnvCheckItem("系统镜像", EnvCheckLevel.Info, "探测失败：" + ex.GetType().Name);
        }
    }

    /// <summary>桥程序：arm64-only 的 catclaw-bridge.apk，由客户端首次运行时自动装进模拟器。</summary>
    private static EnvCheckItem ProbeBridgeApk(string baseDir)
    {
        try
        {
            var apk = Path.Combine(baseDir, "BridgeApk", "catclaw-bridge.apk");
            if (!File.Exists(apk))
                return new EnvCheckItem("爬虫桥程序", EnvCheckLevel.Fail, "未随包",
                    "缺少 BridgeApk\\catclaw-bridge.apk ⇒ jar 爬虫源与 Guard 解密网盘源不可用。建议重新安装");
            return new EnvCheckItem("爬虫桥程序", EnvCheckLevel.Ok,
                $"catclaw-bridge.apk · {FileSizeText(new FileInfo(apk).Length)}");
        }
        catch (Exception ex)
        {
            return new EnvCheckItem("爬虫桥程序", EnvCheckLevel.Info, "探测失败：" + ex.GetType().Name);
        }
    }

    private static string AndroidNameOf(string api) => api switch
    {
        "28" => "Android 9", "29" => "Android 10", "30" => "Android 11", "31" => "Android 12",
        "32" => "Android 12L", "33" => "Android 13", "34" => "Android 14", "35" => "Android 15",
        "36" => "Android 16", "37" => "Android 17",
        _ => "Android",
    };

    private static string FileSizeText(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024.0 / 1024 / 1024:0.#} GB"
        : $"{bytes / 1024.0 / 1024:0.#} MB";

    /// <summary>
    /// Windows 版本描述，形如「Windows 11 Pro · 26200.9457」。
    ///
    /// <para><b>为什么不能直接读注册表 ProductName</b>：微软从 Windows 11 21H2 起就没再更新过那个键 ——
    /// 实测本机（Build 26200 / 25H2，实打实的 Windows 11）注册表里仍写着
    /// <c>ProductName = "Windows 10 Pro"</c>，照样认错。</para>
    ///
    /// <para><b>也不能用 Environment.OSVersion</b>：它在 Win11 上谎报 <c>10.0.x</c>
    /// （兼容性谎报），本机实测 <c>10.0.26200.0</c>。</para>
    ///
    /// <para><b>可靠判据只有 Build 号</b>：≥22000 即 Windows 11（微软官方分界），
    /// ≥20348 是 Server 2022，≥19041 是 Server / Win10 21H1 一线。Build 号本身是真实的。</para>
    /// </summary>
    private static string WindowsVersionText()
    {
        var build = 0; var ubr = 0;
        try { build = Environment.OSVersion.Version.Build; } catch { }

        var product = ""; var display = "";
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                product = key.GetValue("ProductName") as string ?? "";
                display = key.GetValue("DisplayVersion") as string ?? "";
                ubr = Convert.ToInt32(key.GetValue("UBR") ?? 0);
            }
        }
        catch { }

        var edition = product.Replace("Windows 10", "").Replace("Windows 11", "")
                            .Replace("Windows 11 ", "").Replace("Windows 10 ", "").Trim();
        var isServer = product.Contains("Server", StringComparison.OrdinalIgnoreCase);

        var sysName = isServer
            ? "Windows Server"
            : (build >= 22000 ? "Windows 11" : "Windows 10");

        var name = edition.Length > 0 ? $"{sysName} {edition}" : sysName;
        var buildText = build > 0
            ? (ubr > 0 ? $"{build}.{ubr}" : build.ToString())
            : (product.Length > 0 ? product : "未知版本");
        return $"{name} · {buildText}" + (display.Length > 0 ? $"（{display}）" : "");
    }
#endif
}
