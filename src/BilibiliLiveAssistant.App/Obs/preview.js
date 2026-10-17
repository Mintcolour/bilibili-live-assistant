'use strict';
(() => {
  let base, sequence = 20;
  const label = document.getElementById('bla-preview-status');
  const replay = document.getElementById('bla-replay');
  const toolbar = document.getElementById('bla-workbench'), stage = document.querySelector('.bla-preview-stage');
  const toolbarResize = new ResizeObserver(() => { stage.style.top = toolbar.offsetHeight + 'px'; window.dispatchEvent(new Event('resize')); });
  toolbarResize.observe(toolbar);
  window.addEventListener('pagehide', () => toolbarResize.disconnect());
  const sample = (id, type, body, extra = {}) => ({id,type,name:'示例观众',body,color:'#617ca8',avatar:'sample-avatar.svg',medal:'粉丝牌 12',medalColor:'#3d8599',guard:'舰长',guardLevel:3,
    segments:[{text:body}],...extra});
  let rows = [sample(1,'Danmaku','你好，欢迎来到直播间！'),sample(2,'Danmaku','透明表情示例 ',{segments:[{text:'透明表情示例 '},{text:'[开心]',image:'sample-emote.svg',width:64,height:64}]}),
    sample(3,'SuperChat','这是一条醒目留言，列表保留完整内容。',{price:'¥100',amountMilliYuan:100000,priceTier:3}),
    sample(4,'Gift','星星 × 3',{giftName:'星星',quantity:3,price:'¥15',amountMilliYuan:15000,priceTier:3}),
    sample(5,'Guard','开通舰长',{price:'¥198',amountMilliYuan:198000,priceTier:5}),
    sample(6,'Danmaku','长消息换行示例：调整字体、表情、气泡内边距后，可以在这里观察排版。This is an example message with mixed text. 中文与 English 一起显示。'),
    sample(7,'Follow','关注了主播'),sample(8,'Like','为直播间点赞')];
  function snapshot() {
    const s = base.style || {};
    const decorate = m => ({...m,avatar:s.showAvatars === false ? '' : m.avatar,medal:s.showMedals === false ? '' : m.medal,guard:s.showIdentity === false ? '' : m.guard});
    return {...base,roomId:0,now:Date.now(),rows:rows.map(decorate),
      sc:decorate(sample(100,'SuperChat','固定 SC 示例：感谢陪伴！这是一段用于检查正文行数、字号和换行的长文字。'.repeat(5),{price:'¥100',amountMilliYuan:100000,priceTier:3})),
      gift:decorate(sample(101,'Gift','星星 × 3',{giftName:'星星',quantity:3,price:'¥15',amountMilliYuan:15000,priceTier:3})),
      entry:decorate(sample(102,'Enter','进入直播间',{avatar:s.showEntryAvatars === false ? '' : 'sample-avatar.svg'})),
      follow:decorate(sample(103,'Follow','关注了主播')),status:['1234','1.2 万'],
      ranks:[{rank:1,message:decorate(sample(-1,'Danmaku','',{name:'示例舰长',avatar:''}))},
        {rank:2,message:decorate(sample(-2,'Danmaku','',{name:'示例观众',avatar:'',guard:'',guardLevel:0}))},
        {rank:3,message:decorate(sample(-3,'Danmaku','',{name:'另一位观众',avatar:'',medal:''}))}]};
  }
  window.BlaPreviewState = value => {
    if (!value.config) return value;
    base = value; label.textContent = `${value.width} × ${value.height} · 草稿已更新`; replay.disabled = false; return snapshot();
  };
  replay.disabled = true;
  replay.addEventListener('click', () => { if (!base) return; rows.push(sample(++sequence,'Danmaku','新消息滑入示例 ' + (sequence-20))); rows = rows.slice(-12); window.BlaPreviewRender(snapshot()); });
  document.getElementById('bla-background').addEventListener('click', () => {
    const values = ['checker','dark','light']; document.body.className = values[(values.indexOf(document.body.className)+1)%3];
  });
  window.addEventListener('bla-preview-disconnected', () => { base = undefined; rows = []; replay.disabled = true; label.textContent = '预览已断开，请回到软件重新打开，并只保留一个预览页。'; });
})();
