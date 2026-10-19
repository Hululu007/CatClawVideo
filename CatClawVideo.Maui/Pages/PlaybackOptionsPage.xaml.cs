namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 自绘「音画设置」面板（2026-10-19 用户要求"美化一下"）。
/// <para>为什么不再用 <c>DisplayActionSheetAsync</c>：那是**系统原生**灰列表，无法着色/分组/加当前值，
/// 而且原来 15 项平铺在一列里很难按 —— 本面板按 字幕/音轨/画面/弹幕/其他 分组，
/// 字幕偏移做成「提前/归零/延后」三个按钮（一次点击即生效，原本要在一长串里找）。</para>
/// <para>用法：<c>await PlaybackOptionsPage.ShowAsync(...)</c> 返回所选动作的 key（取消返回 null）。</para>
/// </summary>
public partial class PlaybackOptionsPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _tcs = new();

    public PlaybackOptionsPage(string subtitleSummary, bool locked)
    {
        InitializeComponent();
        SubtitleSummary.Text = subtitleSummary;
        LockButton.Text = locked ? "解除锁定" : "锁定画面";
    }

    /// <summary>弹出面板并等用户选择；取消/返回 → null。</summary>
    public static async Task<string?> ShowAsync(Page host, string subtitleSummary, bool locked)
    {
        var page = new PlaybackOptionsPage(subtitleSummary, locked);
        await host.Navigation.PushModalAsync(page, animated: true);
        return await page._tcs.Task;
    }

    private async Task CloseAsync(string? action)
    {
        _tcs.TrySetResult(action);
        try { await Navigation.PopModalAsync(animated: true); } catch { }
    }

    private void OnCancelClicked(object? sender, EventArgs e) => _ = CloseAsync(null);
    private void OnPickSubtitle(object? sender, EventArgs e) => _ = CloseAsync("pick-subtitle");
    private void OnSubDelay(object? sender, EventArgs e) => _ = CloseAsync("sub-delay");
    private void OnSubAdvance(object? sender, EventArgs e) => _ = CloseAsync("sub-advance");
    private void OnSubReset(object? sender, EventArgs e) => _ = CloseAsync("sub-reset");
    private void OnSubOff(object? sender, EventArgs e) => _ = CloseAsync("sub-off");
    private void OnAudioTrack(object? sender, EventArgs e) => _ = CloseAsync("audio-track");
    private void OnEmbeddedSub(object? sender, EventArgs e) => _ = CloseAsync("embedded-sub");
    private void OnAspect(object? sender, EventArgs e) => _ = CloseAsync("aspect");
    private void OnAbLoop(object? sender, EventArgs e) => _ = CloseAsync("ab-loop");
    private void OnOrientation(object? sender, EventArgs e) => _ = CloseAsync("orientation");
    private void OnDanmaku(object? sender, EventArgs e) => _ = CloseAsync("danmaku");
    private void OnCast(object? sender, EventArgs e) => _ = CloseAsync("cast");
    private void OnExternalPlayer(object? sender, EventArgs e) => _ = CloseAsync("external-player");
    private void OnLock(object? sender, EventArgs e) => _ = CloseAsync("lock");
}
