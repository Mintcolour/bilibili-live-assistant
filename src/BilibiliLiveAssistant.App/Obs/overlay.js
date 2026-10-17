'use strict';
(() => {
  const panel = document.getElementById('panel');
  const list = document.getElementById('list');
  const sections = ['sc', 'gift', 'entry', 'follow'];
  const rowNodes = new Map();
  const sectionKeys = new Map();
  const leavingNodes = new Map();
  let listReady = false, latestListId = 0;
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
    node.style.setProperty('--large-emote', (fx.largeEmoteHeight || 100) + 'px');
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
  function cancelMotion(node) { for (const animation of node.getAnimations()) { animation.onfinish = null; animation.cancel(); } }
  function stopListMotion() {
    for (const entry of rowNodes.values()) { cancelMotion(entry.node); entry.fadeUntil = 0; }
    for (const node of leavingNodes.keys()) { cancelMotion(node); release(node); }
    leavingNodes.clear(); listReady = false;
  }
  function listGeometry() {
    const rect = list.getBoundingClientRect();
    return {rect, scale: list.clientWidth > 0 ? rect.width / list.clientWidth : 1};
  }
  function captureList() {
    const geometry = listGeometry(), entries = new Map();
    if (!(geometry.scale > 0)) return {geometry, entries};
    for (const [id, entry] of rowNodes) {
      const rect = entry.node.getBoundingClientRect();
      entries.set(id, {top: (rect.top - geometry.rect.top) / geometry.scale,
        height: rect.height / geometry.scale, width: rect.width / geometry.scale,
        alpha: Number(getComputedStyle(entry.node).opacity), fadeUntil: entry.fadeUntil || 0});
    }
    return {geometry, entries};
  }
  function tween(node, frames, duration) {
    if (duration <= 0) return;
    const animation = node.animate(frames, {duration, easing: 'cubic-bezier(0.215,0.61,0.355,1)'});
    // No fill mode: completed effects release their compositor resources.
    animation.onfinish = () => animation.cancel();
    return animation;
  }
  function animateList(before, removed, enabled, duration, changed) {
    const geometry = listGeometry(), now = performance.now(), height = list.clientHeight;
    const rootShift = geometry.scale > 0 ? (before.geometry.rect.top - geometry.rect.top) / geometry.scale : 0;
    const oldHeight = before.geometry.scale > 0 ? before.geometry.rect.height / before.geometry.scale : 0;
    const animate = enabled && listReady && !document.hidden && geometry.scale > 0 &&
      Math.abs(before.geometry.rect.width - geometry.rect.width) < .5;
    if (!animate) {
      stopListMotion();
      for (const item of removed) release(item.node);
    } else if (changed) {
      // Sample before DOM changes, then retarget straight from the visual
      // position to the latest layout. There is no animation playback queue.
      for (const entry of rowNodes.values()) cancelMotion(entry.node);
      for (const [id, entry] of rowNodes) {
        const rect = entry.node.getBoundingClientRect();
        const top = (rect.top - geometry.rect.top) / geometry.scale;
        const rowHeight = rect.height / geometry.scale, previous = before.entries.get(id);
        const wasVisible = previous && previous.top < oldHeight && previous.top + previous.height > 0;
        const visible = top < height && top + rowHeight > 0;
        if (!visible && !wasVisible) continue;
        const entering = !previous && id > latestListId && visible;
        const delta = previous ? previous.top + rootShift - top : entering ? 14 : 0;
        if (Math.abs(delta) > .1) tween(entry.node, [{transform: `translateY(${delta}px)`}, {transform: 'translateY(0)'}], duration);
        if (entering) entry.fadeUntil = now + duration;
        else entry.fadeUntil = previous?.fadeUntil || 0;
        if (!visible && wasVisible) tween(entry.node, [{opacity: previous.alpha}, {opacity: 0}], duration);
        else if (entry.fadeUntil > now) tween(entry.node, [{opacity: entering ? 0 : previous?.alpha ?? 1}, {opacity: 1}], entry.fadeUntil - now);
      }
      for (const item of removed) {
        cancelMotion(item.node);
        const previous = before.entries.get(item.id);
        if (!previous || previous.top >= oldHeight || previous.top + previous.height <= 0 || previous.width * previous.height * 4 > 4 * 1024 * 1024) { release(item.node); continue; }
        let bytes = previous.width * previous.height * 4;
        for (const info of leavingNodes.values()) bytes += info.bytes;
        while (leavingNodes.size && (leavingNodes.size >= 4 || bytes > 4 * 1024 * 1024)) {
          const [node, info] = leavingNodes.entries().next().value;
          bytes -= info.bytes; cancelMotion(node); leavingNodes.delete(node); release(node);
        }
        const fromTop = previous.top + rootShift;
        Object.assign(item.node.style, {position: 'absolute', top: fromTop + 'px', left: '0', width: previous.width + 'px', height: previous.height + 'px', pointerEvents: 'none'});
        list.append(item.node);
        const destination = Math.min(-14, -previous.height - fromTop - 4);
        const animation = tween(item.node, [{transform: 'translateY(0)', opacity: previous.alpha}, {transform: `translateY(${destination}px)`, opacity: 0}], duration);
        leavingNodes.set(item.node, {bytes: previous.width * previous.height * 4});
        animation.onfinish = () => { animation.cancel(); leavingNodes.delete(item.node); release(item.node); };
      }
    }
    latestListId = Math.max(0, ...rowNodes.keys()); listReady = !!state?.config?.showList && height > 0;
  }
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
    if (!next.config) { stopListMotion(); return; }
    const before = captureList(), previousRoom = state?.roomId;
    state = next;
    const c = next.config, s = next.style;
    const sk = JSON.stringify([c, s]);
    const styleChanged = styleKey !== sk;
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
    const removed = []; let listChanged = false;
    for (const [id, entry] of rowNodes) if (!ids.has(id)) { entry.node.remove(); removed.push({id, node: entry.node}); rowNodes.delete(id); listChanged = true; }
    for (const row of rows) {
      const key = JSON.stringify(row); const existing = rowNodes.get(row.id);
      if (existing && existing.key === key) continue;
      listChanged = true;
      const node = message(row);
      if (existing) { existing.node.replaceWith(node); cancelMotion(existing.node); release(existing.node); }
      else list.append(node);
      rowNodes.set(row.id, {key, node, fadeUntil: existing?.fadeUntil || 0});
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
    const currentBounds = list.getBoundingClientRect();
    const geometryChanged = Math.abs(before.geometry.rect.height - currentBounds.height) >= .5 || Math.abs(before.geometry.rect.top - currentBounds.top) >= .5;
    animateList(before, removed, c.listAnimationEnabled !== false && !styleChanged && previousRoom === next.roomId,
      Math.max(100, Math.min(500, c.listAnimationDurationMs || 200)), listChanged || geometryChanged);
    clearTimeout(expiry);
    const expires = sections.map(id => next[id]?.expires).filter(t => t && t > now);
    if (expires.length) expiry = setTimeout(() => render({...state, now: now + Math.max(1, Math.min(...expires) - now + 10)}), Math.max(1, Math.min(...expires) - now + 10));
  }
  function connect() {
    if (source || document.hidden) return;
    source = new EventSource('events');
    source.addEventListener('open', stopListMotion);
    source.addEventListener('error', stopListMotion);
    source.addEventListener('state', event => { try { render(JSON.parse(event.data)); } catch { /* wait for the next complete state */ } });
  }
  function disconnect() {
    stopListMotion(); latestListId = 0;
    source?.close(); source = undefined; clearTimeout(expiry);
    for (const node of panel.querySelectorAll('img')) { observer.unobserve(node); node.removeAttribute('src'); }
    for (const id of sections) document.getElementById(id).replaceChildren(); list.replaceChildren(); document.getElementById('status').replaceChildren();
    rowNodes.clear(); sectionKeys.clear(); statusKey = ''; state = undefined;
  }
  document.addEventListener('visibilitychange', () => document.hidden ? disconnect() : connect());
  addEventListener('pagehide', disconnect); addEventListener('resize', () => { stopListMotion(); fit(); }); connect();
})();
