// 用 node 抓 NuGet 包（node 走 OpenSSL，能连通；dotnet restore 的 schannel 在本机握手失败）
// 用法: node fetch-nupkg.mjs <输出路径.nupkg> [包id] [版本]

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

const out = process.argv[2];
const id = (process.argv[3] ?? 'microsoft.web.webview2').toLowerCase();
const ver = process.argv[4] ?? '1.0.4258.31';

if (!out) {
  console.error('用法: node fetch-nupkg.mjs <输出路径> [包id] [版本]');
  process.exit(2);
}

const url = `https://api.nuget.org/v3-flatcontainer/${id}/${ver}/${id}.${ver}.nupkg`;
console.log('GET', url);

const res = await fetch(url, { signal: AbortSignal.timeout(120000) });
if (!res.ok) {
  console.error('HTTP', res.status, res.statusText);
  process.exit(1);
}

const buf = Buffer.from(await res.arrayBuffer());
fs.mkdirSync(path.dirname(out), { recursive: true });
fs.writeFileSync(out, buf);

const sha = crypto.createHash('sha256').update(buf).digest('hex');
console.log('saved  :', out);
console.log('bytes  :', buf.length);
console.log('sha256 :', sha);
