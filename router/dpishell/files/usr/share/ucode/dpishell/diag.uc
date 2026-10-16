// Диагностика, отчёт, чужие обходы, проверка одного сайта (06 §3.4, §3.6; 07 §5).
'use strict';

import { readfile, stat, popen, lsdir, readlink } from 'fs';
import * as st from 'dpishell.state';
import * as pr from 'dpishell.probe';

// Чужие обходы, которые мешают нашему (06 §3.6). Имя — init-скрипт в /etc/init.d.
const CONFLICTS = ['zapret2', 'youtubeUnblock', 'b4', 'zapret-manager', 'zapret-manager-beam', 'byedpi', 'podkop'];

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function q(s) {
	return `'${replace(`${s}`, "'", "'\\''")}'`;
}

function conflicts() {
	let out = [];
	for (let n in CONFLICTS)
		if (stat(`/etc/init.d/${n}`))
			push(out, { kind: 'service', name: n, running: sh(`/etc/init.d/${n} running`).rc == 0 });
	// nfqws/nfqws2 не из нашего ядра.
	for (let pid in lsdir('/proc') ?? []) {
		if (!match(pid, /^[0-9]+$/))
			continue;
		let exe = readlink(`/proc/${pid}/exe`);
		if (exe && match(exe, /\/nfqws2?$/) && exe != `${st.P.zapret}/nfq/nfqws`)
			push(out, { kind: 'process', name: exe, running: true });
	}
	return out;
}

function conflict_stop(name) {
	if (index(CONFLICTS, name) < 0 || !stat(`/etc/init.d/${name}`))
		return 'Нет такой службы';
	sh(`/etc/init.d/${name} stop; /etc/init.d/${name} disable`);
	return null;
}

function flow_offload() {
	return trim(sh('uci -q get firewall.@defaults[0].flow_offloading').out) == '1';
}

function ipv6_wan() {
	let dev = trim(sh(". /lib/functions/network.sh; network_get_device d wan6 || network_get_device d wan; echo $d").out);
	return dev != '' && match(sh(`ip -6 addr show dev ${q(dev)} scope global`).out, /inet6/) != null;
}

function overlay_free_kb() {
	let m = match(sh('df -k /overlay 2>/dev/null || df -k /').out, /\n[^ ]+ +[0-9]+ +[0-9]+ +([0-9]+)/);
	return m ? +m[1] : null;
}

function diag() {
	let dns = st.read_json(`${st.P.etc}/dns-status.json`, null);
	return {
		conflicts: conflicts(),
		flow_offload: flow_offload(),
		ipv6_wan: ipv6_wan(),
		dns: { spoofed: dns?.spoofed ?? null, checked_at: dns?.checked_at ?? null },
		overlay_free_kb: overlay_free_kb(),
	};
}

function report() {
	let parts = [];
	let add = (title, text) => push(parts, `== ${title} ==\n${trim(text ?? '')}\n`);
	add('Версия', `dpishell ${st.VERSION}\n${readfile('/etc/openwrt_release')}`);
	add('Настройки', sprintf('%.2J', st.settings()));
	add('Ядро', readfile(`${st.P.etc}/core-installed.json`));
	add('Диагностика', sprintf('%.2J', diag()));
	add('config zapret', readfile(st.P.zapret_config));
	add('nft inet zapret', sh('nft list table inet zapret').out);
	add('Последняя проверка', readfile(`${st.P.etc}/last-check.json`));
	add('Последний подбор', readfile('/tmp/dpishell/select.json'));
	add('Журнал', sh("logread | grep -iE 'zapret|nfqws|dpishell' | tail -n 80").out);
	return join('\n', parts);
}

// «Сайт не открывается с обходом»: с обходом и без (адрес временно в наборе nozapret zapret — остальная сеть не затронута).
function site_check(url) {
	let host = pr.host_of(url);
	let ips = pr.doh_resolve(host, {});
	if (!length(ips))
		return { running: false, url, error: 'Не удалось узнать адрес сайта', with_bypass: { ok: false, detail: '' }, without_bypass: { ok: false, detail: '' }, suggest_exclude: false };
	let t = { url };
	let tls = ['1.3'];
	let with_b = pr.check_batch(map(tls, v => ({ target: t, tls: v })), { parallel: 2 });
	let added = [];
	for (let ip in ips)
		if (sh(`nft add element inet zapret nozapret { ${ip} }`).rc == 0)
			push(added, ip);
	let without = pr.check_batch(map(tls, v => ({ target: t, tls: v })), { parallel: 2 });
	for (let ip in added)
		sh(`nft delete element inet zapret nozapret { ${ip} }`);
	let w = with_b[0], wo = without[0];
	return {
		running: false, url,
		with_bypass: { ok: w.ok, detail: w.detail },
		without_bypass: { ok: wo.ok, detail: length(added) ? wo.detail : 'обход для адреса отключить не удалось' },
		suggest_exclude: !w.ok && wo.ok && length(added) > 0,
	};
}

export { conflicts, conflict_stop, diag, report, site_check, flow_offload };
