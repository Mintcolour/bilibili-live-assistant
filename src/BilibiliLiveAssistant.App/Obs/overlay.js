'use strict';
(() => {
  const panel = document.getElementById('panel');
  const list = document.getElementById('list');
  const sections = ['sc', 'gift', 'entry', 'follow'];
  const preview = document.documentElement.dataset.preview === 'true';
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
    if (/^image\/[0-9a-f]{64}$/.test(path || '') || preview && /^sample-(avatar|emote)\.svg$/.test(path || '')) { element.dataset.image = path; observer.observe(element); }
    return element;
  }
  function badge(parent, text, background, className = '') {
    if (!text) return;
    const element = document.createElement('span'); element.className = 'badge ' + className; element.textContent = text;
    element.style.setProperty('--bla-event-badge', color(background, '#47678e')); parent.append(element); return element;
  }
  function message(m) {
    const node = document.createElement('article'); node.className = 'message bla-message'; node.dataset.type = m.type;
    node.dataset.guardLevel = String(m.guardLevel || 0); node.dataset.priceTier = String(m.priceTier || 0);
    node.dataset.amountKnown = String(m.amountMilliYuan != null);
    if (m.amountMilliYuan != null) node.dataset.amountMilliYuan = String(m.amountMilliYuan);
    const content = document.createElement('div'); content.className = 'bla-content'; node.append(content);
    if (m.avatar) {
      const wrap = document.createElement('span'); wrap.className = 'avatar-wrap'; wrap.append(image(m.avatar, 'avatar'));
      if (m.frame) wrap.append(image(m.frame, 'avatar-frame')); content.append(wrap);
    }
    const label = {SuperChat:'SC', Guard:'上舰', Gift:'礼物', Follow:'关注', Like:'点赞'}[m.type];
    const typeBadge = badge(content, label, m.color, 'bla-type-badge');
    if (m.type === 'SuperChat' && m.price && typeBadge) { const price = document.createElement('span'); price.className = 'bla-price'; price.textContent = ' ' + m.price; typeBadge.append(price); }
    badge(content, m.medal, m.medalColor, 'bla-fan-medal'); badge(content, m.guard, '#458ac1', 'bla-identity bla-guard'); if (m.admin) badge(content, '房管', '#358867', 'bla-identity bla-admin');
    const name = document.createElement('span'); name.className = 'name bla-name'; name.textContent = m.name + ' '; content.append(name);
    const body = document.createElement('span'); body.className = 'body bla-body';
    for (const segment of m.segments || []) {
      if (!segment.image) { body.append(document.createTextNode(segment.text)); continue; }
      const emote = image(segment.image, 'emote' + (segment.large ? ' large' : ''));
      emote.alt = segment.text;
      if (segment.width > 0 && segment.height > 0) emote.style.aspectRatio = `${segment.width}/${segment.height}`;
      body.append(emote);
    }
    // Gift name and quantity remain separate optional metadata; do not duplicate the existing body text.
    if (m.giftName && (m.type === 'Gift' || m.type === 'Guard')) {
      node.dataset.hasGiftDetails = 'true';
      const detail = document.createElement('span'); detail.className = 'bla-gift-details';
      for (const [className, text] of [['bla-gift-name',m.giftName],['bla-quantity',m.quantity == null ? '' : ' ×' + m.quantity],['bla-price',m.price || '']]) {
        const part = document.createElement('span'); part.className = className; part.textContent = text; detail.append(part);
      }
      content.append(detail);
    }
    content.append(body); return node;
  }
  function release(node) { rowResize.unobserve(node); for (const img of node.querySelectorAll('img')) { observer.unobserve(img); img.removeAttribute('src'); } node.remove(); }
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
    if (active) { const node = message(active); host.append(node); rowResize.observe(node); } sectionKeys.set(id, key);
  }
  function fit() {
    if (!state) return;
    const stage = preview ? document.querySelector('.bla-preview-stage') : null;
    panel.style.width = state.width + 'px'; panel.style.height = state.height + 'px';
    panel.style.transform = `scale(${Math.min((stage?.clientWidth || innerWidth) / state.width, (stage?.clientHeight || innerHeight) / state.height)})`;
  }
  function render(next) {
    if (!next.config) { stopListMotion(); return; }
    const before = captureList(), previousRoom = state?.roomId;
    state = next;
    const c = next.config, s = next.style;
    const sk = JSON.stringify([c, s, next.width, next.height]);
    const styleChanged = styleKey !== sk;
    if (styleKey !== sk) {
      styleKey = sk; window.BlaTheme.update(next); fit();
    }
    // A font/image resize or theme redraw must not restart an old snapshot's remaining lifetime.
    // The loopback producer and OBS share this machine's clock.
    const now = Date.now();
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
      rowNodes.set(row.id, {key, node, fadeUntil: existing?.fadeUntil || 0}); rowResize.observe(node);
    }
    const rankKey = JSON.stringify([c.showStatus, next.status, next.ranks]);
    if (rankKey !== statusKey) {
      statusKey = rankKey; const header = document.getElementById('status'); for (const node of Array.from(header.children)) release(node); header.replaceChildren();
      if (c.showStatus) {
        const counts = document.createElement('span'); counts.className = 'bla-counts';
        for (const [className, icon, value] of [['bla-audience','◉',next.status?.[0] || '—'],['bla-watched','👁',next.status?.[1]]]) {
          if (!value) continue;
          const part = document.createElement('span'); part.className = className;
          const mark = document.createElement('span'); mark.className = 'bla-metric-icon'; mark.textContent = icon + ' '; mark.setAttribute('aria-hidden','true');
          const count = document.createElement('span'); count.className = 'bla-metric-value'; count.textContent = value;
          part.append(mark,count,document.createTextNode(' ')); counts.append(part);
        } header.append(counts);
        const ranks = document.createElement('span'); ranks.className = 'bla-ranks';
        const rankIcon = document.createElement('span'); rankIcon.className = 'bla-rank-icon'; rankIcon.textContent = '🏆'; rankIcon.setAttribute('aria-label','高能榜');
        const rankList = document.createElement('span'); rankList.className = 'bla-rank-list';
        for (const rank of (next.ranks || []).slice(0, 3)) {
          const member = document.createElement('span'); member.className = 'member bla-rank-member'; member.dataset.rank = String(rank.rank);
          const msg = rank.message; member.dataset.guardLevel = String(msg.guardLevel || 0);
          const index = document.createElement('span'); index.className = 'bla-rank-index'; index.textContent = '#' + rank.rank + ' '; member.append(index);
          badge(member, msg.medal, msg.medalColor, 'bla-fan-medal'); badge(member, msg.guard, '#458ac1', 'bla-identity');
          const name = document.createElement('span'); name.className = 'name bla-name'; name.textContent = msg.name; member.append(name); rankList.append(member);
        }
        if (rankList.childElementCount) { ranks.append(rankIcon, rankList); header.append(ranks); }
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
    rowResize.observe(list); rowResize.observe(document.getElementById('status'));
    source = new EventSource(preview ? 'preview-events' : 'events');
    source.addEventListener('open', stopListMotion);
    source.addEventListener('error', () => {
      stopListMotion();
      if (preview) { disconnect(); window.BlaTheme.set({}); window.dispatchEvent(new Event('bla-preview-disconnected')); }
    });
    source.addEventListener('state', event => { try { const next = JSON.parse(event.data); render(preview ? window.BlaPreviewState(next) : next); } catch { /* wait for the next complete state */ } });
    source.addEventListener('theme', event => { try { if (window.BlaTheme.set(JSON.parse(event.data))) { stopListMotion(); if (state) render(state); } } catch { /* Keep the last applied theme. */ } });
  }
  function disconnect() {
    stopListMotion(); latestListId = 0;
    source?.close(); source = undefined; clearTimeout(expiry);
    for (const node of panel.querySelectorAll('img')) { observer.unobserve(node); node.removeAttribute('src'); }
    for (const id of sections) document.getElementById(id).replaceChildren(); list.replaceChildren(); document.getElementById('status').replaceChildren();
    rowResize.disconnect(); clearTimeout(resizePending); resizePending = undefined; rowNodes.clear(); sectionKeys.clear(); statusKey = ''; styleKey = ''; state = undefined;
    window.BlaTheme.clear();
  }
  // Event-driven only. Image/font/CSS layout changes are coalesced; there is no frame polling.
  let resizePending;
  const rowSizes = new WeakMap();
  const rowResize = new ResizeObserver(entries => {
    let changed = false;
    for (const entry of entries) {
      const size = `${entry.target.offsetWidth},${entry.target.offsetHeight}`;
      if (rowSizes.has(entry.target) && rowSizes.get(entry.target) !== size) changed = true;
      rowSizes.set(entry.target, size);
    }
    if (!changed || !state || document.hidden || resizePending) return;
    resizePending = setTimeout(() => { resizePending = undefined; if (state && !document.hidden) { stopListMotion(); render(state); } }, 50);
  });
  if (preview) window.BlaPreviewRender = value => render(value);
  document.addEventListener('visibilitychange', () => document.hidden ? disconnect() : connect());
  addEventListener('pagehide', disconnect); addEventListener('resize', () => { stopListMotion(); fit(); }); connect();
})();
