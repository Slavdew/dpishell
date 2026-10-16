// Цели, оценка стратегий, подбор и проверка (02-spec §4.3–4.4, 06 §3.3, §3.5, §4).
'use strict';

import { readfile, writefile, stat, unlink, popen } from 'fs';
import * as cat from 'dpishell.catalog';
import * as st from 'dpishell.state';
import * as pr from 'dpishell.probe';
import * as srch from 'dpishell.search';

const TLS = ['1.3', '1.2'];
const PROGRESS = '/tmp/dpishell/select.json';
const CANCEL = '/tmp/dpishell/select.cancel';

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function nfqws_pid() { return trim(sh('pidof nfqws').out); }

// init.d zapret возвращается раньше, чем procd поднимет nfqws (в эмуляторе — 1–2 с): ждём новый процесс,
// иначе проверка идёт без обхода и даёт ложный провал. busybox sleep дробных секунд не понимает — sleep() ucode, мс.
function zapret_restart() {
	let old = nfqws_pid();
	let r = sh('/etc/init.d/zapret restart');
	if (r.rc != 0)
		return trim(r.out);
	for (let i = 0; i < 80; i++) {
		let pid = nfqws_pid();
		if (length(pid) && pid != old) {
			sleep(300);
			return null;
		}
		sleep(100);
	}
	return 'nfqws не запустился';
}

function zapret_stop() {
	sh('/etc/init.d/zapret stop');
	for (let i = 0; i < 50 && length(nfqws_pid()); i++)
		sleep(100);
}

// Группы для проверки: выбранные из targets.json + «Свои адреса» роутера; контрольная — всегда, если есть в данных.
function load_groups(selected, mode) {
	let data = cat.load_json(`${st.P.share}/targets.json`);
	let groups = [];
	for (let g in data.groups) {
		if (g.control === true || g.id == 'control') {
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
		sleep(500);
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
				if (v.required && !v.ok)
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
	let avg = length(ok) ? 1.0 * sum / length(ok) : 99;
	return {
		group_share: total ? 1.0 * passed / total : 0,
		target_share: length(normal) ? 1.0 * length(ok) / length(normal) : 0,
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

function zapret_apply_for(strat, s) {
	return st.write_zapret_config_for(strat, s) ?? zapret_restart();
}

function zapret_apply(strategy_id, s) {
	return st.write_zapret_config(strategy_id, s) ?? zapret_restart();
}

function ts_get() { return trim(readfile('/proc/sys/net/ipv4/tcp_timestamps') ?? '1'); }
function ts_set(v) { writefile('/proc/sys/net/ipv4/tcp_timestamps', `${v}\n`); }

function progress(p) { st.write_atomic(PROGRESS, sprintf('%J\n', p)); }

function cancel_requested() {
	if (!stat(CANCEL))
		return false;
	unlink(CANCEL);
	return true;
}

function strategy_order() {
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	let all = [];
	for (let c in st.catalogs())
		for (let s in c.strategies)
			push(all, s);
	// Порядок по статистике (02-spec §9.16): стабильные — первыми, новые — между проверенно рабочими и нерабочими.
	return sort(all, (a, b) => {
		let ra = stats[a.id]?.runs ? 1.0 * stats[a.id].passed / stats[a.id].runs : 0.5;
		let rb = stats[b.id]?.runs ? 1.0 * stats[b.id].passed / stats[b.id].runs : 0.5;
		return rb - ra;
	});
}

// Полная проверка одной стратегии — строка таблицы подбора.
function eval_strategy(strat, groups, items, cache, s, ts_was) {
	let row = { id: strat.id, title: strat.title ?? strat.id, group_share: 0, target_share: 0, avg_s: null, error: null, needs_ts: false };
	let e = zapret_apply_for(strat, s);
	if (e) {
		row.error = `ошибка запуска: ${e}`;
		return row;
	}
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
	// avg_s 99 — внутренняя заглушка «ничего не открылось» для сравнения; в таблицу — null (прочерк).
	return { ...row, group_share: sc.group_share, target_share: sc.target_share, avg_s: sc.target_share > 0 ? sc.avg_s : null, error: sc.error, perfect: sc.perfect };
}

// Без обхода: что открывается и так — не оцениваем; контрольные — оставляем только открывающиеся.
function baseline_groups(groups, cache) {
	zapret_stop();
	let base = run_checks(items_of(groups, TLS), cache);
	let blocked = {}, control_ok = {};
	for (let v in base) {
		let g = filter(groups, x => x.id == v.group)[0];
		if (g.control) { if (v.ok) control_ok[v.url] = true; }
		else if (!v.ok) blocked[`${v.group}|${v.url}`] = true;
	}
	for (let g in groups)
		g.targets = filter(g.targets, t => g.control ? control_ok[t.url] : blocked[`${g.id}|${t.url}`]);
	return filter(groups, g => length(g.targets));
}

// Итог: лучшая включается сама (02-spec §9.17), до 3 запасных. rows — только полные проверки (не кандидаты поиска).
function finish(p, rows, groups, s, ts_was, restore) {
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

const SEARCH_REFINE = 30, SEARCH_CONTROL_EVERY = 10;
const SEARCH_LIMITED = 'Провайдер, похоже, временно ограничил соединения — продолжите поиск позже';

function same_roles(a, b) {
	if (length(keys(a)) != length(keys(b)))
		return false;
	for (let k in a)
		if (a[k] != b[k])
			return false;
	return true;
}

// Найденные стратегии роутера: последние 20, id r-…, source user (07-router-api §5.1, вопрос 8).
function save_found(list) {
	let cur = st.found_catalog().strategies ?? [];
	let ids = map(list, x => x.id);
	let all = [...filter(cur, x => index(ids, x.id) < 0), ...list];
	if (length(all) > 20)
		all = slice(all, length(all) - 20);
	st.write_atomic(`${st.P.etc}/found-strategies.json`, sprintf('%J\n', { strategies: all }));
}

// Глубокий поиск (02-spec §4.7, как StrategySearch.cs): роли google и general подбираются раздельно на обязательных
// целях своих групп (одна попытка, короткие таймауты, обе версии TLS), затем 2 лучших приёма каждой роли собираются
// в стратегии и вместе с базовой проходят полную проверку. Возвращает { generated, rows, stop, base }.
function deep_search(p, groups, s, ts_was, cache) {
	let r_all = st.roles();
	let base = cat.find(st.catalogs(), s.strategy ?? 'alt9') ?? cat.find(st.catalogs(), 'alt9') ?? st.catalog().strategies[0];
	let state = { stop: null, n: 0, t0: time() };
	let by_role = {};
	let roles = filter(keys(srch.ROLE_GROUPS), role => length(filter(groups, g => !g.control && index(srch.ROLE_GROUPS[role], g.id) >= 0)));
	// Оценка объёма: грубый проход + уточнение + контрольные повторы на роль.
	p.total = length(roles) * (length(srch.coarse()) + SEARCH_REFINE + 6);

	for (let role in roles) {
		let rg = [];
		for (let g in groups)
			if (!g.control && index(srch.ROLE_GROUPS[role], g.id) >= 0) {
				let req = filter(g.targets, t => t.required !== false);
				if (length(req))
					push(rg, { ...g, targets: req });
			}
		if (!length(rg))
			continue;
		let items = items_of(rg, TLS);
		let results = [], tried = {};

		let try_c = function(desync) {
			if (state.stop || tried[desync])
				return null;
			tried[desync] = true;
			if (cancel_requested()) {
				state.stop = 'cancel';
				return null;
			}
			let strat = { id: 'search', title: 'поиск', roles: { ...base.roles, [role]: desync } };
			if (cat.validate(strat, r_all))
				return null;
			state.n++;
			let title = srch.describe(desync, role);
			p.index = state.n;
			p.message = title;
			p.eta_s = state.n > 1 ? int(1.0 * (time() - state.t0) / (state.n - 1) * (p.total > state.n ? p.total - state.n : 1)) : null;
			progress(p);
			let ok = 0, total = 0, sum = 0, err = zapret_apply_for(strat, s);
			if (!err) {
				let uses_ts = cat.uses_ts(strat);
				if (uses_ts) ts_set(0);
				let res = pr.check_batch(items, { parallel: 8, dns_cache: cache, connect_s: 2, read_s: 4 });
				if (uses_ts) ts_set(ts_was);
				let v = {};
				for (let i = 0; i < length(items); i++) {
					let k = `${items[i].group}|${items[i].target.url}`;
					let x = v[k] ??= { ok: true, seconds: 0 };
					x.ok = x.ok && res[i].ok;
					if (res[i].seconds > x.seconds) x.seconds = res[i].seconds;
				}
				for (let k in v) {
					total++;
					if (v[k].ok) { ok++; sum += v[k].seconds; }
				}
			}
			let c = { role, desync, ok, total, share: total ? 1.0 * ok / total : 0, seconds: ok ? 1.0 * sum / ok : 99, cost: srch.cost(desync) };
			push(results, c);
			push(p.rows, { id: `cand-${state.n}`, title, candidate: true, ok, total, group_share: c.share, target_share: c.share,
				avg_s: ok ? c.seconds : null, error: err ? `ошибка запуска: ${err}` : null, needs_ts: false });
			progress(p);
			sleep(300);
			return c;
		};

		// Контроль: базовый приём роли работал и вдруг перестал — провайдер, похоже, режет нас самих.
		let control = try_c(base.roles[role] ?? srch.coarse()[0]);
		let ctl = { works: control?.share == 1, since: 0, fails: 0 };
		let control_ok = function() {
			if (!ctl.works || ++ctl.since < SEARCH_CONTROL_EVERY)
				return true;
			ctl.since = 0;
			delete tried[control.desync];
			let c = try_c(control.desync);
			ctl.fails = c?.share == 1 ? 0 : ctl.fails + 1;
			return ctl.fails < 2;
		};

		for (let c in srch.coarse()) {
			try_c(c);
			if (state.stop) break;
			if (!control_ok()) { state.stop = 'limited'; break; }
		}

		// Уточнение «первое улучшение» (как StrategySearch.RefineAsync): семейства по очереди, от каждого шагаем
		// к первому соседу лучше текущего, пока улучшения есть. Бюджет общий, проверенное не повторяется (try_c → null);
		// контроль провайдера — после каждого кандидата, до сравнения. Обход слоями тратил бюджет на первый слой соседей.
		let budget = SEARCH_REFINE;
		for (let seed in filter(srch.best(results, 3), r => r.ok > 0)) {
			let current = seed, improved = true;
			while (improved && budget > 0 && !state.stop) {
				improved = false;
				for (let n in srch.neighbours(current.desync)) {
					if (budget <= 0 || state.stop) break;
					let r = try_c(n);
					if (r == null) continue;
					budget--;
					if (!control_ok()) { state.stop = 'limited'; break; }
					if (srch.cand_better(r, current)) { current = r; improved = true; break; }
				}
			}
			if (budget <= 0 || state.stop) break;
		}
		by_role[role] = results;
		if (state.stop) break;
	}

	// Сборка: 2 лучших приёма каждой роли, все сочетания.
	let generated = [];
	if (state.stop != 'cancel') {
		let tops = {};
		for (let role in keys(srch.ROLE_GROUPS))
			tops[role] = srch.best(filter(by_role[role] ?? [], x => x.ok > 0), 2);
		let lt = localtime();
		let stamp = sprintf('%02d%02d%02d-%02d%02d', lt.year % 100, lt.mon, lt.mday, lt.hour, lt.min);
		for (let g in (length(tops.google) ? tops.google : [null]))
			for (let d in (length(tops.general) ? tops.general : [null])) {
				if (g == null && d == null) continue;
				let roles2 = { ...base.roles };
				if (g) roles2.google = g.desync;
				if (d) {
					roles2.general = d.desync;
					// ipset-профиль общего трафика — тот же приём; игровой TCP-профиль базы построен на её general — убираем.
					if (roles2.ipset_tcp != null) roles2.ipset_tcp = d.desync;
					delete roles2.game_tcp;
				}
				// Такая стратегия уже есть (каталог, прошлый поиск, базовая) — проверяем её, дубль не создаём.
				let same = null;
				for (let c in st.catalogs())
					for (let x in c.strategies)
						if (!same && same_roles(x.roles, roles2))
							same = x;
				for (let x in generated)
					if (!same && same_roles(x.roles, roles2))
						same = x;
				if (same) {
					if (same.id != base.id && index(map(generated, x => x.id), same.id) < 0)
						push(generated, same);
					continue;
				}
				let n = length(filter(generated, x => index(x.id, 'r-') == 0)) + 1;
				let strat = { id: `r-${stamp}-${n}`, title: `Найденная ${n} (${stamp})`, source: 'user', roles: roles2 };
				if (!cat.validate(strat, r_all))
					push(generated, strat);
			}
	}
	return { generated, stop: state.stop, base };
}

// Подбор. args: { groups, mode: full|quick, depth: main|deep, started_by }
function select_run(args) {
	let s = st.settings();
	let prev = { enabled: s.enabled, strategy: s.strategy };
	let groups = load_groups(args.groups, s.mode);
	let ts_was = ts_get();
	let deep = args.depth == 'deep';
	let p = { running: true, started_by: args.started_by ?? 'app', depth: deep ? 'deep' : 'main', index: 0, total: 0, eta_s: null,
		message: 'Проверка без обхода', rows: [], finished_at: null, cancelled: false, best: null, applied: false, backups: [] };
	progress(p);
	let cache = {};
	let restore = () => {
		ts_set(ts_was);
		if (prev.enabled && prev.strategy)
			zapret_apply(prev.strategy, s);
		else
			zapret_stop();
	};
	let cancelled = () => {
		restore();
		p = { ...p, running: false, cancelled: true, applied: false, finished_at: time(), message: deep ? 'Поиск остановлен' : 'Подбор остановлен' };
		progress(p);
		return p;
	};

	// 1. Без обхода.
	groups = baseline_groups(groups, cache);
	if (!length(filter(groups, g => !g.control))) {
		restore();
		p = { ...p, running: false, finished_at: time(), message: 'Блокировки не обнаружено — всё открывается и без обхода' };
		progress(p);
		return p;
	}

	// 2. Список для полной проверки: готовые стратегии или (глубокий поиск) базовая + собранные из найденных приёмов.
	let list, note = null;
	if (deep) {
		let ds = deep_search(p, groups, s, ts_was, cache);
		if (ds.stop == 'cancel')
			return cancelled();
		if (!length(ds.generated)) {
			restore();
			p = { ...p, running: false, finished_at: time(), eta_s: 0,
				message: ds.stop == 'limited' ? SEARCH_LIMITED : 'Глубокий поиск не нашёл рабочих приёмов — прежнее состояние восстановлено' };
			progress(p);
			return p;
		}
		if (ds.stop == 'limited')
			note = SEARCH_LIMITED;
		list = [ds.base, ...ds.generated];
		p.total = p.index + length(list);
	} else {
		list = strategy_order();
		p.total = length(list);
	}

	let items = items_of(groups, TLS);
	let rows = [], start = p.index, t0 = time();
	for (let i = 0; i < length(list); i++) {
		if (cancel_requested())
			return cancelled();
		let strat = list[i];
		p.index = start + i + 1;
		p.message = `${strat.title ?? strat.id}`;
		p.eta_s = i ? int(1.0 * (time() - t0) / i * (length(list) - i)) : (deep ? null : p.eta_s);
		progress(p);
		let row = eval_strategy(strat, groups, items, cache, s, ts_was);
		push(rows, row);
		push(p.rows, row);
		progress(p);
		if (args.mode == 'quick' && !deep && row.perfect && !row.needs_ts)
			break;
		sleep(300);
	}

	// 3. Найденное, прошедшее полную проверку, — в свои стратегии роутера (до включения: apply ищет по id).
	if (deep) {
		let good = filter(list, x => x.source == 'user' && index(x.id, 'r-') == 0 &&
			filter(rows, r => r.id == x.id && r.error == null && r.group_share > 0)[0]);
		if (length(good))
			save_found(good);
		rows = filter(rows, r => index(r.id, 'r-') != 0 || filter(good, x => x.id == r.id)[0]);
	}
	p = finish(p, rows, groups, s, ts_was, restore);
	if (note) {
		p.message = `${p.message}. ${note}`;
		progress(p);
	}
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
