// Каталог стратегий и сборка конфигурации zapret (nfqws) из стратегии.
// Повторяет WinwsArgs.Validate/Build Windows-версии; роли, фильтры и белый список — в roles.json, не в коде.
'use strict';

import { readfile } from 'fs';

const TOKEN_RX = /^--[a-z0-9-]+(=[A-Za-z0-9_.,+=\/{}!-]+)?$/;
const FILE_VALUE_RX = /^(\{fake\}\/[A-Za-z0-9_.-]+\.bin|0x[0-9A-Fa-f]+|!)$/;
const MODES = { all: true, lists: true };
const FLOWOFFLOAD = { none: true, software: true, hardware: true };

function load_json(path) {
	let text = readfile(path);
	if (text == null)
		die(`нет файла ${path}`);
	return json(text);
}

function index_of(arr, v) {
	for (let i = 0; i < length(arr); i++)
		if (arr[i] == v)
			return i;
	return -1;
}

function role_ids(roles) {
	return map(roles.roles, r => r.id);
}

// Ошибка проверки — строка вида "<поле>: <текст>", null — всё в порядке.
// field — путь для ответа ubus (router-api §4: items[3].roles.google).
function validate(strategy, roles, field) {
	field ??= strategy.id;
	if (type(strategy.roles) != 'object' || !length(strategy.roles))
		return `${field}.roles: нет ролей`;
	let known = role_ids(roles);
	for (let role in strategy.roles) {
		let f = `${field}.roles.${role}`;
		if (index_of(known, role) < 0)
			return `${f}: неизвестная роль ${role}`;
		let args = strategy.roles[role];
		if (type(args) != 'string')
			return `${f}: не строка`;
		for (let tok in split(trim(args), /[ ]+/)) {
			if (!length(tok))
				continue;
			if (!match(tok, TOKEN_RX))
				return `${f}: недопустимые символы в ${tok}`;
			let eq = index(tok, '=');
			let name = eq < 0 ? tok : substr(tok, 0, eq);
			let val = eq < 0 ? '' : substr(tok, eq + 1);
			if (index_of(roles.options, name) < 0 && index_of(roles.role_options?.[role] ?? [], name) < 0)
				return `${f}: параметр ${name} не в списке разрешённых для роли ${role}`;
			if (index_of(roles.file_options, name) >= 0 ? !match(val, FILE_VALUE_RX) : index(val, '/') >= 0)
				return `${f}: путь вне папки фейков: ${tok}`;
			if (index(val, '..') >= 0)
				return `${f}: недопустимое значение ${tok}`;
		}
	}
	return null;
}

function subst(tok, opts) {
	tok = replace(tok, '{lists}', opts.lists_dir);
	tok = replace(tok, '{fake}', opts.fake_dir);
	return replace(tok, '{ipset_file}', opts.ipset_file);
}

// Профили nfqws для стратегии: массив токенов (профили разделены --new).
// opts: { mode: "all"|"lists", lists_dir, fake_dir, ipset_file, features: { game } }
function build(strategy, roles, opts) {
	if (!MODES[opts.mode])
		die(`неизвестный режим ${opts.mode}`);
	let args = [], adv = opts.advanced;
	// Продвинутый режим: строки «сайты → стратегия» — первыми профилями (первое совпадение), с десинком роли general
	// стратегии строки и, если у неё есть quic, — UDP 443 с тем же списком. Домены уже проверены valid_domain_line.
	for (let r in adv?.rows ?? []) {
		let doms = join(',', r.domains);
		for (let pair in [['general', '--filter-tcp=80,443'], ['quic', '--filter-udp=443']]) {
			let desync = r.strategy.roles?.[pair[0]];
			if (desync == null)
				continue;
			if (length(args))
				push(args, '--new');
			push(args, pair[1], `--hostlist-domains=${doms}`);
			for (let t in split(trim(desync), /[ ]+/))
				if (length(t))
					push(args, subst(t, opts));
		}
	}
	// «Остальные адреса — напрямую»: роли каталога не включаются.
	let catalog_roles = adv?.others == 'direct' ? [] : roles.roles;
	let direct = length(adv?.direct ?? []) ? `--hostlist-exclude-domains=${join(',', adv.direct)}` : null;
	for (let role in catalog_roles) {
		let desync = strategy.roles[role.id];
		// Роли функций (игровой режим) — только когда функция включена.
		if (desync == null || (role.feature && !opts.features?.[role.feature]))
			continue;
		if (length(args))
			push(args, '--new');
		for (let t in role.filter)
			push(args, subst(t, opts));
		let set = (opts.mode == 'all' && role.lists_all) ? role.lists_all : role.lists;
		if (set)
			for (let t in roles.list_sets[set])
				push(args, subst(t, opts));
		// Строки «напрямую» — мимо всех ролей каталога (в т.ч. «Все сайты»).
		if (direct)
			push(args, direct);
		for (let t in split(trim(desync), /[ ]+/))
			if (length(t))
				push(args, subst(t, opts));
	}
	return args;
}

// Содержимое /opt/zapret/config. Значения проверены validate() и путями пакета: кавычек, $ и ` в них нет.
function render_config(strategy, roles, opts) {
	let tokens = build(strategy, roles, opts);
	let fo = opts.flowoffload ?? 'none';
	if (!FLOWOFFLOAD[fo])
		die(`неизвестный FLOWOFFLOAD ${fo}`);
	for (let t in tokens)
		if (match(t, /["$`\\]/))
			die(`недопустимый символ в аргументе: ${t}`);
	let lines = [
		'# Создано DpiShell. Ручные правки перезапишутся.',
		`# strategy=${strategy.id} mode=${opts.mode}`,
		'FWTYPE=nftables',
		// Нечего обрабатывать (продвинутый режим: остальные напрямую и ни одной строки со стратегией) — nfqws не нужен.
		`NFQWS_ENABLE=${length(tokens) ? 1 : 0}`,
		`NFQWS_PORTS_TCP=${roles.ports.tcp}${opts.features?.game ? ',' + roles.ports.game : ''}`,
		`NFQWS_PORTS_UDP=${roles.ports.udp}${opts.features?.game ? ',' + roles.ports.game : ''}`,
		'NFQWS_TCP_PKT_OUT=9',
		'NFQWS_TCP_PKT_IN=3',
		'NFQWS_UDP_PKT_OUT=9',
		'NFQWS_UDP_PKT_IN=0',
		`NFQWS_OPT="${join(' ', tokens)}"`,
		'MODE_FILTER=none',
		// software/hardware — разгрузку ведёт zapret со своим исключением для пакетов nfqws (architecture.md «Диагностика роутера»), firewall её не включает.
		`FLOWOFFLOAD=${fo}`,
		'INIT_APPLY_FW=1',
		`DISABLE_IPV6=${opts.ipv6 ? 0 : 1}`,
		'DESYNC_MARK=0x40000000',
		'DESYNC_MARK_POSTNAT=0x20000000',
		'TPWS_ENABLE=0',
		'TPWS_SOCKS_ENABLE=0',
		'FILTER_TTL_EXPIRED_ICMP=1',
		'GZIP_LISTS=1',
		''
	];
	return join('\n', lines);
}

// Стратегия по id из каталога и своих стратегий (свои — после каталога, тот же validate).
function find(catalogs, id) {
	for (let c in catalogs)
		for (let s in c.strategies)
			if (s.id == id)
				return s;
	return null;
}

function uses_ts(strategy) {
	for (let role in strategy.roles)
		if (match(strategy.roles[role], /--dpi-desync-fooling=([a-z0-9]+,)*ts(,| |$)/))
			return true;
	return false;
}

export { load_json, validate, build, render_config, find, uses_ts };
