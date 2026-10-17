// Обновление DpiShell (07 §9): автопроверка раз в сутки, установка из витрины или из папки, которую положило окно ПК.
// Пакеты подписаны (индекс витрины — нашим ключом); из папки — только наши два пакета и только по SHA256SUMS.
'use strict';

import { readfile, stat, popen, lsdir } from 'fs';
import * as st from 'dpishell.state';

const FILE = '/etc/dpishell/update.json';
const RELEASE_URL = 'https://slavdew.github.io/dpishell/feed/release.json';
const LOCAL_DIR = '/tmp/dpishell-update';
const PACKAGES = ['dpishell', 'luci-app-dpishell'];
const DAY = 86400;

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function apk() { return !!stat('/usr/bin/apk'); }

function installed_version(name) {
	if (apk()) {
		let db = readfile('/lib/apk/db/installed') ?? '';
		let m = match(db, regexp(`(^|\n)P:${name}\nV:([^\n]+)`));
		return m ? m[2] : null;
	}
	let m = match(sh(`opkg status ${name}`).out, /Version: ([^\n]+)/);
	return m ? m[1] : null;
}

// Версия в индексе витрины (после apk update).
function feed_version(name) {
	if (apk()) {
		for (let l in split(sh(`apk search -x ${name}`).out, '\n'))
			if (index(l, `${name}-`) == 0 && match(substr(l, length(name) + 1), /^[0-9]/))
				return trim(substr(l, length(name) + 1));
		return null;
	}
	let m = match(sh(`opkg info ${name}`).out, /Version: ([^\n]+)/);
	return m ? m[1] : null;
}

// a < b по правилам пакетного менеджера.
function older(a, b) {
	if (!a || !b)
		return false;
	if (apk())
		return trim(sh(`apk version -t '${a}' '${b}'`).out) == '<';
	return sh(`opkg compare-versions '${a}' '<<' '${b}'`).rc == 0;
}

function notes(version) {
	let r = sh(`curl -sS --max-time 10 '${RELEASE_URL}'`);
	let j;
	try { j = json(r.out); } catch (e) { return []; }
	// Заметки — к той версии, что в витрине (release.json без суффикса сборки -rN).
	return (j?.version && index(version ?? '', j.version) == 0) ? slice(j.changes ?? [], 0, 15) : [];
}

// Проверка: есть ли в витрине версия новее установленной. Сеть недоступна — прежний результат остаётся.
function check() {
	if (apk()) sh('apk update'); else sh('opkg update');
	let inst = installed_version('dpishell');
	let av = feed_version('dpishell');
	if (!av)
		return st.read_json(FILE, null);
	let res = { checked_at: time(), installed: inst, version: av, available: older(inst, av), notes: older(inst, av) ? notes(av) : [] };
	st.write_atomic(FILE, sprintf('%J\n', res));
	return res;
}

// Раз в сутки (зовёт таймер run watch, cron каждые 30 мин).
function check_daily() {
	let last = st.read_json(FILE, null);
	if (last && time() - last.checked_at < DAY)
		return last;
	return check();
}

// Для status: доступность — заново против установленной (после обновления плашка гаснет сразу).
function status() {
	let u = st.read_json(FILE, null);
	if (!u)
		return { available: false, version: null, notes: [], checked_at: null };
	let avail = older(installed_version('dpishell'), u.version);
	return { available: avail, version: u.version, notes: avail ? (u.notes ?? []) : [], checked_at: st.iso(u.checked_at) };
}

// Пакеты из папки окна: только наши имена, суммы совпали. Возвращает список путей или текст ошибки.
function local_files() {
	if (!stat(`${LOCAL_DIR}/SHA256SUMS`))
		return `Нет ${LOCAL_DIR}/SHA256SUMS`;
	let files = [];
	for (let f in lsdir(LOCAL_DIR) ?? []) {
		if (f == 'SHA256SUMS')
			continue;
		let ok = false;
		for (let n in PACKAGES)
			if (match(f, regexp(`^${replace(n, '-', '\\-')}-[0-9][A-Za-z0-9._~+-]*\\.(apk|ipk)$`)))
				ok = true;
		if (!ok)
			return `Лишний файл в ${LOCAL_DIR}: ${f}`;
		push(files, `${LOCAL_DIR}/${f}`);
	}
	if (!length(files))
		return `В ${LOCAL_DIR} нет пакетов DpiShell`;
	let r = sh(`cd ${LOCAL_DIR} && sha256sum -c SHA256SUMS`);
	if (r.rc != 0)
		return `Файлы не совпали с SHA256SUMS: ${trim(r.out)}`;
	return files;
}

// Установка обновления. from: feed | local. null — успех, иначе текст ошибки.
function install(from) {
	let names = filter(PACKAGES, n => installed_version(n) != null);
	if (!length(names))
		names = ['dpishell'];
	let r;
	if (from == 'local') {
		let files = local_files();
		if (type(files) == 'string')
			return files;
		r = apk() ? sh(`apk add --allow-untrusted ${join(' ', files)}`) : sh(`opkg install --force-reinstall ${join(' ', files)}`);
	} else if (apk()) {
		// Сбой постороннего фида (код ≠ 0) не мешает: наш индекс обновился или есть в кэше — решает сам upgrade.
		sh('apk update');
		r = sh(`apk upgrade ${join(' ', names)}`);
	} else {
		sh('opkg update');
		r = sh(`opkg upgrade ${join(' ', names)}`);
	}
	if (r.rc != 0)
		return `Не удалось обновить: ${trim(r.out)}`;
	check();
	return null;
}

export { check, check_daily, status, install, installed_version, LOCAL_DIR };
