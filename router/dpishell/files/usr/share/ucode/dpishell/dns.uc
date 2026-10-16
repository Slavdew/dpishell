// DNS для всей сети (06 §3.8): анализ подмены, защищённый DNS (https-dns-proxy) по кнопке, возврат как было.
// https-dns-proxy ставится только по кнопке: при установке он сам переключает dnsmasq (проверено в эмуляторе).
'use strict';

import { readfile, stat, popen } from 'fs';
import { cursor } from 'uci';
import * as st from 'dpishell.state';
import * as pr from 'dpishell.probe';

const STATUS = '/etc/dpishell/dns-status.json';
const BACKUP = '/etc/dpishell/dns-backup.json';
const NEUTRAL = ['example.com', 'wikipedia.org'];

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function q(s) {
	return `'${replace(`${s}`, "'", "'\\''")}'`;
}

function provider_servers() {
	let text = readfile('/tmp/resolv.conf.d/resolv.conf.auto') ?? readfile('/tmp/resolv.conf.auto') ?? '';
	let out = [];
	for (let l in split(text, '\n')) {
		let m = match(l, /^nameserver[ \t]+([0-9.]+)/);
		if (m && m[1] != '127.0.0.1')
			push(out, m[1]);
	}
	return out;
}

// Ответ обычного DNS (UDP 53) сервера; null — нет ответа, [] — имени нет.
function lookup(host, server) {
	let r = sh(`nslookup -type=a ${q(host)} ${q(server)}`);
	if (match(r.out, /timed out|no servers could be reached/))
		return null;
	// Адреса ответа идут после «Name:»; без него ответа нет (NXDOMAIN), а строка «Address» — это сам сервер.
	let after = index(r.out, 'Name:');
	if (after < 0)
		return [];
	let ips = [];
	for (let m in match(substr(r.out, after), /Address:[ \t]*([0-9.]+)/g) ?? [])
		push(ips, m[1]);
	return ips;
}

// Заглушки и служебные адреса — признак подмены.
function bogon(ip) {
	return match(ip, /^(0\.|10\.|127\.|169\.254\.|172\.(1[6-9]|2[0-9]|3[01])\.|192\.168\.|100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.)/) != null;
}

function net16(ip) {
	let p = split(ip, '.');
	return `${p[0]}.${p[1]}`;
}

// Совпадает, если пересекаются /16 (CDN отдают разные адреса — 02-spec §10).
function agrees(a, b) {
	for (let x in a)
		for (let y in b)
			if (net16(x) == net16(y))
				return true;
	return false;
}

function hosts_to_check() {
	let hosts = [];
	let data = st.read_json(`${st.P.share}/targets.json`, { groups: [] });
	for (let g in data.groups)
		if (g.id != 'control')
			for (let t in g.targets)
				push(hosts, pr.host_of(t.url));
	for (let u in st.read_json(`${st.P.etc}/user-targets.json`, []))
		push(hosts, pr.host_of(u.url));
	return uniq([...hosts, ...NEUTRAL]);
}

function doh_timing() {
	let out = [];
	for (let d in [{ name: 'Google', url: 'https://8.8.8.8/resolve?type=A&name=example.com' },
	               { name: 'Cloudflare', url: 'https://1.1.1.1/dns-query?type=A&name=example.com', accept: 'application/dns-json' }]) {
		let r = sh(`curl -sS -o /dev/null --max-time 4 -w '%{http_code} %{time_total}' ${d.accept ? '-H ' + q('accept: ' + d.accept) : ''} ${q(d.url)}`);
		let w = split(trim(r.out), ' ');
		push(out, { name: d.name, doh_ok: w[0] == '200', ms: w[0] == '200' ? int(+w[1] * 1000) : null });
	}
	return out;
}

function mode() {
	return stat('/etc/init.d/https-dns-proxy') && sh('/etc/init.d/https-dns-proxy enabled').rc == 0 ? 'doh' : 'provider';
}

function analyze() {
	let servers = provider_servers();
	let rows = [], spoofed = false;
	for (let h in hosts_to_check()) {
		let ref = pr.doh_resolve(h, {});
		let prov = length(servers) ? lookup(h, servers[0]) : null;
		let ok = prov != null && length(ref) && length(prov) && !length(filter(prov, bogon)) && agrees(prov, ref);
		// Адреса разошлись, но это может быть CDN: проверяем делом — отвечает ли адрес провайдера с верным сертификатом.
		// Не отвечает и по адресу из DoH — мешает DPI, а не DNS: подменой не считаем.
		if (!ok && prov != null && length(prov) && length(ref) && !length(filter(prov, bogon))) {
			let t = { url: `https://${h}/`, handshake_only: true };
			let r = pr.check_batch([{ target: t, tls: '1.3', ip: prov[0] }, { target: t, tls: '1.3', ip: ref[0] }], { parallel: 2 });
			ok = r[0].ok || !r[1].ok;
		}
		let neutral = index(NEUTRAL, h) >= 0;
		if (!ok && !neutral && length(ref) && prov != null)
			spoofed = true;
		push(rows, { host: h, provider: prov, doh: ref, ok: !!ok });
	}
	let res = { checked_at: time(), mode: mode(), provider_servers: servers, spoofed, rows, servers: doh_timing() };
	st.write_atomic(STATUS, sprintf('%J\n', res));
	return res;
}

function status() {
	let s = st.read_json(STATUS, {});
	return {
		mode: mode(), spoofed: s.spoofed ?? null, checked_at: s.checked_at ?? null,
		servers: s.servers ?? [], fallback_active: !!stat('/tmp/dpishell/dns-fallback'),
	};
}

function resolves_locally() {
	return match(sh('nslookup example.com 127.0.0.1').out, /Address:[ \t]*[0-9.]+/) != null;
}

function pkg(action, name) {
	if (stat('/usr/bin/apk'))
		return sh(`apk ${action == 'install' ? 'add' : 'del'} ${q(name)}`);
	return sh(`opkg ${action == 'install' ? 'update >/dev/null; opkg install' : 'remove'} ${q(name)}`);
}

function restore() {
	let b = st.read_json(BACKUP, null);
	if (stat('/etc/init.d/https-dns-proxy'))
		sh('/etc/init.d/https-dns-proxy stop; /etc/init.d/https-dns-proxy disable');
	pkg('remove', 'https-dns-proxy');
	if (b) {
		let c = cursor();
		let sec = b.section ?? c.get_first('dhcp', 'dnsmasq');
		for (let k in ['noresolv', 'server', 'doh_backup_noresolv', 'doh_backup_server', 'doh_server'])
			c.delete('dhcp', sec, k);
		if (b.noresolv != null) c.set('dhcp', sec, 'noresolv', b.noresolv);
		if (b.server != null) c.set('dhcp', sec, 'server', b.server);
		c.commit('dhcp');
		if (b.peerdns != null) { c.set('network', 'wan', 'peerdns', b.peerdns); c.commit('network'); }
	}
	// После удаления пакета dnsmasq сам не перезапускается — DNS роутера пропадает (проверено в эмуляторе).
	sh('/etc/init.d/dnsmasq restart');
	sh('sleep 2');
	if (!resolves_locally()) {
		sh('/etc/init.d/dnsmasq restart');
		sh('sleep 2');
	}
	sh('rm -f /tmp/dpishell/dns-fallback');
	if (resolves_locally()) {
		sh(`rm -f ${q(BACKUP)}`);
		return null;
	}
	return 'DNS роутера не отвечает после возврата — перезагрузите роутер';
}

function enable() {
	let t = doh_timing();
	if (!length(filter(t, x => x.doh_ok)))
		return 'Серверы защищённого DNS недоступны — включать нельзя, сеть останется без DNS';
	if (!stat(BACKUP)) {
		let c = cursor();
		let sec = c.get_first('dhcp', 'dnsmasq');
		st.write_atomic(BACKUP, sprintf('%J\n', {
			at: time(), section: sec,
			noresolv: c.get('dhcp', sec, 'noresolv'), server: c.get('dhcp', sec, 'server'),
			peerdns: c.get('network', 'wan', 'peerdns'),
		}));
	}
	let r = pkg('install', 'https-dns-proxy');
	if (r.rc != 0 && !stat('/etc/init.d/https-dns-proxy'))
		return `Не удалось установить https-dns-proxy: ${trim(r.out)}`;
	sh('/etc/init.d/https-dns-proxy enable; /etc/init.d/https-dns-proxy restart');
	sh('sleep 2');
	if (!resolves_locally()) {
		restore();
		return 'Защищённый DNS не заработал — всё возвращено как было';
	}
	return null;
}


// Таймер: оба DoH-сервера пропали — временно DNS провайдера; вернулись — снова защищённый (06 §3.8).
function watch() {
	if (mode() != 'doh' && !stat('/tmp/dpishell/dns-fallback'))
		return null;
	let up = length(filter(doh_timing(), x => x.doh_ok)) > 0;
	if (!stat('/tmp/dpishell/dns-fallback') && !resolves_locally() && !up) {
		sh('/etc/init.d/https-dns-proxy stop; touch /tmp/dpishell/dns-fallback');
		return 'dns_fallback';
	}
	if (stat('/tmp/dpishell/dns-fallback') && up) {
		sh('/etc/init.d/https-dns-proxy start; rm -f /tmp/dpishell/dns-fallback');
		return 'dns_back';
	}
	return null;
}

export { analyze, status, enable, restore, watch };
