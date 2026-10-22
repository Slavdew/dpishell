// Снимки страниц LuCI эмулятора и ошибки JS в консоли — Edge headless по CDP, один экземпляр, страницы по очереди
// (параллельные экземпляры путают язык и мешают друг другу).
//   node router/tools/luci-shot.mjs <папка-вывода> <метка> <путь1> [<путь2> …]
// Путь — после /cgi-bin/luci/, без ведущего слеша (admin/status/overview). Вход root/test (LUCI_USER, LUCI_PASS, LUCI_URL).
// Вывод: PNG <метка>-<путь>.png и строка JSON на страницу { path, ok, http, errors: [...] }; код выхода 1 — есть ошибки.
import { spawn } from 'node:child_process';
import { writeFileSync, mkdtempSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [, , outDir, label, ...paths] = process.argv;
const BASE = process.env.LUCI_URL ?? 'http://127.0.0.1:8080/cgi-bin/luci/';
const USER = process.env.LUCI_USER ?? 'root', PASS = process.env.LUCI_PASS ?? 'test';
const WIDTH = +(process.env.SHOT_WIDTH ?? 1280);
const EDGE = process.env.EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
mkdirSync(outDir, { recursive: true });
const prof = mkdtempSync(join(tmpdir(), 'edge-luci-'));
const port = 9333 + Math.floor(Math.random() * 500);
const ed = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${prof}`, '--no-first-run',
	'--lang=ru', `--window-size=${WIDTH},1000`, 'about:blank'], { stdio: 'ignore' });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let list;
for (let i = 0; i < 75; i++) {
	try { list = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (list.find(t => t.type == 'page')) break; } catch {}
	await sleep(200);
}
const ws = new WebSocket(list.find(t => t.type == 'page').webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
let id = 0; const pending = {};
let errors = [], status = null;
ws.onmessage = m => {
	const d = JSON.parse(m.data);
	if (d.id && pending[d.id]) { pending[d.id](d.result ?? d.error); delete pending[d.id]; return; }
	if (d.method == 'Runtime.exceptionThrown')
		errors.push('exception: ' + (d.params.exceptionDetails.exception?.description ?? d.params.exceptionDetails.text).split('\n')[0]);
	else if (d.method == 'Runtime.consoleAPICalled' && d.params.type == 'error')
		errors.push('console: ' + d.params.args.map(a => a.value ?? a.description ?? '').join(' ').slice(0, 300));
	else if (d.method == 'Log.entryAdded' && d.params.entry.level == 'error')
		errors.push('log: ' + d.params.entry.text.slice(0, 300) + (d.params.entry.url ? ' ' + d.params.entry.url : ''));
	else if (d.method == 'Network.responseReceived' && d.params.type == 'Document')
		status = d.params.response.status;
};
const cmd = (method, params = {}) => new Promise(r => { pending[++id] = r; ws.send(JSON.stringify({ id, method, params })); });
const ev = async expr => (await cmd('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }))?.result?.value;
await cmd('Runtime.enable'); await cmd('Log.enable'); await cmd('Network.enable');
await cmd('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: 1000, deviceScaleFactor: 1, mobile: false });
await cmd('Page.navigate', { url: BASE });
await sleep(2500);
await ev(`(function(){var u=document.querySelector('input[name=luci_username]');if(u){u.value=${JSON.stringify(USER)};document.querySelector('input[name=luci_password]').value=${JSON.stringify(PASS)};document.querySelector('form').submit();}})()`);
await sleep(3000);
let bad = 0;
for (const p of paths) {
	errors = []; status = null;
	await cmd('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: 1000, deviceScaleFactor: 1, mobile: false });
	await cmd('Page.navigate', { url: BASE + p });
	await sleep(5000);
	// Страница LuCI отрисовалась: есть основной блок и нет сообщения об ошибке загрузки вида.
	const view = await ev(`(function(){var m=document.querySelector('#view, #maincontent');var t=document.body?document.body.innerText:'';return {main:!!m,login:!!document.querySelector('input[name=luci_password]'),fail:/Failed to execute|Unable to load|RPC call .* failed|TypeError/.test(t),len:t.length};})()`);
	const h = await ev('document.documentElement.scrollHeight');
	await cmd('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: Math.min(h || 1000, 6000), deviceScaleFactor: 1, mobile: false });
	await sleep(400);
	const shot = await cmd('Page.captureScreenshot', { format: 'png' });
	const file = join(outDir, `${label}-${p.replace(/[^a-z0-9]+/gi, '_')}.png`);
	if (shot?.data) writeFileSync(file, Buffer.from(shot.data, 'base64'));
	if (view?.fail) errors.push('на странице — текст ошибки загрузки');
	if (view?.login) errors.push('страница входа вместо раздела');
	const ok = status == 200 && !!view?.main && !errors.length;
	if (!ok) bad++;
	console.log(JSON.stringify({ path: p, ok, http: status, errors, png: file }));
}
ws.close(); ed.kill();
await sleep(500);
try { rmSync(prof, { recursive: true, force: true }); } catch {}
process.exit(bad ? 1 : 0);
