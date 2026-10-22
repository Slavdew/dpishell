// Проверка целей с роутера через curl — те же правила, что Probe.cs Windows-версии (architecture.md «Проверка одной цели»):
// адрес через DoH (DNS провайдера подменяет ответы), тело читается целиком, редирект только на тот же сайт.
'use strict';

import { readfile, writefile, popen, mkdir, stat, unlink, lsdir } from 'fs';
import * as st from 'dpishell.state';

const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36';
const CONNECT_S = 3, READ_S = 6, MAX_BYTES = 1048576, MAX_REDIRECTS = 5;
let batch_seq = 0;

const DOH_IPS = ['8.8.8.8', '1.1.1.1'];

const DOH = [
	{ name: 'Google', url: 'https://8.8.8.8/resolve?type=A&name=' },
	{ name: 'Cloudflare', url: 'https://1.1.1.1/dns-query?type=A&name=', accept: 'application/dns-json' },
];

function q(s) {
	return `'${replace(`${s}`, "'", "'\\''")}'`;
}

function run(cmd) {
	let p = popen(cmd);
	if (!p)
		return { rc: -1, out: '' };
	let out = p.read('all');
	return { rc: p.close(), out };
}

function host_of(url) {
	let m = match(url, /^https?:\/\/([^\/:?#]+)/);
	return m ? lc(m[1]) : null;
}

function is_ip(h) {
	return match(h, /^[0-9.]+$/) != null || index(h, ':') >= 0;
}

// Тот же сайт: равны, поддомен, или соседние поддомены одного родителя, не похожего на суффикс (co.uk).
function same_site(a, b) {
	a = rtrim(lc(a), '.');
	b = rtrim(lc(b), '.');
	if (a == b)
		return true;
	if (is_ip(a) || is_ip(b))
		return false;
	let ends = (x, y) => length(x) > length(y) + 1 && substr(x, length(x) - length(y) - 1) == '.' + y;
	if (ends(a, b) || ends(b, a))
		return true;
	let parent = h => {
		let i = index(h, '.');
		return (i > 0 && index(substr(h, i + 1), '.') > 0) ? substr(h, i + 1) : null;
	};
	let pa = parent(a), pb = parent(b);
	if (pa == null || pa != pb)
		return false;
	let p = split(pa, '.');
	return !(length(p) == 2 && length(p[1]) == 2 && length(p[0]) <= 3);
}

function doh_try(host) {
	for (let d in DOH) {
		let cmd = `curl -sS --max-time 4 ${d.accept ? '-H ' + q('accept: ' + d.accept) : ''} ${q(d.url + host)} 2>/dev/null`;
		let r = run(cmd);
		let j;
		try { j = json(r.out); } catch (e) { continue; }
		let ips = map(filter(j?.Answer ?? [], a => a.type == 1 && match(a.data, /^[0-9.]+$/)), a => a.data);
		if (length(ips))
			return ips;
	}
	return [];
}

// Адреса IPv4 через DoH; [] — не удалось (вызывающий решает, идти ли через системный DNS).
function doh_resolve(host, cache) {
	if (cache?.[host])
		return cache[host];
	if (is_ip(host))
		return [host];
	let ips = doh_try(host);
	if (!length(ips)) {
		// Обход мог сломать сам DoH (режим «Все сайты», адреса Google/Cloudflare в IP-списке): на время запроса
		// выводим DoH-серверы из-под обхода через набор nozapret zapret — остальная сеть не затронута.
		let added = filter(DOH_IPS, ip => run(`nft add element inet zapret nozapret { ${ip} } 2>/dev/null`).rc == 0);
		if (length(added)) {
			ips = doh_try(host);
			for (let ip in added)
				run(`nft delete element inet zapret nozapret { ${ip} } 2>/dev/null`);
		}
	}
	if (length(ips) && cache)
		cache[host] = ips;
	return ips;
}


function header_value(headers, name) {
	// Последний ответ в файле (-D пишет все, но без -L ответ один).
	let v = null;
	for (let line in split(headers ?? '', '\n')) {
		let m = match(line, regexp(`^${name}:[ \t]*([^\r]*)`, 'i'));
		if (m)
			v = m[1];
	}
	return v;
}

function curl_cmd(t, tls, ip, out_prefix, tm) {
	let cs = tm?.connect_s ?? CONNECT_S, rs = tm?.read_s ?? READ_S;
	let host = host_of(t.url);
	let port = match(t.url, /^https:/) ? 443 : 80;
	let pm = match(t.url, /^https?:\/\/[^\/:]+:([0-9]+)/);
	if (pm)
		port = +pm[1];
	let args = [
		'curl', '-sS', '-o', '/dev/null', '-D', q(`${out_prefix}.h`),
		'--connect-timeout', cs, '--max-time', cs + rs, '--max-filesize', MAX_BYTES * 64,
		'-A', q(UA), '-H', q('Accept: */*'),
		'-w', q('%{http_code} %{size_download} %{exitcode} %{time_total} %{redirect_url}'),
	];
	if (match(t.url, /^https:/))
		push(args, tls == '1.3' ? '--tlsv1.3 --tls-max 1.3' : '--tlsv1.2 --tls-max 1.2');
	if (ip)
		push(args, '--resolve', q(`${host}:${port}:${ip}`));
	push(args, q(t.url));
	return `${join(' ', args)} > ${q(out_prefix + '.w')} 2> ${q(out_prefix + '.e')}`;
}

// Разбор результата одного запроса по правилам Probe.cs.
function classify(t, out_prefix) {
	let w = split(trim(readfile(`${out_prefix}.w`) ?? ''), ' ');
	let code = +w[0], bytes = +w[1], exitcode = +w[2], secs = +w[3], redir = w[4] ?? '';
	let headers = readfile(`${out_prefix}.h`);
	// Вывод curl — в текст исхода, не длиннее 300 знаков.
	let errtext = substr(trim(readfile(`${out_prefix}.e`) ?? ''), 0, 300);
	let res = (outcome, detail) => ({ outcome, ok: outcome == 'ok', seconds: secs, bytes, detail });

	if (code == 0) {
		if (exitcode == 28)
			return res('timeout', 'таймаут');
		if (exitcode == 35 || exitcode == 60)
			return res('tls_failed', errtext);
		if (exitcode == 56 || exitcode == 52)
			return res('reset', errtext);
		if (exitcode == 7 || exitcode == 6)
			return res('connect_failed', errtext);
		return res('error', errtext || `curl ${exitcode}`);
	}
	// Тексты исходов — те же, что на ПК: служебный адрес без страницы и антибот-ответ — не «HTTP 404» голым кодом.
	if (t.handshake_only)
		return res('ok', code >= 200 && code < 300 ? 'соединение есть' : 'соединение есть (страницы у адреса нет — это нормально)');
	if (code >= 300 && code < 400) {
		let loc = redir || (header_value(headers, 'location') ?? '');
		let to = host_of(loc) ?? host_of(t.url);
		// Имя из заголовка Location пишет сайт (или подмена провайдера) — в текст и дальше только чистым (security-audit-router №2).
		if (!match(to ?? '', /^[a-z0-9.-]{1,253}$/))
			return res('foreign_redirect', 'редирект на другой адрес');
		if (!same_site(t.site ?? host_of(t.url), to))
			return res('foreign_redirect', `редирект на ${to}`);
		// Свой редирект: check_batch идёт по нему до конечной страницы (до MAX_REDIRECTS) и судит по ней.
		let r = res('redirect', `HTTP ${code} → ${to}`);
		r.next = match(loc, /^https?:\/\//) ? loc : null;
		return r;
	}
	if (code == 451)
		return res('blocked', 'HTTP 451 — доступ ограничен');
	if (code == 400 || code == 403 || code == 404 || code >= 500)
		return res('bad_status', `HTTP ${code}`);
	let expected = header_value(headers, 'content-length');
	let full = bytes >= MAX_BYTES;
	if (exitcode == 28)
		return full ? res('ok', `HTTP ${code}, ${int(bytes / 1024)} КБ`) : res(bytes > 0 ? 'truncated' : 'timeout', bytes > 0 ? `поток оборвался на ${bytes} байтах` : 'таймаут');
	if (exitcode == 18 || exitcode == 56)
		return res('truncated', `поток оборвался на ${bytes} байтах`);
	if (exitcode != 0)
		return res('error', errtext || `curl ${exitcode}`);
	if (!full && expected != null && bytes < +expected)
		return res('truncated', `получено ${bytes} из ${expected}`);
	if (bytes < (t.min_bytes ?? 0))
		return res('truncated', `получено ${bytes}, нужно ≥ ${t.min_bytes}`);
	if ((code < 200 || code >= 300) && !(t.min_bytes > 0))
		return res('ok', `сервер отвечает (для проверки достаточно), HTTP ${code}`);
	return res('ok', `HTTP ${code}, ${int(bytes / 1024)} КБ`);
}

// Имитация DPI для проверки подбора и глубокого поиска в эмуляторе (настоящего DPI там нет, сеть QEMU пересобирает TCP).
// Включается файлом в каталоге root (0700): { "rules": [ { "host": "youtube.com", "profile": "google.txt", "need": ["multidisorder"] } ] }.
// Цель под правилом открывается, только если nfqws запущен и в профиле nfqws с подстрокой profile есть все подстроки need;
// цели без правила открываются всегда (как обычные сайты).
const FAKE_DPI = '/tmp/dpishell/fake-dpi.json';

function fake_dpi(items) {
	// Только файл root: иначе подложенный файл подменил бы исходы проверок (security-audit-router №4).
	if (!st.root_file(FAKE_DPI))
		return null;
	let rules;
	try { rules = json(readfile(FAKE_DPI)).rules ?? []; }
	catch (e) { die(`${FAKE_DPI}: не JSON`); }
	let running = length(trim(run('pidof nfqws').out)) > 0;
	let m = match(readfile('/opt/zapret/config') ?? '', /NFQWS_OPT="([^"]*)"/);
	let profiles = m ? split(m[1], ' --new ') : [];
	let res = [];
	for (let it in items) {
		let host = host_of(it.target.url);
		let rule = filter(rules, r => index(host, r.host) >= 0)[0];
		// bypass_breaks: сайт ломает сам обход — открывается только без nfqws (проверка сторожа).
		let ok = !rule || (rule.bypass_breaks ? !running : (running && length(filter(profiles, pf => index(pf, rule.profile) >= 0 &&
			!length(filter(rule.need, n => index(pf, n) < 0)))) > 0));
		push(res, { outcome: ok ? 'ok' : 'reset', ok, seconds: 0.3, bytes: ok ? 1000 : 0, detail: 'имитация DPI',
			url: it.target.url, tls: it.tls, dns: 'fake' });
	}
	return res;
}

// Проверить набор (цель × версия TLS) параллельно, до `parallel` запросов сразу.
// items: [{ target: {url, min_bytes, handshake_only}, tls: '1.3'|'1.2' }]; opts: { parallel, dns_cache, use_doh, connect_s, read_s }
function check_batch(items, opts) {
	opts ??= {};
	let fake = fake_dpi(items);
	if (fake)
		return fake;
	let dir = `/tmp/dpishell/probe-${time()}-${++batch_seq}`;
	st.own_dir('/tmp/dpishell', 0o700);
	mkdir(dir, 0o700);
	let cache = opts.dns_cache ?? {};
	let cmds = [];
	for (let i = 0; i < length(items); i++) {
		let host = host_of(items[i].target.url);
		let ip = items[i].ip ?? null;
		if (ip)
			items[i].dns = 'given';
		else if (opts.use_doh !== false) {
			let ips = doh_resolve(host, cache);
			ip = ips[0];
			items[i].dns = ip ? 'doh' : 'system';
		}
		push(cmds, curl_cmd(items[i].target, items[i].tls, ip, `${dir}/${i}`, opts));
	}
	// Пачками по parallel: фоновые процессы и wait.
	let par = opts.parallel ?? 8;
	let run_all = list => {
		for (let i = 0; i < length(list); i += par)
			run(`( ${join(' & ', slice(list, i, i + par))} & wait ) 2>/dev/null`);
	};
	run_all(cmds);
	let res = [];
	for (let i = 0; i < length(items); i++)
		push(res, classify(items[i].target, `${dir}/${i}`));
	// Свои редиректы — до конечной страницы, как на ПК: новое имя тоже через DoH, MinBytes и обрыв — по конечной.
	let cur = map(items, it => it.target);
	let hops = map(items, () => 0);
	for (let hop = 1; hop <= MAX_REDIRECTS; hop++) {
		let idx = [];
		for (let i = 0; i < length(res); i++)
			if (res[i].outcome == 'redirect' && res[i].next)
				push(idx, i);
		if (!length(idx))
			break;
		let hc = [];
		for (let i in idx) {
			cur[i] = { ...cur[i], url: res[i].next, site: cur[i].site ?? host_of(items[i].target.url) };
			let ip = opts.use_doh !== false ? doh_resolve(host_of(cur[i].url), cache)[0] : null;
			push(hc, curl_cmd(cur[i], items[i].tls, ip, `${dir}/${i}-${hop}`, opts));
		}
		run_all(hc);
		for (let i in idx) {
			let r = classify(cur[i], `${dir}/${i}-${hop}`);
			r.seconds += res[i].seconds;
			res[i] = r;
			hops[i] = hop;
		}
	}
	let results = [];
	for (let i = 0; i < length(items); i++) {
		let r = res[i];
		// Больше MAX_REDIRECTS своих редиректов (или без адреса): ответы HTTP шли — соединение не режется; но если нужно тело
		// (min_bytes), содержимое не проверено — провал (как на ПК).
		if (r.outcome == 'redirect' && hops[i] && (items[i].target.min_bytes ?? 0) > 0)
			r = { ...r, outcome: 'truncated', ok: false, detail: `не дошли до страницы: больше ${MAX_REDIRECTS} перенаправлений` };
		else if (r.outcome == 'redirect')
			r = { ...r, outcome: 'ok', ok: true, detail: 'сервер отвечает (для проверки достаточно)' };
		delete r.next;
		if (hops[i])
			r = { ...r, final_url: cur[i].url, detail: `${r.detail} (после ${hops[i]} редир.)` };
		push(results, { ...r, url: items[i].target.url, tls: items[i].tls, dns: items[i].dns });
	}
	for (let f in lsdir(dir) ?? [])
		unlink(`${dir}/${f}`);
	run(`rmdir ${q(dir)}`);
	return results;
}

export { doh_resolve, same_site, host_of, check_batch, classify, curl_cmd };
