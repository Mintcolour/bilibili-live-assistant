const $ = id => document.getElementById(id);
let busy = false;
async function refresh() {
  const state = await chrome.runtime.sendMessage({ type: 'view' });
  if (!$('name').value) $('name').value = state.name || '';
  $('follow').checked = state.follow;
  $('follow').disabled = !state.bound || busy;
  $('sync').disabled = $('disconnect').disabled = !state.bound || busy;
  $('complete').disabled = !state.pending || busy;
  $('pair').disabled = busy;
  $('detail').textContent = `${state.bound ? '已配对' : '未配对'}${state.configurePending ? ' · 跟随设置待确认' : ''}${state.lastSync ? ' · 最近同步 ' + new Date(state.lastSync).toLocaleTimeString() : ''}${state.error ? '\n' + state.error : ''}`;
}
async function action(type, extra = {}) {
  if (busy) return; busy = true; await refresh(); $('status').textContent = '处理中…';
  try { const reply = await chrome.runtime.sendMessage({ type, ...extra }); $('status').textContent = reply.message || '操作完成'; }
  catch { $('status').textContent = '暂不可用，请重新打开扩展重试'; }
  finally { busy = false; await refresh(); }
}
$('pair').addEventListener('click', () => action('pair', { name: $('name').value }));
for (const type of ['complete', 'sync', 'disconnect']) $(type).addEventListener('click', () => action(type));
$('follow').addEventListener('change', () => action('follow', { enabled: $('follow').checked }));
void refresh();
