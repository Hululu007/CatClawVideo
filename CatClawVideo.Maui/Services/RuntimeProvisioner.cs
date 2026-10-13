using System.Security.Cryptography;
using System.Text;

namespace CatClawVideo.Maui.Services;

/// <summary>
/// Android 运行环境（模拟器 + 系统镜像 + adb）的**首运下载器**。
///
/// <para><b>背景（2026-10-13）</b>：安装包已把 AndroidSdk 整体移出（919MB → ~130MB），
/// 运行环境改为发布在 CatClaw.Qemu 仓库的 Release 附件上，首次使用 jar 源时提示下载。
/// 落点刻意选 <c>%LOCALAPPDATA%\CatClawVideo\AndroidSdk</c>：安装目录（Program Files）普通用户
/// 不可写，而模拟器的 AVD 家目录本来就在 LOCALAPPDATA（见 SpiderBridgeClient.BundledAvdHome）。</para>
///
/// <para><b>为什么用 zip 而不是 7z</b>：zip 用 .NET 自带的 <see cref="System.IO.Compression.ZipFile"/>
/// 就能解压，零新增依赖；7z 能再省 ~140MB 下载但要引入 SharpCompress 或随包 7z.exe（原生件），
/// 不值得。镜像内容本身是 erofs-lz4，deflate 对它无能为力是数据性质，不是工具问题。</para>
///
/// <para>断点续传：GitHub Releases 的资产响应支持 Range，.part 尾追即可续；校验失败的 zip
/// 删除重来（不尝试修复半截包）。</para>
/// </summary>
public static class RuntimeProvisioner
{
    /// <summary>资产直链（CatClaw.Qemu Release android-runtime-v1）。</summary>
    public const string AssetUrl =
        "https://github.com/kankejiang/CatClaw.Qemu/releases/download/android-runtime-v1/catclaw-android-runtime-win64.zip";

    /// <summary>整包 SHA256（构建期由 stage-runtime.ps1 计算，与 Release 说明一致）。</summary>
    public const string AssetSha256 = "c818323cd830ab6975cea6c95ab07f58cca5e8c24462b78ab0f3ffdd40253c75";

    /// <summary>资产大小（字节，用于下载前磁盘预检与提示文案）。</summary>
    public const long AssetBytes = 983_680_874;

    /// <summary>解压后体积（约 2.6GB，提示与磁盘预检用）。</summary>
    public const long UnpackedBytes = 2_711_270_400;

    /// <summary>下载+解压+AVD 展开的磁盘需求（zip 本体 + 解压内容 + 模拟器首启 scratch 余量）。</summary>
    public static long RequiredFreeBytes => AssetBytes + UnpackedBytes + (2L << 30);

    /// <summary>下载落点与解压目标都在 LOCALAPPDATA 应用目录下。
    /// <para>⚠ <b>刻意不跟 AppPaths.LocalRoot 的 Debug 后缀</b>：这是「跨 Debug/Release 共用的一份运行时」，
    /// 与 AVD 家目录（SpiderBridgeClient.BundledAvdHome 同样是 <c>CatClawVideo</c> 不带后缀）保持一致 ——
    /// 否则 Debug 包解压到 <c>CatClawVideo.debug\AndroidSdk</c>，而宿主按 <c>CatClawVideo\AndroidSdk</c> 找，
    /// 下完却用不上；开发时也会白白多下一份 0.9GB。</para></summary>
    private static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CatClawVideo");

    /// <summary>下载中的 zip 落点（.part 后缀由本类自己管理，便于断点续传）。</summary>
    public static string ZipPath => Path.Combine(RuntimeRoot, "runtime", "catclaw-android-runtime-win64.zip");

    /// <summary>解压目标 SDK 根（与 SpiderBridgeClient.ProvisionedSdkRoot 同一处）。</summary>
    public static string TargetRoot => Path.Combine(RuntimeRoot, "AndroidSdk");

    /// <summary>运行环境是否已就绪（随包目录或已下载解压的目录均可）。</summary>
    public static bool IsProvisioned =>
        File.Exists(Path.Combine(TargetRoot, "emulator", "emulator.exe"))
        && File.Exists(Path.Combine(TargetRoot, "system-images", "android-36", "google_apis_playstore", "x86_64", "system.img"));

    /// <summary>已下载的 .part 现有字节数（断点续传起点；无则 0）。</summary>
    public static long ResumedBytes => File.Exists(ZipPath + ".part") ? new FileInfo(ZipPath + ".part").Length : 0;

    /// <summary>删除下载缓存（.part / 完整 zip）：校验失败或用户重试时用。</summary>
    public static void CleanupDownload()
    {
        try { File.Delete(ZipPath + ".part"); } catch { }
        try { File.Delete(ZipPath); } catch { }
    }

    /// <summary>
    /// 下载（断点续传）→ SHA256 校验 → 解压 → 必需件自检。
    /// 每一步抛出的异常都带可读中文说明（给确认弹窗直接展示）。
    /// </summary>
    /// <param name="progress">0~1 总进度（下载占 0.9，解压占 0.1）。</param>
    /// <param name="stage">阶段文案（供 UI 展示：「下载中 45%」「解压中…」等）。</param>
    public static async Task ProvisionAsync(IProgress<double>? progress, Action<string>? stage, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ZipPath)!);

        // ── 磁盘预检：下载 0.94GB + 解压 2.6GB + 模拟器首启 scratch 2GB 余量 ──
        var drive = new DriveInfo(Path.GetPathRoot(RuntimeRoot)!);
        if (drive.AvailableFreeSpace < RequiredFreeBytes)
            throw new IOException($"磁盘空间不足：{drive.AvailableFreeSpace / 1024 / 1024 / 1024:0.#} GB 可用，" +
                                  $"本步骤需要约 {(AssetBytes + UnpackedBytes) / 1024 / 1024 / 1024:0.#} GB（另留模拟器运行余量）");

        // ── 下载（断点续传）──
        stage?.Invoke("下载中…");
        await DownloadAsync(progress, ct).ConfigureAwait(false);

        // ── 校验 ──
        stage?.Invoke("校验完整性…");
        progress?.Report(0.9);
        var actual = await Task.Run(() => ComputeSha256(ZipPath), ct).ConfigureAwait(false);
        if (!string.Equals(actual, AssetSha256, StringComparison.OrdinalIgnoreCase))
        {
            CleanupDownload();
            throw new InvalidDataException("下载的运行环境包校验失败（SHA256 不匹配），已删除缓存，请重试");
        }

        // ── 解压（.NET 自带 ZipFile；overwrite=true 允许半截解压后重跑）──
        stage?.Invoke("解压中…（约 2.6GB，需几分钟）");
        var final = new Progress<double>(p => progress?.Report(0.9 + p * 0.1));
        await Task.Run(() => ExtractWithProgress(ZipPath, TargetRoot, final, ct), ct).ConfigureAwait(false);

        // ── 必需件自检（缺一件即失败，给可读清单）──
        var missing = RequiredFiles().Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("解压完成但缺少必需文件：" + string.Join("；", missing.Select(Path.GetFileName)));

        stage?.Invoke("完成");
        progress?.Report(1.0);
    }

    /// <summary>解压后必须存在的文件（缺任一即视为未就绪）。</summary>
    private static IEnumerable<string> RequiredFiles() => new[]
    {
        Path.Combine(TargetRoot, "emulator", "emulator.exe"),
        Path.Combine(TargetRoot, "platform-tools", "adb.exe"),
        Path.Combine(TargetRoot, "system-images", "android-36", "google_apis_playstore", "x86_64", "system.img"),
        Path.Combine(TargetRoot, "system-images", "android-36", "google_apis_playstore", "x86_64", "VerifiedBootParams.textproto"),
    };

    /// <summary>断点续传下载：已验证过的 .part 从尾巴接着拉；没有 Range 支持或长度对不上就重头下。</summary>
    private static async Task DownloadAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var part = ZipPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
        using var http = CatClawVideo.Core.Services.Doh.NewClient(600);
        http.Timeout = Timeout.InfiniteTimeSpan;

        for (var attempt = 1; ; attempt++)
        {
            var resumeFrom = 0L;
            if (File.Exists(part)) resumeFrom = new FileInfo(part).Length;
            if (resumeFrom > AssetBytes) { try { File.Delete(part); } catch { } resumeFrom = 0; }

            using var req = new HttpRequestMessage(HttpMethod.Get, AssetUrl);
            if (resumeFrom > 0) req.Headers.TryAddWithoutValidation("Range", $"bytes={resumeFrom}-");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // 206 = 续传成功；200 = 服务端不给 Range（或 .part 已失效）→ 重头写
            var appending = (int)resp.StatusCode == 206 && resumeFrom > 0;
            var start = appending ? resumeFrom : 0;
            if (!appending && File.Exists(part)) { try { File.Delete(part); } catch { } }

            var total = resp.Content.Headers.ContentLength is { } cl ? cl + start : AssetBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(part, appending ? FileMode.Append : FileMode.Create, FileAccess.Write);

            var buf = new byte[1024 * 1024];
            long written = start;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                var n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                written += n;
                if (sw.ElapsedMilliseconds > 300)
                {
                    sw.Restart();
                    progress?.Report(0.9 * Math.Clamp((double)written / total, 0, 1));
                }
            }

            if (written >= AssetBytes) { progress?.Report(0.9); return; }

            // 少于预期（连接中断且流正常结束）→ 重试（.part 已在盘上，下次 Range 续）
            if (attempt >= 5) throw new IOException($"下载不完整：{written / 1024 / 1024} / {AssetBytes / 1024 / 1024} MB（已重试 {attempt} 次）");
        }
    }

    /// <summary>带进度与可取消的解压（整体进度占 0.1，所以内部只报 0~1 的相对值）。</summary>
    private static void ExtractWithProgress(string zipPath, string destDir, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
        var entries = zip.Entries;
        var done = 0;
        var next = 0;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destDir, e.FullName));
            if (!target.StartsWith(destDir, StringComparison.OrdinalIgnoreCase))   // 防压缩包路径穿越
                continue;
            if (string.IsNullOrEmpty(e.Name))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var es = e.Open();
                using var ts = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 1024 * 1024, FileOptions.Asynchronous);
                es.CopyTo(ts, 1024 * 1024);
                // 还原可执行位在 Windows 上无意义（zip 里记录的是 Unix 权限），无需处理
            }
            done++;
            if (done >= next)
            {
                progress.Report((double)done / entries.Count);
                next = done + Math.Max(1, entries.Count / 100);   // 每百分之一报一次
            }
        }
        progress.Report(1.0);
    }

    private static string ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4 * 1024 * 1024, FileOptions.Asynchronous);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
