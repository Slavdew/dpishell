// Цели, оценка стратегий, подбор и проверка (architecture.md «Подбор стратегии», «Подбор на роутере»).
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
// Во время подбора config кандидата — в ОЗУ (st.zapret_env: ZAPRET_CONFIG), во флеш пишется только итог.
function zapret_restart() {
	let old = nfqws_pid();
	let r = sh(`${st.zapret_env()}/etc/init.d/zapret restart`);
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
// with_adv — для подбора: строки продвинутого режима со стратегией «auto» — свои группы adv-<номер строки> (цель — первый
// домен строки). В оценку стратегии они не входят (score), по ним подбор выбирает стратегию строки (resolve_adv).
function load_groups(selected, mode, with_adv) {
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
	let a = with_adv ? st.advanced() : null;
	if (a?.enabled)
		for (let i = 0; i < length(a.rows); i++)
			if (a.rows[i].strategy == 'auto' && length(a.rows[i].domains))
				push(groups, { id: `adv-${i}`, title: a.rows[i].domains[0], adv: true,
					targets: [{ url: `https://${a.rows[i].domains[0]}/`, required: true }] });
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

// Разбивка строки подбора по целям для «Подробнее» (router-api.md §5.1 select_progress): исход одной строкой,
// у провала — что сломалось и на какой версии TLS.
// detail_msg — та же строка ключами (router-api.md §9), если хоть одна часть пришла ключом (редирект на чужой адрес и т. п.).
function brief_verdicts(vs) {
	return map(vs, v => {
		let bad = filter(v.probes, p => !p.ok);
		let parts = length(bad) ? bad : (v.probes[0] ? [v.probes[0]] : []);
		let keyed = length(filter(parts, p => p.detail_msg)) > 0;
		let pieces = map(parts, p => {
			let text = length(bad) && length(v.probes) > 1 && p.tls ? `TLS ${p.tls}: ${p.detail}` : (p.detail ?? '');
			let m = p.detail_msg ?? { text: p.detail ?? '' };
			return { text, msg: text != (p.detail ?? '') ? { key: 'router.probe.tls', params: { tls: p.tls, detail: m }, text } : m };
		});
		let detail = join('; ', map(pieces, x => x.text));
		let r = { group: v.group, url: v.url, ok: v.ok, detail, seconds: 1.0 * int(v.seconds * 100 + 0.5) / 100 };
		if (keyed && length(pieces)) {
			let msg = pieces[0].msg, text = pieces[0].text;
			for (let i = 1; i < length(pieces); i++) {
				text = `${text}; ${pieces[i].text}`;
				msg = { key: 'router.probe.pair', params: { a: msg, b: pieces[i].msg }, text };
			}
			r.detail_msg = msg;
		}
		return r;
	});
}

// Оценка: доля прошедших групп, доля целей, среднее время; контрольная цель сломана — 0.
function score(groups, verdicts) {
	let total = 0, passed = 0, broken = [];
	for (let g in groups) {
		let vs = filter(verdicts, v => v.group == g.id);
		if (!length(vs))
			continue;
		if (g.adv)
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
	let normal = filter(verdicts, v => !filter(groups, g => g.id == v.group && (g.control || g.adv))[0]);
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
		groups_ok: passed,
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
	return st.write_zapret_config_for(strat, s, true) ?? zapret_restart();
}

function zapret_apply(strategy_id, s) {
	return st.write_zapret_config(strategy_id, s) ?? zapret_restart();
}

function ts_get() { return trim(readfile('/proc/sys/net/ipv4/tcp_timestamps') ?? '1'); }
function ts_set(v) { writefile('/proc/sys/net/ipv4/tcp_timestamps', `${v}\n`); }

function progress(p) { st.write_atomic(PROGRESS, sprintf('%J\n', p)); }

// stats.json — только стратегии, что есть в каталогах (удалённые свои и найденные не копятся); во флеш — только если изменился.
function stats_save(stats) {
	let known = {};
	for (let c in st.catalogs())
		for (let x in c.strategies)
			known[x.id] = true;
	let out = {};
	for (let id in stats)
		if (known[id])
			out[id] = stats[id];
	st.write_if_changed(`${st.P.etc}/stats.json`, sprintf('%J\n', out));
}

function cancel_requested() {
	if (!stat(CANCEL))
		return false;
	unlink(CANCEL);
	return true;
}

function strategy_order(current) {
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	let all = [];
	for (let c in st.catalogs())
		for (let s in c.strategies)
			push(all, s);
	// Как быстрый подбор окна ПК («до первой рабочей, начиная с прежних», матрица ПК×роутер п. 24): включённая — первой,
	// затем открывшие всё при последней проверке, затем по доле удачных подборов (architecture.md «Порядок по статистике»):
	// стабильные — первыми, новые — между проверенно рабочими и нерабочими.
	let key = x => [x.id == current ? 1 : 0, stats[x.id]?.last?.ok ? 1 : 0,
		stats[x.id]?.runs ? 1.0 * stats[x.id].passed / stats[x.id].runs : 0.5];
	return sort(all, (a, b) => {
		let ka = key(a), kb = key(b);
		for (let i = 0; i < 3; i++)
			if (ka[i] != kb[i])
				return kb[i] > ka[i] ? 1 : -1;
		return 0;
	});
}

// Полная проверка одной стратегии — строка таблицы подбора.
function eval_strategy(strat, groups, items, cache, s, ts_was) {
	let row = { id: strat.id, title: strat.title ?? strat.id, group_share: 0, target_share: 0, avg_s: null, error: null, needs_ts: false,
		groups_ok: 0, groups_total: length(filter(groups, g => !g.control)) };
	let e = zapret_apply_for(strat, s);
	if (e) {
		row.error = `ошибка запуска: ${e}`;
		return row;
	}
	let uses_ts = cat.uses_ts(strat);
	// Стратегии с ts оцениваем глазами клиента Windows: без меток времени (architecture.md «Проверка одной цели»).
	if (uses_ts) ts_set(0);
	let vs = run_checks(items, cache);
	let sc = score(groups, vs);
	if (uses_ts) {
		ts_set(ts_was);
		if (!sc.perfect) {
			let vs2 = run_checks(items, cache);
			let sc2 = score(groups, vs2);
			if (better(sc2, sc) && sc2.group_share > sc.group_share) { sc = sc2; vs = vs2; row.needs_ts = true; }
		}
	}
	// avg_s 99 — внутренняя заглушка «ничего не открылось» для сравнения; в таблицу — null (прочерк).
	return { ...row, group_share: sc.group_share, target_share: sc.target_share, avg_s: sc.target_share > 0 ? sc.avg_s : null, error: sc.error, perfect: sc.perfect,
		groups_ok: sc.groups_ok, groups_total: sc.groups_total, verdicts: brief_verdicts(vs) };
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

// Сообщение хода и итога: text — для старых окон, message_msg — ключ общего словаря и сырые параметры (router-api.md §9, 2026-10-20).
function say(p, key, text, params) {
	p.message = text;
	p.message_msg = { key, params: params ?? {}, text };
	return p;
}

// Последняя проверка стратегии для списка стратегий (router-api.md §5.1 strategies.items[].last): что не открылось — названиями групп.
const GROUP_TITLE = { youtube: 'YouTube', discord: 'Discord', user: 'Свои адреса', control: 'Обычные сайты' };

function last_of(ok, failed_ids) {
	let seen = {}, failed = [];
	for (let g in failed_ids)
		if (!seen[g]) { seen[g] = true; push(failed, GROUP_TITLE[g] ?? g); }
	return { at: time(), ok, failed };
}

// Итог: лучшая включается сама (architecture.md «Автоприменение»), до 3 запасных. rows — только полные проверки (не кандидаты поиска).
function finish(p, rows, groups, s, ts_was, restore) {
	let ranked = sort(filter(rows, r => r.error == null && r.group_share > 0), (a, b) => better(a, b) ? -1 : 1);
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	for (let r in rows) {
		let x = stats[r.id] ??= { runs: 0, passed: 0 };
		x.runs++;
		if (r.perfect) x.passed++;
		x.last = last_of(!!r.perfect, map(filter(r.verdicts ?? [], v => !v.ok), v => v.group));
	}
	stats_save(stats);
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
		if (e)
			say(p, 'web.msg.apply_failed', `Не удалось включить ${best.id}: ${e}`, { name: best.title ?? best.id, error: e });
		else
			say(p, 'web.select.result_applied', `Включена ${best.title}`, { name: best.title ?? best.id });
	} else {
		restore();
		say(p, 'select.result.none', 'Рабочей стратегии не найдено');
	}
	p.running = false;
	p.finished_at = time();
	progress(p);
	// Итог — только в /tmp/dpishell/select.json: архив подборов во флеше (results/) не читал никто, а рос без предела.
	st.write_status_json();
	return p;
}

// Итог проверки выбранных стратегий (select_run с ids): статистика, без включения; всё — как было до проверки.
function finish_ids(p, rows, groups, ts_was, restore) {
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	for (let r in rows) {
		let x = stats[r.id] ??= { runs: 0, passed: 0 };
		x.runs++;
		if (r.perfect) x.passed++;
		x.last = last_of(!!r.perfect, map(filter(r.verdicts ?? [], v => !v.ok), v => v.group));
	}
	stats_save(stats);
	restore();
	let ok = filter(rows, r => r.perfect);
	p.best = length(ok) ? ok[0].id : null;
	p.applied = false;
	p.backups = [];
	if (length(rows) == 1)
		say(p, rows[0].perfect ? 'web.msg.one_ok' : 'web.msg.one_partial', rows[0].perfect ? `${rows[0].title}: всё открывается` : `${rows[0].title}: открывается не всё`,
			{ name: rows[0].title, ok: rows[0].groups_ok, total: rows[0].groups_total });
	p.running = false;
	p.finished_at = time();
	progress(p);
	st.write_status_json();
	return p;
}

const SEARCH_REFINE = 30, SEARCH_CONTROL_EVERY = 10;
// Предел глубокого поиска — «до 15 мин», как на ПК (матрица ПК×роутер п. 25): перебор приёмов — до 13 мин, остальное —
// полная проверка собранных. Живой замер у владельца — 10,4 мин (2026-10-20).
const SEARCH_MAX_S = 13 * 60;
// Предел обычного подбора: в худшем случае 20 стратегий × 2,5 мин — почти час нестабильной сети у всех в доме.
const SELECT_MAX_S = 20 * 60;
const SEARCH_LIMITED = 'Провайдер, похоже, временно ограничил соединения — продолжите поиск позже';

function same_roles(a, b) {
	if (length(keys(a)) != length(keys(b)))
		return false;
	for (let k in a)
		if (a[k] != b[k])
			return false;
	return true;
}

// Найденные стратегии роутера: последние 20, id r-…, source user (router-api §5.1).
function save_found(list) {
	let cur = st.found_catalog().strategies ?? [];
	let ids = map(list, x => x.id);
	let all = [...filter(cur, x => index(ids, x.id) < 0), ...list];
	if (length(all) > 20)
		all = slice(all, length(all) - 20);
	st.write_atomic(`${st.P.etc}/found-strategies.json`, sprintf('%J\n', { strategies: all }));
}

// Смешанная (mixed-strategies.md §2–3): ни одна стратегия не открыла всё — для каждой группы берём лучшую по её целям из уже
// сделанного прогона и собираем роли группы из неё. Роли частей: YouTube — google; Discord — discord_tcp (у каталожных его
// нет — берём их general), discord_udp, discord_media; свои — general, quic, ipset_*, game_*. Стратегии с ошибкой (ломают
// обычные сайты) не берём. null — смешивать нечего (все группы лучше всего у одной стратегии).
// Лучшая для группы g стратегия прогона: доля открывшихся целей группы, затем время; с ошибкой (ломает обычные сайты) и
// кандидаты поиска не берутся. null — ни у кого ничего не открылось.
function group_best(rows, g) {
	let best = null, bs = null;
	for (let r in rows) {
		if (r.error != null || r.candidate) continue;
		let vs = filter(r.verdicts ?? [], v => v.group == g);
		if (!length(vs)) continue;
		let ok = filter(vs, v => v.ok), sum = 0;
		for (let v in ok) sum += v.seconds;
		let sc = { share: 1.0 * length(ok) / length(vs), t: length(ok) ? 1.0 * sum / length(ok) : 99 };
		if (!bs || sc.share > bs.share || (sc.share == bs.share && sc.t < bs.t)) { best = r.id; bs = sc; }
	}
	return best && bs.share > 0 ? best : null;
}

// Строки «auto» продвинутого режима: стратегия строки — лучшая по её группе adv-<i> (mixed-strategies.md §6).
// Строка, у которой не открылось ничего, остаётся без resolved — идёт по текущей стратегии.
function resolve_adv(rows) {
	let a = st.advanced();
	if (!a.enabled)
		return;
	let changed = false;
	for (let i = 0; i < length(a.rows); i++) {
		if (a.rows[i].strategy != 'auto')
			continue;
		let best = group_best(rows, `adv-${i}`);
		if (best != a.rows[i].resolved) { a.rows[i].resolved = best; changed = true; }
	}
	if (changed)
		st.write_atomic(st.ADVANCED, sprintf('%J\n', a));
}

// Длина строки в символах (length() считает байты UTF-8): не считаем байты продолжения 0x80–0xBF.
function ulen(s) {
	let n = 0;
	for (let i = 0; i < length(s); i++)
		if ((ord(s, i) & 0xC0) != 0x80) n++;
	return n;
}

// Подпись смешанной — как MixBuilder.MakeTitle (ПК): «Смешанная · YouTube — ALT · Discord — ALT2 · свои сайты — ALT10»;
// части из одной стратегии сливаются (Discord и свои — «остальное», прочие — «YouTube и Discord»); имя части —
// «general (ALT10)» → «ALT10», длиннее 18 знаков — id; не влезает в 60 знаков — «свои» вместо «свои сайты».
// parts: [ [группа, стратегия] ] в порядке youtube, discord, user.
function mix_title(parts) {
	let groups = [];
	for (let p in parts) {
		let g = filter(groups, x => x.strategy.id == p[1].id)[0];
		if (g) push(g.parts, p[0]);
		else push(groups, { parts: [p[0]], strategy: p[1] });
	}
	let part_name = s => {
		let t = s.title ?? s.id, m = match(t, /\(([^()]+)\)[ ]*$/);
		let n = (m && index(lc(t), 'general') == 0) ? m[1] : t;
		return ulen(n) > 18 ? s.id : n;
	};
	let label = (p, short) => p == 'youtube' ? 'YouTube' : p == 'discord' ? 'Discord' : short ? 'свои' : 'свои сайты';
	let make = short => 'Смешанная · ' + join(' · ', map(groups, g =>
		(length(g.parts) == 2 && index(g.parts, 'discord') >= 0 && index(g.parts, 'user') >= 0 ? 'остальное'
			: join(' и ', map(g.parts, p => label(p, short)))) + ` — ${part_name(g.strategy)}`));
	let full = make(false);
	return ulen(full) <= 60 ? full : make(true);
}

const MIX_ROLES = { youtube: ['google'], discord: ['discord_tcp', 'discord_udp', 'discord_media'],
	user: ['general', 'quic', 'ipset_udp', 'ipset_tcp', 'game_tcp', 'game_udp'] };

function mix_from(rows, list, groups) {
	let gids = filter(map(filter(groups, g => !g.control), g => g.id), id => MIX_ROLES[id]);
	if (length(gids) < 2)
		return null;
	let pick = {};
	for (let g in gids) {
		let best = group_best(rows, g);
		if (best) pick[g] = best;
	}
	let ids = uniq(values(pick));
	if (length(keys(pick)) < 2 || length(ids) < 2)
		return null;
	let src = id => filter(list, x => x.id == id)[0];
	// Чего нет в pick (группа не открылась ни у кого) — из основы: «свои», иначе Discord, иначе YouTube (как MixBuilder.BasePart:
	// общие роли general/quic/ipset лучше брать у части, рассчитанной на обычные сайты).
	let base_id = pick.user ?? pick.discord ?? pick.youtube, base = src(base_id);
	let roles = {};
	for (let g in keys(MIX_ROLES)) {
		let from = src(pick[g]) ?? base;
		if (!from) continue;
		for (let role in MIX_ROLES[g]) {
			let v = from.roles?.[role] ?? (role == 'discord_tcp' ? from.roles?.general : null);
			if (v != null) roles[role] = v;
		}
	}
	let title = mix_title(map(['youtube', 'discord', 'user'], g => [g, src(pick[g] ?? base_id)]));
	// id по частям: один и тот же набор частей — одна стратегия (статистика копится, копии не плодятся).
	let key = join('|', map(['youtube', 'discord', 'user'], g => pick[g] ?? '')), h = 5381;
	for (let i = 0; i < length(key); i++) h = (h * 33 + ord(key, i)) % 2147483647;
	return { id: sprintf('r-mix-%08x', h), title, source: 'user', roles, parts: pick };
}

// Глубокий поиск (architecture.md «Глубокий поиск», как StrategySearch.cs): роли google и general подбираются раздельно на обязательных
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
			if (time() - state.t0 > SEARCH_MAX_S) {
				state.stop = 'time';
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
			let ok = 0, total = 0, sum = 0, gk = {}, err = zapret_apply_for(strat, s);
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
				for (let i = 0; i < length(items); i++)
					gk[items[i].group] = (gk[items[i].group] ?? true) && v[`${items[i].group}|${items[i].target.url}`].ok;
				for (let k in v) {
					total++;
					if (v[k].ok) { ok++; sum += v[k].seconds; }
				}
			}
			let c = { role, desync, ok, total, share: total ? 1.0 * ok / total : 0, seconds: ok ? 1.0 * sum / ok : 99, cost: srch.cost(desync) };
			push(results, c);
			push(p.rows, { id: `cand-${state.n}`, title, candidate: true, ok, total,
				groups_ok: err ? 0 : length(filter(values(gk), x => x)), groups_total: length(rg), group_share: c.share, target_share: c.share,
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
		// Название — словами (UX-аудит п. 55): «Новая стратегия 2 (поиск 16.10 23:19)»; id — с меткой времени.
		// Часовой пояс не задан (/etc/TZ — GMT0/UTC, как на роутере владельца) — местного времени роутер не знает: только дата по UTC.
		let tz = trim(readfile('/etc/TZ') ?? '');
		let when = (tz != '' && index(['GMT0', 'UTC', 'UTC0'], tz) < 0)
			? sprintf('%02d.%02d %02d:%02d', lt.mday, lt.mon, lt.hour, lt.min)
			: sprintf('%02d.%02d', gmtime().mday, gmtime().mon);
		// Как StrategySearch.Assemble: лучший приём каждой роли поверх базовой и по одной запасной на роль со вторым
		// приёмом (остальные — лучшие). Роли независимы (свои списки) — сочетания не перемножаем.
		let best = {}, picks = [];
		for (let role in keys(srch.ROLE_GROUPS))
			if (length(tops[role])) best[role] = tops[role][0].desync;
		if (length(keys(best))) {
			push(picks, best);
			for (let role in keys(srch.ROLE_GROUPS))
				if (length(tops[role]) > 1)
					push(picks, { ...best, [role]: tops[role][1].desync });
		}
		for (let pick in picks) {
				let roles2 = { ...base.roles };
				for (let role in pick) roles2[role] = pick[role];
				if (pick.general) {
					// ipset-профиль общего трафика — тот же приём; игровой TCP-профиль базы построен на её general — убираем.
					if (roles2.ipset_tcp != null) roles2.ipset_tcp = pick.general;
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
				let strat = { id: `r-${stamp}-${n}`, title: `Новая стратегия ${n} (поиск ${when})`, source: 'user', roles: roles2 };
				if (!cat.validate(strat, r_all))
					push(generated, strat);
			}
	}
	return { generated, stop: state.stop, base };
}

// Подбор. args: { groups, mode: full|quick, depth: main|deep, ids?: [id], started_by }
// ids — проверить только эти стратегии (как «Проверить только её» на ПК): без автоприменения, прежняя стратегия и вкл/выкл
// возвращаются, запасные не трогаются; best — проверенная, если открыла всё.
function select_run(args) {
	let s = st.settings();
	let prev = { enabled: s.enabled, strategy: s.strategy };
	let groups = load_groups(args.groups, s.mode, true);
	let ts_was = ts_get();
	let ids = type(args.ids) == 'array' && length(args.ids) ? args.ids : null;
	let deep = args.depth == 'deep' && !ids;
	let p = { running: true, started_by: args.started_by ?? 'app', depth: deep ? 'deep' : 'main', index: 0, total: 0, eta_s: null,
		groups: map(groups, g => g.id), ids, rows: [], finished_at: null, cancelled: false, best: null, applied: false, backups: [] };
	p.groups = filter(p.groups, g => g != 'control');
	say(p, 'web.msg.baseline', 'Проверка без оптимизации');
	progress(p);
	let cache = {};
	let restore = () => {
		ts_set(ts_was);
		if (prev.enabled && prev.strategy)
			zapret_apply(prev.strategy, s);
		else {
			zapret_stop();
			unlink(st.P.trial_config);
		}
	};
	let t_start = time();
	let cancelled = () => {
		restore();
		p = { ...p, running: false, cancelled: true, applied: false, finished_at: time() };
		say(p, deep ? 'web.msg.search_stopped' : 'web.msg.select_stopped', deep ? 'Поиск остановлен' : 'Подбор остановлен');
		progress(p);
		return p;
	};

	// 1. Без обхода.
	groups = baseline_groups(groups, cache);
	if (!length(filter(groups, g => !g.control))) {
		restore();
		p = { ...p, running: false, finished_at: time() };
		say(p, 'select.result.nothing_blocked', 'всё открывается и без оптимизации — подбирать нечего');
		progress(p);
		return p;
	}

	// 2. Список для полной проверки: готовые стратегии или (глубокий поиск) базовая + собранные из найденных приёмов.
	let list, note = null, note_key = null;
	if (deep) {
		let ds = deep_search(p, groups, s, ts_was, cache);
		if (ds.stop == 'cancel')
			return cancelled();
		if (!length(ds.generated)) {
			restore();
			p = { ...p, running: false, finished_at: time(), eta_s: 0 };
			if (ds.stop == 'limited')
				say(p, 'web.msg.limited', SEARCH_LIMITED);
			else
				say(p, 'web.msg.deep_none', 'Глубокий поиск: подходящей стратегии не нашлось. Всё возвращено как было.');
			progress(p);
			return p;
		}
		if (ds.stop == 'limited')
			note = SEARCH_LIMITED, note_key = 'web.msg.limited';
		else if (ds.stop == 'time')
			note = 'Поиск ограничен 15 минутами', note_key = 'web.msg.time_limit';
		list = [ds.base, ...ds.generated];
		p.total = p.index + length(list);
	} else if (ids) {
		list = filter(map(ids, id => cat.find(st.catalogs(), id)), x => x);
		p.total = length(list);
	} else {
		list = strategy_order(prev.enabled ? prev.strategy : null);
		p.total = length(list);
	}

	let items = items_of(groups, TLS);
	let rows = [], start = p.index, t0 = time();
	for (let i = 0; i < length(list); i++) {
		if (cancel_requested())
			return cancelled();
		// Общий предел обычного подбора (у глубокого — свой, SEARCH_MAX_S): итог — из уже проверенных.
		if (!deep && !ids && i > 0 && time() - t_start > SELECT_MAX_S) {
			note = 'Подбор ограничен 20 минутами', note_key = 'web.msg.select_time_limit';
			break;
		}
		let strat = list[i];
		p.index = start + i + 1;
		say(p, 'web.msg.checking', `${strat.title ?? strat.id}`, { name: strat.title ?? strat.id });
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

	// Проверка выбранных: статистика — как у подбора, включённое — как было.
	if (ids)
		return finish_ids(p, rows, groups, ts_was, restore);

	// 3. Найденное, прошедшее полную проверку, — в свои стратегии роутера (до включения: apply ищет по id).
	if (deep) {
		let good = filter(list, x => x.source == 'user' && index(x.id, 'r-') == 0 &&
			filter(rows, r => r.id == x.id && r.error == null && r.group_share > 0)[0]);
		if (length(good))
			save_found(good);
		rows = filter(rows, r => index(r.id, 'r-') != 0 || filter(good, x => x.id == r.id)[0]);
	}

	// 4. Ни одна не открыла всё — смешанная из лучших по группам и один проверочный прогон (с контрольной группой).
	// Берём, только если открыла больше групп, чем лучшая одиночная; иначе её строки не остаётся.
	if (!deep && !length(filter(rows, r => r.perfect)) && !cancel_requested()) {
		let mix = mix_from(rows, list, groups);
		if (mix) {
			p.index = p.total = p.index + 1;
			say(p, 'web.msg.checking', mix.title, { name: mix.title });
			progress(p);
			let row = { ...eval_strategy(mix, groups, items, cache, s, ts_was), parts: mix.parts };
			// Как MixBuilder.BeatsSingle: больше доля групп, при равной — больше доля адресов, чем у лучшей одиночной.
			let top = null;
			for (let r in rows)
				if (r.error == null && (!top || r.group_share > top.group_share || (r.group_share == top.group_share && r.target_share > top.target_share)))
					top = r;
			if (row.error == null && row.groups_ok > 0 && (!top || row.group_share > top.group_share ||
					(row.group_share == top.group_share && row.target_share > top.target_share))) {
				save_found([mix]);
				push(rows, row);
				push(p.rows, row);
			}
			progress(p);
		}
	}
	// 5. Строки «auto» продвинутого режима — до включения итога: config итога пишется уже с их стратегиями.
	if (!deep)
		resolve_adv(rows);
	p = finish(p, rows, groups, s, ts_was, restore);
	if (note) {
		say(p, 'web.msg.with_note', `${p.message}. ${note}`, { message: p.message_msg, note: { key: note_key, text: note } });
		progress(p);
	}
	return p;
}

// Проверка текущей стратегии. light — для таймера: обязательные цели, одна версия TLS (architecture.md «Проверка по таймеру и запасные»).
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
	// Флеш — только при перемене (проверка по таймеру раз в час иначе переписывала бы 4 файла): свежая запись — в /tmp
	// (st.last_check читает её первой), во флеш — если сменился итог или группы, нет файла или проверку запустил человек.
	st.write_atomic(`${st.P.run}/last-check.json`, sprintf('%J\n', lc));
	let flash = st.read_json(`${st.P.etc}/last-check.json`, null);
	if (source != 'timer' || flash?.ok != lc.ok || sprintf('%J', flash?.groups) != sprintf('%J', lc.groups) || flash?.error != lc.error)
		st.write_atomic(`${st.P.etc}/last-check.json`, sprintf('%J\n', lc));
	let stats = st.read_json(`${st.P.etc}/stats.json`, {});
	let x = stats[s.strategy] ??= { runs: 0, passed: 0 };
	let last = last_of(!!sc.perfect, map(filter(verdicts, v => v.required && !v.ok), v => v.group));
	if (source != 'timer' || x.last?.ok != last.ok || sprintf('%J', x.last?.failed) != sprintf('%J', last.failed)) {
		x.last = last;
		stats_save(stats);
	}
	let was = st.read_json(`${st.P.etc}/fail-streak.json`, 0);
	let streak = lc.ok ? 0 : was + 1;
	if (streak != was)
		st.write_atomic(`${st.P.etc}/fail-streak.json`, `${streak}\n`);
	st.write_status_json();
	return { last_check: lc, fail_streak: streak };
}

export { load_groups, items_of, run_checks, score, select_run, check_run, mix_from, resolve_adv, PROGRESS, CANCEL };
