using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal sealed class CompactQuickEditor : Forms.Form
{
    public CompactQuickEditor(LiveAssistantViewModel vm, Action<CompactOverlaySettings?>? preview = null)
    {
        Text = "紧凑面板样式"; Width = 330; Height = 540; TopMost = true; ShowInTaskbar = false;
        StartPosition = Forms.FormStartPosition.CenterParent; FormBorderStyle = Forms.FormBorderStyle.FixedToolWindow;
        var draft = vm.Settings.Compact;
        var panel = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(12) };
        Controls.Add(panel);
        void Refresh() => preview?.Invoke(draft.Normalize());
        void Number(string title, decimal value, decimal min, decimal max, Action<decimal> change)
        {
            panel.Controls.Add(new Forms.Label { Text = title, AutoSize = true });
            var input = new Forms.NumericUpDown { Value = Math.Clamp(value, min, max), Minimum = min, Maximum = max, Width = 260 };
            input.ValueChanged += (_, _) => { change(input.Value); Refresh(); }; panel.Controls.Add(input);
        }
        Number("文字大小", draft.FontSize, 12, 48, v => draft = draft with { FontSize = (int)v });
        Number("表情大小", draft.EmoteSize, 16, 128, v => draft = draft with { EmoteSize = (int)v });
        Number("内容不透明度 %", (decimal)(draft.Opacity * 100), 10, 100, v => draft = draft with { Opacity = (double)v / 100 });
        Number("背景不透明度 %", (decimal)(draft.BackgroundOpacity * 100), 0, 100, v => draft = draft with { BackgroundOpacity = (double)v / 100 });
        foreach (var (title, read, write) in new (string, Func<CompactOverlaySettings, bool>, Func<CompactOverlaySettings, bool, CompactOverlaySettings>)[]
        {
            ("人数／高能榜", s => s.ShowStatus, (s,v) => s with { ShowStatus = v }), ("SC 固定区", s => s.ShowSuperChat, (s,v) => s with { ShowSuperChat = v }),
            ("礼物／上舰固定区", s => s.ShowGift, (s,v) => s with { ShowGift = v }), ("混合列表", s => s.ShowList, (s,v) => s with { ShowList = v }),
            ("进房", s => s.ShowEntry, (s,v) => s with { ShowEntry = v }), ("关注", s => s.ShowFollow, (s,v) => s with { ShowFollow = v }),
        })
        { var check = new Forms.CheckBox { Text = title, Checked = read(draft), AutoSize = true }; check.CheckedChanged += (_, _) => { draft = write(draft, check.Checked); Refresh(); }; panel.Controls.Add(check); }
        var save = new Forms.Button { Text = "保存并应用", AutoSize = true }; panel.Controls.Add(save);
        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            try
            {
                await vm.UpdateCompactAsync(current => draft with { X = current.X, Y = current.Y, Width = current.Width, Height = current.Height });
                DialogResult = Forms.DialogResult.OK; Close();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { vm.ReportSettingsError("紧凑面板样式保存失败，请重试。"); save.Enabled = true; }
        };
        FormClosed += (_, _) => preview?.Invoke(null);
    }
}
