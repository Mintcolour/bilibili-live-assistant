using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.App;

// Deterministic incident probe, isolated from live transport and user data.
// Reflection remains here, never in production UI or playback interfaces.
public partial class App
{
    private async Task VerifyPlaybackAsync()
    {
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            File.WriteAllText(Path.Combine(_config.Output, "playback-check.json"), JsonSerializer.Serialize(checks));
            if (!passed) throw new InvalidOperationException("Playback probe failed: " + name);
        }
        static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
        static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
        static void Process(object region) => region.GetType().GetMethod("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(region, null);
        static LiveEvent Event(LiveEventType type, int id) => new(type, "隔离验证" + id, "编号 " + id, "#FFFFFF", id)
            { HistoryFields = new(GiftId: 1, GiftName: "礼物", Quantity: 1, CoinType: "gold", TotalCoin: 100) };
        var card = new OverlayRegionSettings { ModeVersion = 1, Mode = OverlayDisplayMode.Cards, Width = 440, Height = 520,
            CardHeight = 100, MaxVisible = 3, MergeSeconds = 0, UpdateIntervalMs = 50, DurationSeconds = 20, GuardDurationSeconds = 30 };
        await ViewModel.SaveAsync(ViewModel.Settings with { Display = ViewModel.Settings.Display with
            { GiftRegion = card, SuperChatRegion = card }, Dashboard = ViewModel.Settings.Dashboard with { ShowAvatars = false } });
        foreach (var kind in new[] { OverlayRegionKind.Gift, OverlayRegionKind.SuperChat })
        {
            using var region = new StationaryOverlayWindow(ViewModel, kind);
            region.ShowRegion();
            var type = kind == OverlayRegionKind.SuperChat ? LiveEventType.SuperChat : LiveEventType.Gift;
            region.EnqueueBatch([Event(type, 1), Event(type, 2), Event(type, 3)]); Process(region);
            var views = (IDictionary)Field(region, "_views");
            var survivors = new[] { views[1]!, views[2]! };
            var bitmaps = survivors.Select(v => v.GetType().GetField("Image")!.GetValue(v)).ToArray();
            var timed = (Array)Field(region, "_timed");
            var expires = new[] { Property(timed.GetValue(1)!, "Expires"), Property(timed.GetValue(2)!, "Expires") };
            var presentations = (long)Field(region, "_presentations");
            region.EnqueueBatch([Event(type, 4)]); Process(region);
            Check(kind + "-enqueue-does-not-repaint", presentations == (long)Field(region, "_presentations"));
            timed.GetValue(0)!.GetType().GetProperty("Expires")!.SetValue(timed.GetValue(0), DateTimeOffset.UtcNow.AddSeconds(-1));
            Process(region);
            var ordered = views.Values.Cast<object>().OrderBy(v => ((Rectangle)Property(v, "Bounds")).Y)
                .Select(v => ((LiveEvent)Property(v, "SourceValue")).Uid).ToArray();
            Check(kind + "-visual-order-after-expiry", ordered.SequenceEqual(new long[] { 2, 3, 4 }));
            Check(kind + "-survivors-reuse-images", survivors.Select((v, i) => ReferenceEquals(v, views[i + 1])
                && ReferenceEquals(bitmaps[i], v.GetType().GetField("Image")!.GetValue(v))).All(v => v));
            Check(kind + "-expiry-not-restarted", expires.Select((e, i) => Equals(e, Property(timed.GetValue(i + 1)!, "Expires"))).All(v => v));
            region.HideRegion();
        }
        var now = DateTimeOffset.UtcNow; var queue = new TimedOverlayQueue();
        queue.Add(Event(LiveEventType.Gift, 1), now, 0); queue.Add(Event(LiveEventType.Guard, 2), now, 3); queue.Add(Event(LiveEventType.Guard, 3), now, 3);
        Check("timed-guard-priority-and-fifo", new[] { queue.Take(now, false)!.Uid, queue.Take(now, false)!.Uid, queue.Take(now, false)!.Uid }.SequenceEqual(new long[] { 2, 3, 1 }));
        for (var i = 1; i <= 65; i++) queue.Add(Event(LiveEventType.SuperChat, i), now, 0);
        Check("timed-sc-capacity-preserves-admitted", queue.Count == 64 && queue.Omitted == 1 && queue.Take(now, true)!.Uid == 1);
        queue.Clear(); queue.Add(Event(LiveEventType.Gift, 1), now, 0); queue.Add(Event(LiveEventType.Guard, 2), now, 0);
        queue.Expire(now.AddSeconds(61)); Check("timed-independent-timeouts", queue.Count == 1 && queue.Take(now.AddSeconds(61), false)!.Uid == 2);

        await ViewModel.SaveAsync(ViewModel.Settings with { Display = ViewModel.Settings.Display with
            { GiftRegion = card with { Mode = OverlayDisplayMode.Scrolling, MaxVisible = 1, MergeSeconds = 3 },
              SuperChatRegion = card with { Mode = OverlayDisplayMode.Scrolling, MaxVisible = 1 } } });
        using var clock = new OverlayFrameClock();
        using (var region = new FixedOverlayWindow(ViewModel, OverlayRegionKind.Gift, clock))
        {
            region.ShowRegion(); region.Enqueue(Event(LiveEventType.Guard, 1)); region.Enqueue(Event(LiveEventType.Guard, 1));
            var waiting = (IList)Field(region, "_waiting");
            Check("scrolling-guards-not-merged", waiting.Count == 2);
            Check("scrolling-guard-no-merge-delay", (long)Property(waiting[0]!, "ReadyAt") - (long)Property(waiting[0]!, "CreatedAt") < Stopwatch.Frequency);
            for (var i = 2; i <= 65; i++) region.Enqueue(Event(LiveEventType.Guard, i));
            for (var i = 100; i <= 164; i++) region.Enqueue(Event(LiveEventType.Gift, i));
            Check("scrolling-separate-capacity", waiting.Cast<object>().Count(p => ((LiveEvent)Property(p, "Event")).Type == LiveEventType.Guard) == 64
                && waiting.Cast<object>().Count(p => ((LiveEvent)Property(p, "Event")).Type == LiveEventType.Gift) == 64);
            // Back the read-only timestamps only inside this isolated probe.
            foreach (var p in waiting.Cast<object>()) p.GetType().GetField("<ReadyAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(p, 0L);
            Process(region);
            var sprites = (IList)Field(region, "_sprites");
            Check("scrolling-guard-fifo-before-gifts", ((LiveEvent)Property(sprites[0]!, "Event")).Uid == 1);
            region.HideRegion();
        }
        foreach (var type in new[] { LiveEventType.Gift, LiveEventType.SuperChat })
        {
            using var region = new FixedOverlayWindow(ViewModel, type == LiveEventType.Gift ? OverlayRegionKind.Gift : OverlayRegionKind.SuperChat, clock);
            region.ShowRegion(); region.Enqueue(Event(type, 10)); region.Enqueue(Event(type, 11));
            var waiting = (IList)Field(region, "_waiting");
            foreach (var p in waiting.Cast<object>()) p.GetType().GetField("<ReadyAt>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(p, 0L);
            Process(region);
            var sprites = (IList)Field(region, "_sprites");
            Check("scrolling-" + type + "-fifo", ((LiveEvent)Property(sprites[0]!, "Event")).Uid == 10);
            region.HideRegion();
        }
        VerifyRaster(Check);
    }

    private void VerifyRaster(Action<string, bool> check)
    {
        using var fixture = new Bitmap(640, 48, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(fixture))
        using (var font = new Font("Microsoft YaHei UI", 24, FontStyle.Bold))
        using (var shadow = new SolidBrush(Color.FromArgb(170, Color.Black)))
        using (var text = new SolidBrush(Color.FromArgb(230, Color.White)))
        { g.Clear(Color.Transparent); g.DrawString("透明阴影 ABC 弹幕 012345", font, shadow, 3, 3); g.DrawString("透明阴影 ABC 弹幕 012345", font, text, 1, 1); }
        var clips = Enumerable.Range(0, 6).Select(i => new Rectangle(i % 2 * 31, i * 64, 1200 - i % 3 * 23, 51)).ToArray();
        var union = clips.Aggregate(Rectangle.Union);
        Bitmap Render(bool split, float offset)
        {
            var bitmap = new Bitmap(1280, 400, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent); graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half; graphics.CompositingQuality = CompositingQuality.HighSpeed;
            void Draw(Rectangle clip)
            {
                for (var row = 0; row < 6; row++) foreach (var x in new[] { -310.25f + offset, 280.5f + offset, 890.75f + offset })
                    if (clip.IntersectsWith(new Rectangle((int)Math.Floor(x) - 1, row * 64, fixture.Width + 3, 50)))
                        OverlayBitmapBlitter.Draw(graphics, fixture, x, row * 64 + 1, union);
            }
            if (split) foreach (var clip in clips) { graphics.SetClip(clip); Draw(clip); }
            else { using var region = new Region(Rectangle.Empty); foreach (var clip in clips) region.Union(clip); graphics.SetClip(region, CombineMode.Replace); Draw(union); }
            return bitmap;
        }
        static byte[] Pixels(Bitmap bitmap)
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { var bytes = new byte[bitmap.Width * bitmap.Height * 4]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length); return bytes; }
            finally { bitmap.UnlockBits(data); }
        }
        foreach (var offset in new[] { 0f, .125f, .5f, .875f, 100f })
        {
            using var old = Render(false, offset); using var current = Render(true, offset);
            var a = Pixels(old); var b = Pixels(current);
            if (!a.SequenceEqual(b))
            {
                old.Save(Path.Combine(_config.Output, "raster-old.png")); current.Save(Path.Combine(_config.Output, "raster-new.png"));
                var differences = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
                File.WriteAllText(Path.Combine(_config.Output, "raster-diff.json"), JsonSerializer.Serialize(new
                { bytes = differences.Length, maxDelta = differences.Max(i => Math.Abs(a[i] - b[i])), first = differences.Take(20).Select(i => new { x = i / 4 % 1280, y = i / 4 / 1280, channel = i % 4, old = a[i], current = b[i] }) }));
            }
            check("raster-identical-" + offset, a.SequenceEqual(b));
        }
        var timings = new List<object>();
        // Alternating batches reduce order bias; fixture only, not live CPU acceptance.
        for (var batch = 0; batch < 6; batch++) foreach (var split in batch % 2 == 0 ? new[] { false, true } : new[] { true, false })
        { var watch = Stopwatch.StartNew(); for (var i = 0; i < 40; i++) { using var image = Render(split, i * .125f); } timings.Add(new { batch, split, frames = 40, ms = watch.Elapsed.TotalMilliseconds }); }
        File.WriteAllText(Path.Combine(_config.Output, "raster-comparison.json"), JsonSerializer.Serialize(timings));
    }
}
