#if WINDOWS
namespace CatClawVideo.Maui.Pages;

/// <summary>
/// 左上角播放状态浮层（2026-10-18 用户要求）：
/// <code>
/// 源 25.00fps · 1920×1080
/// 出 60.00fps · 2560×1080      ← 仅当开了插帧/超分时出现
/// </code>
/// <para>全部取**真实**数值：源尺寸/帧率来自 mpv 的 video-params 与 container-fps，
/// 输出帧率来自 estimated-vf-fps（插帧生效时它真的会涨），输出尺寸来自 osd-dimensions
/// （超分生效时就是放大后的显示表面）。没出画时给占位，不编数字。</para>
/// <para>显隐与底部控件条一致：WatchPage 在切 ControlBar.IsVisible 的同一处调 SyncStatsOverlay。</para>
/// </summary>
public partial class WatchPage
{
    private IDispatcherTimer? _statsTimer;

    private void SyncStatsOverlay(bool visible)
    {
        try
        {
            StatsOverlay.IsVisible = visible;
            if (!visible) { _statsTimer?.Stop(); return; }
            UpdateStatsOverlay();
            _statsTimer ??= CreateStatsTimer();
            _statsTimer.Start();
        }
        catch { }
    }

    private IDispatcherTimer CreateStatsTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += (_, _) => UpdateStatsOverlay();
        return timer;
    }

    private void UpdateStatsOverlay()
    {
        try
        {
            var stats = Platforms.Windows.Mpv.MpvVideoBackend.Current?.ReadStats();
            if (stats is not { } s)
            {
                StatsOverlayText.Text = "尚未出画…";
                return;
            }

            var interp = Services.InterpolationPrefs.Load() != Services.InterpolationMode.Off;
            var enhance = Services.EnhancePrefs.Load() != Services.EnhanceMode.Off;
            var src = $"源 {s.SrcFps:0.##}fps · {(long)s.SrcW}×{(long)s.SrcH}";

            if (!interp && !enhance)
            {
                StatsOverlayText.Text = src;
                return;
            }

            var outLine = $"出 {s.OutFps:0.##}fps · {(long)s.OutW}×{(long)s.OutH}";
            var tags = (interp ? "插帧" : "") + (interp && enhance ? "+" : "") + (enhance ? "超分" : "");
            StatsOverlayText.Text = src + "\n" + outLine + (tags.Length > 0 ? $"（{tags}）" : "");
        }
        catch { }
    }
}
#endif
