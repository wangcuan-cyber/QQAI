#!/usr/bin/env node
/**
 * say.mjs — 「随时读一段文字，用指定音色发成 QQ 语音」的独立模块。
 *
 * 设计目标：**不依赖 AI 模型、不依赖桥接进程**。
 * 只要 SnowLuma（OneBot :3000）活着就能说话。模型 token 失效、桥接没启动、
 * DSH 关掉了，都不影响它。
 *
 * 组成：voice_engine.py（Edge TTS + 后处理）→ OneBot HTTP send_*_msg
 *
 * 命令行：
 *   node say.mjs "你好呀"
 *   echo "你好呀" | node say.mjs
 *   node say.mjs --file 台词.txt
 *   node say.mjs "你好" --to group:1127087649
 *   node say.mjs "你好" --pitch +12Hz --rate -8% --post sweet
 *   node say.mjs --list-voices
 *
 * 作为模块：
 *   // ⚠️ Windows 绝对路径必须写成 file:// URL，直接写 C:/... 会报
 *   //    ERR_UNSUPPORTED_ESM_URL_SCHEME（Node 的 ESM loader 只认 file:/data:/node:）
 *   import { say } from 'file:///C:/WhaleQQ/tools/say.mjs';
 *   await say('你好呀', { to: 'private:112528513' });
 *
 *   或者从项目内用相对路径（相对的是**导入方文件**的位置）：
 *   import { say } from './tools/say.mjs';
 *
 * 配置：读 qq-bridge/config.json 的 socialV2.voice.tts（音色/音高/语速/后处理），
 *       目标默认取 ownerQQ。命令行参数可逐项覆盖。
 */
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// C:\WhaleQQ 是项目根的 ASCII 联接；从本文件位置反推更稳（tools/ 的上一级）。
const ROOT = path.resolve(__dirname, '..');
const BRIDGE_DIR = path.join(ROOT, 'qq-bridge');
const CFG_PATH = path.join(BRIDGE_DIR, 'config.json');
const ENGINE = path.join(__dirname, 'voice', 'voice_engine.py');
const DEFAULT_PYTHON = path.join(
  os.homedir(),
  '.dsh', 'dsh-runtimes', 'dsh-primary-runtime', 'dependencies', 'python', 'python.exe',
);

function readJsonSafe(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return null; }
}

/** 合并配置：config.json 的 tts 段 + 调用方覆盖。 */
function resolveOptions(overrides = {}) {
  const cfg = readJsonSafe(CFG_PATH) || {};
  const voice = cfg.socialV2?.voice ?? {};
  const tts = voice.tts ?? {};
  const snow = cfg.snowluma ?? {};
  return {
    voice: overrides.voice || tts.voice || 'zh-CN-XiaoyiNeural',
    pitch: overrides.pitch ?? tts.edgePitch ?? '+8Hz',
    rate: overrides.rate ?? tts.edgeRate ?? '+0%',
    postprocess: overrides.postprocess ?? tts.postprocess ?? 'dry',
    backend: overrides.backend || tts.backend || 'edge',
    timeoutMs: Number(overrides.timeoutMs ?? tts.timeoutMs ?? 60000),
    // OneBot HTTP
    httpUrl: String(overrides.httpUrl || snow.httpUrl || 'http://127.0.0.1:3000').replace(/\/+$/, ''),
    accessToken: overrides.accessToken ?? snow.accessToken ?? '',
    // 默认发给主人
    to: overrides.to || (cfg.ownerQQ ? `private:${cfg.ownerQQ}` : ''),
    keepFile: overrides.keepFile === true,
  };
}

/** 跑 voice_engine.py，返回 { ok, path, bytes } 或 { ok:false, error }。 */
function synth(text, opts) {
  return new Promise((resolve) => {
    const outPath = path.join(os.tmpdir(), `say-${Date.now()}-${Math.random().toString(36).slice(2, 7)}.mp3`);
    const args = [
      ENGINE, 'tts', '--text', text, outPath,
      '--backend', opts.backend,
      `--voice=${opts.voice}`,
      // 等号形式：'-8%' 这种以减号开头的值不能被 argparse 当成新选项
      `--edge-pitch=${opts.pitch}`,
      `--edge-rate=${opts.rate}`,
      `--postprocess=${opts.postprocess}`,
    ];
    let child;
    try {
      child = spawn(DEFAULT_PYTHON, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    } catch (e) {
      resolve({ ok: false, error: `无法启动 python：${e?.message ?? e}` });
      return;
    }
    let out = '';
    let err = '';
    let settled = false;
    const settle = (v) => { if (!settled) { settled = true; clearTimeout(timer); resolve(v); } };
    const timer = setTimeout(() => {
      try { child.kill(); } catch {}
      settle({ ok: false, error: `合成超时（${Math.round(opts.timeoutMs / 1000)}s）` });
    }, opts.timeoutMs);
    child.stdout.on('data', (b) => { out += b.toString('utf8'); });
    child.stderr.on('data', (b) => { err += b.toString('utf8'); });
    child.on('error', (e) => settle({ ok: false, error: `启动失败：${e?.message ?? e}` }));
    child.on('close', () => {
      const t = out.trim();
      if (!t) { settle({ ok: false, error: `引擎无输出${err ? '：' + err.trim().slice(0, 200) : ''}` }); return; }
      try {
        const j = JSON.parse(t);
        settle(j.ok ? { ...j, path: j.path || outPath } : j);
      } catch {
        settle({ ok: false, error: `输出无法解析：${t.slice(0, 200)}` });
      }
    });
  });
}

/** 把音频文件当语音条发给 QQ。 */
async function sendRecord(file, to, opts) {
  const m = /^(private|group):(\d+)$/.exec(String(to || ''));
  if (!m) return { ok: false, error: `目标格式应为 private:QQ号 或 group:群号（收到「${to}」）` };
  const [kind, id] = [m[1], Number(m[2])];
  const action = kind === 'private' ? 'send_private_msg' : 'send_group_msg';
  const body = kind === 'private'
    ? { user_id: id, message: [{ type: 'record', data: { file } }] }
    : { group_id: id, message: [{ type: 'record', data: { file } }] };
  const headers = { 'content-type': 'application/json' };
  if (opts.accessToken) headers.authorization = `Bearer ${opts.accessToken}`;
  try {
    const res = await fetch(`${opts.httpUrl}/${action}`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(90000),
    });
    const j = await res.json().catch(() => ({}));
    if (!res.ok || j.status !== 'ok' || j.retcode !== 0) {
      const hint = res.status === 426 ? '（HTTP 426：httpUrl 可能指向了 WebSocket 端口）' : '';
      return { ok: false, error: `OneBot 发送失败: ${j.wording || j.retcode || res.status}${hint}` };
    }
    return { ok: true, messageId: j.data?.message_id ?? null };
  } catch (e) {
    return { ok: false, error: `发送异常：${e?.message ?? e}` };
  }
}

/**
 * 说一句话：合成语音并发出。
 * @param {string} text
 * @param {object} [overrides] 可覆盖 voice/pitch/rate/postprocess/to/httpUrl/accessToken/keepFile
 * @returns {Promise<{ok:boolean, messageId?:number, bytes?:number, voice?:string, pitch?:string, rate?:string, postprocess?:string, error?:string}>}
 */
export async function say(text, overrides = {}) {
  const clean = String(text ?? '').trim();
  if (!clean) return { ok: false, error: '文本为空' };
  const opts = resolveOptions(overrides);
  if (!opts.to) return { ok: false, error: '没有发送目标：config.json 里没有 ownerQQ，也没传 to' };

  const syn = await synth(clean, opts);
  if (!syn.ok) return { ok: false, error: `合成失败：${syn.error}` };

  // OneBot 按路径读音频，必须放进它可访问的位置（语音库最合适）。
  const libDir = path.resolve(BRIDGE_DIR, 'audio');
  let finalPath = syn.path;
  try {
    fs.mkdirSync(libDir, { recursive: true });
    finalPath = path.join(libDir, `say-${Date.now()}.mp3`);
    fs.copyFileSync(syn.path, finalPath);
  } catch (e) {
    return { ok: false, error: `拷贝到语音库失败：${e?.message ?? e}` };
  }

  const sent = await sendRecord(finalPath, opts.to, opts);
  // 延迟清理：SnowLuma 的 voice 缓存按路径索引，立刻删会让后续 get_record 找不到。
  if (!opts.keepFile) {
    const p = finalPath;
    setTimeout(() => { try { fs.unlinkSync(p); } catch {} }, 10 * 60 * 1000).unref?.();
  }
  try { fs.unlinkSync(syn.path); } catch {}

  if (!sent.ok) return { ok: false, error: sent.error };
  return {
    ok: true,
    messageId: sent.messageId,
    bytes: syn.bytes,
    voice: opts.voice,
    pitch: opts.pitch,
    rate: opts.rate,
    postprocess: opts.postprocess,
    to: opts.to,
    text: clean,
  };
}

/** 列出可用的 Edge TTS 中文女声（需要联网）。 */
export async function listVoices() {
  return new Promise((resolve) => {
    const code = [
      'import asyncio, edge_tts, json',
      'async def m():',
      '    vs = await edge_tts.list_voices()',
      '    out = [{"name": v["ShortName"], "gender": v["Gender"], "locale": v["Locale"],',
      '            "categories": v.get("VoiceTag", {}).get("ContentCategories"),',
      '            "personalities": v.get("VoiceTag", {}).get("VoicePersonalities")}',
      '           for v in vs if v["Locale"].startswith("zh")]',
      '    print(json.dumps(out, ensure_ascii=False))',
      'asyncio.run(m())',
    ].join('\n');
    const child = spawn(DEFAULT_PYTHON, ['-c', code], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let out = '';
    child.stdout.on('data', (b) => { out += b.toString('utf8'); });
    child.on('close', () => {
      try { resolve(JSON.parse(out)); } catch { resolve([]); }
    });
    child.on('error', () => resolve([]));
  });
}

// ── CLI ──────────────────────────────────────────────────────────────────
function parseArgs(argv) {
  const opts = {};
  const positional = [];
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (!a.startsWith('--')) { positional.push(a); continue; }
    const key = a.slice(2);
    const next = argv[i + 1];
    const take = () => { i += 1; return next; };
    switch (key) {
      case 'to': opts.to = take(); break;
      case 'voice': opts.voice = take(); break;
      case 'pitch': opts.pitch = take(); break;
      case 'rate': opts.rate = take(); break;
      case 'post': opts.postprocess = take(); break;
      case 'backend': opts.backend = take(); break;
      case 'file': opts.file = take(); break;
      case 'http-url': opts.httpUrl = take(); break;
      case 'token': opts.accessToken = take(); break;
      case 'keep': opts.keepFile = true; break;
      case 'list-voices': opts.listVoices = true; break;
      case 'help': opts.help = true; break;
      default: break;
    }
  }
  return { opts, positional };
}

async function readStdin() {
  if (process.stdin.isTTY) return '';
  return new Promise((resolve) => {
    let buf = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (c) => { buf += c; });
    process.stdin.on('end', () => resolve(buf));
    process.stdin.on('error', () => resolve(''));
  });
}

async function main() {
  const { opts, positional } = parseArgs(process.argv.slice(2));

  if (opts.help) {
    console.log(`用法:
  node say.mjs "要说的文字"
  echo "文字" | node say.mjs
  node say.mjs --file 台词.txt
  node say.mjs "文字" --to group:1127087649
  node say.mjs "文字" --pitch +12Hz --rate -8% --post sweet
  node say.mjs --list-voices

选项:
  --to <private:QQ|group:群号>  目标（默认 config.json 的 ownerQQ）
  --voice <名字>               音色（默认 zh-CN-XiaoyiNeural）
  --pitch <+8Hz>               音高（默认读 config）
  --rate <+0%>                 语速（默认读 config）
  --post <dry|sweet|...>       后处理档位（默认读 config）
  --file <路径>                从文件读文本
  --keep                       保留音频文件（默认 10 分钟后清理）
  --list-voices                列出中文音色
  --help`);
    return 0;
  }

  if (opts.listVoices) {
    const vs = await listVoices();
    if (!vs.length) { console.error('取不到音色列表（需要联网）'); return 1; }
    for (const v of vs) {
      console.log(`${v.name.padEnd(34)} ${v.gender.padEnd(7)} ${v.categories} | ${v.personalities}`);
    }
    return 0;
  }

  let text = positional.join(' ').trim();
  if (!text && opts.file) {
    try { text = fs.readFileSync(opts.file, 'utf8').trim(); }
    catch (e) { console.error(`读文件失败：${e.message}`); return 1; }
  }
  if (!text) {
    const fromStdin = await readStdin();
    text = fromStdin.trim();
  }
  if (!text) { console.error('没有文本。用参数、--file 或 stdin 提供。'); return 1; }

  const r = await say(text, opts);
  if (!r.ok) { console.error(`失败：${r.error}`); return 1; }
  console.log(`OK  ${r.bytes ? (r.bytes / 1024).toFixed(1) + 'KB  ' : ''}${r.voice}  ${r.pitch}  ${r.rate}  post=${r.postprocess}  -> ${r.to}  msgId=${r.messageId}`);
  return 0;
}

// 只在被直接执行时跑 CLI（被 import 时不执行）。
// ⚠️ 必须用 realpath 比较：本文件常通过 C:\WhaleQQ 这个**目录联接**调用，
// 而 import.meta.url 解析出的是真实路径（Desktop\鲸鲸的QQ\...），
// 直接比 path.resolve 会恒不相等 —— 表现为脚本静默不执行、没有任何输出。
const invokedDirectly = (() => {
  const self = fileURLToPath(import.meta.url);
  const entry = process.argv[1];
  if (!entry) return false;
  try {
    return fs.realpathSync(entry) === fs.realpathSync(self);
  } catch {
    return path.resolve(entry) === path.resolve(self);
  }
})();
if (invokedDirectly) {
  main().then((code) => process.exit(code)).catch((e) => { console.error(e); process.exit(1); });
}
