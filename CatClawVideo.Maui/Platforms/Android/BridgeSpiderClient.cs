using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CatClawVideo.Core.Models;

namespace CatClawVideo.Maui.Platforms.Android;

/// <summary>
/// Guard 加固源的桥客户端：把 <c>csp_*Guard</c> 站点转给**同设备**上的 arm64 bridge APK
/// （<c>com.catclaw.bridge</c>，127.0.0.1:18600，换行分隔 JSON + id 回显协议）。
///
/// <para><b>为什么必须外置</b>：安卓的 native bridge（libndk_translation）是**按 app 命名空间**
/// 开启的，开启条件是 <c>primaryCpuAbi</c> 为 ARM ABI。主程序包内含 <c>lib/x86_64</c>，
/// 设备 abilist 优先选 x86_64 ⇒ 命名空间不 bridged ⇒ 壳自己的 <c>System.load(绝对路径)</c>
/// 被 linker 以 <c>is for EM_AARCH64 (183) instead of EM_X86_64 (62)</c> 拒收。
/// bridge APK 只含 <c>lib/arm64-v8a</c>，命名空间 bridged，壳在其中可正常装载。</para>
///
/// <para>桥不可达时全部方法返回 null，调用方回落到本地 DexClassLoader 路径（对 Guard 会失败，
/// 但行为与接入前一致，不会更差）。</para>
/// </summary>
internal static class BridgeSpiderClient
{
    private const string Host = "127.0.0.1";
    private const int Port = 18600;

    internal static Action<string>? Log;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Socket? _sock;
    private static NetworkStream? _ns;
    private static byte[] _rbuf = new byte[64 * 1024];
    private static int _rlen, _rpos;
    private static int _nextId = 1;
    private static readonly HashSet<string> Loaded = new(StringComparer.Ordinal);

    /// <summary>Guard 加固包：壳入口类名固定以 <c>Guard</c> 结尾（WoGGGuard / MyDriveGuard …）。</summary>
    internal static bool ShouldRoute(VodSiteInfo site) =>
        site.Type == 3
        && !string.IsNullOrEmpty(site.Api)
        && site.Api.StartsWith("csp_", StringComparison.OrdinalIgnoreCase)
        && site.Api.EndsWith("Guard", StringComparison.OrdinalIgnoreCase);

    internal static void Forget()
    {
        try { _ns?.Dispose(); } catch { }
        try { _sock?.Dispose(); } catch { }
        _ns = null; _sock = null; _rlen = _rpos = 0; Loaded.Clear();
    }

    private static bool EnsureConnected()
    {
        if (_sock is { Connected: true } && _ns is not null) return true;
        Forget();
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            s.Connect(Host, Port);
            _sock = s;
            _ns = new NetworkStream(s, ownsSocket: false);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[bridge] 连接 {Host}:{Port} 失败：{ex.Message}（Guard 源需先启动 com.catclaw.bridge）");
            Forget();
            return false;
        }
    }

    private static async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var acc = new List<byte>(1024);
        while (true)
        {
            if (_rpos >= _rlen)
            {
                _rlen = await _ns!.ReadAsync(_rbuf.AsMemory(), ct).ConfigureAwait(false);
                _rpos = 0;
                if (_rlen <= 0) throw new IOException("bridge EOF");
            }
            while (_rpos < _rlen)
            {
                var b = _rbuf[_rpos++];
                if (b == (byte)'\n') return Encoding.UTF8.GetString(acc.ToArray());
                acc.Add(b);
            }
        }
    }

    private static async Task<JsonElement> RpcAsync(JsonElement req, CancellationToken ct)
    {
        var json = req.GetRawText();
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _ns!.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await _ns.FlushAsync(ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.Clone();
    }

    /// <summary>调桥。返回 null = 桥不可用/该站点不支持 ⇒ 调用方走本地路径。</summary>
    internal static async Task<string?> TryCallAsync(VodSiteInfo site, string method, string[] args, CancellationToken ct)
    {
        if (!ShouldRoute(site)) return null;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!EnsureConnected()) return null;

            if (!Loaded.Contains(site.Key))
            {
                var jar = (site.Jar ?? "").Split(";md5;")[0];
                if (string.IsNullOrWhiteSpace(jar)) { Log?.Invoke($"[bridge] {site.Key} 无 jar 地址"); return null; }
                using var ld = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    id = _nextId++,
                    op = "load",
                    site = site.Key,
                    className = site.Api,
                    ext = site.Ext ?? "",
                    jars = new[] { jar }
                }));
                var lr = await RpcAsync(ld.RootElement, ct).ConfigureAwait(false);
                if (!(lr.TryGetProperty("ok", out var ok) && ok.GetBoolean()))
                {
                    var err = lr.TryGetProperty("error", out var e) ? e.ToString() : "?";
                    Log?.Invoke($"[bridge] {site.Key} load 失败：{err}");
                    return null;
                }
                Loaded.Add(site.Key);
                Log?.Invoke($"[bridge] {site.Key} 已装载（{site.Api}）");
            }

            using var cd = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                id = _nextId++,
                op = "call",
                site = site.Key,
                method,
                args
            }));
            var cr = await RpcAsync(cd.RootElement, ct).ConfigureAwait(false);
            if (!(cr.TryGetProperty("ok", out var cok) && cok.GetBoolean()))
            {
                var err = cr.TryGetProperty("error", out var e) ? e.ToString() : "?";
                Log?.Invoke($"[bridge] {site.Key}.{method} 失败：{err}");
                return null;
            }
            var result = cr.TryGetProperty("result", out var rr) ? rr.GetString() : null;
            return result ?? "";
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[bridge] {site.Key}.{method} 异常：{ex.Message}");
            Forget();
            return null;
        }
        finally { Gate.Release(); }
    }
}
