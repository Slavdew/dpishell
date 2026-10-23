// Установка чужих пакетов (apk add / opkg install) только с запасом места и с уборкой за собой (decisions-owner-2026-10-20.md,
// «Ресурсы роутера», «работа с файлами»; weak-router-test.md: на полном флеше apk ставил https-dns-proxy без libcares, а
// остатки ломали следующие попытки). Порядок: plan() — что поставится и сколько займёт (размеры из индекса) → space_check()
// → add(): не встало — apk del только того, чего не было до вызова, и файлы этих пакетов, которых не было до вызова.
// Ошибки — сообщением { key, params, text, detail }: key/params — фраза словаря (ru/en), text — готовый русский, detail — сырой
// вывод apk («Подробнее»).
'use strict';

import { readfile, writefile, rename, stat, lstat, popen, lsdir, unlink, rmdir, mkdir } from 'fs';

// Запас сверх размера пакетов: база apk переписывается целиком (~150 КБ), конфиги и uci commit.
const MARGIN_KB = 1024;
const FETCH = '/tmp/dpishell/pkg-fetch';
const NAME_RX = /^[A-Za-z0-9][A-Za-z0-9._+-]*$/;

function q(s) { return `'${replace(`${s}`, "'", "'\\''")}'`; }

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function apk() { return !!stat('/usr/bin/apk'); }

// Свободно во флеше (overlay; на образе без overlay — корень), КБ; null — узнать не удалось.
function free_kb() {
	let m = match(sh('df -k /overlay 2>/dev/null || df -k /').out, /\n[^ ]+ +[0-9]+ +[0-9]+ +([0-9]+)/);
	return m ? +m[1] : null;
}

function mb(kb) {
	return sprintf('%.1f', 1.0 * kb / 1024);
}

// Сообщение роутера (router-api.md §9): detail — сырой текст для «Подробнее», не для показа строкой.
function msg(key, params, text, detail) {
	let m = { key, params: params ?? {}, text };
	if (detail != null && length(trim(detail)))
		m.detail = trim(detail);
	return m;
}

// Текст сообщения (строка — как есть): для старых окон и журналов.
function text_of(e) {
	return type(e) == 'object' ? (e.text ?? e.key ?? '') : e;
}

function tail(out, n) {
	let l = filter(split(trim(out ?? ''), '\n'), x => length(trim(x)) && !match(x, /^WARNING: opening from cache/));
	return join('\n', slice(l, -n));
}

function space_msg(need, free) {
	return msg('web.err.flash_space', { need: mb(need), free: mb(free) },
		`Мало места во флеше: нужно ${mb(need)} МБ, свободно ${mb(free)} МБ`);
}

// Хватает ли места: need_kb — с запасом. null — хватает (или узнать не удалось — тогда не мешаем).
function space_check(need_kb) {
	let f = free_kb();
	return f != null && f < need_kb ? space_msg(need_kb, f) : null;
}

// Ошибка apk/opkg словами; сырой хвост вывода — в detail.
function apk_error(out, name) {
	let raw = tail(out, 8);
	if (match(out, /No space left|ENOSPC|disk full|not enough space/i))
		return msg('web.err.apk_space', { name }, `Не хватило места во флеше при установке ${name}`, raw);
	if (match(out, /UNTRUSTED|BAD signature|signature/i))
		return msg('web.err.apk_sig', { name }, `${name}: подпись пакета не сошлась — установка отменена`, raw);
	if (match(out, /unable to select packages|no such package|breaks:|conflicts:|Cannot satisfy|unsatisfied/i))
		return msg('web.err.apk_deps', { name }, `Не удалось установить ${name}: в источнике пакетов нет нужного пакета или его зависимостей`, raw);
	if (match(out, /could not connect|unable to fetch|fetch .*failed|DNS lookup|temporary error|timed out|Network unreachable|Failed to download|stale\/unavailable|unavailable repositories/i))
		return msg('web.err.apk_net', { name }, `Не удалось загрузить ${name}: нет связи с источником пакетов`, raw);
	return msg('web.err.apk_failed', { name }, `Не удалось установить ${name}`, raw);
}

// Установленные пакеты: { имя: true } (база apk или opkg).
function installed_names() {
	let out = {};
	if (apk()) {
		for (let m in match(readfile('/lib/apk/db/installed') ?? '', /(^|\n)P:([^\n]+)/g) ?? [])
			out[m[2]] = true;
	} else
		for (let f in lsdir('/usr/lib/opkg/info') ?? [])
			if (substr(f, -8) == '.control')
				out[substr(f, 0, -8)] = true;
	return out;
}

// Размер после установки, КБ, по именам (индекс apk; при нескольких версиях — наибольший). Неизвестно — 0.
function sizes_kb(names) {
	if (!apk() || !length(names))
		return 0;
	let best = {}, cur = null;
	for (let l in split(sh(`apk query --fields name,installed-size ${join(' ', map(names, q))}`).out, '\n')) {
		let m = match(l, /^Name: (.+)$/);
		if (m) { cur = trim(m[1]); continue; }
		m = match(l, /^Installed-Size: ([0-9]+)/);
		if (m && cur && +m[1] > (best[cur] ?? 0))
			best[cur] = +m[1];
	}
	let kb = 0;
	for (let n in keys(best)) {
		let b = best[n] + 1023;
		kb += int(b / 1024);
	}
	return kb;
}

// apk add из файла пишет в /etc/apk/world «имя><хеш» — пакет пришпилен к файлу. Пока файла нет, apk отказывает в любом
// apk add («breaks: world[dpishell><Q1…]», weak-router-test, 2026-10-23) и upgrade его не трогает. Наши пакеты — к обычному «имя».
const OWN = ['dpishell', 'luci-app-dpishell'];

function unpin(names) {
	if (!apk())
		return;
	let w = readfile('/etc/apk/world');
	if (w == null)
		return;
	let fixed = w;
	for (let n in names ?? OWN)
		fixed = replace(fixed, regexp(`(^|[ \n])${replace(n, '-', '\\-')}><[^ \n]*`, 'g'), `$1${n}`);
	if (fixed != w && writefile('/etc/apk/world.tmp', fixed) != null && !rename('/etc/apk/world.tmp', '/etc/apk/world'))
		unlink('/etc/apk/world.tmp');
}

// Что поставит apk add и сколько места займёт: { names, kb } или { error }. opkg — { names: [], kb: null } (размер неизвестен).
function plan(names) {
	if (!apk())
		return { names: [], kb: null };
	unpin();
	let r = sh(`apk add --simulate ${join(' ', map(names, q))}`);
	if (r.rc != 0)
		return { error: apk_error(r.out, join(', ', names)) };
	let list = [];
	for (let m in match(r.out, /\([0-9]+\/[0-9]+\) (Installing|Upgrading|Replacing) ([^ ]+) /g) ?? [])
		if (match(m[2], NAME_RX))
			push(list, m[2]);
	return { names: list, kb: sizes_kb(list) };
}

// Нужно места для установки по плану: размер пакетов + запас, не меньше floor_kb.
function need_kb(p, floor_kb) {
	let n = (p?.kb ?? 0) + MARGIN_KB;
	return n > floor_kb ? n : floor_kb;
}

// Файлы и каталоги пакетов (apk fetch во /tmp + apk adbdump) — пути без «/» в начале. Для уборки после сбоя установки.
function manifest(names) {
	let out = [];
	if (!apk() || !length(names))
		return out;
	sh(`rm -rf ${q(FETCH)}`);
	mkdir('/tmp/dpishell', 0o700);
	mkdir(FETCH, 0o700);
	sh(`cd ${q(FETCH)} && apk fetch ${join(' ', map(names, q))}`);
	for (let f in lsdir(FETCH) ?? []) {
		let text = sh(`apk adbdump ${q(FETCH + '/' + f)}`).out;
		let at = index(text, '\npaths:');
		if (at < 0)
			continue;
		let dir = null;
		for (let l in split(substr(text, at + 1), '\n')) {
			if (match(l, /^[a-z]/) && substr(l, 0, 6) != 'paths:')
				break;
			let m = match(l, /^  - name: (.+)$/);
			if (m) { dir = trim(m[1]); push(out, dir); continue; }
			m = match(l, /^      - name: (.+)$/);
			if (m && dir != null)
				push(out, `${dir}/${trim(m[1])}`);
		}
	}
	sh(`rm -rf ${q(FETCH)}`);
	// Только обычные относительные пути: без «..», управляющих символов и пустых частей.
	return filter(uniq(out), p => length(p) && !match(p, /(^|\/)\.\.?(\/|$)|\/\/|^\/|[[:cntrl:]]/));
}

// Убрать поставленное этим вызовом: службы новых пакетов — стоп; apk del пакетов, которых не было до вызова; файлы и пустые
// каталоги из их списка, которых не было до вызова (что было — не трогаем, чужое не трогаем).
function undo_add(before, wanted, paths, existed) {
	let now = installed_names();
	let added = filter(keys(now), n => !before[n] && match(n, NAME_RX));
	for (let n in wanted)
		if (!before[n] && index(added, n) < 0)
			push(added, n);
	for (let p in paths)
		if (match(p, /^etc\/init\.d\/[A-Za-z0-9._-]+$/) && !existed[p] && stat(`/${p}`))
			sh(`/${p} stop; /${p} disable`);
	if (length(added))
		sh(apk() ? `apk del ${join(' ', map(added, q))}` : `opkg remove ${join(' ', map(added, q))}`);
	let dirs = [];
	for (let p in paths) {
		if (existed[p])
			continue;
		let s = lstat(`/${p}`);
		if (!s)
			continue;
		if (s.type == 'directory')
			push(dirs, p);
		else
			unlink(`/${p}`);
	}
	// Глубже — раньше; rmdir убирает только пустой каталог.
	for (let d in sort(dirs, (a, b) => length(b) - length(a)))
		rmdir(`/${d}`);
	return added;
}

// Поставить пакеты; verify() — проверка после установки (null — работает, иначе текст). Не встало или не работает —
// убрать своё (undo_add). null — успех, иначе сообщение.
function add(names, label, verify) {
	let before = installed_names();
	let p = plan(names);
	if (p.error)
		return p.error;
	let paths = manifest(p.names);
	let existed = {};
	for (let x in paths)
		existed[x] = !!lstat(`/${x}`);
	let r = apk() ? sh(`apk add ${join(' ', map(names, q))}`)
		: sh(`opkg update >/dev/null; opkg install ${join(' ', map(names, q))}`);
	let e = r.rc != 0 ? apk_error(r.out, label) : null;
	if (!e && verify) {
		let v = verify();
		if (v)
			e = msg('web.err.apk_broken', { name: label }, `${label} установился с ошибкой и убран`, `${v}\n${tail(r.out, 4)}`);
	}
	if (e)
		undo_add(before, names, paths, existed);
	return e;
}

export { unpin, free_kb, space_check, space_msg, plan, need_kb, add, manifest, apk_error, msg, text_of, sizes_kb, installed_names, MARGIN_KB };
