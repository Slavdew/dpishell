// Обновление DpiShell (router-api.md §5): автопроверка раз в сутки, установка из витрины или из папки, которую положило окно ПК.
// Пакеты подписаны (индекс витрины — нашим ключом); из папки — только наши два пакета, файлы root, по SHA256SUMS и, если окно
// положило release.json с подписью, — по подписи ключом выпуска (security-audit-router №3).
'use strict';

import { readfile, writefile, stat, lstat, popen, lsdir, unlink, rename, mkdir } from 'fs';
import * as st from 'dpishell.state';
import * as pk from 'dpishell.pkgs';

const FILE = '/etc/dpishell/update.json';
const RELEASE_URL = 'https://slavdew.github.io/dpishell/feed/release.json';
const LOCAL_DIR = '/tmp/dpishell-update';
const PACKAGES = ['dpishell', 'luci-app-dpishell'];
const DAY = 86400;
const JOB = '/tmp/dpishell/update_job.json';
// Открытый ключ выпуска (тот же ed25519, что packaging/keys/release-ed25519.pem: им CI подписывает release.json) в формате usign;
// совпадение ключей проверяет tools/release-key-check.py в CI.
const RELEASE_KEY = '/usr/share/dpishell/release-key.pub';
const VERSION_RX = /^[A-Za-z0-9._~+-]{1,64}$/;

function q(s) { return `'${replace(`${s}`, "'", "'\\''")}'`; }

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function apk() { return !!stat('/usr/bin/apk'); }

// Версии пакетов: status зовёт это на каждый опрос страницы. База пакетов (сотни КБ) разбирается заново, только если
// сменилась (размер, время); в rpcd модуль живёт между вызовами.
let ver_memo = { key: null, v: {} };

function installed_version(name) {
	let db_path = apk() ? '/lib/apk/db/installed' : '/usr/lib/opkg/status';
	let s = stat(db_path), key = s ? `${s.size}:${s.mtime}` : null;
	if (key != ver_memo.key)
		ver_memo = { key, v: {} };
	if (key && exists(ver_memo.v, name))
		return ver_memo.v[name];
	let v = null;
	if (apk()) {
		let db = readfile(db_path) ?? '';
		let m = match(db, regexp(`(^|\n)P:${name}\nV:([^\n]+)`));
		v = m ? m[2] : null;
	} else {
		let m = match(sh(`opkg status ${name}`).out, /Version: ([^\n]+)/);
		v = m ? m[1] : null;
	}
	ver_memo.v[name] = v;
	return v;
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

// a < b по правилам пакетного менеджера. Ответ для пары не меняется — запоминаем (status зовёт на каждый опрос).
let older_memo = {};

function older(a, b) {
	if (!a || !b || !match(a, VERSION_RX) || !match(b, VERSION_RX))
		return false;
	let k = `${a} ${b}`;
	if (exists(older_memo, k))
		return older_memo[k];
	let r = apk() ? trim(sh(`apk version -t '${a}' '${b}'`).out) == '<' : sh(`opkg compare-versions '${a}' '<<' '${b}'`).rc == 0;
	if (length(older_memo) > 50)
		older_memo = {};
	older_memo[k] = r;
	return r;
}

// Подпись release.json ключом выпуска (base64 сырых 64 байт ed25519, как release.json.sig в CI) через usign: подпись и ключ
// usign — тот же ed25519 с заголовком «Ed» и отпечатком. true — сошлась, false — нет, null — проверить нечем (нет usign).
function sig_ok(path, sig_b64) {
	if (!stat('/usr/bin/usign') || !stat(RELEASE_KEY))
		return null;
	let key = b64dec(trim(split(readfile(RELEASE_KEY) ?? '', '\n')[1] ?? ''));
	let sig = b64dec(trim(sig_b64 ?? ''));
	if (key == null || length(key) != 42 || sig == null || length(sig) != 64)
		return false;
	st.own_dir('/tmp/dpishell', 0o700);
	let f = '/tmp/dpishell/release.usig';
	writefile(f, `untrusted comment: dpishell release\n${b64enc('Ed' + substr(key, 2, 8) + sig)}\n`);
	let ok = sh(`usign -V -q -p ${RELEASE_KEY} -m ${q(path)} -x ${f}`).rc == 0;
	unlink(f);
	return ok;
}

// release.json витрины (≈2 КБ, подписан ключом выпуска): версия пакета — из имени файла dpishell-<версия>.apk в assets,
// заметки — changes. null — не скачался, подпись не сошлась или формат не тот.
function feed_release() {
	if (!st.own_dir('/tmp/dpishell', 0o700))
		return null;
	let f = '/tmp/dpishell/release.json';
	let r = sh(`curl -sS --fail --max-time 10 --max-filesize 65536 -o ${f} '${RELEASE_URL}'`);
	let sig = sh(`curl -sS --fail --max-time 10 --max-filesize 1024 '${RELEASE_URL}.sig'`);
	// Подписи нет или не сошлась — не верим; проверить нечем (нет usign) — версия только для плашки «Доступно обновление»,
	// сами пакеты ставит apk по подписи витрины.
	let ok = r.rc == 0 ? sig_ok(f, sig.rc == 0 ? sig.out : '') : false;
	let j = null;
	if (ok !== false)
		try { j = json(readfile(f) ?? ''); } catch (e) {}
	unlink(f);
	if (type(j?.version) != 'string')
		return null;
	let v = null;
	for (let a in (type(j.assets) == 'array' ? j.assets : [])) {
		let m = match(a?.name ?? '', /^dpishell-([0-9][A-Za-z0-9._~+-]*)[.]apk$/);
		if (m && match(m[1], VERSION_RX))
			v = m[1];
	}
	// changes — по-русски, changes_en — перевод (с 0.2.0-r15); у старых release.json его нет — заметки только русские.
	let lines = a => type(a) == 'array' ? map(filter(slice(a, 0, 15), x => type(x) == 'string'), x => substr(x, 0, 300)) : [];
	return { base: j.version, version: v, changes: lines(j.changes), changes_en: lines(j.changes_en) };
}

// Заметки — к той версии, что в витрине (release.json — версия без суффикса сборки -rN). en — английские (пусто — перевода нет).
function notes(version, rel, en) {
	return rel && index(version ?? '', rel.base) == 0 ? (en ? rel.changes_en : rel.changes) : [];
}

// ---------- Откат версии (router-api.md §5.2 update_rollback, с 0.2.0-r16) ----------
// Во флеше, в /etc/dpishell/pkg (только root): cur — пакеты установленной версии (при следующем обновлении станут прежними),
// prev — ровно одна прежняя пара. Пишутся, только если после записи во флеше остаётся MARGIN_KB; не хватает — пакеты не
// храним, и откат с роутера честно «недоступен» (остаётся через окно ПК — папка обновления, как update_install local).
// prev.json — какая версия была прежней, когда её пакетов нет (не сохранены прежней версией пакета или не хватило места).
const PKG = '/etc/dpishell/pkg';
const PREV_NOTE = '/etc/dpishell/pkg/prev.json';
const MARGIN_KB = 4096;
const PKG_RX = /^(dpishell|luci-app-dpishell)-([0-9][A-Za-z0-9._~+-]*)\.(apk|ipk)$/;

function sha256(path) {
	let r = sh(`sha256sum ${q(path)}`);
	return r.rc == 0 ? substr(r.out, 0, 64) : null;
}

function free_kb() {
	let m = match(sh('df -k /overlay 2>/dev/null || df -k /').out, /\n[^ ]+ +[0-9]+ +[0-9]+ +([0-9]+)/);
	return m ? +m[1] : null;
}

// Слот cur/prev: meta.json { version, files: { имя: sha256 }, saved_at }. null — слота нет или он испорчен.
function slot_meta(slot) {
	let m = st.read_json(`${PKG}/${slot}/meta.json`, null);
	return type(m?.version) == 'string' && match(m.version, VERSION_RX) && type(m?.files) == 'object' && length(m.files) ? m : null;
}

// Свои точные пути: каталог слота в своём /etc/dpishell/pkg (lstat — ссылку не проходим, rm -rf удалит саму ссылку).
function rm_slot(slot) {
	if (index(['cur', 'prev', 'cur.new'], slot) >= 0 && lstat(`${PKG}/${slot}`))
		sh(`rm -rf ${q(PKG + '/' + slot)}`);
}

// Файлы слота с проверкой: обычные файлы root, имена наши, sha256 как в meta. Список путей или текст ошибки.
function slot_files(slot) {
	let m = slot_meta(slot);
	if (!m)
		return 'Прежние пакеты не сохранены';
	let out = [];
	for (let n in m.files) {
		let p = `${PKG}/${slot}/${n}`;
		if (!match(n, PKG_RX) || !st.root_file(p) || sha256(p) != m.files[n])
			return `Сохранённый пакет ${n} повреждён — откат с роутера недоступен`;
		push(out, p);
	}
	return out;
}

function note_prev(version, reason) {
	if (!version)
		return;
	let cur = st.read_json(PREV_NOTE, null);
	if (cur?.version == version && cur?.reason == reason)
		return;
	st.own_dir(PKG, 0o700);
	st.write_atomic(PREV_NOTE, sprintf('%J\n', { version, reason }));
}

// Сохранить пакеты (пути) в слот cur. null — сохранено, 'space' — не хватает места с запасом, иначе — текст ошибки.
function save_cur(files, version) {
	let kb = 0, names = {};
	for (let f in files) {
		let n = split(f, '/')[-1];
		if (!match(n, PKG_RX) || !st.root_file(f))
			return `не наш пакет: ${n}`;
		let sz = (lstat(f)?.size ?? 0) + 1023;
		kb += int(sz / 1024);
	}
	rm_slot('cur');
	let free = free_kb();
	if (free == null || free - kb < MARGIN_KB)
		return 'space';
	if (!st.own_dir(PKG, 0o700))
		return 'нет каталога';
	let tmp = `${PKG}/cur.new`;
	rm_slot('cur.new');
	mkdir(tmp, 0o700);
	for (let f in files) {
		let n = split(f, '/')[-1];
		if (sh(`cp ${q(f)} ${q(tmp + '/' + n)}`).rc != 0) {
			rm_slot('cur.new');
			return 'не удалось записать';
		}
		names[n] = sha256(`${tmp}/${n}`);
	}
	if (!st.write_atomic(`${tmp}/meta.json`, sprintf('%J\n', { version, files: names, saved_at: time() })) || !rename(tmp, `${PKG}/cur`)) {
		rm_slot('cur.new');
		return 'не удалось записать';
	}
	return null;
}

// Пакеты установленной версии из витрины (apk fetch, по подписанному индексу) — в cur. Только apk; версия в витрине должна
// совпадать с установленной (иначе это уже не «наши» пакеты).
function save_cur_from_feed(version) {
	if (!apk())
		return 'not_saved';
	let d = '/tmp/dpishell/fetch';
	st.own_dir('/tmp/dpishell', 0o700);
	sh(`rm -rf ${q(d)}`);
	mkdir(d, 0o700);
	let names = filter(PACKAGES, n => installed_version(n) != null);
	let r = sh(`apk fetch --output ${q(d)} ${join(' ', names)}`);
	let files = map(filter(lsdir(d) ?? [], f => match(f, PKG_RX)), f => `${d}/${f}`);
	let e = (r.rc != 0 || !length(files) || !length(filter(files, f => index(f, `/dpishell-${version}.`) >= 0)))
		? 'not_saved' : save_cur(files, version);
	sh(`rm -rf ${q(d)}`);
	return e;
}

// После обновления from → установленная: cur (пакеты from, сохранены при её установке) становится prev; нет — запомнить
// версию без пакетов. Затем новые пакеты — в cur. files — пути пакетов из папки окна (null — из витрины).
function after_update(from, files) {
	let inst = installed_version('dpishell');
	if (!from || from == inst)
		return;
	let cur = slot_meta('cur');
	rm_slot('prev');
	if (cur?.version == from && rename(`${PKG}/cur`, `${PKG}/prev`))
		unlink(PREV_NOTE);
	else {
		rm_slot('cur');
		note_prev(from, 'not_saved');
	}
	let e = files ? save_cur(files, inst) : save_cur_from_feed(inst);
	if (e == 'space' && !slot_meta('prev'))
		note_prev(from, 'not_saved');
}

// После отката: prev — это теперь установленная (cur), новее неё пакеты не храним (они в витрине). files — из папки окна.
function after_rollback(files) {
	let inst = installed_version('dpishell');
	unlink(PREV_NOTE);
	if (!files) {
		rm_slot('cur');
		rename(`${PKG}/prev`, `${PKG}/cur`);
	} else {
		rm_slot('prev');
		save_cur(files, inst);
	}
}

// Обновление, сделанное прежней версией пакета (она пакеты не хранила): итог её задания — from_version — запомнить как
// прежнюю без пакетов (откат через окно ПК). Пишется один раз; зовёт status.
function learn_prev() {
	let j = st.read_json(JOB, null);
	if (!j || j.running || !j.ok || j.action != 'update' || type(j.from_version) != 'string')
		return;
	if (slot_meta('prev') || st.read_json(PREV_NOTE, null)?.version == j.from_version)
		return;
	if (older(j.from_version, installed_version('dpishell')))
		note_prev(j.from_version, 'not_saved');
}

// status.rollback: { version, saved, reason } — прежняя версия старше установленной; null — отката нет.
function rollback_status() {
	let inst = installed_version('dpishell');
	let p = slot_meta('prev');
	if (p && older(p.version, inst))
		return { version: p.version, saved: true, reason: null };
	let n = st.read_json(PREV_NOTE, null);
	if (type(n?.version) == 'string' && older(n.version, inst))
		return { version: n.version, saved: false, reason: n.reason == 'space' ? 'space' : 'not_saved' };
	return null;
}

// Проверка: есть ли в витрине версия новее установленной. Сеть недоступна — прежний результат остаётся.
// Хватает release.json витрины (2 КБ): индексы всех фидов (apk update — мегабайты, у opkg ещё и в ОЗУ до перезагрузки)
// качаются, только если его нет или в нём нет пакета для apk; перед установкой install() обновляет индексы сам.
function check() {
	let inst = installed_version('dpishell');
	let rel = apk() ? feed_release() : null;
	let av = rel?.version;
	if (!av) {
		if (apk()) sh('apk update'); else sh('opkg update');
		av = feed_version('dpishell');
	}
	if (!av)
		return st.read_json(FILE, null);
	let newer = older(inst, av);
	if (newer)
		rel ??= feed_release();
	let res = { checked_at: time(), installed: inst, version: av, available: newer, notes: newer ? notes(av, rel) : [], notes_en: newer ? notes(av, rel, true) : [] };
	st.write_atomic(FILE, sprintf('%J\n', res));
	// Пакетов установленной версии нет (её ставила прежняя версия или офлайн-набор), а витрина отдаёт ровно её — взять оттуда:
	// при следующем обновлении они станут прежней парой для отката. Один раз на версию (дальше cur уже она).
	// Не вышло (места нет, витрина не отдала) — до перезагрузки не повторять: не качать пакеты каждые сутки зря.
	let skip = '/tmp/dpishell/fetch-skip';
	if (apk() && inst && av == inst && slot_meta('cur')?.version != inst && trim(readfile(skip) ?? '') != inst && save_cur_from_feed(inst))
		writefile(skip, inst);
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
		return { available: false, version: null, notes: [], notes_en: [], checked_at: null, local_signed: true };
	let avail = older(installed_version('dpishell'), u.version);
	// notes — по-русски (для старых окон), notes_en — по-английски, пусто — перевода нет; язык выбирают окно и страница.
	return { available: avail, version: u.version, notes: avail ? (u.notes ?? []) : [], notes_en: avail ? (u.notes_en ?? []) : [],
		checked_at: st.iso(u.checked_at), local_signed: true };
}

// apk add из файла пишет в /etc/apk/world «имя><хеш» — пакет пришпилен к файлу, и apk upgrade его больше не трогает
// (r8, поставленный офлайн, снова и снова «обновлялся» до r10). Возвращаем наши пакеты к обычному «имя».
function unpin() {
	pk.unpin(PACKAGES);
}

// Папка обновления: каталог root (кладёт окно ПК по SSH), не ссылка, писать в неё может только root. null — годится.
// /tmp общий: папку с пакетом и SHA256SUMS мог бы положить любой процесс (nfqws работает от daemon) — пакет ставится от root.
function local_dir_problem() {
	let s = lstat(LOCAL_DIR);
	if (!s)
		return `Нет ${LOCAL_DIR}`;
	if (s.type != 'directory' || s.uid != 0 || s.perm?.group_write || s.perm?.other_write)
		return `${LOCAL_DIR} — не папка root или открыта на запись другим; положите пакеты заново`;
	return null;
}

const REL = 'release.json', REL_SIG = 'release.json.sig';

// Пакеты из папки окна: только наши имена, файлы root, набор файлов — ровно как в SHA256SUMS, суммы совпали; release.json
// с подписью (если окно его положило) — подпись ключом выпуска и те же sha256. Возвращает список путей или текст ошибки.
function local_files() {
	let bad = local_dir_problem();
	if (bad)
		return bad;
	if (!st.root_file(`${LOCAL_DIR}/SHA256SUMS`))
		return `Нет ${LOCAL_DIR}/SHA256SUMS`;
	let sums = {};
	for (let l in split(trim(readfile(`${LOCAL_DIR}/SHA256SUMS`) ?? ''), '\n')) {
		let m = match(trim(l), /^([0-9a-fA-F]{64}) [ *]([A-Za-z0-9._~+-]+)$/);
		if (!m)
			return `SHA256SUMS: строка не по формату`;
		sums[m[2]] = lc(m[1]);
	}
	let files = [], names = [];
	for (let f in lsdir(LOCAL_DIR) ?? []) {
		if (f == 'SHA256SUMS' || f == REL || f == REL_SIG)
			continue;
		let ok = false;
		for (let n in PACKAGES)
			if (match(f, regexp(`^${replace(n, '-', '\\-')}-[0-9][A-Za-z0-9._~+-]*\\.(apk|ipk)$`)))
				ok = true;
		if (!ok)
			return `Лишний файл в ${LOCAL_DIR}: ${f}`;
		if (!st.root_file(`${LOCAL_DIR}/${f}`))
			return `${f} — не обычный файл root`;
		if (!sums[f])
			return `${f} нет в SHA256SUMS`;
		push(files, `${LOCAL_DIR}/${f}`);
		push(names, f);
	}
	if (!length(files))
		return `В ${LOCAL_DIR} нет пакетов DpiShell`;
	for (let n in keys(sums))
		if (index(names, n) < 0)
			return `Нет файла ${n} из SHA256SUMS`;
	let r = sh(`cd ${LOCAL_DIR} && sha256sum -c SHA256SUMS`);
	if (r.rc != 0)
		return `Файлы не совпали с SHA256SUMS: ${trim(r.out)}`;
	if (stat(`${LOCAL_DIR}/${REL}`) || stat(`${LOCAL_DIR}/${REL_SIG}`)) {
		if (!st.root_file(`${LOCAL_DIR}/${REL}`) || !st.root_file(`${LOCAL_DIR}/${REL_SIG}`))
			return `${REL} без подписи ${REL_SIG}`;
		let ok = sig_ok(`${LOCAL_DIR}/${REL}`, readfile(`${LOCAL_DIR}/${REL_SIG}`));
		if (ok === false)
			return `Подпись ${REL} не сошлась — пакеты не из выпуска DpiShell`;
		// ok === null: на роутере нет usign — остаётся сверка по SHA256SUMS.
		if (ok) {
			let rel = null;
			try { rel = json(readfile(`${LOCAL_DIR}/${REL}`)); } catch (e) {}
			let assets = type(rel?.assets) == 'array' ? rel.assets : [];
			for (let n in names)
				if (!length(filter(assets, a => a?.name == n && lc(a?.sha256 ?? '') == sums[n])))
					return `${n} нет в подписанном ${REL}`;
		}
	}
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

// Места для обновления, КБ: новые версии пакетов (индекс витрины; пакет из папки — файл × 4: распакованный ~3.5× больше)
// + запас pkgs.MARGIN_KB (база apk, конфиги). На полном флеше apk обрывался «No space left» — проверяем заранее.
function update_need_kb(files, names) {
	let kb = 0;
	if (files)
		for (let f in files) {
			let sz = (lstat(f)?.size ?? 0) * 4 + 1023;
			kb += int(sz / 1024);
		}
	let idx = pk.sizes_kb(names);
	return pk.need_kb({ kb: kb > idx ? kb : idx }, 0);
}

// Установка обновления. from: feed | local. job — запись хода (job_put). null — успех, иначе текст ошибки.
// rollback — откат (update_rollback): from saved (prev во флеше) | local (папка окна ПК); ставится только версия старше установленной.
function install(from, job, rollback) {
	let names = filter(PACKAGES, n => installed_version(n) != null);
	if (!length(names))
		names = ['dpishell'];
	let r, files = null;
	if (from == 'local' || from == 'saved') {
		job_put(job, { step: 'download' });
		files = from == 'saved' ? slot_files('prev') : local_files();
		if (type(files) == 'string')
			return files;
		let m = match(join(' ', files), /\/dpishell-([0-9][A-Za-z0-9._~+-]*)\.(apk|ipk)/);
		let target = m ? m[1] : null;
		if (rollback && !older(target, installed_version('dpishell')))
			return `Пакеты ${target ?? '—'} не старше установленной версии — это не откат`;
		let e = pk.space_check(update_need_kb(files, names));
		if (e)
			return e;
		job_put(job, { step: 'install', target });
		// Откат: старшую версию apk меняет на младшую по явному файлу; opkg — только с --force-downgrade.
		r = apk() ? sh(`apk add --allow-untrusted ${join(' ', files)}`)
			: sh(`opkg install --force-reinstall${rollback ? ' --force-downgrade' : ''} ${join(' ', files)}`);
		if (r.rc == 0)
			unpin();
	} else {
		job_put(job, { step: 'download' });
		// Сбой постороннего фида не должен мешать: apk без --force-missing-repositories отказывает в upgrade, если недоступен
		// хоть один репозиторий («Not continuing due to stale/unavailable repositories»), а мы ставим только свои пакеты.
		sh(apk() ? 'apk update' : 'opkg update');
		let e = pk.space_check(update_need_kb(null, names));
		if (e)
			return e;
		job_put(job, { step: 'install', target: feed_version('dpishell') });
		unpin();
		r = sh(apk() ? `apk upgrade --force-missing-repositories ${join(' ', names)}` : `opkg upgrade ${join(' ', names)}`);
	}
	job.log = tail(r.out, 6);
	if (r.rc != 0)
		return pk.apk_error(r.out, 'DpiShell');
	// postinst перезапускает rpcd через 2 с: ждём, пока объект dpishell снова отвечает (до 20 с).
	job_put(job, { step: 'restart' });
	for (let i = 0; i < 20; i++) {
		sleep(1000);
		if (i >= 2 && sh('ubus -t 2 list dpishell').rc == 0)
			break;
	}
	// apk/opkg могут закончить без ошибки, ничего не обновив: успех — только если встала целевая версия.
	let inst = installed_version('dpishell');
	if (job.target && inst != job.target) {
		check();
		return `${rollback ? 'Откат не удался' : 'Обновление не установилось'}: стоит ${inst ?? 'неизвестная версия'}, ожидалась ${job.target}`;
	}
	// Пакеты для отката — до check(): он сам берёт из витрины пакеты установленной версии, если их нет.
	try {
		if (rollback)
			after_rollback(from == 'saved' ? null : files);
		else
			after_update(job.from_version, from == 'feed' ? null : files);
	} catch (x) {}
	check();
	return null;
}

// Папка обновления окна ПК (/tmp/dpishell-update, ~200 КБ в ОЗУ) после установки — успешной или нет — не нужна: окно
// перед следующей загрузкой кладёт её заново. Только своя папка root (не ссылка: rm -rf по ссылке удалил бы саму ссылку,
// но чужой каталог не трогаем). min_age — не моложе, с (tick: окно могло только что положить пакеты и ещё не позвать установку); in_job — зовёт само задание обновления.
function local_cleanup(min_age, in_job) {
	let s = lstat(LOCAL_DIR);
	if (!s || (!in_job && st.job_running('update')))
		return;
	if (s.type == 'directory' && s.uid != 0)
		return;
	// Обновление из этой папки уже закончилось (его вела прежняя версия, она папку не убирала) — убрать и без выдержки.
	let j = st.read_json(JOB, null);
	let done = j && !j.running && j.from == 'local' && j.started_at >= s.mtime;
	if (min_age && !done && time() - s.mtime < min_age)
		return;
	sh(`rm -rf ${q(LOCAL_DIR)}`);
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
		message: ok ? (job.action == 'rollback' ? `Возвращена версия ${inst}` : `Обновлено до ${inst}`)
			: `${job.action == 'rollback' ? 'Откат прервался' : 'Обновление прервалось'}, установлена ${inst ?? 'неизвестная версия'}` });
	return true;
}

export { feed_release, check, check_daily, status, install, installed_version, recover, job_put, local_dir_problem, local_files, local_cleanup, sig_ok, LOCAL_DIR, JOB,
	rollback_status, learn_prev, slot_files };
