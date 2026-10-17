'use strict';
// No per-message CSS, no network loads. Changes replace three bounded style sheets.
window.BlaTheme = (() => {
  const panel = document.getElementById('panel');
  function sheet(id) {
    const node = document.createElement('style'); node.id = id; document.head.append(node); return node;
  }
  const defaults = sheet('bla-defaults'), appearance = sheet('bla-appearance'), custom = sheet('bla-custom');
  let theme = {}, lastDefaults = '', lastTheme = '';
  const number = (v, fallback, min, max) => Number.isFinite(v) ? Math.max(min, Math.min(max, v)) : fallback;
  const color = (v, fallback = '#000000') => /^#[0-9a-f]{6}$/i.test(v || '') ? v : fallback;
  const quoted = value => '"' + String(value || 'Microsoft YaHei UI').slice(0, 128).replace(/["\\\n\r\f]/g, c => '\\' + c.codePointAt(0).toString(16) + ' ') + '"';
  function rgba(value, alpha) {
    const hex = color(value); return `rgba(${parseInt(hex.slice(1,3),16)},${parseInt(hex.slice(3,5),16)},${parseInt(hex.slice(5,7),16)},${number(alpha,0,0,1)})`;
  }
  function shadow(fx) {
    const parts = fx.outline ? ['-1px 0 0 #000', '1px 0 0 #000', '0 -1px 0 #000', '0 1px 0 #000'] : [];
    if (fx.shadow) parts.push(`${number(fx.shadowOffset,1,0,20)}px ${number(fx.shadowOffset,1,0,20)}px ${number(fx.shadowSize,2,0,32)}px ${rgba(fx.shadowColor,fx.shadowOpacity)}`);
    return parts.join(',') || 'none';
  }
  function update(state) {
    if (!state?.config) return;
    const c = state.config, s = state.style || {};
    const key = JSON.stringify([c,s]);
    if (key !== lastDefaults) {
      lastDefaults = key;
      let css = `.bla-overlay{--bla-font-family:${quoted(s.fontFamily)};--bla-font-size:${number(c.fontSize,18,12,64)}px;--bla-opacity:${number(c.opacity,1,0,1)};--bla-avatar-size:${number(Math.min(s.avatarSize || 28,(c.fontSize || 18)+10),28,12,96)}px;--bla-emote-size:${number(c.emoteSize,28,16,128)}px;--bla-sc-lines:${number(c.superChatLines,6,1,12)};--bla-panel-background:${rgba(c.backgroundColor,c.backgroundOpacity)};--bla-name-color:${color(s.usernameColor,'#c7d2f0')};--bla-text-shadow:${shadow(s)};}`;
      for (const r of s.regions || []) {
        if (!/^(Danmaku|SuperChat|Gift|Guard|Enter)$/.test(r.type)) continue;
        const types = r.type === 'Danmaku' ? ':is([data-type="Danmaku"],[data-type="Follow"],[data-type="Like"])' : `[data-type="${r.type}"]`;
        css += `.bla-message${types}{--bla-message-color:${color(r.color,'#f1f3f8')};--bla-message-font:${quoted(r.fontFamily)};--bla-message-weight:${r.bold ? 700 : 400};--bla-message-style:${r.italic ? 'italic' : 'normal'};--bla-message-shadow:${shadow(r)};--bla-large-emote-size:${number(r.largeEmoteHeight,100,16,512)}px;}`;
      }
      defaults.textContent = '@layer bla-defaults{' + css + '}';
    }
    applyAppearance();
  }
  function applyAppearance() {
    panel.dataset.template = ['compact','text','bubble','card'].includes(theme.template) ? theme.template : 'compact';
    for (const [attribute, property] of Object.entries({hideAvatar:'showAvatars',hideMedal:'showMedals',hideIdentity:'showIdentity',hideStatus:'showStatus',hideSc:'showSuperChat',hideGift:'showGift',hideList:'showList',hideEntry:'showEntry',hideFollow:'showFollow'})) panel.dataset[attribute] = String(theme[property] === false);
    if (theme.useSharedAppearance !== false) { appearance.textContent = ''; return; }
    const t = theme;
    appearance.textContent = `@layer bla-appearance{.bla-overlay{--bla-font-family:${quoted(t.fontFamily)};--bla-font-size:${number(t.fontSize,18,12,64)}px;--bla-avatar-size:${number(t.avatarSize,28,12,96)}px;--bla-emote-size:${number(t.emoteSize,28,16,128)}px;--bla-message-gap:${number(t.gap,4,0,32)}px;--bla-content-padding:${number(t.padding,6,0,32)}px;--bla-radius:${number(t.radius,10,0,40)}px;--bla-border-width:${number(t.borderWidth,0,0,8)}px;--bla-border-color:${color(t.borderColor,'#6c7894')};--bla-message-background:${rgba(t.backgroundColor,t.backgroundOpacity)};--bla-panel-background:transparent;--bla-name-color:${color(t.nameColor,'#c7d2f0')};--bla-opacity:${number(t.opacity,1,0,1)};}.bla-message{--bla-message-color:${color(t.textColor,'#f1f3f8')};--bla-message-font:var(--bla-font-family);--bla-message-shadow:${t.shadow ? '1px 1px 2px rgba(0,0,0,.8)' : 'none'};}}`;
  }
  function set(value) {
    const key = JSON.stringify(value || {}); if (key === lastTheme) return false;
    lastTheme = key; theme = value || {};
    // textContent prevents style text from becoming HTML. CSP restricts resource URLs to this server.
    custom.textContent = '@layer bla-custom{' + (typeof theme.customCss === 'string' ? theme.customCss : '') + '\n}';
    applyAppearance(); return true;
  }
  return {update,set,get:() => theme};
})();
