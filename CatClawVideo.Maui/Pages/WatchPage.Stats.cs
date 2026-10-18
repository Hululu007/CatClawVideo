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
    private long _lastStatsLogMs;   // 临时诊断用的节流

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
            // 临时诊断（2026-10-18 自测）：把 mpv 原始数值一起记进 bt.log，用来判定
            // estimated-vf-fps 到底反不反映 lavfi 插帧的实际输出（浮层读数曾与源帧率一样，可疑）。
            if (Environment.TickCount64 - _lastStatsLogMs > 3000)
            {
                _lastStatsLogMs = Environment.TickCount64;
                Maui.Services.BtFileLog.Write(
                    $"[stats] 源={s.SrcW:0}x{s.SrcH:0}@{s.SrcFps:0.##} 出={s.OutW:0}x{s.OutH:0}@{s.OutFps:0.##} " +
                    $"插帧={Services.InterpolationPrefs.Load()} 超分={Services.EnhancePrefs.Load()} 强度={Services.EnhanceStrengthPrefs.Load()} 目标={Services.InterpTargetPrefs.Load()} "
                    + $"实际={Platforms.Windows.Mpv.MpvVideoBackend.Current?.AchievedFps:0.##}fps | {Platforms.Windows.Mpv.MpvVideoBackend.Current?.DescribeFilters()}");
            }

            var src = $"源 {s.SrcFps:0.##}fps · {(long)s.SrcW}×{(long)s.SrcH}";

            // ⚠ 诚实优先（2026-10-18 自测修正）：jav 的 estimated-vf-fps 在 vo=libmpv 这条链上
            //   读不出 lavfi 插帧的实际输出（实测始终等于源帧率），拿它当"出 xx fps"是**假数据**。
            //   同时在 MF 兼容内核下根本不该读 mpv 的数值。所以：
            //   ① 非 mpv 内核 → 明确写出来，不给数；
            //   ② mpv 内核 → 源帧率给真实值（container-fps），插帧显示**目标**并注明档位，
            //      尺寸给真实的显示面（osd-dimensions）。没有一句是编的。
            if (!Platforms.Windows.VideoPlayerViewHandler.ActiveKernelIsMpv)
            {
                StatsOverlayText.Text = src + "\nMF 兼容内核（本条未启用插帧/超分）";
                return;
            }

            if (!interp && !enhance)
            {
                // 没开增强也照样报实际帧率（用户要看的就是这个真实数字）
                var plainFps = Platforms.Windows.Mpv.MpvVideoBackend.Current?.AchievedFps ?? 0;
                StatsOverlayText.Text = plainFps > 0 ? $"{src}\n实际 {plainFps:0.#}fps" : src;
                return;
            }

            // 显示**实际生效**的档位（重源保护会在 4K 上把运动补偿降成平滑运动），
            // 与设置不一致时明确标出来 —— 否则用户会以为插帧没生效、或以为设置没保存。
            var settingMode = Services.InterpolationPrefs.Load();
            var effectiveMode = Platforms.Windows.Mpv.MpvVideoBackend.Current?.EffectiveInterpolation ?? settingMode;
            var parts = new List<string>();
            // ★ 第一项就是**实际上了多少帧**（2026-10-18 用户反馈"显示的是目标、像在骗人"）：
            //   这个数字数的是 mpv 真正送来渲染的新帧，插帧有没有生效看它一眼就知道。
            var achievedFps = Platforms.Windows.Mpv.MpvVideoBackend.Current?.AchievedFps ?? 0;
            if (achievedFps > 0) parts.Add($"实际 {achievedFps:0.#}fps");
            if (interp)
                parts.Add($"插帧 目标{Services.InterpTargetPrefs.Fps(Services.InterpTargetPrefs.Load())}fps（{Services.InterpolationPrefs.Label(effectiveMode)}"
                          + (effectiveMode != settingMode ? "，重源自动降级" : "") + "）");
            if (enhance)
            {
                // 同插帧：显示实际生效档（重源保护会在降采样场景把 Anime4K 降成内建缩放）
                var settingEnh = Services.EnhancePrefs.Load();
                var effEnh = Platforms.Windows.Mpv.MpvVideoBackend.Current?.EffectiveEnhance ?? settingEnh;
                parts.Add($"超分 {Services.EnhancePrefs.Label(effEnh)}"
                          + (effEnh == Services.EnhanceMode.Shaders ? "·" + Services.EnhanceStrengthPrefs.Label(Services.EnhanceStrengthPrefs.Load()) : "")
                          + (effEnh != settingEnh ? "，重源自动降级" : ""));
            }
            parts.Add($"显示面 {(long)s.OutW}×{(long)s.OutH}");
            StatsOverlayText.Text = src + "\n" + string.Join(" · ", parts);
        }
        catch { }
    }
}
#endif
