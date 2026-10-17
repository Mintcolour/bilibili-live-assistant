using System.Drawing;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal sealed class CompactQuickEditor : Forms.Form
{
    private bool _saving;
    public CompactQuickEditor(LiveAssistantViewModel vm, Action<CompactOverlaySettings?>? preview = null, Action? replay = null)
    {
        Text = "紧凑面板样式"; Width = 430; Height = 680; MinimumSize = new(400, 420); TopMost = true; ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent; FormBorderStyle = Forms.FormBorderStyle.SizableToolWindow;
        var draft = vm.Settings.Compact.Normalize();
        var panel = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12) };
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 54, Padding = new(12, 8, 12, 8), FlowDirection = Forms.FlowDirection.RightToLeft };
        Controls.Add(panel); Controls.Add(actions);
        panel.Controls.Add(new Forms.Label { Text = "紧凑面板 · 实时预览", AutoSize = true, Margin = new(4, 4, 4, 8) });
        panel.Controls.Add(new Forms.Label { Text = "调整只改变示例，保存后应用；关闭则放弃修改。", Width = 350, Height = 36, Margin = new(4, 0, 4, 8) });
        void Refresh() => preview?.Invoke(draft.Normalize());
        void Number(string title, decimal value, decimal min, decimal max, Action<decimal> change)
        {
            var host = new Forms.Panel { Width = 355, Height = 72, Margin = new(2, 3, 2, 3) };
            var label = new Forms.Label { Text = title, Left = 2, Top = 0, Width = 350, Height = 24 };
            var slider = new Forms.TrackBar { Left = 0, Top = 25, Width = 260, Minimum = (int)min, Maximum = (int)max, Value = (int)Math.Clamp(value, min, max), TickStyle = Forms.TickStyle.None };
            var input = new Forms.NumericUpDown { Left = 270, Top = 28, Width = 80, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max) };
            var synchronizing = false;
            input.ValueChanged += (_, _) => { if (synchronizing) return; synchronizing = true; slider.Value = (int)input.Value; synchronizing = false; change(input.Value); Refresh(); };
            slider.ValueChanged += (_, _) => { if (synchronizing) return; input.Value = slider.Value; };
            host.Controls.Add(label); host.Controls.Add(slider); host.Controls.Add(input); panel.Controls.Add(host);
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
        var save = new Forms.Button { Text = "保存并应用", AutoSize = true }; actions.Controls.Add(save);
        var cancel = new Forms.Button { Text = "取消", AutoSize = true }; cancel.Click += (_, _) => Close(); actions.Controls.Add(cancel);
        OverlayEditorTheme.Apply(this);
        FormClosing += (_, args) => { if (_saving) args.Cancel = true; };
        save.Click += async (_, _) =>
        {
            if (_saving) return;
            _saving = true; panel.Enabled = false; actions.Enabled = false;
            var saved = draft;
            try
            {
                await vm.UpdateCompactAsync(current => saved with { X = current.X, Y = current.Y, Width = current.Width, Height = current.Height });
                _saving = false;
                if (!IsDisposed && !Disposing) { DialogResult = Forms.DialogResult.OK; Close(); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { RuntimeErrorLog.Record("compact-style-save", ex); vm.ReportSettingsError("紧凑面板样式保存失败，请重试；错误已记入 runtime-errors.log。"); }
            finally { _saving = false; if (!IsDisposed && !Disposing) { panel.Enabled = true; actions.Enabled = true; } }
        };
        FormClosed += (_, _) => preview?.Invoke(null);
    }
}
