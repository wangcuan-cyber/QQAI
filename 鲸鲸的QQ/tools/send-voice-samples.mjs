// 把 4 个语音样本按顺序发到指定 QQ，供用户试听对比。
import fs from 'node:fs';
import path from 'node:path';

const TOKEN = process.env.SL_TOKEN || 'ITMyrKvPMTujRrcOzR7qS4xMngQDCYR-PdTXLuSoSXE';
const TARGET = process.env.SL_TARGET || '112528513';
const SRC = path.join(process.env.TEMP, 'roil');
const LIB = 'C:\\WhaleQQ\\qq-bridge\\audio';

const ORDER = [
  ['A_晓伊默认', 'A'],
  ['B_晓伊微甜', 'B'],
  ['C_晓伊萝莉', 'C'],
  ['D_晓晓温柔', 'D'],
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
  if (!fs.existsSync(src)) { console.log(`skip ${name} (missing)`); continue; }
  const dest = path.join(LIB, `sample-${label}.mp3`);
  fs.copyFileSync(src, dest);
  const r = await sendRecord(dest);
  const ok = r.status === 'ok' && r.retcode === 0;
  console.log(`${label} (${name}) -> ${ok ? 'OK msgId=' + r.data?.message_id : 'FAIL ' + (r.wording || r.retcode)}`);
  await new Promise((res) => setTimeout(res, 1800));
}
