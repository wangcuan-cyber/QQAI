// 把 config.json 的 dsh.baseUrl 同步成当前 DSH 端口。
// 用法：node set-dsh-port.mjs <config.json 路径> <端口>
// 退出码：0 = 已改写；10 = 本来就一致（无需重启桥接）；1 = 出错
import fs from 'node:fs';

const [, , cfgPath, portRaw] = process.argv;
const port = Number(portRaw);
if (!cfgPath || !Number.isFinite(port) || port <= 0) {
  console.error('usage: node set-dsh-port.mjs <config.json> <port>');
  process.exit(1);
}

const cfg = JSON.parse(fs.readFileSync(cfgPath, 'utf8'));
const want = `http://127.0.0.1:${port}`;
const cur = String(cfg?.dsh?.baseUrl ?? '');
if (cur === want) {
  console.log(`unchanged: ${cur}`);
  process.exit(10);
}
cfg.dsh = { ...(cfg.dsh ?? {}), baseUrl: want };
fs.writeFileSync(cfgPath, `${JSON.stringify(cfg, null, 2)}\n`);
console.log(`updated: ${cur || '(empty)'} -> ${want}`);
process.exit(0);
