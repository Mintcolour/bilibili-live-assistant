using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal sealed class CompactQuickEditor : Forms.Form
{
    private bool _saving;
    public CompactQuickEditor(LiveAssistantViewModel vm, Action<CompactOverlaySettings?>? preview = null, Action? replay = null)
    {
        Text = "紧凑面板样式"; Width = 330; Height = 540; TopMost = true; ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent; FormBorderStyle = Forms.FormBorderStyle.FixedToolWindow;
        var draft = vm.Settings.Compact.Normalize();
        var panel = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12) };
        Controls.Add(panel);
        void Refresh() => preview?.Invoke(draft.Normalize());
        void Number(string title, decimal value, decimal min, decimal max, Action<decimal> change)
        {
            panel.Controls.Add(new Forms.Label { Text = title, AutoSize = true });
            // Value validates against the current range immediately. Configure
            // the range first: animation duration defaults to 200, above the
            // control's initial Maximum (100), and emotes can reach 128.
            var input = new Forms.NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 260 };
            input.ValueChanged += (_, _) => { change(input.Value); Refresh(); }; panel.Controls.Add(input);
        }
        Number("文字大小", draft.FontSize, 12, 48, v => draft = draft with { FontSize = (int)v });
        Number("表情大小", draft.EmoteSize, 16, 128, v => draft = draft with { EmoteSize = (int)v });
        Number("内容不透明度 %", (decimal)(draft.Opacity * 100), 10, 100, v => draft = draft with { Opacity = (double)v / 100 });
        Number("背景不透明度 %", (decimal)(draft.BackgroundOpacity * 100), 0, 100, v => draft = draft with { BackgroundOpacity = (double)v / 100 });
        Number("列表动画过渡（毫秒）", draft.ListAnimationDurationMs, 100, 500, v => draft = draft with { ListAnimationDurationMs = (int)v });
        foreach (var (title, read, write) in new (string, Func<CompactOverlaySettings, bool>, Func<CompactOverlaySettings, bool, CompactOverlaySettings>)[]
        {
            ("人数／高能榜", s => s.ShowStatus, (s,v) => s with { ShowStatus = v }), ("SC 固定区", s => s.ShowSuperChat, (s,v) => s with { ShowSuperChat = v }),
            ("礼物／上舰固定区", s => s.ShowGift, (s,v) => s with { ShowGift = v }), ("混合列表", s => s.ShowList, (s,v) => s with { ShowList = v }),
            ("进房", s => s.ShowEntry, (s,v) => s with { ShowEntry = v }), ("关注", s => s.ShowFollow, (s,v) => s with { ShowFollow = v }),
            ("混合列表出入动画", s => s.ListAnimationEnabled, (s,v) => s with { ListAnimationEnabled = v }),
        })
        { var check = new Forms.CheckBox { Text = title, Checked = read(draft), AutoSize = true }; check.CheckedChanged += (_, _) => { draft = write(draft, check.Checked); Refresh(); }; panel.Controls.Add(check); }
        if (replay is not null)
        {
            var play = new Forms.Button { Text = "播放出入示例", AutoSize = true };
            play.Click += (_, _) => replay(); panel.Controls.Add(play);
        }
        var save = new Forms.Button { Text = "保存并应用", AutoSize = true }; panel.Controls.Add(save);
        FormClosing += (_, args) => { if (_saving) args.Cancel = true; };
        save.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; panel.Enabled = false;
            var saved = draft;
            try
            {
                await vm.UpdateCompactAsync(current => saved with { X = current.X, Y = current.Y, Width = current.Width, Height = current.Height });
                _saving = false;
                if (!IsDisposed && !Disposing) { DialogResult = Forms.DialogResult.OK; Close(); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { RuntimeErrorLog.Record("compact-style-save", ex); vm.ReportSettingsError("紧凑面板样式保存失败，请重试；错误已记入 runtime-errors.log。"); }
            finally { _saving = false; if (!IsDisposed && !Disposing) panel.Enabled = true; }
        };
        FormClosed += (_, _) => preview?.Invoke(null);
    }
}
