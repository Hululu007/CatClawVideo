namespace CatClawVideo.Core.Services;

/// <summary>
/// 「当前播放上下文」进程级快照 —— 对齐 TVBox 的 <c>App.getVodInfo()</c>。
///
/// <para><b>为什么需要它</b>：TVBox 的 <c>RemoteServer.normalizeDanmuParams</c> 在处理
/// <c>/proxy?do=danmu</c> 请求时，会把当<b>前片名</b>（<c>vodName</c>）与<b>当前集序号</b>
/// （<c>vodIndex</c>，取自集名里的数字，见 <c>getCurrentEpisodeIndex</c>/<c>extractNumber</c>）
/// 补进请求参数 —— 爬虫（jar）的 danmaku / proxy 处理器靠它判断「现在在看哪部、第几集」。
/// 我们此前没有这层状态，jar 收到的 danmaku 请求缺这两个键。</para>
///
/// <para>写入时机：<c>WatchPage.PlayEpisodeAsync</c>（起播/换集）。单窗口应用，进程级静态即可。</para>
/// </summary>
public static class PlaybackContext
{
    /// <summary>当前片名（空表示未知，此时不注入）。</summary>
    public static string? VodName { get; private set; }

    /// <summary>当前集序号文本（已是「可直接注入 proxy 参数」的形态；null 表示未知）。</summary>
    public static string? VodIndex { get; private set; }

    /// <summary>是否**正在播放**（进入播放页起播置 true，离开播放页置 false）。
    /// <para>用途：后台预热（PreloadSitesAsync）据此**完全停车** —— 预热与播放抢同一座桥，
    /// 预热在飞时播放/扫码的请求排在后面（2026-10-04 用户实测「加了预热以后扫码变得很卡」，
    /// 2026-10-11 又反馈「预热还会让我播放变得卡顿」）。</para></summary>
    public static bool Active { get; private set; }

    /// <summary>起播/换集时写入。</summary>
    /// <param name="vodName">片名（如页面标题）</param>
    /// <param name="episodeIndex">集在列表中的下标（0 基）</param>
    /// <param name="episodeName">集名（优先从中抽数字，对齐 TVBox）</param>
    public static void Set(string? vodName, int episodeIndex, string? episodeName)
    {
        Active = true;
        VodName = string.IsNullOrWhiteSpace(vodName) ? null : vodName.Trim();

        // TVBox：isNumeric(vodIndex) 为假时才用集名抽出的数字，抽不到就用集名本身
        var n = ExtractNumber(episodeName);
        VodIndex = n is { Length: > 0 }
            ? n
            : (episodeIndex >= 0 ? (episodeIndex + 1).ToString() : null);
    }

    /// <summary>离开播放页时清空（避免把上一部片的上下文带过去）。</summary>
    public static void Clear()
    {
        Active = false;
        VodName = null;
        VodIndex = null;
    }

    /// <summary>从集名里抽第一段数字（对齐 TVBox 的 extractNumber）。</summary>
    public static string? ExtractNumber(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i]))
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                return text.Substring(start, i - start);
            }
        }
        return start >= 0 ? text.Substring(start) : null;
    }
}
