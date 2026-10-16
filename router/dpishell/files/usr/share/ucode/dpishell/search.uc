// Кандидаты глубокого поиска (02-spec §4.7) — копия CandidateGenerator Windows-версии (StrategySearch.cs):
// те же грубый проход, соседи и «цена» приёма, чтобы ПК и роутер искали одинаково. Только параметры из roles.json.
'use strict';

const GOOGLE = '{fake}/tls_clienthello_www_google_com.bin';
const TLS_FAKES = [GOOGLE, '{fake}/tls_clienthello_4pda_to.bin', '{fake}/tls_clienthello_max_ru.bin', '!'];

// Какая роль обслуживает какие группы целей (google.txt — YouTube, general — остальное).
const ROLE_GROUPS = { google: ['youtube'], general: ['discord', 'user'] };

// Грубый проход: по одному-двум представителям каждого семейства приёмов.
function coarse() {
	return [
		'--dpi-desync=multisplit --dpi-desync-split-pos=1',
		'--dpi-desync=multisplit --dpi-desync-split-pos=2,sniext+1',
		'--dpi-desync=multisplit --dpi-desync-split-pos=midsld',
		'--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld',
		`--dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=${GOOGLE}`,
		'--dpi-desync=multisplit --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={fake}/tls_clienthello_4pda_to.bin',
		`--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls=${GOOGLE}`,
		`--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls=${GOOGLE}`,
		`--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-fake-tls=${GOOGLE}`,
		`--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-autottl=2 --dpi-desync-fake-tls=${GOOGLE}`,
		`--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-split-pos=1 --dpi-desync-fake-tls=${GOOGLE}`,
		`--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-split-pos=1 --dpi-desync-fake-tls=${GOOGLE}`,
		`--dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-split-pos=midsld --dpi-desync-fake-tls=${GOOGLE}`,
		'--dpi-desync=fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-split-pos=1 --dpi-desync-fakedsplit-pattern=0x00',
		`--dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls=${GOOGLE}`,
		'--dpi-desync=fakeddisorder --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-split-pos=midsld',
		'--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com',
		'--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=md5sig --dpi-desync-hostfakesplit-mod=host=ya.ru',
	];
}

// Токены вида [имя, значение]; значение может содержать «=» (host=ya.ru).
function parse(desync) {
	let t = [];
	for (let tok in split(trim(desync), /[ ]+/)) {
		if (!length(tok))
			continue;
		let eq = index(tok, '=');
		push(t, eq < 0 ? [tok, ''] : [substr(tok, 0, eq), substr(tok, eq + 1)]);
	}
	return t;
}

function format(t) {
	return join(' ', map(t, x => length(x[1]) ? `${x[0]}=${x[1]}` : x[0]));
}

function get(t, name) {
	for (let x in t)
		if (x[0] == name)
			return x[1];
	return null;
}

function without(t, name) {
	return filter(t, x => x[0] != name);
}

function set(t, name, value) {
	let r = map(t, x => [x[0], x[1]]);
	for (let x in r)
		if (x[0] == name) {
			x[1] = value;
			return format(r);
		}
	push(r, [name, value]);
	return format(r);
}

function has_fake(mode) {
	for (let m in split(mode, ','))
		if (m == 'fake' || m == 'fakedsplit' || m == 'fakeddisorder' || m == 'hostfakesplit')
			return true;
	return false;
}

// Соседи кандидата: меняем по одному параметру (жадный поиск), порядок — как на Windows.
function neighbours(desync) {
	let t = parse(desync), out = [];
	let mode = get(t, '--dpi-desync') ?? '';
	if (get(t, '--dpi-desync-split-pos') != null)
		for (let v in ['1', '2', 'midsld', 'sniext+1', '1,midsld', '2,sniext+1', 'host+1'])
			push(out, set(t, '--dpi-desync-split-pos', v));
	if (get(t, '--dpi-desync-split-seqovl') != null)
		for (let v in ['568', '681', '336'])
			push(out, set(t, '--dpi-desync-split-seqovl', v));
	if (get(t, '--dpi-desync-split-seqovl-pattern') != null)
		for (let f in TLS_FAKES)
			if (f != '!')
				push(out, set(t, '--dpi-desync-split-seqovl-pattern', f));
	if (has_fake(mode)) {
		for (let v in ['ts', 'md5sig', 'badseq', 'badsum', 'datanoack'])
			push(out, set(without(t, '--dpi-desync-autottl'), '--dpi-desync-fooling', v));
		for (let v in ['2', '3'])
			push(out, set(without(t, '--dpi-desync-fooling'), '--dpi-desync-autottl', v));
		for (let v in ['2', '6', '11'])
			push(out, set(t, '--dpi-desync-repeats', v));
	}
	if (get(t, '--dpi-desync-fake-tls') != null) {
		for (let f in TLS_FAKES)
			push(out, set(t, '--dpi-desync-fake-tls', f));
		push(out, set(t, '--dpi-desync-fake-tls-mod', 'rnd,dupsid,sni=www.google.com'));
	}
	return out;
}

// Сколько «шума» создаёт приём: при равном результате выбираем тише (меньше повторов, без фейков).
function cost(desync) {
	let t = parse(desync);
	let mode = get(t, '--dpi-desync') ?? '';
	let r = get(t, '--dpi-desync-repeats');
	let repeats = (r != null && match(r, /^[0-9]+$/)) ? +r : 1;
	let fake = index(mode, 'fake') >= 0;
	return (fake ? repeats : 0) + length(split(mode, ',')) + length(t) * 0.1;
}

const ROLE_TITLES = { google: 'YouTube', general: 'Discord и свои' };

// Короткое описание кандидата для таблицы подбора: «fake + multisplit, ts — YouTube» (07-router-api §8, вопрос 8).
function describe(desync, role) {
	let t = parse(desync);
	let parts = [join(' + ', split(get(t, '--dpi-desync') ?? '?', ','))];
	let fool = get(t, '--dpi-desync-fooling');
	if (fool != null) push(parts, fool);
	let ttl = get(t, '--dpi-desync-autottl');
	if (ttl != null) push(parts, `autottl ${ttl}`);
	let pos = get(t, '--dpi-desync-split-pos');
	if (pos != null) push(parts, `pos ${pos}`);
	let ovl = get(t, '--dpi-desync-split-seqovl');
	if (ovl != null) push(parts, `seqovl ${ovl}`);
	let rep = get(t, '--dpi-desync-repeats');
	if (rep != null) push(parts, `×${rep}`);
	let fk = get(t, '--dpi-desync-fake-tls');
	if (fk != null) push(parts, fk == '!' ? 'фейк !' : replace(replace(fk, '{fake}/tls_clienthello_', ''), '.bin', ''));
	if (get(t, '--dpi-desync-fake-tls-mod') != null) push(parts, 'mod');
	return `${join(', ', parts)} — ${ROLE_TITLES[role] ?? role}`;
}

// Лучше: больше доля, затем тише, затем заметно быстрее (Better в StrategySearch.cs).
function cand_better(a, b) {
	return a.share > b.share || (a.share == b.share && (a.cost < b.cost - 0.01 || (a.cost <= b.cost && a.seconds < b.seconds * 0.8)));
}

// n лучших: доля ↓, цена ↑, время ↑.
function best(list, n) {
	let s = sort(slice(list), (a, b) => a.share != b.share ? (b.share - a.share) : a.cost != b.cost ? (a.cost - b.cost) : (a.seconds - b.seconds));
	return slice(s, 0, n);
}

export { ROLE_GROUPS, coarse, parse, neighbours, cost, describe, cand_better, best };
