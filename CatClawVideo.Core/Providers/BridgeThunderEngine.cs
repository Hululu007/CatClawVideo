using System.Text.Json.Nodes;
using CatClawVideo.Core.Interfaces;

namespace CatClawVideo.Core.Providers;

/// <summary>
/// 迅雷磁力引擎的**外置 Android 桥**实现（com.catclaw.bridge）。
///
/// <para><b>为什么不跑在宿主 / QEMU guest</b>：libxl_thunder_sdk.so(5.4MB) / libxl_stat.so(818KB)
/// 只有 <b>arm64</b> 版本。主程序（primaryCpuAbi=x86_64）的命名空间不 bridged
/// ⇒ <c>dlopen failed: library "libxl_stat.so" not found</c>（实测 2026-10-11）；
/// 而原来绕开它的办法是在宿主 QEMU 里跑 x86_64 ART guest，再用 <c>qemu-aarch64-static</c>
/// 用户态模拟跑迅雷 harness —— 只为了这一个引擎就要多背 ~2.5GB 常驻内存。</para>
///
/// <para>桥 APK 只装 <c>lib/arm64-v8a</c>，命名空间被 native bridge（libndk_translation）接管，
/// arm64 的 .so 直接 dlopen 成功（实测 <c>nativeloader: Load …/lib/arm64/libxl_thunder_sdk.so
/// using class loader ns clns-9</c>）。链路：宿主 → adb forward → 桥 <c>op=thunder</c> →
/// <c>ThunderBridge</c>（照搬 Android 侧那份 Java 桥）。</para>
///
/// <para><b>回落契约</b>：任何一步失败返回 null，调用方回落内置 BT。</para>
/// </summary>
public sealed class BridgeThunderEngine : IPreferredMagnetEngine
{
    public string Name => "迅雷(桥)";

    /// <summary>init 成功且未 Stop。</summary>
    public bool IsReady { get; private set; }

    /// <summary>是否已有活跃播放会话（探测类调用应让位）。</summary>
    public bool IsBusy { get; private set; }

    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _triedInit;
    private bool _initFailed;
    private string? _playUrl;

    public BridgeThunderEngine(Action<string>? log = null) => _log = log;

    private void Log(string m) { _log?.Invoke("[迅雷桥] " + m); }

    public async Task<bool> EnsureReadyAsync()
    {
        if (IsReady) return true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsReady) return true;
            // ⚠ 只有**确定性失败**（桥明确回了 ERR，如 appKey 失效）才永久放弃；
            //   桥那一刻不可达/正在装载（null）不记账 —— 否则引导里一次瞬时失败就把
            //   「桥迅雷」永久判死，随后回落 QEMU guest（2026-10-11 实测踩到）。
            if (_triedInit && _initFailed) return false;
            var r = await SpiderBridgeClient.ThunderAsync("init", null, CancellationToken.None).ConfigureAwait(false);
            Log($"init → {r ?? "(桥不可达)"}");
            if (r is null) return false;                       // 瞬时：不记账，下次再试
            _triedInit = true;                                 // 拿到明确答复才记账
            IsReady = r.StartsWith("OK", StringComparison.Ordinal);
            _initFailed = !IsReady;
            return IsReady;
        }
        finally { _gate.Release(); }
    }

    public async Task<List<MagnetFile>?> ListFilesAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!await EnsureReadyAsync().ConfigureAwait(false)) return null;
        // 单会话引擎：播放中不要展开别的磁力（会把播放会话顶掉）
        if (IsBusy && !string.Equals(magnet, _busyMagnet, StringComparison.Ordinal)) { Log("播放中，跳过探测"); return null; }
        var raw = await SpiderBridgeClient.ThunderAsync("list",
            new Dictionary<string, object?> { ["magnet"] = magnet, ["preferName"] = preferName ?? "" }, ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.StartsWith("ERR", StringComparison.Ordinal)) { Log($"list 失败：{raw}"); return null; }
        var list = new List<MagnetFile>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Split('\t');
            if (p.Length < 3 || !int.TryParse(p[0], out var idx) || !long.TryParse(p[1], out var size)) continue;
            list.Add(new MagnetFile(idx, size, p[2]));
        }
        Log($"list → {list.Count} 个文件");
        return list.Count > 0 ? list : null;
    }

    private string? _busyMagnet;

    public async Task<MagnetPlayback?> TryOpenAsync(string magnet, string? preferName = null, CancellationToken ct = default)
    {
        if (!await EnsureReadyAsync().ConfigureAwait(false)) return null;
        var files = await ListFilesAsync(magnet, preferName, ct).ConfigureAwait(false);
        if (files is null || files.Count == 0) return null;

        // 选片：优先名字匹配，否则最大的视频文件
        MagnetFile pick = files[0];
        if (!string.IsNullOrEmpty(preferName))
        {
            var hit = files.FirstOrDefault(f => f.Name.Contains(preferName, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) pick = hit;
        }
        else
        {
            var vids = files.Where(f => IsVideo(f.Name)).ToList();
            pick = (vids.Count > 0 ? vids : files).OrderByDescending(f => f.Size).First();
        }

        var r = await SpiderBridgeClient.ThunderAsync("play",
            new Dictionary<string, object?> { ["index"] = pick.Index }, ct).ConfigureAwait(false);
        if (r is null || !r.StartsWith("OK:", StringComparison.Ordinal)) { Log($"play 失败：{r}"); return null; }
        var url = r[3..];
        // ★ 流地址是**模拟器内**的 127.0.0.1:<随机端口>，必须把它 forward 到宿主同一端口
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            if (!await SpiderBridgeClient.EnsurePortForwardedAsync(u.Port, ct).ConfigureAwait(false))
            { Log($"端口 {u.Port} 转发失败，播放地址宿主够不到"); return null; }
        }
        _playUrl = url; _busyMagnet = magnet; IsBusy = true;
        Log($"起播 index={pick.Index}（{pick.Name}）→ {url}");
        return new MagnetPlayback(string.Empty, pick.Index, pick.Size, pick.Name, url);
    }

    private static bool IsVideo(string n)
    {
        var e = Path.GetExtension(n).ToLowerInvariant();
        return e is ".mkv" or ".mp4" or ".avi" or ".ts" or ".m2ts" or ".mov" or ".wmv" or ".flv" or ".rmvb" or ".webm";
    }

    public void Stop()
    {
        _ = SpiderBridgeClient.ThunderAsync("stop", null, CancellationToken.None);
        _playUrl = null; _busyMagnet = null; IsBusy = false;
    }
}
