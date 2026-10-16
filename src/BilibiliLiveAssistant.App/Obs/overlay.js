'use strict';
(() => {
  const panel = document.getElementById('panel');
  const list = document.getElementById('list');
  const sections = ['sc', 'gift', 'entry', 'follow'];
  const rowNodes = new Map();
  const sectionKeys = new Map();
  let source, state, expiry, statusKey = '', styleKey = '';
  const color = (value, fallback) => /^#[0-9a-f]{6}$/i.test(value || '') ? value : fallback;
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) {
      const image = entry.target;
      if (entry.isIntersecting && image.dataset.image) image.src = image.dataset.image;
      else image.removeAttribute('src');
    }
  }, {root: panel});
  function image(path, className) {
    const element = document.createElement('img');
    element.className = className;
    element.alt = '';
    if (/^image\/[0-9a-f]{64}$/.test(path || '')) { element.dataset.image = path; observer.observe(element); }
    return element;
  }
  function badge(parent, text, background) {
    if (!text) return;
    const element = document.createElement('span'); element.className = 'badge'; element.textContent = text;
    element.style.backgroundColor = color(background, '#47678e'); parent.append(element);
  }
  function message(m) {
    const node = document.createElement('article'); node.className = 'message'; node.dataset.type = m.type;
    node.style.color = color(m.color, '#f1f3f8');
    const fx = m.effects || {};
    node.style.fontFamily = fx.fontFamily || 'Microsoft YaHei UI';
    node.style.fontWeight = fx.bold ? '700' : '400'; node.style.fontStyle = fx.italic ? 'italic' : 'normal';
    const shadows = fx.outline ? ['-1px 0 0 #000','1px 0 0 #000','0 -1px 0 #000','0 1px 0 #000'] : [];
    if (fx.shadow) {
      const hex = color(fx.shadowColor, '#000000');
      const rgba = `rgba(${parseInt(hex.slice(1,3),16)},${parseInt(hex.slice(3,5),16)},${parseInt(hex.slice(5,7),16)},${Math.max(0, Math.min(1, fx.shadowOpacity))})`;
      shadows.push(`${fx.shadowOffset}px ${fx.shadowOffset}px ${fx.shadowSize}px ${rgba}`);
    }
    node.style.textShadow = shadows.length ? shadows.join(',') : 'none';
    if (m.avatar) {
      const wrap = document.createElement('span'); wrap.className = 'avatar-wrap'; wrap.append(image(m.avatar, 'avatar'));
      if (m.frame) wrap.append(image(m.frame, 'avatar-frame')); node.append(wrap);
    }
    const label = {SuperChat:'SC', Guard:'上舰', Gift:'礼物', Follow:'关注', Like:'点赞'}[m.type];
    badge(node, label ? `${label}${m.type === 'SuperChat' && m.price ? ' ' + m.price : ''}` : '', m.color);
    badge(node, m.medal, m.medalColor); badge(node, m.guard, '#458ac1'); if (m.admin) badge(node, '房管', '#358867');
    const name = document.createElement('span'); name.className = 'name'; name.textContent = m.name + ' '; node.append(name);
    const body = document.createElement('span'); body.className = 'body';
    for (const segment of m.segments || []) {
      if (!segment.image) { body.append(document.createTextNode(segment.text)); continue; }
      const emote = image(segment.image, 'emote' + (segment.large ? ' large' : ''));
      emote.alt = segment.text;
      if (segment.width > 0 && segment.height > 0) emote.style.aspectRatio = `${segment.width}/${segment.height}`;
      body.append(emote);
    }
    node.append(body); return node;
  }
  function release(node) { for (const img of node.querySelectorAll('img')) { observer.unobserve(img); img.removeAttribute('src'); } node.remove(); }
  function section(id, item, show, now) {
    const host = document.getElementById(id);
    const active = show && item && (!item.expires || item.expires > now) ? item : null;
    const key = active ? JSON.stringify(active) : '';
    if (sectionKeys.get(id) === key) return;
    for (const node of Array.from(host.children)) release(node);
    if (active) host.append(message(active)); sectionKeys.set(id, key);
  }
  function fit() {
    if (!state) return;
    panel.style.width = state.width + 'px'; panel.style.height = state.height + 'px';
    panel.style.transform = `scale(${Math.min(innerWidth / state.width, innerHeight / state.height)})`;
  }
  function render(next) {
    state = next; if (!next.config) return;
    const c = next.config, s = next.style;
    const sk = JSON.stringify([c, s]);
    if (styleKey !== sk) {
      styleKey = sk; panel.style.fontFamily = s.fontFamily; panel.style.fontSize = c.fontSize + 'px'; panel.style.opacity = c.opacity;
      panel.style.setProperty('--emote', c.emoteSize + 'px'); panel.style.setProperty('--avatar', Math.min(s.avatarSize, c.fontSize + 10) + 'px');
      panel.style.setProperty('--sc-lines', c.superChatLines);
      const hex = color(c.backgroundColor, '#242833');
      panel.style.backgroundColor = `rgba(${parseInt(hex.slice(1,3),16)},${parseInt(hex.slice(3,5),16)},${parseInt(hex.slice(5,7),16)},${c.backgroundOpacity})`;
      panel.style.textShadow = s.shadow ? `1px 1px ${s.shadowSize}px rgba(0,0,0,${s.shadowOpacity})` : 'none'; fit();
    }
    const now = next.now;
    section('sc', next.sc, c.showSuperChat, now); section('gift', next.gift, c.showGift, now);
    section('entry', next.entry, c.showEntry, now); section('follow', next.follow, c.showFollow, now);
    const rows = c.showList ? (next.rows || []).slice(-32) : [];
    const ids = new Set(rows.map(m => m.id));
    for (const [id, entry] of rowNodes) if (!ids.has(id)) { release(entry.node); rowNodes.delete(id); }
    for (const row of rows) {
      const key = JSON.stringify(row); const existing = rowNodes.get(row.id);
      if (existing && existing.key === key) continue;
      const node = message(row);
      if (existing) { existing.node.replaceWith(node); release(existing.node); }
      else list.append(node);
      rowNodes.set(row.id, {key, node});
    }
    const rankKey = JSON.stringify([c.showStatus, next.status, next.ranks]);
    if (rankKey !== statusKey) {
      statusKey = rankKey; const header = document.getElementById('status'); for (const node of Array.from(header.children)) release(node); header.replaceChildren();
      if (c.showStatus) {
        const counts = document.createElement('span'); counts.textContent = `◉ ${next.status?.[0] || '—'}${next.status?.[1] ? '  👁 ' + next.status[1] : ''}`; header.append(counts);
        for (const rank of next.ranks || []) {
          const member = document.createElement('span'); member.className = 'member';
          const msg = rank.message; member.append(document.createTextNode('#' + rank.rank + ' '));
          if (msg.avatar) member.append(image(msg.avatar, 'avatar'));
          badge(member, msg.medal, msg.medalColor); badge(member, msg.guard, '#458ac1');
          const name = document.createElement('span'); name.className = 'name'; name.textContent = msg.name; member.append(name); header.append(member);
        }
      }
    }
    clearTimeout(expiry);
    const expires = sections.map(id => next[id]?.expires).filter(t => t && t > now);
    if (expires.length) expiry = setTimeout(() => render({...state, now: now + Math.max(1, Math.min(...expires) - now + 10)}), Math.max(1, Math.min(...expires) - now + 10));
  }
  function connect() {
    if (source || document.hidden) return;
    source = new EventSource('events');
    source.addEventListener('state', event => { try { render(JSON.parse(event.data)); } catch { /* wait for the next complete state */ } });
  }
  function disconnect() {
    source?.close(); source = undefined; clearTimeout(expiry);
    for (const node of panel.querySelectorAll('img')) { observer.unobserve(node); node.removeAttribute('src'); }
    for (const id of sections) document.getElementById(id).replaceChildren(); list.replaceChildren(); document.getElementById('status').replaceChildren();
    rowNodes.clear(); sectionKeys.clear(); statusKey = ''; state = undefined;
  }
  document.addEventListener('visibilitychange', () => document.hidden ? disconnect() : connect());
  addEventListener('pagehide', disconnect); addEventListener('resize', fit); connect();
})();
