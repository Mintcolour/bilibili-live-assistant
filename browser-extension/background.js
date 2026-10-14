const HOST = 'cn.bililiveassistant.browser_sync';
const ALARM = 'bili-sync-pending';
const NAMES = ['SESSDATA', 'bili_jct', 'buvid3'];
const TARGETS = ['https://api.bilibili.com/x/web-interface/nav', 'https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo'];
let timer, sending = false, metadataQueue = Promise.resolve();
const boot = chrome.storage.local.get('state').then(async ({ state }) => {
  if (!state) await chrome.storage.local.set({ state: { sourceId: crypto.randomUUID(), revision: 0, dirtyRevision: 0, follow: false, dirty: false } });
});
async function read() { await boot; return (await chrome.storage.local.get('state')).state; }
function update(change) {
  const work = metadataQueue.then(async () => { const state = await read(); change(state); await chrome.storage.local.set({ state }); return state; });
  metadataQueue = work.catch(() => {}); return work;
}
async function native(type, extra = {}) {
  const state = await read();
  const message = { protocolVersion: 1, type, requestId: crypto.randomUUID(), sourceId: state.sourceId,
    token: state.binding?.token || '', bindingGeneration: state.binding?.generation || '', ...extra };
  if (new TextEncoder().encode(JSON.stringify(message)).length > 65536) throw new Error('同步内容超出限制');
  try { return await chrome.runtime.sendNativeMessage(HOST, message); }
  catch { throw new Error('请在助手安装或修复本机连接组件，再点击立即同步'); }
}
async function forgetBinding() {
  clearTimeout(timer); await chrome.alarms.clear(ALARM);
  await update(s => { s.binding = null; s.dirty = false; s.follow = false; s.pending = null; s.fingerprint = ''; s.configurePending = false; });
}
async function checkReply(reply) {
  if (!reply.ok) { if (reply.code === 'unpaired') await forgetBinding(); throw new Error(reply.message || '请重新连接直播助手'); }
  return reply;
}
function applicable(cookie, target) {
  const url = new URL(target), domain = cookie.domain.replace(/^\./, '');
  const domainMatch = cookie.hostOnly ? url.hostname === domain : url.hostname === domain || url.hostname.endsWith('.' + domain);
  const path = cookie.path || '/';
  return domainMatch && (url.pathname === path || url.pathname.startsWith(path.endsWith('/') ? path : path + '/'));
}
async function snapshot() {
  if (!await chrome.permissions.contains({ permissions: ['cookies'], origins: ['https://api.bilibili.com/*', 'https://api.live.bilibili.com/*'] }))
    throw new Error('B站站点权限不可用，请在扩展管理页恢复权限后立即同步');
  const values = {}, expirations = {};
  for (const name of NAMES) {
    // Omitting storeId selects this ordinary extension context's Cookie Store.
    const perTarget = await Promise.all(TARGETS.map(url => chrome.cookies.getAll({ url, name })));
    const candidates = perTarget.flat().filter(c => !c.partitionKey);
    if (new Set(candidates.map(c => c.storeId)).size > 1) throw new Error('浏览器 Cookie 来源冲突，请使用普通配置重新连接');
    const picked = TARGETS.map((target, index) => {
      const matches = perTarget[index].filter(c => !c.partitionKey && applicable(c, target));
      matches.sort((a, b) => b.path.length - a.path.length || b.domain.length - a.domain.length);
      if (matches.length > 1 && matches[0].path.length === matches[1].path.length && matches[0].domain.length === matches[1].domain.length && matches[0].value !== matches[1].value)
        throw new Error('存在冲突的登录状态，请重新登录 B站');
      return matches[0];
    });
    if ((picked[0]?.value || '') !== (picked[1]?.value || '')) throw new Error('账号 Cookie 不适用于两个目标接口，请重新登录 B站');
    const cookie = picked[0];
    const value = cookie?.value || '';
    if (new TextEncoder().encode(value).length > 8192 || /[\x00-\x1f\x7f;]/.test(value)) throw new Error('登录字段格式无效');
    values[name] = value; expirations[name] = cookie?.expirationDate ?? null;
  }
  if (!await chrome.permissions.contains({ permissions: ['cookies'], origins: ['https://api.bilibili.com/*', 'https://api.live.bilibili.com/*'] }))
    throw new Error('B站站点权限已改变，请恢复权限后立即同步');
  return { cookies: { sessdata: values.SESSDATA, biliJct: values.bili_jct, buvid3: values.buvid3 }, expirations };
}
async function digest(value) {
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify(value)));
  return [...new Uint8Array(hash)].map(b => b.toString(16).padStart(2, '0')).join('');
}
async function schedule() {
  const state = await read(); clearTimeout(timer);
  if (!state.binding || state.stopped || (!state.dirty && !state.configurePending)) { await chrome.alarms.clear(ALARM); return; }
  if (!state.follow && !state.configurePending && !state.retryAt) { await chrome.alarms.clear(ALARM); return; }
  const now = Date.now();
  const due = state.retryAt || Math.max(Math.min((state.lastEventAt || now) + 2000, (state.firstDirtyAt || now) + 10000), (state.sentAt || 0) + 5000);
  timer = setTimeout(() => { void run(false); }, Math.max(0, due - now));
  const when = Math.max(now + 30000, due);
  const alarm = await chrome.alarms.get(ALARM);
  if (!alarm || alarm.scheduledTime > when + 1000) await chrome.alarms.create(ALARM, { when });
}
async function failure(text) {
  const state = await update(s => {
    s.error = text; s.failures = (s.failures || 0) + 1;
    const waits = [30000, 120000, 600000];
    s.stopped = s.failures > waits.length;
    s.retryAt = s.stopped ? 0 : Date.now() + waits[s.failures - 1];
  });
  if (state.stopped) await chrome.alarms.clear(ALARM);
  else await schedule();
}
async function run(manual) {
  if (sending) return { ok: false, message: '正在同步，新的变化会合并到下一份状态' };
  sending = true;
  try {
    let state = await read();
    if (!state.binding) return { ok: false, message: '请先连接直播助手' };
    if (!manual && (!state.follow && !state.configurePending && !state.retryAt || state.stopped)) return { ok: true, message: '自动跟随已关闭' };
    if (!manual && state.retryAt > Date.now()) { await schedule(); return { ok: true, message: '等待重试' }; }
    if (state.configurePending) {
      const reply = await checkReply(await native('sync.configure', { follow: state.followRequested }));
      if (!reply.notified) throw new Error('自动跟随设置已保存，但助手尚未确认；请打开助手后重试');
      await update(s => { s.follow = reply.follow; s.configurePending = false; s.error = ''; s.retryAt = 0; s.failures = 0; s.stopped = false; });
      state = await read();
    }
    if (!manual && !state.follow && !state.pending) return { ok: true, message: '自动跟随已关闭' };
    const capturedDirty = state.dirtyRevision;
    // Reserve a revision before reading. Retry an identical pending payload with its original ID.
    state = await update(s => {
      if (s.revision >= Number.MAX_SAFE_INTEGER - 1) throw new Error('同步序号已满，请重新安装并连接扩展');
      s.revision++;
    });
    const attempt = { revision: state.revision, requestId: crypto.randomUUID() };
    const payload = await snapshot();
    const fingerprint = await digest(payload);
    if (fingerprint === state.fingerprint && !state.pending) {
      const reply = manual ? await checkReply(await native('sync.status')) : null;
      await update(s => { if (s.dirtyRevision === capturedDirty) s.dirty = false; s.firstDirtyAt = 0; s.error = ''; s.retryAt = 0; s.failures = 0; });
      return { ok: true, message: reply ? `状态未变化 · ${accountLabel(reply.accountState)}` : '状态未变化' };
    }
    state = await update(s => {
      if (!s.pending || s.pending.fingerprint !== fingerprint) {
        s.pending = { ...attempt, fingerprint };
      }
      s.sentAt = Date.now();
    });
    const reply = await checkReply(await native('session.snapshot', { ...payload, sourceRevision: state.pending.revision,
      requestId: state.pending.requestId, capturedAt: new Date().toISOString(), reason: manual ? 'manual' : 'cookies_changed' }));
    await update(s => {
      s.fingerprint = fingerprint; s.lastSync = Date.now();
      if (reply.notified) { s.pending = null; if (s.dirtyRevision === capturedDirty) s.dirty = false; s.firstDirtyAt = 0; s.failures = 0; s.retryAt = 0; s.stopped = false; s.error = ''; }
    });
    if (!reply.notified) await failure('已保存本机状态，助手尚未确认；打开助手或刷新本机状态');
    return { ok: true, message: `${reply.message} · ${accountLabel(reply.accountState)}` };
  } catch (error) {
    await failure(error.message || '同步暂不可用，请手动重试'); return { ok: false, message: error.message || '同步暂不可用' };
  } finally { sending = false; await schedule(); }
}
function accountLabel(value) {
  return { LoggedIn: '助手已验证登录', Pending: '等待助手验证', SignedOut: '未登录', Expired: '登录已失效', Unavailable: '暂不可验证' }[value] || '本机状态已保存';
}
async function markDirty() {
  await update(s => { s.dirty = true; s.dirtyRevision++; const now = Date.now(); s.firstDirtyAt ||= now; s.lastEventAt = now; });
  await schedule();
}
chrome.cookies.onChanged.addListener(({ cookie }) => {
  if (NAMES.includes(cookie.name) && !cookie.partitionKey && (cookie.domain === 'bilibili.com' || cookie.domain.endsWith('.bilibili.com')))
    void read().then(s => { if (s.binding && s.follow) return markDirty(); }).catch(() => {});
});
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === ALARM) void run(false); });
chrome.runtime.onStartup.addListener(() => { void read().then(s => s.binding && s.follow ? markDirty() : schedule()).catch(() => {}); });
chrome.runtime.onInstalled.addListener(() => { void schedule(); });
chrome.runtime.onMessage.addListener((request, sender, respond) => {
  if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('popup.html')) return false;
  void action(request).then(respond).catch(() => respond({ ok: false, message: '操作未完成，请稍后重试' }));
  return true;
});
async function action(request) {
  if (request.type === 'view') {
    await schedule(); const s = await read();
    return { ok: true, bound: !!s.binding, follow: s.follow, pending: !!s.pairRequestId, configurePending: !!s.configurePending,
      name: s.name || '', lastSync: s.lastSync, error: s.error || '', stopped: s.stopped };
  }
  if (sending) return { ok: false, message: '正在同步，请等待本次操作结束再修改连接' };
  if (request.type === 'pair') {
    if ((await read()).binding) return { ok: false, message: '请先断开已有绑定，再连接新来源' };
    const name = String(request.name || '').trim().slice(0, 60);
    if (!name) return { ok: false, message: '请为这个浏览器配置命名' };
    const id = crypto.randomUUID(), browser = navigator.userAgent.includes('Edg/') ? 'Edge' : 'Chrome';
    const reply = await checkReply(await native('pair.begin', { token: '', requestId: id, name, browser }));
    await update(s => { s.pairRequestId = id; s.name = name; }); return { ok: true, message: reply.message };
  }
  if (request.type === 'complete') {
    const s = await read();
    if (!s.pairRequestId) return { ok: false, message: '请先连接直播助手' };
    const reply = await checkReply(await native('pair.complete', { requestId: s.pairRequestId, token: '' }));
    await update(s => { s.binding = { token: reply.token, generation: reply.bindingGeneration }; s.revision = Math.max(s.revision, reply.sourceRevision);
      s.follow = reply.follow; s.fingerprint = ''; s.pending = null; s.pairRequestId = ''; s.stopped = false; s.failures = 0; s.retryAt = 0; });
    return await run(true);
  }
  if (request.type === 'sync') { await update(s => { s.stopped = false; s.failures = 0; s.retryAt = 0; }); return await run(true); }
  if (request.type === 'follow') {
    await update(s => { s.followRequested = request.enabled === true; s.configurePending = true; s.stopped = false; s.failures = 0; s.retryAt = 0; });
    return await run(true);
  }
  if (request.type === 'disconnect') {
    sending = true;
    try { const reply = await checkReply(await native('sync.disconnect')); await forgetBinding(); return { ok: true, message: reply.message }; }
    finally { sending = false; await schedule(); }
  }
  return { ok: false, message: '不支持的操作' };
}
