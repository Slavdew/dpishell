// «Безопасность роутера» — справочно, только чтение (router-api §5.1 security_check; исследование — openwrt-hardening.md).
// Решение владельца (22.10): настройки сети DpiShell не меняет; здесь — что защищено (ok), что можно улучшить (warn),
// что не удалось проверить (unknown). Секреты (хеш пароля, ключи Wi-Fi) не выходят из функций — только «задан/не задан».
// Тексты — у окна ПК и страницы по ключам router.security.<id>[.<detail>]; роутер отдаёт id, состояние и сырые числа.
'use strict';

import { readfile, stat } from 'fs';
import { cursor } from 'uci';

function on(v, dflt) {
	if (v == null || v == '')
		return dflt;
	return index(['1', 'on', 'yes', 'true', 'enabled'], lc(`${v}`)) >= 0;
}

function list(v) {
	if (v == null)
		return [];
	if (type(v) == 'array')
		return v;
	return filter(split(`${v}`, /[ ,]+/), x => length(x));
}

function sections(c, conf, typ) {
	let out = [];
	c.foreach(conf, typ, s => { push(out, s); });
	return out;
}

function row(id, state, detail, params) {
	let r = { id, state };
	if (detail)
		r.detail = detail;
	if (params)
		r.params = params;
	return r;
}

// Пароль root: пустое поле в /etc/shadow — не задан. Хеш не копируется дальше этой функции.
function root_password() {
	let text = readfile('/etc/shadow');
	if (text == null)
		return row('root_password', 'unknown');
	for (let line in split(text, '\n'))
		if (substr(line, 0, 5) == 'root:')
			return row('root_password', length(split(line, ':')[1] ?? '') ? 'ok' : 'warn');
	return row('root_password', 'unknown');
}

// Адрес прослушивания без адреса или 0.0.0.0 / [::] — все сети роутера.
function listens_all(addrs) {
	for (let a in addrs) {
		a = trim(a);
		if (match(a, /^[0-9]+$/) || substr(a, 0, 8) == '0.0.0.0:' || substr(a, 0, 5) == '[::]:' || substr(a, 0, 2) == '*:')
			return true;
	}
	return false;
}

function ports_of(addrs) {
	let out = [];
	for (let a in addrs) {
		let m = match(trim(a), /([0-9]+)$/);
		if (m)
			push(out, m[1]);
	}
	return out;
}

// Панель (uhttpd): вход по HTTP и где слушает. Окно ПК входит только по HTTP (RouterClient) — поэтому отдельный пункт.
function uhttpd(c) {
	let http = list(c.get('uhttpd', 'main', 'listen_http')), https = list(c.get('uhttpd', 'main', 'listen_https'));
	if (!length(http) && !length(https))
		return { rows: [row('http_login', 'unknown'), row('panel_listen', 'unknown')], ports: [] };
	let login;
	if (!length(http))
		login = row('http_login', 'ok');
	else if (length(https) && on(c.get('uhttpd', 'main', 'redirect_https'), false))
		login = row('http_login', 'ok', 'redirect');
	else
		login = row('http_login', 'warn', length(https) ? 'https_optional' : null);
	return {
		rows: [login, row('panel_listen', listens_all([...http, ...https]) ? 'warn' : 'ok')],
		ports: [...ports_of(http), ...ports_of(https)],
	};
}

function key_count() {
	let n = 0;
	for (let l in split(readfile('/etc/dropbear/authorized_keys') ?? '', '\n')) {
		l = trim(l);
		if (length(l) && substr(l, 0, 1) != '#')
			n++;
	}
	return n;
}

// SSH (dropbear): где слушает, пускает ли по паролю. Других SSH-серверов не знаем — «не удалось проверить».
function dropbear(c) {
	let ss = filter(sections(c, 'dropbear', 'dropbear'), s => on(s.enable, true));
	if (!length(ss))
		return { rows: [row('ssh_listen', 'unknown'), row('ssh_password', 'unknown')], ports: [] };
	let all = false, pass = false, ports = [];
	for (let s in ss) {
		if (s.Interface == null || s.Interface == '')
			all = true;
		if (on(s.PasswordAuth, true) && on(s.RootPasswordAuth, true))
			pass = true;
		push(ports, `${s.Port ?? 22}`);
	}
	let nkeys = key_count();
	return {
		rows: [
			row('ssh_listen', all ? 'warn' : 'ok'),
			pass ? row('ssh_password', 'warn', nkeys ? 'warn_keys' : null) : row('ssh_password', 'ok'),
		],
		ports,
	};
}

function port_in(spec, ports) {
	for (let part in list(spec)) {
		let m = match(part, /^([0-9]+)[-:]([0-9]+)$/);
		for (let p in ports)
			if (m ? (+p >= +m[1] && +p <= +m[2]) : part == p)
				return true;
	}
	return false;
}

// Межсетевой экран: зона wan, правила к роутеру с wan на порты панели/SSH, перенаправления портов.
function firewall(c, admin_ports) {
	let defs = sections(c, 'firewall', 'defaults')[0] ?? {};
	let wan = null;
	for (let z in sections(c, 'firewall', 'zone'))
		if (z.name == 'wan' || index(list(z.network), 'wan') >= 0) { wan = z; break; }
	if (!wan)
		return [row('wan_firewall', 'unknown'), row('wan_admin', 'unknown'), row('port_forwards', 'unknown')];
	let input = wan.input ?? defs.input, fwd = wan.forward ?? defs.forward;
	let fw = input == null || fwd == null ? 'unknown' : (input == 'ACCEPT' || fwd == 'ACCEPT') ? 'warn' : 'ok';

	let open = [];
	for (let r in sections(c, 'firewall', 'rule')) {
		if (!on(r.enabled, true) || r.src != wan.name || r.dest != null || r.target != 'ACCEPT')
			continue;
		let proto = lc(`${r.proto ?? 'tcp udp'}`);
		if (!match(proto, /tcp|all/))
			continue;
		if (r.dest_port == null) { open = ['*']; break; }
		for (let p in uniq(admin_ports))
			if (port_in(r.dest_port, [p]) && index(open, p) < 0)
				push(open, p);
	}
	let fwds = 0;
	for (let r in sections(c, 'firewall', 'redirect'))
		if (on(r.enabled, true) && (r.target ?? 'DNAT') == 'DNAT' && r.src == wan.name)
			fwds++;
	return [
		row('wan_firewall', fw),
		length(open) ? row('wan_admin', 'warn', open[0] == '*' ? 'warn_all' : null, open[0] == '*' ? null : { ports: join(', ', sort(open, (a, b) => +a - +b)) })
			: row('wan_admin', 'ok'),
		fwds ? row('port_forwards', 'warn', null, { n: fwds }) : row('port_forwards', 'ok'),
	];
}

function upnp(c) {
	if (!stat('/etc/config/upnpd'))
		return row('upnp', 'ok', 'none');
	if (!on(c.get('upnpd', 'config', 'enabled'), false))
		return row('upnp', 'ok');
	return row('upnp', 'warn', on(c.get('upnpd', 'config', 'secure_mode'), true) ? null : 'warn_insecure');
}

// Wi-Fi: только точки доступа, не выключенные. Имена сетей и ключи не выходят — только число сетей.
function wifi(c) {
	let open = 0, weak = 0, total = 0;
	for (let s in sections(c, 'wireless', 'wifi-iface')) {
		if (on(s.disabled, false) || (s.mode ?? 'ap') != 'ap')
			continue;
		total++;
		let enc = lc(`${s.encryption ?? 'none'}`), base = split(enc, '+')[0];
		if (base == 'none' || base == '')
			open++;
		else if (substr(base, 0, 3) == 'wep' || index(['psk', 'psk-mixed', 'wpa', 'wpa-mixed'], base) >= 0 || index(enc, 'tkip') >= 0)
			weak++;
	}
	if (!total)
		return null;
	if (open)
		return row('wifi_encryption', 'warn', 'open', { n: open });
	if (weak)
		return row('wifi_encryption', 'warn', 'weak', { n: weak });
	return row('wifi_encryption', 'ok');
}

// dnsmasq: защита от DNS rebind (по умолчанию в init-скрипте — включена) и ответы на внешнем адресе.
function dnsmasq(c) {
	let s = sections(c, 'dhcp', 'dnsmasq')[0];
	if (!s)
		return [row('dns_rebind', 'unknown'), row('dns_wan', 'unknown')];
	let bound = index(list(s.notinterface), 'wan') >= 0 || length(list(s.interface)) > 0;
	return [
		row('dns_rebind', on(s.rebind_protection, true) ? 'ok' : 'warn'),
		bound ? row('dns_wan', 'ok') : on(s.localservice, false) ? row('dns_wan', 'ok', 'local') : row('dns_wan', 'warn'),
	];
}

function check() {
	let c = cursor();
	let rows = [root_password()];
	let u = uhttpd(c), d = dropbear(c);
	push(rows, u.rows[0], u.rows[1], d.rows[0], d.rows[1]);
	for (let r in firewall(c, [...u.ports, ...d.ports]))
		push(rows, r);
	push(rows, upnp(c));
	let w = wifi(c);
	if (w)
		push(rows, w);
	for (let r in dnsmasq(c))
		push(rows, r);
	return { checks: rows, checked_at: time() };
}

export { check };
