// Probe: which image formats/sizes does SnowLuma's add_custom_face accept?
// Calls the OneBot HTTP API directly (add_custom_face) with base64 payloads.
import fs from 'node:fs';

const TOKEN = process.env.SL_TOKEN || '';
const BASE = 'http://127.0.0.1:3000';

async function tryAdd(label, file) {
  const b64 = fs.readFileSync(file).toString('base64');
  const res = await fetch(`${BASE}/add_custom_face`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${TOKEN}` },
    body: JSON.stringify({ file: 'base64://' + b64 }),
  });
  const j = await res.json();
  const ok = j.status === 'ok' && j.retcode === 0;
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${label} (${(fs.statSync(file).size / 1024).toFixed(1)}KB) -> ${ok ? j.data?.emoji_id : (j.wording || j.retcode || JSON.stringify(j).slice(0, 120))}`);
  return ok;
}

const files = process.argv.slice(2);
for (const f of files) {
  try {
    await tryAdd(f.split(/[\\/]/).pop(), f);
  } catch (e) {
    console.log(`ERR  ${f} -> ${String(e.message).slice(0, 120)}`);
  }
}
