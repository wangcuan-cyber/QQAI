// Delete collected stickers by emoji_id (test cleanup).
// Usage: node delete-stickers.mjs <emoji_id> [emoji_id ...]
const TOKEN = process.env.SL_TOKEN || '';
const BASE = 'http://127.0.0.1:3000';

async function call(action, body) {
  const res = await fetch(`${BASE}/${action}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${TOKEN}` },
    body: JSON.stringify(body),
  });
  return res.json();
}

const ids = process.argv.slice(2);
for (const id of ids) {
  const r = await call('delete_custom_face', { emoji_id: id });
  console.log(`${id.slice(20, 32)}... -> ${r.status}/${r.retcode}${r.wording ? ' ' + r.wording : ''}`);
}

const list = await call('fetch_custom_face_detail', { count: 100 });
console.log(`\n剩余 ${(list.data || []).length} 个：`);
for (const e of list.data || []) console.log(`  ${e.emoji_id}  ${e.desc || '(无备注)'}`);
