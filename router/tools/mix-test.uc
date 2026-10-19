// Тест сборки смешанной стратегии (select.uc mix_from) на искусственном прогоне. Запуск в эмуляторе:
//   ucode -L '/usr/share/ucode/*.uc' /tmp/mix-test.uc
'use strict';
import * as sel from 'dpishell.select';

let fails = 0;
function ok(cond, what) { if (!cond) { fails++; print(`FAIL ${what}\n`); } else print(`ok   ${what}\n`); }

let groups = [{ id: 'youtube' }, { id: 'discord' }, { id: 'user' }, { id: 'control', control: true }];
let list = [
	{ id: 'A', title: 'A', roles: { google: 'gA', general: 'nA', quic: 'qA', discord_udp: 'duA' } },
	{ id: 'B', title: 'B', roles: { google: 'gB', general: 'nB', discord_media: 'dmB' } },
	{ id: 'C', title: 'C', roles: { google: 'gC', general: 'nC' } },
];
let v = (g, k) => ({ group: g, ok: k, seconds: 1 });
let rows = [
	{ id: 'A', verdicts: [v('youtube', true), v('youtube', true), v('discord', false), v('user', false)] },
	{ id: 'B', verdicts: [v('youtube', false), v('discord', true), v('discord', true), v('user', false)] },
	{ id: 'C', verdicts: [v('youtube', false), v('discord', false), v('user', true)] },
];
let m = sel.mix_from(rows, list, groups);
ok(m != null, 'смешанная собрана');
ok(m?.parts?.youtube == 'A' && m?.parts?.discord == 'B' && m?.parts?.user == 'C', 'части: YouTube A, Discord B, свои C');
ok(m?.roles?.google == 'gA', 'google из A');
ok(m?.roles?.discord_tcp == 'nB', 'discord_tcp из general части Discord');
ok(m?.roles?.discord_media == 'dmB', 'discord_media из части Discord');
ok(m?.roles?.discord_udp == null, 'discord_udp не берётся из другой части');
ok(m?.roles?.general == 'nC' && m?.roles?.quic == null, 'general из C, quic только из части «свои»');
ok(match(m?.id ?? '', /^r-mix-[0-9a-f]{8}$/) != null, 'id r-mix-…');
ok(sel.mix_from(rows, list, groups)?.id == m?.id, 'id детерминирован');
ok(m?.title == 'Смешанная · YouTube — A · Discord — B · свои сайты — C', 'название по частям (как MixBuilder)');
// Основа недостающих частей: «свои» → Discord → YouTube. Свои не открылись ни у кого — общие роли из части Discord.
let rows4 = [
	{ id: 'A', verdicts: [v('youtube', true), v('discord', false), v('user', false)] },
	{ id: 'B', verdicts: [v('youtube', false), v('discord', true), v('user', false)] },
];
let m4 = sel.mix_from(rows4, list, groups);
ok(m4?.parts?.user == null && m4?.roles?.general == 'nB', 'без части «свои» общие роли — из Discord');
ok(m4?.title == 'Смешанная · YouTube — A · остальное — B', 'Discord и свои из одной — «остальное»');
// Длинные имена частей: «general (ALT10)» → «ALT10», не влезает в 60 знаков — «свои».
let list5 = [
	{ id: 'alt10', title: 'general (ALT10)', roles: { google: 'g1', general: 'n1' } },
	{ id: 'r-261019-2120-2', title: 'Новая стратегия 2 (поиск 19.10)', roles: { google: 'g2', general: 'n2' } },
	{ id: 'alt3', title: 'general (ALT3)', roles: { google: 'g3', general: 'n3' } },
];
let rows5 = [
	{ id: 'alt10', verdicts: [v('youtube', true), v('discord', false), v('user', false)] },
	{ id: 'r-261019-2120-2', verdicts: [v('youtube', false), v('discord', true), v('user', false)] },
	{ id: 'alt3', verdicts: [v('youtube', false), v('discord', false), v('user', true)] },
];
let t5 = sel.mix_from(rows5, list5, groups)?.title;
ok(t5 == 'Смешанная · YouTube — ALT10 · Discord — r-261019-2120-2 · свои — ALT3', `длинная подпись сокращается: ${t5}`);

// Одна стратегия лучше всех во всех группах — смешивать нечего.
let rows2 = [
	{ id: 'A', verdicts: [v('youtube', true), v('discord', true), v('user', false)] },
	{ id: 'B', verdicts: [v('youtube', false), v('discord', false), v('user', false)] },
];
ok(sel.mix_from(rows2, list, groups) == null, 'одна лучшая во всём — null');
// Стратегия с ошибкой (ломает обычные сайты) в части не идёт.
let rows3 = [{ ...rows[0], error: 'ломает обычные сайты' }, rows[1], rows[2]];
ok(sel.mix_from(rows3, list, groups)?.parts?.youtube == null, 'стратегия с ошибкой не берётся');
// Строки «auto» продвинутого режима: стратегия строки — лучшая по её группе adv-<i>.
import * as st from 'dpishell.state';
import { readfile, writefile, unlink } from 'fs';
let bak = readfile(st.ADVANCED);
writefile(st.ADVANCED, sprintf('%J', { enabled: true, others: 'lists', rows: [
	{ domains: ['a.example'], strategy: 'auto' }, { domains: ['b.example'], strategy: 'alt' }, { domains: ['c.example'], strategy: 'auto' } ] }));
sel.resolve_adv([
	{ id: 'A', verdicts: [v('adv-0', false), v('adv-2', false)] },
	{ id: 'B', verdicts: [v('adv-0', true), v('adv-2', false)] },
]);
let a = st.advanced();
ok(a.rows[0].resolved == 'B', 'auto: строка 0 — B (открыла её сайт)');
ok(a.rows[1].resolved == null, 'строка с выбранной стратегией не трогается');
ok(a.rows[2].resolved == null, 'auto: ничего не открылось — без resolved');
if (bak != null) writefile(st.ADVANCED, bak); else unlink(st.ADVANCED);

print(fails ? `mix: ${fails} не прошло\n` : 'mix: всё прошло\n');
exit(fails ? 1 : 0);
