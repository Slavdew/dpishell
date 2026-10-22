// Состояние пакета: пути, настройки uci, свои данные, запись config zapret и status.json.
'use strict';

import { readfile, writefile, rename, mkdir, rmdir, stat, lstat, lsdir, symlink, unlink, access, chmod, readlink, open } from 'fs';
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
	// status.json пишется в ОЗУ (меняется при каждой проверке), в /www — ссылка на него: флеш не изнашивается.
	pub: '/tmp/dpishell-pub',
	pub_status: '/tmp/dpishell-pub/status.json',
	// config zapret на время подбора (ZAPRET_CONFIG): кандидаты не пишутся во флеш, /opt/zapret/config — только итог и откат.
	trial_config: '/tmp/dpishell/zapret.config',
};

const VERSION = '0.2.0';
// api — «мажор.минор» (router-api.md §5): минор растёт при добавлениях без поломок, мажор — при несовместимости.
const API = '1.0';
// Минимальная версия окна ПК, которую понимает этот роутер.
const MIN_APP = '0.9.0';
const MODES = ['all', 'lists'];
const HOURS = [0.5, 1, 3, 6, 12, 24];
const USER_LISTS = ['user-general', 'user-exclude', 'user-ipset', 'user-ipset-exclude'];
// ipset-all.txt (540 КБ) в пакет роутера не входит с r14: профили роутера берут заглушку ipset-none.txt.
const STD_LISTS = ['general.txt', 'google.txt', 'discord.txt', 'exclude.txt', 'ipset-exclude.txt'];
const DOMAIN_RX = /^\^?(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+(xn--[a-z0-9-]{2,59}|[a-z]{2,63})$/;
const SUBNET_RX = /^([0-9]{1,3}\.){3}[0-9]{1,3}(\/[0-9]{1,2})?$/;

// nfqws работает от пользователя daemon (WS_USER zapret): списки и фейки должны быть ему читаемы.
// Свои настройки и результаты — только root; status.json — публичный для uhttpd (router-api §2).
const DIRS = { '/etc/dpishell': 0o711, '/etc/dpishell/lists': 0o755, '/tmp/dpishell': 0o700, '/tmp/dpishell-pub': 0o755, '/www/dpishell': 0o755 };

// Каталог только root (security-audit-router №4): /tmp общий, и /tmp/dpishell мог заранее создать другой пользователь
// (nfqws работает от daemon) или подложить ссылку. Чужой, ссылка или не каталог — в сторону, создаётся свой. true — каталог наш.
function own_dir(d, mode) {
	let s = lstat(d);
	if (s && (s.type != 'directory' || s.uid != 0)) {
		let aside = `${d}.bad-${time()}`;
		if (!rename(d, aside))
			system(`rm -rf '${d}'`);
		s = null;
	}
	if (!s)
		mkdir(d, mode);
	chmod(d, mode);
	s = lstat(d);
	return !!(s && s.type == 'directory' && s.uid == 0);
}

// Файл годится, только если он обычный и принадлежит root (не ссылка, не подложен другим пользователем).
function root_file(path) {
	let s = lstat(path);
	return !!(s && s.type == 'file' && s.uid == 0);
}

// Своё прежних версий, которое больше не нужно (r14): results/ — итоги подборов, их не читал никто, а росли без предела;
// ссылка на ipset-all.txt. Удаляется только своё: файлы «<число>.json» и их .tmp в своём каталоге, без перехода по ссылкам.
function cleanup_legacy() {
	let r = `${P.etc}/results`, s = lstat(r);
	if (s?.type == 'link')
		unlink(r);
	else if (s?.type == 'directory' && s.uid == 0) {
		for (let f in lsdir(r) ?? [])
			if (match(f, /^[0-9]+[.]json([.]tmp)?$/) && index(['file', 'link'], lstat(`${r}/${f}`)?.type) >= 0)
				unlink(`${r}/${f}`);
		rmdir(r);
	}
	let ia = `${P.lists}/ipset-all.txt`;
	if (readlink(ia) == `${P.share}/lists/ipset-all.txt`)
		unlink(ia);
}

function ensure_dirs() {
	for (let d in DIRS)
		own_dir(d, DIRS[d]);
	cleanup_legacy();
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
	// writefile возвращает число записанных байт: 0 у пустого файла — не ошибка. Не записалось (кончилось место) —
	// недописанный .tmp убрать: на полном флеше он занимал бы последнее.
	if (writefile(tmp, text) == null || !rename(tmp, path)) {
		unlink(tmp);
		return false;
	}
	return true;
}

// Записать, только если содержимое другое (одинаковое во флеш не переписываем). true — файл такой, как надо.
function write_if_changed(path, text) {
	return readfile(path) == text || write_atomic(path, text);
}

// Повреждённый файл не «обнуляется» молча: копия .bad рядом и строка в журнале (следующая запись затёрла бы исходник).
function read_json(path, dflt) {
	let t = readfile(path);
	if (t == null)
		return dflt;
	try { return json(t); } catch (e) {
		// Только свои настройки (/etc/dpishell): там потеря заметна, и каталог пишет лишь root.
		if (index(path, `${P.etc}/`) == 0 && !stat(`${path}.bad`)) {
			writefile(`${path}.bad`, t);
			system(`logger -t dpishell 'Повреждён файл ${replace(path, "'", '')}, копия — .bad'`);
		}
		return dflt;
	}
}

function uci_get(opt, dflt, c) {
	c ??= cursor();
	let v = c.get('dpishell', 'main', opt);
	return v == null ? dflt : v;
}

// «Изменено DpiShell» (changes.uc): время появления пунктов, которого не видно по файлам — включение, IPv6, ускорение сети,
// DNS. Пишется во флеш только при появлении пункта (переход «как до DpiShell» → «изменено»); ключей не больше MARKS.
const CHANGES = '/etc/dpishell/changes.json';
const MARKS = { enabled: ['enable', '1'], ipv6: ['ipv6', '1'], fw_offload_prev: ['flow_offload', null], dns_preset: ['dns', null] };

function changes_mark(ids) {
	if (!length(ids))
		return;
	let m = read_json(CHANGES, {});
	if (type(m) != 'object')
		m = {};
	for (let id in ids)
		m[id] = time();
	// Только известные ключи: чужое или старое в файле не копится.
	let out = {};
	for (let k in MARKS)
		if (m[MARKS[k][0]] != null)
			out[MARKS[k][0]] = m[MARKS[k][0]];
	write_if_changed(CHANGES, sprintf('%J\n', out));
}

function uci_set(values) {
	let c = cursor();
	let marks = [];
	for (let k in values) {
		let v = values[k], mk = MARKS[k];
		// Переход в «изменено»: enabled/ipv6 → 1, fw_offload_prev — появился, dns_preset — не провайдер (а был провайдер или пусто).
		if (mk && v != null) {
			let old = c.get('dpishell', 'main', k);
			if (mk[1] ? (`${v}` == mk[1] && old != mk[1]) : (k == 'dns_preset' ? (v != 'provider' && (old == null || old == 'provider')) : old == null))
				push(marks, mk[0]);
		}
		if (v == null)
			c.delete('dpishell', 'main', k);
		else
			c.set('dpishell', 'main', k, type(v) == 'array' ? v : `${v}`);
	}
	c.commit('dpishell');
	changes_mark(marks);
}

// Один курсор uci на все параметры: /etc/config/dpishell разбирается один раз.
function settings() {
	let c = cursor();
	let g = (k, d) => uci_get(k, d, c);
	let backups = g('backups', []);
	return {
		enabled: g('enabled', '0') == '1',
		strategy: g('strategy', '') || null,
		backups: type(backups) == 'array' ? backups : [backups],
		mode: g('mode', 'all'),
		ipv6: g('ipv6', '0') == '1',
		flowoffload: g('flowoffload', 'none'),
		schedule: {
			enabled: g('schedule_enabled', '1') == '1',
			hours: +g('schedule_hours', '1'),
			auto_select: g('auto_select', '0') == '1',
			watchdog: g('watchdog', '1') == '1',
		},
	};
}


function roles() { return cat.load_json(`${P.share}/roles.json`); }
function catalog() { return cat.load_json(`${P.share}/strategies.json`); }
function user_catalog() { return read_json(`${P.etc}/user-strategies.json`, { strategies: [] }); }
// Найденные глубоким поиском на роутере (id r-…): отдельный файл, чтобы strategies_user_set приложения их не затирал.
function found_catalog() { return read_json(`${P.etc}/found-strategies.json`, { strategies: [] }); }
function catalogs() { return [catalog(), user_catalog(), found_catalog()]; }

// Продвинутый режим (router-api §5.2 advanced_set, mixed-strategies.md §6): { enabled, others: direct|lists|all,
// rows: [ { domains, strategy: auto|direct|id, resolved } ] }. Выключен — всё как раньше, строки хранятся.
const ADVANCED = '/etc/dpishell/advanced.json';
const ADV_OTHERS = ['direct', 'lists', 'all'];

function advanced() {
	let a = read_json(ADVANCED, null) ?? {};
	return { enabled: !!a.enabled, others: index(ADV_OTHERS, a.others) >= 0 ? a.others : 'lists', rows: type(a.rows) == 'array' ? a.rows : [] };
}

// Совместная работа с ПК (router-api §5.2 pc_link_set, mixed-strategies.md §7): { items: [ { id, macs, domains,
// seen_at, expires_at } ] }. Связка действует, пока не истекла (24 ч без продления) и оптимизация включена.
const PC_LINKS = '/etc/dpishell/pc-links.json';
const PC_MAX = 4, PC_TTL = 24 * 3600;
const CUSTOM_SRC = '/usr/share/dpishell/zapret-custom.sh';

// Продление связки (pc_link_set раз в 30 мин) меняет только seen_at/expires_at — они живут в ОЗУ, во флеш пишутся лишь
// новые MAC и сайты. После перезагрузки — сроки из файла (окно продлит связку при следующем обращении).
const PC_EXPIRY = '/tmp/dpishell/pc-expiry.json';

function pc_links() {
	let l = read_json(PC_LINKS, null);
	let items = type(l?.items) == 'array' ? l.items : [];
	let ex = read_json(PC_EXPIRY, {});
	return map(items, x => (ex[x.id]?.expires_at > x.expires_at) ? { ...x, seen_at: ex[x.id].seen_at, expires_at: ex[x.id].expires_at } : x);
}

function pc_active(s) {
	return s.enabled ? filter(pc_links(), x => x.expires_at > time()) : [];
}

// Домены, которыми владеет роутер: его списки ролей (YouTube, Discord) и строки продвинутого режима. Совпадающие домены
// ПК роутер к себе не берёт («у адреса один хозяин»), в обе стороны: сам домен, поддомен и надомен.
function owned_domains() {
	let out = [];
	for (let f in ['google.txt', 'discord.txt'])
		for (let l in split(readfile(`${P.share}/lists/${f}`) ?? '', '\n'))
			if (length(trim(l))) push(out, lc(trim(l)));
	let a = advanced();
	if (a.enabled)
		for (let r in a.rows) push(out, ...r.domains);
	return out;
}

function pc_split(domains) {
	let own = owned_domains(), kept = [], ignored = [];
	let covers = (a, b) => b == a || (length(b) > length(a) && substr(b, length(b) - length(a) - 1) == `.${a}`);
	for (let d in domains)
		push(length(filter(own, o => covers(o, d) || covers(d, o))) ? ignored : kept, d);
	return { kept, ignored };
}

// strat — стратегия, для которой пишется config. Строка «auto» идёт по resolved (итог подбора для строки), а во время
// подбора (selecting) и до него — по проверяемой/текущей стратегии.
function build_opts(s, strat, selecting) {
	let o = {
		mode: s.mode, lists_dir: P.lists, fake_dir: P.fake,
		ipset_file: 'ipset-none.txt', ipv6: s.ipv6, flowoffload: s.flowoffload, features: {},
	};
	// ПК: исключения — в файл pc-<n>.txt (без доменов роутера); номер — порядок активных связок.
	let pcs = slice(pc_active(s), 0, PC_MAX);
	if (length(pcs)) {
		o.pcs = [];
		for (let i = 0; i < length(pcs); i++) {
			let file = `${P.lists}/pc-${i + 1}.txt`, kept = pc_split(pcs[i].domains).kept;
			write_if_changed(file, length(kept) ? join('\n', kept) + '\n' : '');
			chmod(file, 0o644);   // nfqws читает списки от daemon
			push(o.pcs, { id: pcs[i].id, macs: pcs[i].macs, exclude: file });
		}
	}
	let a = advanced();
	if (!a.enabled)
		return o;
	if (a.others != 'direct')
		o.mode = a.others;
	let rows = [], direct = [];
	for (let r in a.rows) {
		if (r.strategy == 'direct') {
			push(direct, ...r.domains);
			continue;
		}
		let id = r.strategy == 'auto' ? (selecting ? null : r.resolved) : r.strategy;
		let rs = (id ? cat.find(catalogs(), id) : null) ?? strat;
		if (rs) push(rows, { domains: r.domains, strategy: rs });
	}
	o.advanced = { others: a.others, rows, direct };
	return o;
}

// Записать /opt/zapret/config для стратегии-объекта (в т.ч. кандидата поиска). null — успех, иначе текст ошибки.
// selecting — кандидат подбора: config в ОЗУ (P.trial_config), zapret перезапускается с ZAPRET_CONFIG (zapret_env).
function write_zapret_config_for(st, s, selecting) {
	let r = roles();
	let err = cat.validate(st, r);
	if (err)
		return `Стратегия ${st.id} не прошла проверку: ${err}`;
	ensure_dirs();
	let o = build_opts(s, st, selecting);
	// Скрипт custom.d (второй nfqws для ПК): без связок он ничего не делает, но держим на месте — ядро ставится отдельно.
	let cd = `${P.zapret}/init.d/openwrt/custom.d`;
	if (stat(cd) && readfile(`${cd}/50-dpishell`) != readfile(CUSTOM_SRC))
		write_atomic(`${cd}/50-dpishell`, readfile(CUSTOM_SRC) ?? '');
	write_atomic(`${P.run}/pc-rendered.json`, sprintf('%J\n', map(o.pcs ?? [], x => x.id)));
	let text = cat.render_config(st, r, o);
	if (selecting)
		return write_atomic(P.trial_config, text) ? null : 'Не удалось записать config zapret';
	unlink(P.trial_config);
	return write_if_changed(P.zapret_config, text) ? null : 'Не удалось записать config zapret';
}

// Окружение для init.d zapret: во время подбора — config кандидата из ОЗУ (zapret v72: ZAPRET_CONFIG в init.d/openwrt/functions).
function zapret_env() {
	return root_file(P.trial_config) ? `ZAPRET_CONFIG=${P.trial_config} ` : '';
}

// Config, с которым сейчас работает zapret (имитация DPI в эмуляторе читает профили nfqws).
function active_config() {
	return root_file(P.trial_config) ? P.trial_config : P.zapret_config;
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

// Аварийный выключатель (architecture.md «Аварийный выключатель»): ожидающее подтверждение включения
// { until, revert_to: 'off'|id, token } и последнее выключение без человека { by, at, text, targets }.
const PENDING = '/etc/dpishell/pending-confirm.json';
const LAST_OFF = '/etc/dpishell/last-off.json';
// Подбор после сторожа уже был { at, ok, best } — больше сам не пробует, пока человек не включит обход или не запустит подбор.
const WATCHDOG_SELECT = '/etc/dpishell/watchdog-select.json';

function pending_confirm() {
	let p = read_json(PENDING, null);
	return p ? { until: iso(p.until), left_s: max(0, p.until - time()), revert_to: p.revert_to } : null;
}

function last_off() {
	let o = read_json(LAST_OFF, null);
	return o ? { ...o, at: iso(o.at) } : null;
}

// status.json: файл в ОЗУ (P.pub_status), в /www/dpishell — ссылка на него (создаётся один раз; uhttpd отдаёт ссылки).
function write_status_json() {
	let s = settings(), lc = last_check();
	let pub = {
		product: 'dpishell-router', api: API, min_app: MIN_APP, version: VERSION, engine: 'zapret1', core: 'v72.13',
		enabled: s.enabled, strategy: s.strategy, catalog_version: catalog().version,
		checked_at: iso(lc?.at), last_check_ok: lc?.ok,
		pending_confirm: pending_confirm(), last_off: s.enabled ? null : last_off(),
	};
	if (!own_dir(P.pub, 0o755))
		return;
	write_if_changed(P.pub_status, sprintf('%J\n', pub));
	chmod(P.pub_status, 0o644);
	if (readlink(P.status_json) != P.pub_status) {
		own_dir('/www/dpishell', 0o755);
		let tl = `${P.status_json}.lnk`;
		unlink(tl);
		if (!symlink(P.pub_status, tl) || !rename(tl, P.status_json))
			unlink(tl);
	}
}

// Замки фоновых заданий /tmp/dpishell/<имя>.lock (security-audit-router №4, №7, 2.1):
//   «PID время_старта» — задание идёт, пока жив процесс с этим PID и тем же временем старта (поле 22 /proc/<pid>/stat):
//     PID, доставшийся другому процессу, замок не держит, и off/uninstall его не убьют;
//   «start <метка> <время>» — rpcd занял замок до запуска задания (job_reserve): два вызова подряд не запустят два задания;
//     задание с этой меткой (DPS_LOCK) забирает замок себе, без запуска замок сам истекает через RESERVE_S.
const RESERVE_S = 20;

function proc_start(pid) {
	let t = readfile(`/proc/${pid}/stat`);
	if (!t)
		return null;
	// Имя процесса в скобках может содержать пробелы — поля считаются от последней «)»: поле 3 — [0], поле 22 — [19].
	let rest = split(substr(t, rindex(t, ')') + 2), ' ');
	return rest[19];
}

function self_lock_id() {
	let pid = +split(readfile('/proc/self/stat'), ' ')[0];
	return `${pid} ${proc_start(pid)}`;
}

function lock_alive(text) {
	let f = split(trim(text ?? ''), ' ');
	if (f[0] == 'start')
		return time() - +(f[2] ?? 0) < RESERVE_S;
	let pid = +f[0];
	if (type(pid) != 'int' || pid <= 1)
		return false;
	let start = proc_start(pid);
	if (start == null)
		return false;
	if (length(f[1] ?? ''))
		return start == f[1];
	// Замок прежнего формата (только PID, до r12): живой процесс должен быть нашим run.
	return index(readfile(`/proc/${pid}/cmdline`) ?? '', 'dpishell/run') >= 0;
}

// Фоновое задание идёт: замок есть и его процесс жив (мёртвый замок после ошибки не считается).
function job_running(name) {
	return lock_alive(readfile(`${P.run}/${name}.lock`));
}

// Создать замок атомарно (O_EXCL); занятый мёртвым — убрать и попробовать ещё раз. true — замок наш.
function lock_create(f, text) {
	for (let i = 0; i < 2; i++) {
		let h = open(f, 'wx', 0o600);
		if (h) {
			h.write(text);
			h.close();
			return true;
		}
		if (lock_alive(readfile(f)))
			return false;
		unlink(f);
	}
	return false;
}

// rpcd: занять замок до запуска задания. Возвращает метку для DPS_LOCK или null — задание уже идёт.
function job_reserve(name) {
	ensure_dirs();
	let tok = replace(trim(readfile('/proc/sys/kernel/random/uuid') ?? `${time()}`), /[^0-9a-f-]/g, '');
	return lock_create(`${P.run}/${name}.lock`, `start ${tok} ${time()}\n`) ? tok : null;
}

// Задание (run): взять замок — свой, занятый для него rpcd (метка DPS_LOCK) или свободный. Путь замка или null — занято.
function job_lock(name) {
	ensure_dirs();
	let f = `${P.run}/${name}.lock`, me = `${self_lock_id()}\n`;
	let tok = getenv('DPS_LOCK'), cur = split(trim(readfile(f) ?? ''), ' ');
	if (tok && cur[0] == 'start' && cur[1] == tok)
		return write_atomic(f, me) ? f : null;
	return lock_create(f, me) ? f : null;
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
	ensure_dirs, own_dir, root_file, write_atomic, write_if_changed, read_json, zapret_env, active_config, job_reserve, job_lock, uci_set, settings, roles, catalog, user_catalog, found_catalog, catalogs,
	write_zapret_config, write_zapret_config_for, core_installed, last_check, write_status_json, valid_domain_line, valid_subnet_line, job_running, iso,
	PENDING, LAST_OFF, WATCHDOG_SELECT, pending_confirm, last_off, ADVANCED, ADV_OTHERS, advanced,
	PC_LINKS, PC_EXPIRY, PC_MAX, PC_TTL, pc_links, pc_active, pc_split, CHANGES
};
