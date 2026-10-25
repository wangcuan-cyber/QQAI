// Probe DSH candidate ports and print the one that really serves the JSON API.
//
// Reads candidate ports from stdin (one per line) and prints the winning port
// (or nothing).
//
// Why this exists: the DSH desktop process listens on SEVERAL ports at once
// (observed 8326 / 55525 / 61800). Only one of them is the JSON API - the
// others answer 401 (different auth scope) or 404. Picking the highest
// listening port therefore picked a dead port and left the bridge pointed at
// garbage.
//
// One process probes all candidates (spawning node per port would cost
// ~200ms each and the watchdog runs this every 30s).
import { pathToFileURL } from 'node:url';
import fs from 'node:fs';
import path from 'node:path';

const BRIDGE = 'C:/WhaleQQ/qq-bridge';

async function probeOne(mod, port) {
  const base = `http://127.0.0.1:${port}`;
  let cookie = null;
  try {
    cookie = mod.discoverDshSessionCookie(base);
  } catch {
    cookie = null;
  }
  try {
    const headers = { 'content-type': 'application/json' };
    if (cookie) headers.cookie = cookie;
    const res = await fetch(`${base}/api/settings/describe`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ type: 'client-request', rpcId: 'probe', method: 'settings/describe', payload: { args: {} } }),
    });
    const text = await res.text();
    return res.status === 200 && text.includes('"result"');
  } catch {
    return false;
  }
}

async function main() {
  const lines = fs.readFileSync(0, 'utf8').split(/\r?\n/).map((s) => s.trim()).filter(Boolean);
  const ports = lines.map((s) => Number(s)).filter((n) => Number.isFinite(n) && n > 0);
  if (!ports.length) {
    process.stdout.write('');
    return;
  }

  let mod = null;
  try {
    mod = await import(pathToFileURL(path.join(BRIDGE, 'src', 'dsh-client.js')).href);
  } catch {
    mod = null;
  }

  for (const port of ports) {
    const ok = mod ? await probeOne(mod, port) : await probeOne({ discoverDshSessionCookie: () => null }, port);
    if (ok) {
      process.stdout.write(String(port));
      return;
    }
  }
  process.stdout.write('');
}

main().catch(() => {
  process.stdout.write('');
  process.exit(0);
});
