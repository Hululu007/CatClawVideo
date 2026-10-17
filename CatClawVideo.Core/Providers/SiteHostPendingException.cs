namespace CatClawVideo.Core.Providers;

/// <summary>宿主准入未通过：spider 装得上，但 jar 的壳（FishNative/FishCrypto 那一族）
/// 没跟宿主握上手，homeContent 只回一条占位
/// <c>{"hostAdmission":"pending","list":[{"vod_id":"__host_pending__",…}]}</c>。
/// <para>为什么单独一个异常类型：这类站重试多少次都是同一句占位，5 次 × 8 秒纯属白等
/// （2026-10-17 真机实测：首选站是 🍼┆设置┆中心 时首载要 37 秒）；而且它不该继续占着
/// 「首选站」的位子 —— 上层捕获后直接跳过剩余重试并把首选站退位。</para></summary>
public sealed class SiteHostPendingException(string siteName)
    : Exception($"{siteName}：爬虫组件未获宿主准入（hostAdmission=pending）")
{
    public string SiteName { get; } = siteName;
}

/// <summary>「壳在等宿主准入」的识别。刻意不塞进 SpiderJsonParser：那个类在本仓库有
/// Core 与 Maui 两份同名副本（谁参与编译取决于工程引法），加方法要改两处、还容易只改一份。</summary>
public static class HostPending
{
    /// <summary>判据取两个都会出现的特征串：<c>hostAdmission</c>、<c>__host_pending__</c>。
    /// 只看「list 为空」会把「真的没分类」误判成准入问题，所以必须认特征串。</summary>
    public static bool LooksLike(string? raw) =>
        !string.IsNullOrEmpty(raw)
        && (raw.Contains("\"hostAdmission\"", StringComparison.Ordinal)
            || raw.Contains("__host_pending__", StringComparison.Ordinal));
}
