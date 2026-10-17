// Состояние пакета: пути, настройки uci, свои данные, запись config zapret и status.json.
'use strict';

import { readfile, writefile, rename, mkdir, stat, lsdir, symlink, unlink, access, chmod, readlink } from 'fs';
import { cursor } from 'uci';
import * as cat from 'dpishell.catalog';

const P = {
	share: '/usr/share/dpishell',
	etc: '/etc/dpishell',
	lists: '/etc/dpishell/lists',       // {lists} для zapret: свои файлы + ссылки на стандартные
	fake: '/usr/share/dpishell/fake',
	run: '/tmp/dpishell',
	zapret: '/opt/zapret',
	zapret_config: '/opt/zapret/config',
	status_json: '/www/dpishell/status.json',
};

const VERSION = '0.2.0';
// api — «мажор.минор» (router-api.md §5): минор растёт при добавлениях без поломок, мажор — при несовместимости.
const API = '1.0';
// Минимальная версия окна ПК, которую понимает этот роутер.
const MIN_APP = '1.0.0';
const MODES = ['all', 'lists'];
const HOURS = [0.5, 1, 3, 6, 12, 24];
const USER_LISTS = ['user-general', 'user-exclude', 'user-ipset', 'user-ipset-exclude'];
const STD_LISTS = ['general.txt', 'google.txt', 'exclude.txt', 'ipset-exclude.txt', 'ipset-all.txt'];
const DOMAIN_RX = /^\^?(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+(xn--[a-z0-9-]{2,59}|[a-z]{2,63})$/;
const SUBNET_RX = /^([0-9]{1,3}\.){3}[0-9]{1,3}(\/[0-9]{1,2})?$/;

// nfqws работает от пользователя daemon (WS_USER zapret): списки и фейки должны быть ему читаемы.
// Свои настройки и результаты — только root; status.json — публичный для uhttpd (router-api §2).
const DIRS = { '/etc/dpishell': 0o711, '/etc/dpishell/lists': 0o755, '/etc/dpishell/results': 0o700, '/tmp/dpishell': 0o700, '/www/dpishell': 0o755 };

function ensure_dirs() {
	for (let d in DIRS) {
		if (!stat(d))
			mkdir(d, DIRS[d]);
		chmod(d, DIRS[d]);
	}
	// Заглушка IP-списка, как на Windows (IpLists.Placeholder): тестовая сеть, профили ipset ничего не ловят.
	if (!stat(`${P.lists}/ipset-none.txt`))
		writefile(`${P.lists}/ipset-none.txt`, '203.0.113.113/32\n');
	// Стандартные списки — ссылками из пакета, свои — пустыми файлами: zapret ждёт все пути из профилей.
	for (let f in STD_LISTS) {
		let dst = `${P.lists}/${f}`, src = `${P.share}/lists/${f}`;
		if (!stat(dst) && stat(src))
			symlink(src, dst);
	}
	for (let n in USER_LISTS)
		if (!stat(`${P.lists}/${n}.txt`))
			writefile(`${P.lists}/${n}.txt`, '');
	for (let f in lsdir(P.lists) ?? [])
		if (!readlink(`${P.lists}/${f}`))
			chmod(`${P.lists}/${f}`, 0o644);
}

function write_atomic(path, text) {
	let tmp = `${path}.tmp`;
	// writefile возвращает число записанных байт: 0 у пустого файла — не ошибка.
	if (writefile(tmp, text) == null)
		return false;
	return rename(tmp, path);
}

function read_json(path, dflt) {
	let t = readfile(path);
	if (t == null)
		return dflt;
	try { return json(t); } catch (e) { return dflt; }
}

function uci_get(opt, dflt) {
	let c = cursor();
	let v = c.get('dpishell', 'main', opt);
	return v == null ? dflt : v;
}

function uci_set(values) {
	let c = cursor();
	for (let k in values) {
		let v = values[k];
		if (v == null)
			c.delete('dpishell', 'main', k);
		else
			c.set('dpishell', 'main', k, type(v) == 'array' ? v : `${v}`);
	}
	c.commit('dpishell');
}

function settings() {
	let backups = uci_get('backups', []);
	return {
		enabled: uci_get('enabled', '0') == '1',
		strategy: uci_get('strategy', '') || null,
		backups: type(backups) == 'array' ? backups : [backups],
		mode: uci_get('mode', 'all'),
		ipv6: uci_get('ipv6', '0') == '1',
		flowoffload: uci_get('flowoffload', 'none'),
		schedule: {
			enabled: uci_get('schedule_enabled', '1') == '1',
			hours: +uci_get('schedule_hours', '1'),
			auto_select: uci_get('auto_select', '0') == '1',
		},
	};
}

function roles() { return cat.load_json(`${P.share}/roles.json`); }
function catalog() { return cat.load_json(`${P.share}/strategies.json`); }
function user_catalog() { return read_json(`${P.etc}/user-strategies.json`, { strategies: [] }); }
// Найденные глубоким поиском на роутере (id r-…): отдельный файл, чтобы strategies_user_set приложения их не затирал.
function found_catalog() { return read_json(`${P.etc}/found-strategies.json`, { strategies: [] }); }
function catalogs() { return [catalog(), user_catalog(), found_catalog()]; }

function build_opts(s) {
	return {
		mode: s.mode, lists_dir: P.lists, fake_dir: P.fake,
		ipset_file: 'ipset-none.txt', ipv6: s.ipv6, flowoffload: s.flowoffload, features: {},
	};
}

// Записать /opt/zapret/config для стратегии-объекта (в т.ч. кандидата поиска). null — успех, иначе текст ошибки.
function write_zapret_config_for(st, s) {
	let r = roles();
	let err = cat.validate(st, r);
	if (err)
		return `Стратегия ${st.id} не прошла проверку: ${err}`;
	ensure_dirs();
	return write_atomic(P.zapret_config, cat.render_config(st, r, build_opts(s))) ? null : 'Не удалось записать config zapret';
}

// То же по id из каталогов.
function write_zapret_config(strategy_id, s) {
	let st = cat.find(catalogs(), strategy_id);
	if (!st)
		return `Стратегия ${strategy_id} не найдена`;
	return write_zapret_config_for(st, s);
}

function core_installed() {
	return !!(stat(`${P.zapret}/nfq/nfqws`) && stat(`${P.zapret}/init.d/openwrt/zapret`));
}

function last_check() { return read_json(`${P.run}/last-check.json`, null) ?? read_json(`${P.etc}/last-check.json`, null); }

// Время в ответах API — ISO 8601 UTC (router-api §5); внутри храним секунды.
function iso(t) {
	if (t == null)
		return null;
	let g = gmtime(t);
	return sprintf('%04d-%02d-%02dT%02d:%02d:%02dZ', g.year, g.mon, g.mday, g.hour, g.min, g.sec);
}

function write_status_json() {
	let s = settings(), lc = last_check();
	let pub = {
		product: 'dpishell-router', api: API, min_app: MIN_APP, version: VERSION, engine: 'zapret1', core: 'v72.13',
		enabled: s.enabled, strategy: s.strategy, catalog_version: catalog().version,
		checked_at: iso(lc?.at), last_check_ok: lc?.ok,
	};
	write_atomic(P.status_json, sprintf('%J\n', pub));
	chmod(P.status_json, 0o644);
}

// Фоновое задание идёт: замок есть и его процесс жив (мёртвый замок после ошибки не считается).
function job_running(name) {
	let pid = +trim(readfile(`${P.run}/${name}.lock`) ?? '');
	return !!(pid && stat(`/proc/${pid}`));
}

function valid_domain_line(l) {
	return match(l, DOMAIN_RX) != null;
}

function valid_subnet_line(l) {
	if (!match(l, SUBNET_RX))
		return false;
	let parts = split(split(l, '/')[0], '.');
	for (let p in parts)
		if (+p > 255)
			return false;
	return true;
}

export {
	P, VERSION, API, MIN_APP, MODES, HOURS, USER_LISTS,
	ensure_dirs, write_atomic, read_json, uci_set, settings, roles, catalog, user_catalog, found_catalog, catalogs,
	write_zapret_config, write_zapret_config_for, core_installed, last_check, write_status_json, valid_domain_line, valid_subnet_line, job_running, iso
};
