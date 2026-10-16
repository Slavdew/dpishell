// Тесты оценки подбора: контрольная группа «Обычные сайты» (флаг control: true, 06-router-spec §4.1).
// Запуск: ucode -L <ucode-dir> select_test.uc
'use strict';

import * as sel from 'dpishell.select';

let failed = 0, passed = 0;
function check(name, ok, detail) {
	if (ok) { passed++; return; }
	failed++;
	print(`FAIL ${name}${detail != null ? ': ' + detail : ''}\n`);
}

function v(group, url, ok, required) {
	return { group, url, ok, required: required !== false, seconds: 1, probes: [] };
}

let groups = [
	{ id: 'youtube', targets: [] },
	{ id: 'banks', control: true, targets: [] },
];

let sc = sel.score(groups, [ v('youtube', 'https://www.youtube.com/', true), v('banks', 'https://ya.ru/', true) ]);
check('всё открылось — идеально', sc.perfect && sc.error == null, sc);
check('контрольная не в счёте групп', sc.groups_total == 1 && sc.group_share == 1, sc);
check('контрольная не в счёте целей', sc.target_share == 1, sc);

sc = sel.score(groups, [ v('youtube', 'https://www.youtube.com/', true), v('banks', 'https://ya.ru/', false) ]);
check('сломана контрольная — брак', !sc.perfect && index(sc.error ?? '', 'ya.ru') >= 0, sc);
check('сломанная контрольная не портит долю целей', sc.target_share == 1, sc);

sc = sel.score(groups, [ v('youtube', 'https://www.youtube.com/', true), v('banks', 'https://example.com/', false, false) ]);
check('необязательная контрольная не бракует', sc.perfect && sc.error == null, sc);

// В ucode деление целых — целое: доля 1 из 2 групп должна быть 0.5, а не 0.
let g2 = [ { id: 'youtube', targets: [] }, { id: 'discord', targets: [] } ];
sc = sel.score(g2, [ v('youtube', 'https://www.youtube.com/', true), v('discord', 'https://discord.com/', false) ]);
check('доля групп дробная', sc.group_share == 0.5 && sc.target_share == 0.5, sc);

print(`select:${passed} прошло, ${failed} не прошло\n`);
exit(failed ? 1 : 0);
