// Цели, оценка стратегий, подбор и проверка (02-spec §4.3–4.4, 06 §3.3, §3.5, §4).
'use strict';

import { readfile, writefile, stat, unlink, popen } from 'fs';
import * as cat from 'dpishell.catalog';
import * as st from 'dpishell.state';
import * as pr from 'dpishell.probe';

const TLS = ['1.3', '1.2'];
const PROGRESS = '/tmp/dpishell/select.json';
const CANCEL = '/tmp/dpishell/select.cancel';

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function sleep_s(s) { sh(`sleep ${s}`); }

// Группы для проверки: выбранные из targets.json + «Свои адреса» роутера; контрольная — всегда, если есть в данных.
function load_groups(selected, mode) {
	let data = cat.load_json(`${st.P.share}/targets.json`);
	let groups = [];
	for (let g in data.groups) {
		if (g.id == 'control') {
			if (mode == 'all')
				push(groups, { ...g, control: true });
			continue;
		}
		if (!selected || index(selected, g.id) >= 0)
			push(groups, g);
	}
	if (!selected || index(selected, 'user') >= 0) {
		let user = st.read_json(`${st.P.etc}/user-targets.json`, []);
		if (length(user))
			push(groups, { id: 'user', title: 'Свои адреса',
				targets: map(user, u => ({ url: u.url, required: u.important === true })) });
	}
	return groups;
}

function items_of(groups, tls_list, only_required) {
	let items = [];
	for (let g in groups)
		for (let t in g.targets) {
			if (only_required && t.required === false)
				continue;
			for (let v in tls_list)
				push(items, { group: g.id, target: t, tls: v });
		}
	return items;
}

// Проверка с повтором неудачных (две попытки, как на Windows); вердикт цели — все версии TLS прошли.
function run_checks(items, cache) {
	let res = pr.check_batch(items, { parallel: 8, dns_cache: cache });
	let again = [];
	for (let i = 0; i < length(res); i++)
		if (!res[i].ok)
			push(again, i);
	if (length(again)) {
		sleep_s('0.5');
		let r2 = pr.check_batch(map(again, i => items[i]), { parallel: 8, dns_cache: cache });
		for (let k = 0; k < length(again); k++)
			if (r2[k].ok)
				res[again[k]] = r2[k];
	}
	let verdicts = {};
	for (let i = 0; i < length(items); i++) {
		let key = `${items[i].group}|${items[i].target.url}`;
		let v = verdicts[key] ??= { group: items[i].group, url: items[i].target.url, required: items[i].target.required !== false, ok: true, seconds: 0, probes: [] };
		push(v.probes, res[i]);
		v.ok = v.ok && res[i].ok;
		if (res[i].seconds > v.seconds)
			v.seconds = res[i].seconds;
	}
	return values(verdicts);
}

// Оценка: доля прошедших групп, доля целей, среднее время; контрольная цель сломана — 0.
function score(groups, verdicts) {
	let total = 0, passed = 0, broken = [];
	for (let g in groups) {
		let vs = filter(verdicts, v => v.group == g.id);
		if (!length(vs))
			continue;
		if (g.control) {
			for (let v in vs)
				if (!v.ok)
					push(broken, pr.host_of(v.url));
			continue;
		}
		total++;
		if (length(filter(vs, v => v.required && !v.ok)) == 0 && length(filter(vs, v => v.ok)) > 0)
			passed++;
	}
	let normal = filter(verdicts, v => !filter(groups, g => g.id == v.group && g.control)[0]);
	let ok = filter(normal, v => v.ok);
	let sum = 0;
	for (let v in ok) sum += v.seconds;
	let avg = length(ok) ? sum / length(ok) : 99;
	return {
		group_share: total ? passed / total : 0,
		target_share: length(normal) ? length(ok) / length(normal) : 0,
		avg_s: avg,
		perfect: total > 0 && passed == total && !length(broken),
		error: length(broken) ? `ломает обычные сайты: ${join(', ', broken)}` : null,
		groups_total: total,
	};
}

function better(a, b) {
	if (!b) return true;
	if ((a.error == null) != (b.error == null)) return a.error == null;
	if (a.group_share != b.group_share) return a.group_share > b.group_share;
	if (a.target_share != b.target_share) return a.target_share > b.target_share;
	if (!!a.needs_ts != !!b.needs_ts) return !a.needs_ts;
	return a.avg_s < b.avg_s;
}

function zapret_apply(strategy_id, s) {
	let e = st.write_zapret_config(strategy_id, s);
	if (e) return e;
	let r = sh('/etc/init.d/zapret restart');
	return r.rc == 0 ? null : trim(r.out);
}

function ts_get() { return trim(readfile('/proc/sys/net/ipv4/tcp_timestamps') ?? '1'); }
function ts_set(v) { writefile('/proc/sys/net/ipv4/tcp_timestamps', `${v}\n`); }

function progress(p) { st.write_atomic(PROGRESS, sprintf('%J\n', p)); }

function strategy_order() {
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	let all = [];
	for (let c in st.catalogs())
		for (let s in c.strategies)
			push(all, s);
	// Порядок по статистике (02-spec §9.16): стабильные — первыми, новые — между проверенно рабочими и нерабочими.
	return sort(all, (a, b) => {
		let ra = stats[a.id]?.runs ? stats[a.id].passed / stats[a.id].runs : 0.5;
		let rb = stats[b.id]?.runs ? stats[b.id].passed / stats[b.id].runs : 0.5;
		return rb - ra;
	});
}

// Подбор. args: { groups, mode: full|quick, depth: main|deep, started_by }
function select_run(args) {
	let s = st.settings();
	let prev = { enabled: s.enabled, strategy: s.strategy };
	let groups = load_groups(args.groups, s.mode);
	let ts_was = ts_get();
	let p = { running: true, started_by: args.started_by ?? 'app', depth: args.depth ?? 'main', index: 0, total: 0, eta_s: null,
		message: 'Проверка без обхода', rows: [], finished_at: null, cancelled: false, best: null, applied: false, backups: [] };
	progress(p);
	let cache = {};
	let restore = () => {
		ts_set(ts_was);
		if (prev.enabled && prev.strategy)
			zapret_apply(prev.strategy, s);
		else
			sh('/etc/init.d/zapret stop');
	};

	// 1. Без обхода: что открывается и так — не оцениваем; контрольные — оставляем только открывающиеся.
	sh('/etc/init.d/zapret stop');
	let base = run_checks(items_of(groups, TLS), cache);
	let blocked = {}, control_ok = {};
	for (let v in base) {
		let g = filter(groups, x => x.id == v.group)[0];
		if (g.control) { if (v.ok) control_ok[v.url] = true; }
		else if (!v.ok) blocked[`${v.group}|${v.url}`] = true;
	}
	for (let g in groups)
		g.targets = filter(g.targets, t => g.control ? control_ok[t.url] : blocked[`${g.id}|${t.url}`]);
	groups = filter(groups, g => length(g.targets));
	if (!length(filter(groups, g => !g.control))) {
		restore();
		p = { ...p, running: false, finished_at: time(), message: 'Блокировки не обнаружено — всё открывается и без обхода' };
		progress(p);
		return p;
	}

	// 2. Стратегии.
	let list = strategy_order();
	p.total = length(list);
	let items = items_of(groups, TLS);
	let rows = [], t0 = time();
	for (let i = 0; i < length(list); i++) {
		if (stat(CANCEL)) {
			unlink(CANCEL);
			restore();
			p = { ...p, running: false, cancelled: true, finished_at: time(), message: 'Подбор остановлен' };
			progress(p);
			return p;
		}
		let strat = list[i];
		p.index = i + 1;
		p.message = `${strat.title ?? strat.id}`;
		p.eta_s = i ? int((time() - t0) / i * (length(list) - i)) : null;
		progress(p);
		let row = { id: strat.id, title: strat.title ?? strat.id, group_share: 0, target_share: 0, avg_s: null, error: null, needs_ts: false };
		let e = zapret_apply(strat.id, s);
		if (e) {
			row.error = `ошибка запуска: ${e}`;
		} else {
			sleep_s(1);
			let uses_ts = cat.uses_ts(strat);
			// Стратегии с ts оцениваем глазами клиента Windows: без меток времени (06 §4.3).
			if (uses_ts) ts_set(0);
			let sc = score(groups, run_checks(items, cache));
			if (uses_ts) {
				ts_set(ts_was);
				if (!sc.perfect) {
					let sc2 = score(groups, run_checks(items, cache));
					if (better(sc2, sc) && sc2.group_share > sc.group_share) { sc = sc2; row.needs_ts = true; }
				}
			}
			row = { ...row, group_share: sc.group_share, target_share: sc.target_share, avg_s: sc.avg_s, error: sc.error, perfect: sc.perfect };
		}
		push(rows, row);
		p.rows = rows;
		progress(p);
		if (args.mode == 'quick' && row.perfect && !row.needs_ts)
			break;
		sleep_s('0.3');
	}

	// 3. Итог: лучшая включается сама (02-spec §9.17), до 3 запасных.
	let ranked = sort(filter(rows, r => r.error == null && r.group_share > 0), (a, b) => better(a, b) ? -1 : 1);
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	for (let r in rows) {
		let x = stats[r.id] ??= { runs: 0, passed: 0 };
		x.runs++;
		if (r.perfect) x.passed++;
	}
	st.write_atomic(`${st.P.etc}/stats.json`, sprintf('%J\n', stats));
	ts_set(ts_was);
	if (length(ranked)) {
		let best = ranked[0], backups = map(slice(ranked, 1, 4), r => r.id);
		s.strategy = best.id;
		let e = zapret_apply(best.id, s);
		if (!e) {
			sh('/etc/init.d/zapret enable');
			st.uci_set({ strategy: best.id, enabled: 1, backups });
			p.applied = true;
		}
		p.best = best.id;
		p.backups = backups;
		p.message = e ? `Не удалось включить ${best.id}: ${e}` : `Включена ${best.title}`;
	} else {
		restore();
		p.message = 'Рабочей стратегии не найдено';
	}
	p.running = false;
	p.finished_at = time();
	progress(p);
	st.write_atomic(`${st.P.etc}/results/${p.finished_at}.json`, sprintf('%J\n', { ...p, groups: map(groups, g => g.id) }));
	st.write_status_json();
	return p;
}

// Проверка текущей стратегии. light — для таймера: обязательные цели, одна версия TLS (06 §3.5).
function check_run(source, light) {
	let s = st.settings();
	if (!s.enabled || !s.strategy)
		return null;
	let groups = load_groups(null, s.mode);
	let strat = cat.find(st.catalogs(), s.strategy);
	let ts_was = ts_get();
	if (strat && cat.uses_ts(strat)) ts_set(0);
	let verdicts = run_checks(items_of(groups, light ? ['1.3'] : TLS, light), {});
	ts_set(ts_was);
	let sc = score(groups, verdicts);
	let lc = { at: time(), source, ok: sc.perfect, groups: map(filter(groups, g => !g.control), g => ({
		id: g.id, ok: length(filter(verdicts, v => v.group == g.id && v.required && !v.ok)) == 0 })) , error: sc.error };
	st.write_atomic(`${st.P.etc}/last-check.json`, sprintf('%J\n', lc));
	let streak = lc.ok ? 0 : st.read_json(`${st.P.etc}/fail-streak.json`, 0) + 1;
	st.write_atomic(`${st.P.etc}/fail-streak.json`, `${streak}\n`);
	st.write_status_json();
	return { last_check: lc, fail_streak: streak };
}

export { load_groups, items_of, run_checks, score, select_run, check_run, PROGRESS, CANCEL };
