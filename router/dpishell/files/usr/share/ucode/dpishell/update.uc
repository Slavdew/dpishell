// Обновление DpiShell (router-api.md §5): автопроверка раз в сутки, установка из витрины или из папки, которую положило окно ПК.
// Пакеты подписаны (индекс витрины — нашим ключом); из папки — только наши два пакета и только по SHA256SUMS.
'use strict';

import { readfile, stat, popen, lsdir } from 'fs';
import * as st from 'dpishell.state';

const FILE = '/etc/dpishell/update.json';
const RELEASE_URL = 'https://slavdew.github.io/dpishell/feed/release.json';
const LOCAL_DIR = '/tmp/dpishell-update';
const PACKAGES = ['dpishell', 'luci-app-dpishell'];
const DAY = 86400;
const JOB = '/tmp/dpishell/update_job.json';

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

// Последние строки вывода apk/opkg — в итог задания (окно и страница показывают, что случилось).
function tail(out, n) {
	let l = filter(split(trim(out ?? ''), '\n'), x => length(trim(x)));
	return slice(l, -n);
}

// Ход задания обновления (router-api.md §5.2 update_install): step download → install → restart → done.
// Пишется на каждом шаге — окно и страница видят, что идёт, а не ждут молча.
function job_put(job, extra) {
	for (let k in extra)
		job[k] = extra[k];
	job.at = time();
	st.write_atomic(JOB, sprintf('%J\n', job));
}

// Установка обновления. from: feed | local. job — запись хода (job_put). null — успех, иначе текст ошибки.
function install(from, job) {
	let names = filter(PACKAGES, n => installed_version(n) != null);
	if (!length(names))
		names = ['dpishell'];
	let r;
	if (from == 'local') {
		job_put(job, { step: 'download' });
		let files = local_files();
		if (type(files) == 'string')
			return files;
		let m = match(join(' ', files), /dpishell-([0-9][A-Za-z0-9._~+-]*)\.(apk|ipk)/);
		job_put(job, { step: 'install', target: m ? m[1] : null });
		r = apk() ? sh(`apk add --allow-untrusted ${join(' ', files)}`) : sh(`opkg install --force-reinstall ${join(' ', files)}`);
	} else {
		job_put(job, { step: 'download' });
		// Сбой постороннего фида не должен мешать: apk без --force-missing-repositories отказывает в upgrade, если недоступен
		// хоть один репозиторий («Not continuing due to stale/unavailable repositories»), а мы ставим только свои пакеты.
		sh(apk() ? 'apk update' : 'opkg update');
		job_put(job, { step: 'install', target: feed_version('dpishell') });
		r = sh(apk() ? `apk upgrade --force-missing-repositories ${join(' ', names)}` : `opkg upgrade ${join(' ', names)}`);
	}
	job.log = tail(r.out, 6);
	if (r.rc != 0)
		return `Не удалось обновить: ${join('; ', tail(r.out, 2))}`;
	// postinst перезапускает rpcd через 2 с: ждём, пока объект dpishell снова отвечает (до 20 с).
	job_put(job, { step: 'restart' });
	for (let i = 0; i < 20; i++) {
		sleep(1000);
		if (i >= 2 && sh('ubus -t 2 list dpishell').rc == 0)
			break;
	}
	check();
	return null;
}

// Задание обновления, чей процесс умер (убит вместе с rpcd при переходе со старой версии, перезагрузка): итог по факту —
// установленная версия против целевой. Зовут status (чтение), run tick и старт пакета. true — запись исправлена.
function recover() {
	let job = st.read_json(JOB, null);
	if (!job?.running || st.job_running('update'))
		return false;
	let inst = installed_version('dpishell');
	// Запись старой версии (до r7) без целевой и исходной — сверка с последней версией витрины.
	let feed = st.read_json(FILE, null)?.version;
	let ok = job.target ? inst == job.target
		: job.from_version != null ? inst != job.from_version
		: (inst != null && feed != null && !older(inst, feed));
	job_put(job, { running: false, step: 'done', ok,
		message: ok ? `Обновлено до ${inst}` : `Обновление прервалось, установлена ${inst ?? 'неизвестная версия'}` });
	return true;
}

export { check, check_daily, status, install, installed_version, recover, job_put, LOCAL_DIR, JOB };
