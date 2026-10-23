// DNS для всей сети (architecture.md «DNS для всей сети»): анализ подмены, варианты DNS (от провайдера, Google, Cloudflare, Quad9, Яндекс, свой)
// по кнопке, возврат как было. DoH — через https-dns-proxy: ставится только по кнопке, при установке сам переключает
// dnsmasq (проверено в эмуляторе). Вариант только с обычным DNS — серверы dnsmasq, прокси выключен.
'use strict';

import { readfile, writefile, stat, popen, unlink } from 'fs';
import { cursor } from 'uci';
import * as st from 'dpishell.state';
import * as pr from 'dpishell.probe';
import * as pk from 'dpishell.pkgs';

const STATUS = '/etc/dpishell/dns-status.json';
const BACKUP = '/etc/dpishell/dns-backup.json';
const CUSTOM = '/etc/dpishell/dns-custom.json';
const NEUTRAL = ['example.com', 'wikipedia.org'];
// Запрос A example.com в формате RFC 8484 (GET ?dns=): понимают все DoH-серверы, в отличие от JSON.
const PROBE_QUERY = 'AAABAAABAAAAAAAAB2V4YW1wbGUDY29tAAABAAE';
const FIRST_PORT = 5053;
const PROXY_CONF = '/etc/config/https-dns-proxy';
// Полные копии настроек в ОЗУ до изменений (weak-router-test.md: uci commit на полном флеше оставлял пустой dhcp).
const SNAP = '/tmp/dpishell/dns-snap';
const SNAP_FILES = ['dhcp', 'network', 'https-dns-proxy'];
// Место для установки прокси: пакеты из индекса + запас, не меньше 2 МБ; без установки (только uci commit) — 256 КБ.
const PROXY_MIN_KB = 2048;
const CONF_MIN_KB = 256;

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

// Совпадает, если пересекаются /16 (CDN отдают разные адреса — docs/research/dns.md).
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

// Варианты DNS: файл пакета + свой (последний заданный из приложения или страницы).
function presets() {
	return st.read_json(`${st.P.share}/dns-presets.json`, { presets: [] }).presets;
}

function custom() {
	return st.read_json(CUSTOM, null);
}

function preset_of(id) {
	if (id == 'custom') {
		let c = custom();
		return c ? { ...c, id: 'custom' } : null;
	}
	return filter(presets(), p => p.id == id)[0];
}

function proxy_enabled() {
	return !!stat('/etc/init.d/https-dns-proxy') && sh('/etc/init.d/https-dns-proxy enabled').rc == 0;
}

// Текущий вариант; до появления вариантов «защищённый DNS» = default (Google + Cloudflare).
function current_id() {
	let id = trim(sh('uci -q get dpishell.main.dns_preset').out);
	if (length(id) && preset_of(id))
		return id;
	return proxy_enabled() ? 'default' : 'provider';
}

function mode() {
	let p = preset_of(current_id());
	if (!p || p.id == 'provider')
		return 'provider';
	return length(p.resolvers ?? []) ? 'doh' : 'plain';
}

// Отвечает ли DoH-сервер: запрос по адресу из bootstrap (DNS провайдера подменяет ответы — docs/research/dns.md).
function doh_check(r) {
	let host = pr.host_of(r.doh);
	let ip = filter(r.bootstrap ?? [], x => match(x, /^[0-9.]+$/))[0];
	let res = ip ? `--resolve ${q(`${host}:443:${ip}`)}` : '';
	let out = sh(`curl -sS -o /dev/null --max-time 4 -w '%{http_code} %{time_total}' ${res} -H 'accept: application/dns-message' ${q(`${r.doh}?dns=${PROBE_QUERY}`)}`);
	let w = split(trim(out.out), ' ');
	let ok = w[0] == '200';
	return { ok, ms: ok ? int(+w[1] * 1000) : null };
}

// Доступность и скорость DoH-серверов всех вариантов (для выбора) или заданного списка.
function doh_timing(list) {
	let out = [];
	for (let p in list ?? filter(presets(), p => length(p.resolvers ?? []) == 1))
		for (let r in p.resolvers ?? []) {
			let c = doh_check(r);
			push(out, { id: p.id, name: p.title, doh_ok: c.ok, ms: c.ms });
		}
	return out;
}

// «Сравнить скорость» (dns_compare, как на ПК): время ответа защищённого DNS каждого варианта, в т. ч. своего.
// Запросы — параллельно (curl по 4 с), чтобы вызов ubus уложился в свой срок; у варианта с двумя серверами — быстрейший.
function compare() {
	let list = filter(presets(), p => length(p.resolvers ?? []));
	let c = custom();
	if (c && length(c.resolvers ?? []))
		push(list, { id: 'custom', resolvers: c.resolvers });
	let cmds = [];
	for (let i = 0; i < length(list); i++)
		for (let r in list[i].resolvers) {
			let host = pr.host_of(r.doh);
			let ip = filter(r.bootstrap ?? [], x => match(x, /^[0-9.]+$/))[0];
			let res = ip ? `--resolve ${q(`${host}:443:${ip}`)}` : '';
			push(cmds, `( echo "${i} $(curl -sS -o /dev/null --max-time 4 -w '%{http_code} %{time_total}' ${res} -H 'accept: application/dns-message' ${q(`${r.doh}?dns=${PROBE_QUERY}`)} 2>/dev/null)" ) &`);
		}
	let out = sh(`${join('\n', cmds)}\nwait`).out;
	let best = {};
	for (let line in split(trim(out), '\n')) {
		let w = split(trim(line), ' ');
		let i = +w[0];
		if (w[1] == '200' && list[i]) {
			let ms = int(+w[2] * 1000);
			if (best[i] == null || ms < best[i])
				best[i] = ms;
		}
	}
	return map(list, (p, i) => ({ id: p.id, ms: best[i] ?? null }));
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
		mode: mode(), preset: current_id(), custom: custom(),
		spoofed: s.spoofed ?? null, checked_at: s.checked_at ?? null,
		servers: s.servers ?? [], fallback_active: !!stat('/tmp/dpishell/dns-fallback'),
	};
}

function resolves_locally() {
	return match(sh('nslookup example.com 127.0.0.1').out, /Address:[ \t]*[0-9.]+/) != null;
}

function pkg_remove(name) {
	if (stat('/usr/bin/apk'))
		return sh(`apk del ${q(name)}`);
	return sh(`opkg remove ${q(name)}`);
}

function dnsmasq_restart_checked() {
	sh('/etc/init.d/dnsmasq restart');
	sh('sleep 2');
	if (!resolves_locally()) {
		sh('/etc/init.d/dnsmasq restart');
		sh('sleep 2');
	}
	return resolves_locally();
}

// DNS роутера жив: dnsmasq запущен и отвечает через 127.0.0.1.
function dns_alive() {
	return sh('pidof dnsmasq').rc == 0 && resolves_locally();
}

// Файл настроек цел: не пустой и uci его читает (пустой dhcp — dnsmasq не стартует: нет DNS и DHCP в сети).
function conf_ok(n) {
	let s = stat(`/etc/config/${n}`);
	if (!s || s.size <= 0)
		return false;
	let r = sh(`uci -q show ${q(n)}`);
	return r.rc == 0 && length(trim(r.out)) > 0;
}

// Вернуть файл настроек целиком; на месте (без .tmp), если на атомарную запись места нет.
function conf_put(n, text) {
	let f = `/etc/config/${n}`;
	if (!st.write_atomic(f, text))
		writefile(f, text);
	return conf_ok(n);
}

// Полные копии dhcp, network и https-dns-proxy в ОЗУ — до любых изменений. Испорченный файл копию не затирает;
// пропавший файл прокси — убирает копию (прокси удалён). dhcp уже испорчен (сбой прежней версии) — вернуть копию из
// dns-backup.json (с r17 она там есть).
function snap_take() {
	st.own_dir('/tmp/dpishell', 0o700);
	st.own_dir(SNAP, 0o700);
	for (let n in SNAP_FILES) {
		if (conf_ok(n))
			writefile(`${SNAP}/${n}`, readfile(`/etc/config/${n}`));
		else if (!stat(`/etc/config/${n}`) && n == 'https-dns-proxy')
			unlink(`${SNAP}/${n}`);
	}
	let b = st.read_json(BACKUP, null);
	if (!conf_ok('dhcp') && type(b?.dhcp_file) == 'string' && length(b.dhcp_file)) {
		if (!stat(`${SNAP}/dhcp`))
			writefile(`${SNAP}/dhcp`, b.dhcp_file);
		conf_put('dhcp', readfile(`${SNAP}/dhcp`));
	}
}

// После каждого изменения настроек (наш uci commit, старт/стоп/удаление прокси — он сам правит dhcp): файл пуст или не
// читается — вернуть копию целиком и перезапустить dnsmasq. Файл прокси проверяется, только если он есть.
function guard() {
	let fixed = false;
	for (let n in SNAP_FILES) {
		let copy = readfile(`${SNAP}/${n}`);
		if (copy == null || (n == 'https-dns-proxy' && !stat(`/etc/config/${n}`)) || conf_ok(n))
			continue;
		conf_put(n, copy);
		fixed = true;
	}
	if (fixed)
		dnsmasq_restart_checked();
	return fixed;
}

// DNS после неудачи не ожил — честно и с инструкцией, как вернуть (decisions-owner-2026-10-20.md, «оборвать связь»).
function broken_msg(why) {
	let hint = `cp ${SNAP}/dhcp /etc/config/dhcp; /etc/init.d/dnsmasq restart`;
	return pk.msg('web.err.dns_broken', { error: why, cmd: hint },
		`${pk.text_of(why)}. DNS роутера не отвечает. Как вернуть: освободите место во флеше и нажмите «Вернуть как было» на вкладке DNS; если не помогло — по ssh до перезагрузки: ${hint}`,
		type(why) == 'object' ? why.detail : null);
}

// Копия могла быть снята, когда https-dns-proxy уже стоял (поставлен вручную): его адреса 127.0.0.1#505x
// и «канарейки» /mask.icloud.com/… — не настройки пользователя, а следы прокси. Без них — и noresolv прокси.
function clean_backup(b) {
	if (!b)
		return b;
	let srv = b.server == null ? null : (type(b.server) == 'array' ? b.server : [b.server]);
	if (srv) {
		let kept = filter(srv, s => !match(s, /^127\.0\.0\.1#50[0-9][0-9]$/) && !match(s, /^\/(mask\.icloud\.com|mask-h2\.icloud\.com|use-application-dns\.net)\/$/));
		if (length(kept) != length(srv))
			b = { ...b, noresolv: null };
		b = { ...b, server: length(kept) ? kept : null };
	}
	return b;
}

// Секции https-dns-proxy как есть (для копии до первого изменения): прокси мог стоять и быть настроен до DpiShell.
function proxy_sections() {
	let c = cursor(), out = [];
	c.load('https-dns-proxy');
	c.foreach('https-dns-proxy', 'https-dns-proxy', s => {
		let o = {};
		for (let k in s)
			if (substr(k, 0, 1) != '.')
				o[k] = s[k];
		push(out, o);
	});
	return out;
}

// Вернуть прокси, поставленный не нами (security-audit-router №6): его секции и включён/выключен — как в копии.
function proxy_put_back(px) {
	let c = cursor();
	c.load('https-dns-proxy');
	let old = [];
	c.foreach('https-dns-proxy', 'https-dns-proxy', s => { push(old, s['.name']); });
	for (let n in old)
		c.delete('https-dns-proxy', n);
	for (let o in px.sections ?? []) {
		let n = c.add('https-dns-proxy', 'https-dns-proxy');
		for (let k in o)
			c.set('https-dns-proxy', n, k, o[k]);
	}
	c.commit('https-dns-proxy');
	sh(px.enabled ? '/etc/init.d/https-dns-proxy enable; /etc/init.d/https-dns-proxy restart'
		: '/etc/init.d/https-dns-proxy stop; /etc/init.d/https-dns-proxy disable');
}

function restore() {
	snap_take();
	let b = clean_backup(st.read_json(BACKUP, null));
	// Пакет и его настройки убираем, только если ставили сами. Копия до r12 (без proxy) — как раньше: прокси наш.
	// Копии нет — DNS не меняли; прокси убираем, лишь если включён наш вариант.
	let ours = b ? !b.proxy?.installed : (cursor().get('dpishell', 'main', 'dns_preset') ?? 'provider') != 'provider';
	let theirs = b?.proxy?.installed && stat('/etc/init.d/https-dns-proxy');
	if (!theirs && ours) {
		if (stat('/etc/init.d/https-dns-proxy'))
			sh('/etc/init.d/https-dns-proxy stop; /etc/init.d/https-dns-proxy disable');
		guard();
		pkg_remove('https-dns-proxy');
		guard();
		// Настройки прокси — как до нас: apk при удалении оставляет изменённый файл настроек.
		if (b?.proxy && exists(b.proxy, 'config')) {
			if (b.proxy.config != null)
				st.write_atomic(PROXY_CONF, b.proxy.config);
			else
				unlink(PROXY_CONF);
			unlink(`${PROXY_CONF}.apk-new`);
		}
	}
	// Чужой прокси останавливаем до возврата dnsmasq (stop сам возвращает свои правки dnsmasq), а запускаем — после:
	// при запуске он снова пропишет себя в dnsmasq, как было до нас.
	if (theirs) {
		sh('/etc/init.d/https-dns-proxy stop');
		guard();
	}
	if (b) {
		let c = cursor();
		let sec = b.section ?? c.get_first('dhcp', 'dnsmasq');
		for (let k in ['noresolv', 'server', 'doh_backup_noresolv', 'doh_backup_server', 'doh_server'])
			c.delete('dhcp', sec, k);
		if (b.noresolv != null) c.set('dhcp', sec, 'noresolv', b.noresolv);
		if (b.server != null) c.set('dhcp', sec, 'server', b.server);
		c.commit('dhcp');
		guard();
		if (b.peerdns != null && c.get('network', 'wan', 'peerdns') != b.peerdns) { c.set('network', 'wan', 'peerdns', b.peerdns); c.commit('network'); guard(); }
	}
	if (theirs) {
		proxy_put_back(b.proxy);
		guard();
	}
	// После удаления пакета dnsmasq сам не перезапускается — DNS роутера пропадает (проверено в эмуляторе).
	let ok = dnsmasq_restart_checked() || (guard() && resolves_locally());
	sh('rm -f /tmp/dpishell/dns-fallback');
	if (ok && dns_alive()) {
		sh(`rm -f ${q(BACKUP)}`);
		// «Как было» — без своей записи (по умолчанию и так провайдер): настройки — как до первого изменения DNS.
		st.uci_set({ dns_preset: null });
		return null;
	}
	return broken_msg(pk.msg('web.err.dns_restore', {}, 'DNS провайдера вернуть не удалось'));
}

// Прежние настройки dnsmasq — один раз, до первого изменения (возврат — restore()).
function backup() {
	if (stat(BACKUP))
		return;
	let c = cursor();
	let sec = c.get_first('dhcp', 'dnsmasq');
	// Прокси, поставленный до нас: его секции и включён ли — restore вернёт их, а пакет не удалит.
	// Прокси нет, а его настройки остались (пакет удаляли, apk оставил изменённый файл): файл как есть — restore вернёт
	// его (config: null — файла не было, restore уберёт созданный нами). С r14.
	let px = stat('/etc/init.d/https-dns-proxy')
		? { installed: true, enabled: sh('/etc/init.d/https-dns-proxy enabled').rc == 0, sections: proxy_sections() }
		: { installed: false, config: readfile(PROXY_CONF) };
	st.write_atomic(BACKUP, sprintf('%J\n', {
		at: time(), section: sec,
		noresolv: c.get('dhcp', sec, 'noresolv'), server: c.get('dhcp', sec, 'server'),
		peerdns: c.get('network', 'wan', 'peerdns'), proxy: px,
		// Весь dhcp как есть (~1 КБ, с r17): испорченный после сбоя файл snap_take вернёт целиком и после перезагрузки.
		dhcp_file: conf_ok('dhcp') ? readfile('/etc/config/dhcp') : null,
	}));
}

// Обычные DNS-серверы для dnsmasq (вариант без DoH) или возврат к прежним (null).
function set_plain(list) {
	let b = clean_backup(st.read_json(BACKUP, {}));
	let c = cursor();
	let sec = b.section ?? c.get_first('dhcp', 'dnsmasq');
	c.delete('dhcp', sec, 'noresolv');
	c.delete('dhcp', sec, 'server');
	if (list) {
		c.set('dhcp', sec, 'noresolv', '1');
		c.set('dhcp', sec, 'server', list);
	} else {
		if (b.noresolv != null) c.set('dhcp', sec, 'noresolv', b.noresolv);
		if (b.server != null) c.set('dhcp', sec, 'server', b.server);
	}
	c.commit('dhcp');
}

// Резолверы https-dns-proxy: свои секции вместо прежних, порты 5053, 5054…
function set_resolvers(list) {
	let c = cursor();
	c.load('https-dns-proxy');
	let old = [];
	c.foreach('https-dns-proxy', 'https-dns-proxy', s => { push(old, s['.name']); });
	for (let n in old)
		c.delete('https-dns-proxy', n);
	for (let i = 0; i < length(list); i++) {
		let n = c.add('https-dns-proxy', 'https-dns-proxy');
		c.set('https-dns-proxy', n, 'resolver_url', list[i].doh);
		// С одним адресом bootstrap прокси падает в цикле (c-ares: «needed more IO event handler, than the number
		// of provided nameservers: 1», проверено в эмуляторе) — тот же адрес дважды, чужой сервер не подставляем.
		let boot = list[i].bootstrap ?? [];
		if (length(boot) == 1)
			boot = [boot[0], boot[0]];
		if (length(boot))
			c.set('https-dns-proxy', n, 'bootstrap_dns', join(',', boot));
		c.set('https-dns-proxy', n, 'listen_port', `${FIRST_PORT + i}`);
	}
	c.commit('https-dns-proxy');
}

// Включить вариант DNS. Не заработал — вернуть предыдущий (или как было у провайдера). null — успех.
function apply(id) {
	let p = preset_of(id);
	if (!p)
		return 'Нет такого варианта DNS';
	if (id == 'provider')
		return restore();
	let prev = current_id();
	let res = p.resolvers ?? [];
	if (length(res) && !length(filter(res, r => doh_check(r).ok)))
		return `Серверы «${p.title}» не отвечают — включать нельзя, сеть останется без DNS`;
	// Место — до любых изменений: прокси с зависимостями (размеры из индекса apk) + запас, не меньше 2 МБ; иначе честный
	// отказ без изменений (на полном флеше apk ставил прокси без libcares, а uci commit оставлял пустой dhcp).
	let install = length(res) && !stat('/etc/init.d/https-dns-proxy');
	let e = null;
	if (install) {
		// Меньше нижней границы — отказ сразу, без apk (на полном флеше и его запросы могут сбоить).
		e = pk.space_check(PROXY_MIN_KB);
		if (e)
			return e;
		if (stat('/usr/bin/apk')) {
			let u = sh('apk update');
			if (u.rc != 0 && match(u.out, /No space left/))
				return pk.apk_error(u.out, 'https-dns-proxy');
		}
		let plan = pk.plan(['https-dns-proxy']);
		if (plan.error)
			return plan.error;
		e = pk.space_check(pk.need_kb(plan, PROXY_MIN_KB));
	} else
		e = pk.space_check(CONF_MIN_KB);
	if (e)
		return e;
	snap_take();
	let fresh = !stat(BACKUP);
	backup();
	if (install) {
		// Прокси, установившийся с ошибкой (нет библиотеки — «Error relocating»), не считается установленным.
		e = pk.add(['https-dns-proxy'], 'https-dns-proxy', () => {
			let r = sh('/usr/sbin/https-dns-proxy -h');
			return !stat('/usr/sbin/https-dns-proxy') || match(r.out, /Error relocating|not found|No such file|symbol/) ? trim(r.out) || 'нет /usr/sbin/https-dns-proxy' : null;
		});
		if (e) {
			// Поставленное убрано (pk.add); настройки — из копии, если прокси успел их испортить; копия «до» — не нужна.
			guard();
			if (fresh && prev == 'provider')
				unlink(BACKUP);
			if (!dns_alive() && !dnsmasq_restart_checked() && !dns_alive())
				return broken_msg(e);
			return e;
		}
	}
	if (length(res)) {
		// Сначала прокси останавливаем (он возвращает dnsmasq как было), затем — свои резолверы и запуск.
		sh('/etc/init.d/https-dns-proxy stop');
		guard();
		set_plain(null);
		guard();
		set_resolvers(res);
		guard();
		sh('/etc/init.d/https-dns-proxy enable; /etc/init.d/https-dns-proxy restart');
		guard();
		// Прокси поднимает соединение с сервером не сразу (свой вариант после обычного DNS — до ~10 с).
		for (let i = 0; i < 15 && !resolves_locally(); i++)
			sleep(1000);
	} else {
		if (stat('/etc/init.d/https-dns-proxy'))
			sh('/etc/init.d/https-dns-proxy stop; /etc/init.d/https-dns-proxy disable');
		guard();
		set_plain(p.plain);
		guard();
		dnsmasq_restart_checked();
	}
	sh('rm -f /tmp/dpishell/dns-fallback');
	if (resolves_locally()) {
		st.uci_set({ dns_preset: id });
		return null;
	}
	let back = (prev != id && prev != 'provider') ? apply(prev) : restore();
	let why = pk.msg('web.err.dns_failed', { title: p.title }, `DNS «${p.title}» не заработал`);
	// «Возвращено как было» — только если DNS роутера действительно отвечает.
	if (!back && dns_alive())
		return pk.msg('web.err.dns_reverted', { title: p.title }, `DNS «${p.title}» не заработал — возвращено как было`);
	return broken_msg(why);
}

// Прежняя кнопка «Включить защищённый DNS» (router-api §5): Google + Cloudflare.
function enable() {
	return apply('default');
}

// Таймер: DoH-серверы текущего варианта пропали — временно DNS провайдера; вернулись — снова защищённый (architecture.md «DNS для всей сети»).
function watch() {
	let p = preset_of(current_id());
	if (!p || !length(p.resolvers ?? []) || (!proxy_enabled() && !stat('/tmp/dpishell/dns-fallback')))
		return null;
	let up = length(filter(p.resolvers, r => doh_check(r).ok)) > 0;
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

export { analyze, status, enable, restore, watch, apply, presets, current_id, compare, CUSTOM, BACKUP };
