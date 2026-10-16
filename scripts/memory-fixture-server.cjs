// Test-only loopback transport. No proxying or production whitelist changes.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = fs.realpathSync(process.argv[2]);
const state = path.resolve(process.argv[3]);
if (fs.existsSync(state)) throw new Error('State already exists');
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8').replace(/^\uFEFF/, ''));
let requests = 0, cancelled = 0, sentBytes = 0;
const server = http.createServer((request, response) => {
  const match = /^\/image\/(\d+)(?:\?size=(64|128|256))?$/.exec(request.url);
  if (!match) { response.writeHead(404).end(); return; }
  const entry = manifest.entries[Number(match[1]) % manifest.entries.length];
  let file = path.join(root, entry.file);
  if (match[2] && fs.existsSync(file + '.thumb' + match[2] + '.png')) file += '.thumb' + match[2] + '.png';
  const number = ++requests;
  if (number % 101 === 0) { response.writeHead(503).end(); return; }
  fs.readFile(file, (error, data) => {
    if (error || response.destroyed) { if (!response.destroyed) response.writeHead(500).end(); return; }
    if (number % 4 !== 0) response.setHeader('Content-Length', data.length);
    response.setHeader('Content-Type', file.endsWith('.png') ? 'image/png' : file.endsWith('.jpg') ? 'image/jpeg' : file.endsWith('.gif') ? 'image/gif' : 'image/webp');
    let offset = 0, timer;
    response.on('close', () => { clearTimeout(timer); if (offset < data.length) cancelled++; });
    function send() {
      if (response.destroyed) return;
      const end = Math.min(offset + 16384, data.length);
      const ready = response.write(data.subarray(offset, end));
      sentBytes += end - offset; offset = end;
      if (offset === data.length) { response.end(); return; }
      if (ready) timer = setTimeout(send, 2);
      else response.once('drain', () => { timer = setTimeout(send, 2); });
    }
    send();
  });
});
server.listen(0, '127.0.0.1', () => {
  fs.writeFileSync(state, JSON.stringify({pid:process.pid,port:server.address().port,root,entries:manifest.entries.length}));
  console.log(JSON.stringify({pid:process.pid,port:server.address().port,entries:manifest.entries.length}));
});
process.on('SIGINT', () => { console.log(JSON.stringify({requests,cancelled,sentBytes})); server.close(); });
