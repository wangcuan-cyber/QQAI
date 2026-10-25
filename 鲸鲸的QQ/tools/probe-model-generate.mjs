// 实测：MiniMax 通道能否真正生成内容（不只是读目录）。
// 建临时会话 → selectModel(minimax) → prompt 一句 → 等 turn/end。
import { pathToFileURL } from 'node:url';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const repo = 'C:/WhaleQQ/qq-bridge';
const { NodeApiClient, unwrap, createTurnCollector } = await import(pathToFileURL(repo + '/src/dsh-client.js').href);
const cfg = JSON.parse(fs.readFileSync(path.join(repo, 'config.json'), 'utf8'));
const base = cfg.dsh.baseUrl;
console.log('DSH:', base, '| provider:', cfg.dsh.provider, '| model:', cfg.dsh.model);

const api = new NodeApiClient(base);
const cwd = path.join(os.tmpdir(), 'call-test-session');
fs.mkdirSync(cwd, { recursive: true });

let created;
try {
  created = unwrap(await api.sessions.create({ cwd }), 'create');
} catch (e) {
  console.log('建会话失败:', e.message);
  process.exit(1);
}
const sessionId = created.sessionId;

try {
  const sel = unwrap(await api.sessions.selectModel({
    sessionId,
    provider: cfg.dsh.provider,
    model: cfg.dsh.model,
    reasoningEffort: cfg.dsh.reasoningEffort || 'max',
  }), 'selectModel');
  console.log('选模型:', JSON.stringify(sel.selected));
} catch (e) {
  console.log('选模型失败:', e.message.slice(0, 200));
  try { await api.workspace.archiveSession({ sessionId }); } catch {}
  process.exit(1);
}

const collector = createTurnCollector();
let resolveOpened;
const opened = new Promise((r) => { resolveOpened = r; });
const stream = api.events.mux({}, undefined, () => resolveOpened());
const done = new Promise((resolve, reject) => {
  const timer = setTimeout(() => reject(new Error('timeout 90s')), 90000);
  (async () => {
    for await (const env of stream) {
      const f = env.payload;
      if (f.type === 'session/event' && f.sessionId === sessionId) {
        const e = collector.push(f.event);
        if (e) { clearTimeout(timer); resolve(e); return; }
      }
      if (f.type === 'stream/error') { clearTimeout(timer); reject(new Error(JSON.stringify(f.error))); return; }
    }
  })().catch((e) => { clearTimeout(timer); reject(e); });
});
await opened;
stream.follow(sessionId);

const t0 = Date.now();
try {
  unwrap(await api.sessions.prompt({
    sessionId, mode: 'queue', content: [{ type: 'text', text: '只回复两个字：在的' }],
  }), 'prompt');
  const ended = await done;
  const ms = Date.now() - t0;
  console.log(`结果: reason=${ended.reason.kind}  耗时=${(ms / 1000).toFixed(1)}s`);
  if (ended.reason.kind === 'error') {
    console.log('错误:', JSON.stringify(ended.reason.error).slice(0, 300));
  } else {
    console.log('回复:', (ended.text || '(空)').slice(0, 100));
  }
} catch (e) {
  console.log('失败:', e.message.slice(0, 300));
}
try { await api.workspace.archiveSession({ sessionId }); } catch {}
process.exit(0);
