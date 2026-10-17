using System.Drawing;
using BilibiliLiveAssistant.Core;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

// A modeless editor with a local draft. Only Save writes settings; closing restores the surface.
internal sealed class OverlayQuickEditor : Forms.Form
{
    private OverlayRegionSettings _draft;
    private readonly Action<OverlayRegionSettings?> _preview;
    private readonly Func<OverlayRegionSettings, Task> _save;
    private readonly Forms.FlowLayoutPanel _fields = new() { Dock = Forms.DockStyle.Fill, FlowDirection = Forms.FlowDirection.TopDown,
        WrapContents = false, AutoScroll = true, Padding = new(12) };
    private readonly Forms.Button _apply = new() { Text = "保存并应用", AutoSize = true };
    private bool _loading = true, _saving;
    private readonly Forms.Timer _previewTimer = new() { Interval = 100 };
    public OverlayQuickEditor(OverlayRegionKind kind, OverlayRegionSettings initial, Action<OverlayRegionSettings?> preview,
        Func<OverlayRegionSettings, Task> save)
    {
        _draft = initial; _preview = preview; _save = save;
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _preview(_draft); };
        Text = OverlayRegions.Title(kind) + " · 外观与时间";
        Width = 430; Height = 730; MinimumSize = new(400, 400); AutoScaleMode = Forms.AutoScaleMode.Dpi;
        FormBorderStyle = Forms.FormBorderStyle.SizableToolWindow; StartPosition = Forms.FormStartPosition.CenterScreen;
        TopMost = true; ShowInTaskbar = false; BackColor = Color.FromArgb(30, 34, 43); ForeColor = Color.WhiteSmoke;
        var actions = new Forms.FlowLayoutPanel { Dock = Forms.DockStyle.Bottom, Height = 46, Padding = new(10, 4, 10, 4), FlowDirection = Forms.FlowDirection.RightToLeft };
        var cancel = new Forms.Button { Text = "取消", AutoSize = true }; cancel.Click += (_, _) => Close();
        _apply.Click += async (_, _) => await SaveAsync(); actions.Controls.Add(_apply); actions.Controls.Add(cancel);
        Controls.Add(_fields); Controls.Add(actions);
        Heading("内容");
        Slider("每条内容大小", 50, 200, (int)(initial.ContentScale * 100), "%", v => _draft = _draft with { ContentScale = v / 100d });
        Slider("字号", 12, 72, initial.FontSize, " px", v => _draft = _draft with { FontSize = v });
        Slider("内容整体不透明度", 10, 100, (int)(initial.Opacity * 100), "%", v => _draft = _draft with { Opacity = v / 100d });
        if (kind is OverlayRegionKind.Danmaku or OverlayRegionKind.SuperChat)
            Slider("行内表情大小", 16, 128, initial.EmoteSize, " px", v => _draft = _draft with { EmoteSize = v });
        if (initial.Mode is OverlayDisplayMode.Cards or OverlayDisplayMode.Grid or OverlayDisplayMode.List)
            Slider("每条卡片高度", 100, 400, initial.CardHeight, " px", v => _draft = _draft with { CardHeight = v });
        Heading("背景与阴影");
        Slider("背景不透明度（0 为完全透明）", 0, 100, (int)(initial.BackgroundOpacity * 100), "%", v => _draft = _draft with { BackgroundOpacity = v / 100d, AppearanceVersion = 1 });
        ColorButton("背景颜色", initial.BackgroundColor, v => _draft = _draft with { BackgroundColor = v, AppearanceVersion = 1 });
        var shadow = new Forms.CheckBox { Text = "文字阴影", Checked = initial.Shadow, AutoSize = true, Margin = new(4, 10, 4, 4) };
        shadow.CheckedChanged += (_, _) => Change(() => _draft = _draft with { Shadow = shadow.Checked }); _fields.Controls.Add(shadow);
        Slider("阴影强度", 0, 100, (int)(initial.ShadowOpacity * 100), "%", v => _draft = _draft with { ShadowOpacity = v / 100d });
        Slider("阴影范围", 0, 6, initial.ShadowSize, " px", v => _draft = _draft with { ShadowSize = v });
        Slider("阴影偏移", 0, 6, initial.ShadowOffset, " px", v => _draft = _draft with { ShadowOffset = v });
        ColorButton("阴影颜色", initial.ShadowColor, v => _draft = _draft with { ShadowColor = v });
        Heading("显示时间与间隔");
        if (initial.Mode == OverlayDisplayMode.Cards)
            Heading("固定礼物／SC 按金额计时，请在可视化设置的“紧凑面板 / OBS / 金额停留”调整。", false);
        else if (kind == OverlayRegionKind.Interaction)
        {
            Slider(kind == OverlayRegionKind.Gift ? "普通礼物停留时间" : "每条停留时间", kind == OverlayRegionKind.Interaction ? 1 : 5, 300,
                (int)initial.DurationSeconds, " 秒", v => _draft = _draft with { DurationSeconds = v });
            if (kind == OverlayRegionKind.Interaction) Slider("关注提醒停留时间", 5, 300, (int)initial.FollowDurationSeconds, " 秒", v => _draft = _draft with { FollowDurationSeconds = v });
        }
        else if (initial.Mode == OverlayDisplayMode.Scrolling)
            Slider("滚动速度（决定完整滚出所需时间）", 30, 500, initial.ScrollSpeed, " px/s", v => _draft = _draft with { ScrollSpeed = v });
        if (kind != OverlayRegionKind.Interaction && !(kind == OverlayRegionKind.Status && initial.Mode == OverlayDisplayMode.Scrolling))
            Slider(initial.Mode is OverlayDisplayMode.List or OverlayDisplayMode.Grid or OverlayDisplayMode.Panel ? "内容刷新显示间隔" : "新消息最小显示间隔", 0, 300, (int)(initial.DisplayIntervalSeconds * 10), " 秒", v => _draft = _draft with { DisplayIntervalSeconds = v / 10d }, 10);
        if (kind == OverlayRegionKind.Status && initial.Mode == OverlayDisplayMode.Scrolling)
            Slider("榜单播报间隔", 5, 600, initial.BroadcastIntervalSeconds, " 秒", v => _draft = _draft with { BroadcastIntervalSeconds = v });
        if (initial.Mode is OverlayDisplayMode.List or OverlayDisplayMode.Grid or OverlayDisplayMode.Panel)
            Heading(initial.Mode == OverlayDisplayMode.Panel ? "固定榜单收到新数据时更新，不定时隐藏。" : "列表和网格沿用记录上限，不定时隐藏。", false);
        Heading("调整实时显示在浮窗中；保存后保留，取消恢复原设置。", false);
        OverlayEditorTheme.Apply(this);
        _loading = false;
        FormClosing += (_, args) => { if (_saving) args.Cancel = true; };
    }
    private void Heading(string text, bool section = true) => _fields.Controls.Add(new Forms.Label
    { Text = text, Width = 355, Height = section ? 28 : 40, Margin = new(4, 12, 4, 2), ForeColor = section ? Color.LightSkyBlue : Color.LightGray });
    private void Slider(string title, int min, int max, int value, string unit, Action<int> assign, int divisor = 1)
    {
        var host = new Forms.Panel { Width = 355, Height = 70, Margin = new(2, 2, 2, 2) };
        var label = new Forms.Label { Left = 2, Top = 2, Width = 350, Height = 24 };
        var slider = new Forms.TrackBar { Left = 0, Top = 26, Width = 350, Minimum = min, Maximum = max, TickStyle = Forms.TickStyle.None,
            Value = Math.Clamp(value, min, max), SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10) };
        void Label() => label.Text = $"{title}  {slider.Value / (double)divisor:0.#}{unit}";
        Label(); slider.ValueChanged += (_, _) => { Label(); Change(() => assign(slider.Value)); };
        host.Controls.Add(label); host.Controls.Add(slider); _fields.Controls.Add(host);
    }
    private void ColorButton(string title, string hex, Action<string> assign)
    {
        var color = ColorTranslator.FromHtml(hex);
        var button = new Forms.Button { Text = title + "  " + hex, Width = 355, Height = 30, ForeColor = Color.Black, BackColor = Color.LightGray };
        button.Click += (_, _) =>
        {
            using var picker = new Forms.ColorDialog { Color = color, FullOpen = true };
            if (picker.ShowDialog(this) != Forms.DialogResult.OK) return;
            color = picker.Color; var next = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; button.Text = title + "  " + next;
            Change(() => assign(next));
        };
        _fields.Controls.Add(button);
    }
    private void Change(Action assign) { if (_loading || _saving) return; assign(); _draft = _draft.Normalize(); _previewTimer.Stop(); _previewTimer.Start(); }
    private async Task SaveAsync()
    {
        if (_saving) return; _previewTimer.Stop(); _saving = true; _apply.Enabled = false; _fields.Enabled = false;
        try { await _save(_draft); _saving = false; Close(); }
        catch (Exception ex) { Forms.MessageBox.Show(this, "保存失败：" + ex.Message, Text, Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning); }
        finally { _saving = false; if (!IsDisposed) { _apply.Enabled = true; _fields.Enabled = true; } }
    }
    protected override void Dispose(bool disposing) { if (disposing) _previewTimer.Dispose(); base.Dispose(disposing); }
}
