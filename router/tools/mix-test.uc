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
ok(index(m?.title ?? '', 'Смешанная · YouTube — A · Discord — B · Свои адреса — C') == 0, 'название по частям');

// Одна стратегия лучше всех во всех группах — смешивать нечего.
let rows2 = [
	{ id: 'A', verdicts: [v('youtube', true), v('discord', true), v('user', false)] },
	{ id: 'B', verdicts: [v('youtube', false), v('discord', false), v('user', false)] },
];
ok(sel.mix_from(rows2, list, groups) == null, 'одна лучшая во всём — null');
// Стратегия с ошибкой (ломает обычные сайты) в части не идёт.
let rows3 = [{ ...rows[0], error: 'ломает обычные сайты' }, rows[1], rows[2]];
ok(sel.mix_from(rows3, list, groups)?.parts?.youtube == null, 'стратегия с ошибкой не берётся');
print(fails ? `mix: ${fails} не прошло\n` : 'mix: всё прошло\n');
exit(fails ? 1 : 0);
