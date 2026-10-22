// «Изменено DpiShell» на роутере (router-api.md §5.2 changes_list / changes_undo / changes_undo_all, с 0.2.0-r16) —
// как «Изменения DpiShell» на ПК: что пакет поменял на роутере, у каждого пункта — «Отменить» или почему нельзя.
// Пункты выводятся из текущего состояния (включено ли, что в списках, есть ли копия DNS…), а не из ленты событий:
// журнал не растёт, не расходится с роутером и не теряется при сбое. Во флеше — только время появления пунктов, у которых
// его не видно по файлам (CHANGES, ≤ 3 ключей, пишется лишь при появлении пункта — state.uc changes_mark).
// «Как было до DpiShell» у своих настроек — пусто (их создаёт пакет), у системных — копии, которые пакет и так ведёт:
// fw_offload_prev (ускорение сети), dns-backup.json (DNS сети).
'use strict';

import { readfile, stat, lstat, unlink, popen } from 'fs';
import { cursor } from 'uci';
import * as st from 'dpishell.state';
import * as dns from 'dpishell.dns';

const JOB = '/tmp/dpishell/changes_job.json';
// Порядок «Отменить всё»: сначала выключить оптимизацию (дальше config zapret не перезапускается на каждом шаге), DNS —
// последним (дольше всех и может на секунды оставить сеть без имён).
const ORDER = ['enable', 'pc_link', 'advanced', 'list', 'ipv6', 'flow_offload', 'dns', 'core'];
const LIST_KEYS = { 'user-general': 'router.changes.list.user_general', 'user-exclude': 'router.changes.list.user_exclude',
	'user-ipset': 'router.changes.list.user_ipset', 'user-ipset-exclude': 'router.changes.list.user_ipset_exclude' };
const LIST_TEXT = { 'user-general': 'Свои сайты', 'user-exclude': 'Свои исключения', 'user-ipset': 'Свои подсети',
	'user-ipset-exclude': 'Исключённые подсети' };

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function mtime(path) {
	return lstat(path)?.mtime ?? null;
}

function lines_of(path) {
	return length(filter(split(readfile(path) ?? '', '\n'), l => length(trim(l)) > 0));
}

function fw(k) {
	return trim(sh(`uci -q get firewall.@defaults[0].${k}`).out) == '1';
}

// Пункт: id, kind, at (секунды), key/params/text — название (ключ общего словаря и русский для старых окон),
// undo — yes | no (нельзя отдельно: note_key/note — почему), risk — link (может на секунды оборвать связь).
function item(id, kind, at, key, params, text, extra) {
	return { id, kind, at, key, params: params ?? {}, text, undo: 'yes', risk: null, ...(extra ?? {}) };
}

// Пункты по текущему состоянию роутера. Порядок — ORDER.
function items() {
	let s = st.settings(), m = st.read_json(st.CHANGES, {}), out = [];
	let c = cursor();
	if (s.enabled)
		push(out, item('enable', 'enable', m.enable, 'router.changes.item.enable', {}, 'Оптимизация включена: nfqws, правила nft, автозапуск'));
	for (let x in st.pc_links())
		push(out, item(`pc:${x.id}`, 'pc_link', x.seen_at, 'router.changes.item.pc_link', { macs: join(', ', x.macs ?? []) },
			`Связка с ПК: ${join(', ', x.macs ?? [])}`));
	if (stat(st.ADVANCED))
		push(out, item('advanced', 'advanced', mtime(st.ADVANCED), 'router.changes.item.advanced', {}, 'Свои настройки для сайтов'));
	for (let n in st.USER_LISTS) {
		let f = `${st.P.lists}/${n}.txt`, k = lines_of(f);
		if (k > 0)
			push(out, item(`list:${n}`, 'list', mtime(f), 'router.changes.item.list', { name: { key: LIST_KEYS[n], params: {}, text: LIST_TEXT[n] }, n: k },
				`${LIST_TEXT[n]}: строк ${k}`));
	}
	if (s.ipv6)
		push(out, item('ipv6', 'ipv6', m.ipv6, 'router.changes.item.ipv6', {}, 'Оптимизация для IPv6'));
	let prev = c.get('dpishell', 'main', 'fw_offload_prev');
	if (prev)
		push(out, item('flow_offload', 'flow_offload', m.flow_offload, 'router.changes.item.flow_offload', { mode: prev },
			'Ускорение сети (flow offloading) выключено', { risk: 'link' }));
	let b = st.read_json(dns.BACKUP, null), preset = c.get('dpishell', 'main', 'dns_preset');
	if (b || (preset && preset != 'provider'))
		push(out, item('dns', 'dns', b?.at ?? m.dns, 'router.changes.item.dns', { preset: preset ?? '' }, 'DNS для всей сети', { risk: 'link' }));
	let core = st.read_json(`${st.P.etc}/core-installed.json`, null);
	if (core)
		push(out, item('core', 'core', core.at, 'router.changes.item.core', { version: core.version ?? '' }, 'Ядро zapret в /opt/zapret',
			{ undo: 'no', note_key: 'router.changes.row.core', note: 'Убирается при удалении DpiShell' }));
	return out;
}

// Для ответа API: время — ISO 8601.
function list() {
	let job = st.read_json(JOB, null);
	if (job) { job.started_at = st.iso(job.started_at); job.at = st.iso(job.at); }
	// Задание умерло (перезагрузка, убито) — итог «прервалось», не «идёт» навсегда.
	if (job?.running && !st.job_running('changes'))
		job = { ...job, running: false, ok: false };
	return { items: map(items(), x => ({ ...x, at: st.iso(x.at) })), job };
}

// config zapret и перезапуск — если оптимизация включена (иначе правка вступит при включении). null — успех.
function reapply() {
	let s = st.settings();
	if (!s.enabled || !s.strategy)
		return null;
	let e = st.write_zapret_config(s.strategy, s);
	if (e)
		return e;
	return sh('/etc/init.d/zapret restart').rc == 0 ? null : 'zapret restart';
}

// Отмена одного пункта. Итог: { status: restored | same | skipped | failed, note }.
function undo_one(it) {
	let k = it.kind;
	if (k == 'enable') {
		if (!st.settings().enabled)
			return { status: 'same' };
		if (st.core_installed()) {
			sh('/etc/init.d/zapret stop');
			sh('/etc/init.d/zapret disable');
		}
		unlink(st.PENDING);
		st.uci_set({ enabled: 0 });
		st.write_status_json();
		return { status: 'restored' };
	}
	if (k == 'pc_link') {
		let id = substr(it.id, 3), items = st.pc_links();
		if (!length(filter(items, x => x.id == id)))
			return { status: 'same' };
		if (!st.write_atomic(st.PC_LINKS, sprintf('%J\n', { items: filter(items, x => x.id != id) })))
			return { status: 'failed', note: 'Не удалось сохранить' };
		return { status: 'restored', note: reapply() };
	}
	if (k == 'advanced') {
		if (!stat(st.ADVANCED))
			return { status: 'same' };
		if (!unlink(st.ADVANCED) && stat(st.ADVANCED))
			return { status: 'failed', note: 'Не удалось сохранить' };
		unlink(`${st.ADVANCED}.prev`);
		return { status: 'restored', note: reapply() };
	}
	if (k == 'list') {
		let n = substr(it.id, 5), f = `${st.P.lists}/${n}.txt`;
		if (index(st.USER_LISTS, n) < 0)
			return { status: 'failed', note: 'Нет такого списка' };
		if (!lines_of(f))
			return { status: 'same' };
		return st.write_atomic(f, '') ? { status: 'restored' } : { status: 'failed', note: 'Не удалось сохранить' };
	}
	if (k == 'ipv6') {
		if (!st.settings().ipv6)
			return { status: 'same' };
		st.uci_set({ ipv6: 0 });
		return { status: 'restored', note: reapply() };
	}
	if (k == 'flow_offload') {
		let prev = cursor().get('dpishell', 'main', 'fw_offload_prev');
		if (!prev)
			return { status: 'same' };
		// Пользователь включил ускорение сам после нас — «уже как было», наша отметка больше не нужна.
		let already = fw('flow_offloading') && (prev != 'hardware' || fw('flow_offloading_hw'));
		if (!already) {
			let r = sh(`uci set firewall.@defaults[0].flow_offloading=1${prev == 'hardware' ? ' && uci set firewall.@defaults[0].flow_offloading_hw=1' : ''} && uci commit firewall`);
			if (r.rc != 0)
				return { status: 'failed', note: `firewall: ${trim(r.out)}` };
			sh('/etc/init.d/firewall reload');
		}
		st.uci_set({ fw_offload_prev: null, flowoffload: null });
		let e = reapply();
		return { status: already ? 'same' : 'restored', note: e };
	}
	if (k == 'dns') {
		if (st.job_running('dns'))
			return { status: 'failed', note: 'Идёт настройка DNS', note_key: 'web.err.busy_dns' };
		let l = st.job_lock('dns');
		if (!l)
			return { status: 'failed', note: 'Идёт настройка DNS', note_key: 'web.err.busy_dns' };
		let e = null;
		try { e = dns.restore(); } catch (x) { e = `${x}`; }
		unlink(l);
		return e ? { status: 'failed', note: e } : { status: 'restored' };
	}
	return { status: 'failed', note: 'Отдельно отменить нельзя', note_key: 'router.changes.row.core' };
}

// Задание отмены (run changes_undo <id|all>): итог по пунктам — в JOB. Пункты, которые сейчас уже как было, в «всё» не попадают.
function undo(id) {
	let all = items(), todo = id == 'all' ? filter(all, x => x.undo == 'yes') : filter(all, x => x.id == id && x.undo == 'yes');
	let job = { id, running: true, started_at: time(), items: [] };
	st.write_atomic(JOB, sprintf('%J\n', job));
	todo = sort(todo, (a, b) => index(ORDER, a.kind) - index(ORDER, b.kind));
	for (let it in todo) {
		let r;
		try { r = undo_one(it); } catch (x) { r = { status: 'failed', note: `${x}` }; }
		push(job.items, { id: it.id, kind: it.kind, key: it.key, params: it.params, text: it.text, status: r.status,
			...(r.note ? { note: r.note } : {}), ...(r.note_key ? { note_key: r.note_key } : {}) });
		st.write_atomic(JOB, sprintf('%J\n', job));
	}
	job.running = false;
	job.ok = !length(filter(job.items, x => x.status == 'failed'));
	job.at = time();
	st.write_atomic(JOB, sprintf('%J\n', job));
	st.write_status_json();
	return job;
}

export { items, list, undo, JOB };
