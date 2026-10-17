using BilibiliLiveAssistant.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BilibiliLiveAssistant.App;

internal static class HotPhraseSamples
{
    internal static readonly HotPhrase[] Items =
    [new("好好好[开心]", 37, "sample-a", new() { Emotes = [new("[开心]", "local:hot-smile", 32, 32)] }),
     new("[开心]", 12, "sample-b", new() { FullEmote = new("[开心]", "local:hot-smile", 32, 32) }),
     new("谢谢陪伴", 9, "sample-c"), new("晚上见", 6, "sample-d"), new("欢迎回来", 3, "sample-e")];
}
internal sealed class HotPhraseSettingsEditor : StackPanel
{
    private readonly ToggleSwitch _enabled = new() { Header = "显示高频气泡" }, _animation = new() { Header = "气泡出入与换位动画" };
    private readonly SettingSlider _font = new("字号", 16, 12, 40, " px"), _emote = new("表情大小", 28, 16, 80, " px"),
        _opacity = new("内容不透明度", 100, 10, 100, "%"), _background = new("背景不透明度", 78, 0, 100, "%"), _duration = new("过渡时长", 200, 100, 500, " 毫秒");
    private HotPhraseSettings _current = new();
    public event Action? Changed;
    public HotPhraseSettingsEditor()
    {
        Spacing = 12;
        foreach (var item in new UIElement[] { _enabled, _font, _emote, _opacity, _background, _animation, _duration,
            new TextBlock { Text = "最近 30 秒 · 重复至少 3 次 · 前 5 项。概览与紧凑模式最多两行；普通独立浮窗可在调整模式中拖动、缩放，右键调整样式。", TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 12 } }) Children.Add(item);
        SettingEvents.Watch(this, () => Changed?.Invoke());
    }
    internal void Load(HotPhraseSettings value)
    { _current = value; _enabled.IsOn = value.Enabled; _font.Value = value.FontSize; _emote.Value = value.EmoteSize; _opacity.Value = value.Opacity * 100; _background.Value = value.BackgroundOpacity * 100; _animation.IsOn = value.AnimationEnabled; _duration.Value = value.AnimationDurationMs; }
    internal HotPhraseSettings Read() => (_current with { Enabled = _enabled.IsOn, FontSize = (int)_font.Value, EmoteSize = (int)_emote.Value,
        Opacity = _opacity.Value / 100, BackgroundOpacity = _background.Value / 100, AnimationEnabled = _animation.IsOn, AnimationDurationMs = (int)_duration.Value }).Normalize();
}
internal sealed class HotPhrasePreview : StackPanel, IDisposable
{
    private readonly HotPhrasePanel _panel;
    private HotPhraseSettings _style = new();
    private int _phase;
    private IReadOnlyList<HotPhrase> Samples => _phase == 1 ? HotPhraseSamples.Items.Reverse().Skip(1).ToArray() : HotPhraseSamples.Items;
    internal HotPhrasePreview(LiveAssistantViewModel vm, Func<bool> visible)
    {
        Spacing = 10; _panel = new(vm, visible, true); Children.Add(_panel);
        var replay = UiFactory.Button("演示出入与换位"); replay.Click += (_, _) => { _phase = (_phase + 1) % 3; _panel.Update(Samples, _style); if (_phase == 0) _panel.Replay(); }; Children.Add(replay);
        Children.Add(new TextBlock { Text = "本地静态示例，不请求网络。修改即时预览，保存后应用。", FontSize = 12, Opacity = .65, TextWrapping = TextWrapping.Wrap });
    }
    internal void Update(HotPhraseSettings style) { _style = style; _panel.Update(Samples, style); }
    internal void SetActive(bool active) => _panel.SetActive(active);
    public void Dispose() => _panel.Dispose();
}
