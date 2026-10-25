// 把 roil2 目录里的语音样本按顺序发到 QQ，供用户试听对比。
import fs from 'node:fs';
import path from 'node:path';

const TOKEN = process.env.SL_TOKEN || '';
const TARGET = process.env.SL_TARGET || '112528513';
const SRC = path.join(process.env.TEMP, 'roil2');
const LIB = 'C:\\WhaleQQ\\qq-bridge\\audio';

const ORDER = [
  ['raw', '对照组：只调参数，无后处理'],
  ['soft', '轻微混响'],
  ['softer', '中等混响'],
  ['softest', '最软混响'],
];

async function sendRecord(file) {
  const res = await fetch('http://127.0.0.1:3000/send_private_msg', {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${TOKEN}` },
    body: JSON.stringify({ user_id: Number(TARGET), message: [{ type: 'record', data: { file } }] }),
  });
  return res.json();
}

for (const [name, label] of ORDER) {
  const src = path.join(SRC, `${name}.mp3`);
  if (!fs.existsSync(src)) { console.log(`skip ${name}`); continue; }
  const dest = path.join(LIB, `post-${name}.mp3`);
  fs.copyFileSync(src, dest);
  const r = await sendRecord(dest);
  const ok = r.status === 'ok' && r.retcode === 0;
  console.log(`${name.padEnd(8)} ${label.padEnd(20)} -> ${ok ? 'OK' : 'FAIL ' + (r.wording || r.retcode)}`);
  await new Promise((res) => setTimeout(res, 2000));
}
