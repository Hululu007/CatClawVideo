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

    /// <summary>
    /// 备用下载源（按顺序回退）。直连 GitHub Release 在「github.com 被 hosts 黑洞 / 国内网络」的机器上
    /// 会直接失败（解析到 127.0.0.1 时连得上但没有数据）；镜像只是换条路走，**文件完全相同**，
    /// 后面的 SHA256 校验一样管用。
    /// <para>2026-10-15 本机实测（同一份 938MB 资产，6 秒采样）：</para>
    /// <list type="bullet">
    /// <item>ghfast.top —— <b>37 MB/s</b>，且支持 Range（206）⇒ 断点续传可用 —— 排第一</item>
    /// <item>gh-proxy.com —— 0.15 MB/s（当天路由很差，仅作兜底）</item>
    /// <item>ghproxy.net / cdn.gh-proxy.com —— 0.01~0.06 MB/s；gh.llkk.cc / ghproxy.cc 已失效</item>
    /// </list>
    /// </summary>
    public static readonly string[] MirrorUrls =
    {
        "https://ghfast.top/" + AssetUrl,       // 主力：实测 37~45 MB/s，支持 Range
        "https://gh-proxy.com/" + AssetUrl,     // 备选（实测 0.15~0.5 MB/s）
        "https://ghproxy.net/" + AssetUrl,      // 兜底（很慢，只在前面都不通时用）
    };

    /// <summary>最近一次实际用的下载源（直连 GitHub / 镜像主机名），用于对比镜像速度与排障。</summary>
    public static string LastUsedSource { get; private set; } = "直连 GitHub";

    /// <summary>整包 SHA256（构建期由 stage-runtime.ps1 计算，与 Release 说明一致）。</summary>
    public const string AssetSha256 = "c818323cd830ab6975cea6c95ab07f58cca5e8c24462b78ab0f3ffdd40253c75";

    /// <summary>资产大小（字节，用于下载前磁盘预检与提示文案）。</summary>
    public const long AssetBytes = 983_680_874;

    /// <summary>解压后体积（约 2.6GB，提示与磁盘预检用）。</summary>
    public const long UnpackedBytes = 2_711_270_400;

    /// <summary>下载+解压+AVD 展开的磁盘需求（zip 本体 + 解压内容 + 模拟器首启 scratch 余量）。</summary>
    public static long RequiredFreeBytes => AssetBytes + UnpackedBytes + (2L << 30);

    /// <summary>下载落点与解压目标都在 <see cref="CatClawVideo.Core.AppPaths.RuntimeRoot"/> ——
    /// **与程序同目录**：<c>{程序目录}\runtime\*.zip</c> 与 <c>{程序目录}\AndroidSdk</c>
    /// （程序目录不可写时 AppPaths 会自己退回用户数据目录）。</summary>
    private static string RuntimeRoot => CatClawVideo.Core.AppPaths.RuntimeRoot;

    /// <summary>运行环境是不是就在程序目录里（界面提示用）。</summary>
    public static bool IsPortable => CatClawVideo.Core.AppPaths.RuntimeRootIsPortable;

    /// <summary>运行环境的实际落点（界面/排障显示用）。</summary>
    public static string TargetRootPath => TargetRoot;

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

    /// <summary>已落盘的完整 zip 字节数（无则 0）。自检用它区分「包已就绪、只差点解压」与「下载未完成」。</summary>
    public static long CachedZipBytes => File.Exists(ZipPath) ? new FileInfo(ZipPath).Length : 0;

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
        await DownloadAsync(progress, stage, ct).ConfigureAwait(false);

        // ── 校验 ──
        stage?.Invoke("校验完整性…");
        progress?.Report(0.9);
        var actual = await Task.Run(() => ComputeSha256(ZipPath), ct).ConfigureAwait(false);
        if (!string.Equals(actual, AssetSha256, StringComparison.OrdinalIgnoreCase))
        {
            CleanupDownload();
            throw new InvalidDataException("下载的运行环境包校验失败（SHA256 不匹配），已删除缓存，请重试");
        }

        // ── 解压 + 必需件自检（与手动安装共用同一段收尾）──
        var final = new Progress<double>(p => progress?.Report(0.9 + p * 0.1));
        await ExtractAndVerifyAsync(ZipPath, final, stage, ct).ConfigureAwait(false);

        stage?.Invoke("完成");
        progress?.Report(1.0);
    }

    // ═══════════ 手动安装（自动下载整条链路走不通时的兜底）═══════════
    //
    // 2026-10-14：自动下载只有 GitHub Releases 直连一条路，而实际环境里它可能整条不通 ——
    // 公司代理拦 HTTPS、GitHub 被墙、断流重试到上限，或者用户就是想用迅雷/网盘/别的机器拿包。
    // 手动安装把「怎么拿到包」交给用户，客户端仍负责校验、解压与自检 —— 少掉的是下载，不是正确性。

    /// <summary>所选文件是否就是官方发布包（大小 + SHA256 双判；大小不符直接短路，不必白算 0.9GB 的哈希）。</summary>
    public static async Task<bool> IsOfficialPackageAsync(string zipPath, CancellationToken ct)
    {
        var fi = new FileInfo(zipPath);
        if (!fi.Exists || fi.Length != AssetBytes) return false;
        var actual = await Task.Run(() => ComputeSha256(zipPath), ct).ConfigureAwait(false);
        return string.Equals(actual, AssetSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 手动安装：把用户自己下载/拷贝来的 zip 解压到 <see cref="TargetRoot"/>。
    /// <para>这里不做下载、也不做 SHA 校验 —— 「要不要接受一个非官方原件」是产品决策，由 UI 问过用户再定；
    /// 本方法只保证：是个能打开的 zip、磁盘够、解压后必需件齐全。</para>
    /// </summary>
    public static async Task InstallFromZipAsync(string zipPath, IProgress<double>? progress,
        Action<string>? stage, CancellationToken ct)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("找不到所选的安装包：" + zipPath, zipPath);

        // 先只当 zip 打开一次：选错文件（exe/7z 改名）或半截包要在这里给可读错误，而不是解压到一半才炸。
        // ZipArchive 自己抛的是英文（"End of Central Directory record could not be found."），这里翻成人话。
        try
        {
            await Task.Run(() =>
            {
                using var fs = File.OpenRead(zipPath);
                using var _ = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(
                $"这个文件不是能打开的 zip 包（{ex.Message}）。请确认选中的是运行环境安装包，且下载完整", ex);
        }

        // 磁盘预检：只算解压（zip 已经在本地盘上了）
        var drive = new DriveInfo(Path.GetPathRoot(TargetRoot)!);
        if (drive.AvailableFreeSpace < UnpackedBytes + (2L << 30))
            throw new IOException($"磁盘空间不足：{drive.Name} 可用 {drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:0.#} GB，" +
                                  $"解压本包需要约 {UnpackedBytes / 1024.0 / 1024 / 1024:0.#} GB（另需模拟器运行余量）");

        // 已有解压结果整体清掉再解：手动包可能与本机旧环境不是同一批次，
        // 新旧混装（新 emulator.exe + 旧 system.img）是最难查的一类故障。
        stage?.Invoke("清理旧文件…");
        await Task.Run(TryRemoveTargetRoot, ct).ConfigureAwait(false);

        await ExtractAndVerifyAsync(zipPath, progress, stage, ct).ConfigureAwait(false);

        stage?.Invoke("完成");
        progress?.Report(1.0);
    }

    /// <summary>清掉上一份解压结果。删不掉（模拟器占着 emulator.exe 之类）不致命：解压是覆盖写，
    /// 记一条日志继续，真出问题会由解压或必需件自检报出来。</summary>
    private static void TryRemoveTargetRoot()
    {
        try
        {
            if (Directory.Exists(TargetRoot)) Directory.Delete(TargetRoot, recursive: true);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[runtime] 清理旧运行环境失败（改为覆盖解压）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>解压 + 必需件自检（下载安装与手动安装共用的收尾段；<paramref name="progress"/> 是本段自己的 0~1）。</summary>
    private static async Task ExtractAndVerifyAsync(string zipPath, IProgress<double>? progress,
        Action<string>? stage, CancellationToken ct)
    {
        stage?.Invoke("解压中…（约 2.6GB，需几分钟）");
        await Task.Run(() => ExtractWithProgress(zipPath, TargetRoot, progress ?? new Progress<double>(), ct), ct)
            .ConfigureAwait(false);

        var missing = RequiredFiles().Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException("解压完成但缺少必需文件：" + string.Join("；", missing.Select(Path.GetFileName)));
    }

    /// <summary>解压后必须存在的文件（缺任一即视为未就绪）。</summary>
    private static IEnumerable<string> RequiredFiles() => new[]
    {
        Path.Combine(TargetRoot, "emulator", "emulator.exe"),
        Path.Combine(TargetRoot, "platform-tools", "adb.exe"),
        Path.Combine(TargetRoot, "system-images", "android-36", "google_apis_playstore", "x86_64", "system.img"),
        Path.Combine(TargetRoot, "system-images", "android-36", "google_apis_playstore", "x86_64", "VerifiedBootParams.textproto"),
    };

    /// <summary>断点续传下载：已验证过的 .part 从尾巴接着拉；没有 Range 支持或长度对不上就重头下。
    /// <para>源按「直连 → 镜像」顺序尝试，某个源连不上就换下一个（.part 保留，续传照旧）。</para></summary>
    private static async Task DownloadAsync(IProgress<double>? progress, Action<string>? stage, CancellationToken ct)
    {
        var part = ZipPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(part)!);

        // 已经下完的包直接收工 —— 完整 .part 若再发 Range，服务端会回 416（Range Not Satisfiable），
        // EnsureSuccessStatusCode 一抛就成了「包明明下完了却永远装不上」的死局（0.2.6 实测踩到过）。
        if (CachedZipBytes >= AssetBytes) { progress?.Report(0.9); return; }
        if (File.Exists(part) && new FileInfo(part).Length >= AssetBytes)
        {
            PromotePart(part);
            progress?.Report(0.9);
            return;
        }

        // ⚠ 这里刻意**不用 DoH 客户端**：它的 ConnectCallback 每条连接都要自己解析 DNS + 自己握手，
        //   实测同一台机器、同一个镜像：curl 32~54 MB/s，而走 DoH 客户端只有 ~8.6 MB/s
        //   （2026-10-15 实测：8 秒只涨 69MB）。批量下载要的是吞吐。
        //   镜像域名（ghfast.top / gh-proxy.com / ghproxy.net）本身解析正常；被 hosts 黑洞的直连
        //   在下面会被 IsBlackholed 直接跳过，不依赖 DoH 绕路。
        using var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 8,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // 源列表：直连在前，镜像在后。**两种失败都要换源**：
        //   ① 连不上 / 报错（抛异常）；
        //   ② 连上了却**不下数据** —— hosts 把域名指到 127.0.0.1 时就是这样：TCP 能建、
        //      数据永远不来、既不报错也不结束（2026-10-15 用户实测：卡在 1%、0.0 MB/s）。
        //      所以加了「15 秒无字节」看门狗；解析到回环地址的源更是在连接前直接跳过。
        //   .part 全程保留，换源后从断点续传。
        var urls = new string[MirrorUrls.Length + 1];
        urls[0] = AssetUrl;
        Array.Copy(MirrorUrls, 0, urls, 1, MirrorUrls.Length);

        var urlIdx = 0;
        var attempt = 0;
        var round = 1;
        Exception? lastErr = null;

        while (attempt < MaxDownloadAttempts)
        {
            // 一轮跑完（所有源都失败）再回头重试：镜像会"抽风几分钟"（实测 ghfast 23:12 能跑
            // 45MB/s、23:18 连接超时、23:20 又恢复），只试一轮就放弃太脆。
            if (urlIdx >= urls.Length)
            {
                if (round >= MaxSourceRounds) break;
                round++;
                urlIdx = 1;      // 0 号直连第一轮就判定（黑洞）了，不再回头
                stage?.Invoke($"所有源试过一轮都失败 → 重试第 {round} 轮（镜像抽风通常几分钟就恢复）");
            }
            attempt++;

            var resumeFrom = 0L;
            if (File.Exists(part)) resumeFrom = new FileInfo(part).Length;
            if (resumeFrom > AssetBytes) { try { File.Delete(part); } catch { } resumeFrom = 0; }

            if (IsBlackholed(urls[urlIdx]))
            {
                var bad = SourceName(urlIdx, urls);
                urlIdx++;
                stage?.Invoke($"{bad} 被解析到 127.0.0.1（hosts 黑洞，连上也没数据）→ 跳过"
                              + (urlIdx < urls.Length ? $"，改用 {SourceName(urlIdx, urls)}" : ""));
                continue;
            }

            try
            {
                // 连接阶段单独限时（只管"拿到响应头"）：域名被丢包时不会无限等下去。
                // 正文下载不受它约束，改由无数据看门狗管 —— 大文件要下几分钟。
                using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headCts.CancelAfter(TimeSpan.FromSeconds(ConnectSeconds));
                using var req = new HttpRequestMessage(HttpMethod.Get, urls[urlIdx]);
                if (resumeFrom > 0) req.Headers.TryAddWithoutValidation("Range", $"bytes={resumeFrom}-");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                headCts.CancelAfter(Timeout.InfiniteTimeSpan);

                LastUsedSource = SourceName(urlIdx, urls);
                stage?.Invoke($"下载源：{LastUsedSource}（{urlIdx + 1}/{urls.Length}）");

                // 206 = 续传成功；200 = 服务端不给 Range（或 .part 已失效）→ 重头写
                var appending = (int)resp.StatusCode == 206 && resumeFrom > 0;
                var start = appending ? resumeFrom : 0;
                if (!appending && File.Exists(part)) { try { File.Delete(part); } catch { } }

                var total = resp.Content.Headers.ContentLength is { } cl ? cl + start : AssetBytes;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                long written = start;
                // ⚠⚠ 写入流必须**在自己的作用域里关掉**：改名（PromotePart）在它外面做。
                //   2026-10-15 用户实测踩到的坑：流还开着就 File.Move ⇒
                //   "The process cannot access the file because it is being used by another process"
                //   —— 自己锁自己，938MB 白下、还换了源重下。所以这里刻意用内层块包住 using。
                {
                    await using var dst = new FileStream(part, appending ? FileMode.Append : FileMode.Create, FileAccess.Write);
                    var buf = new byte[1024 * 1024];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var stall = System.Diagnostics.Stopwatch.StartNew();      // 无数据看门狗
                    while (true)
                    {
                        var n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                        if (n == 0) break;
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        written += n;
                        stall.Restart();
                        if (sw.ElapsedMilliseconds > 300)
                        {
                            sw.Restart();
                            progress?.Report(0.9 * Math.Clamp((double)written / total, 0, 1));
                        }
                        if (stall.ElapsedMilliseconds > StallSeconds * 1000L)
                            throw new IOException($"下载卡住（{StallSeconds} 秒没有任何数据）");
                    }
                }   // ← dst 在此关闭，下面的改名才动得了这个文件

                if (written >= AssetBytes)
                {
                    // 下完必须把 .part 落成正式文件名：SHA256 校验与解压都按 ZipPath 走，
                    // 少了这一步 .part 就永远是「100% 却在校验时报 FileNotFound」。
                    // ⚠ 改名失败**绝不换源**（那会把刚下完的 938MB 白白重下一遍）——
                    //   原地等一等重试几次即可（多半是杀软/资源管理器扫到了这个文件）。
                    for (var retry = 1; ; retry++)
                    {
                        try { PromotePart(part); break; }
                        catch (IOException) when (retry < 6)
                        {
                            stage?.Invoke($"包已下完，落名被占用，{retry} 秒后重试…");
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                        }
                    }
                    progress?.Report(0.9);
                    return;
                }

                // 少于预期（连接中断、流正常结束）→ 同一个源续传重试；连续 5 次还这样就当它不行，换下一个
                if (attempt % 5 == 0) urlIdx++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastErr = new IOException($"连接超时（{ConnectSeconds} 秒没拿到响应）");
                var bad = SourceName(urlIdx, urls);
                urlIdx++;
                if (urlIdx < urls.Length)
                    stage?.Invoke($"{bad} 连接超时 → 换 {SourceName(urlIdx, urls)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastErr = ex;
                var bad = SourceName(urlIdx, urls);
                urlIdx++;
                if (urlIdx < urls.Length)
                    stage?.Invoke($"{bad} 不可用（{ex.Message.Trim()}）→ 换 {SourceName(urlIdx, urls)}");
            }
        }

        throw new IOException("所有下载源都失败：" + (lastErr?.Message.Trim() ?? "未知错误"), lastErr);
    }

    /// <summary>连接阶段（拿到响应头）的超时；正文下载不受它约束，由无数据看门狗管。</summary>
    private const int ConnectSeconds = 20;

    /// <summary>无数据看门狗：这么久没收到任何字节就判该源卡住并换源（hosts 黑洞的典型表现）。</summary>
    private const int StallSeconds = 15;

    /// <summary>总尝试次数上限（换源 + 断点续传重试），防死循环。</summary>
    private const int MaxDownloadAttempts = 40;

    /// <summary>整个源列表最多重复几轮（镜像抽风是常态，等一轮回来往往就好了）。</summary>
    private const int MaxSourceRounds = 3;

    /// <summary>源的显示名：0 号是直连，其余用主机名。</summary>
    private static string SourceName(int idx, string[] urls) =>
        idx == 0 ? "直连 GitHub" : new Uri(urls[idx]).Host;

    /// <summary>
    /// 域名是否被解析到回环地址（127.0.0.1 / ::1）—— hosts 黑洞的典型特征。
    /// 这种源连得上但永远没有数据，连接前直接跳过，省掉整个看门狗等待。
    /// </summary>
    private static bool IsBlackholed(string url)
    {
        try
        {
            var ips = System.Net.Dns.GetHostAddresses(new Uri(url).Host);
            return ips.Length > 0 && Array.TrueForAll(ips, System.Net.IPAddress.IsLoopback);
        }
        catch { return false; }
    }

    /// <summary>.part → 正式文件名（下载完成的最后一步）。</summary>
    private static void PromotePart(string part)
    {
        try { File.Move(part, ZipPath, overwrite: true); }
        catch (Exception ex)
        {
            throw new IOException($"下载已完成，但缓存文件改名失败（{ex.Message}）。" +
                                  "多半是有程序正占着它，关掉后重试即可（包不会重下）", ex);
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
