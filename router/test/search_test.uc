// Генератор глубокого поиска совпадает с CandidateGenerator Windows-версии (эталон golden-search.json)
// и не выходит за белый список роутера (roles.json) и набор фейков пакета.
// Запуск: ucode -L <ucode-dir> search_test.uc <golden-search.json> <roles.json> <fake-dir>
'use strict';

import { readfile, stat } from 'fs';
import * as srch from 'dpishell.search';
import * as cat from 'dpishell.catalog';

let failed = 0, passed = 0;
function check(name, ok, detail) {
	if (ok) { passed++; return; }
	failed++;
	print(`FAIL ${name}${detail != null ? ': ' + detail : ''}\n`);
}

let golden = json(readfile(ARGV[0]));
let roles = json(readfile(ARGV[1]));
let fake_dir = ARGV[2];

let coarse = srch.coarse();
check('грубый проход: число', length(coarse) == length(golden.coarse), `${length(coarse)} != ${length(golden.coarse)}`);
for (let i = 0; i < length(golden.coarse); i++)
	check(`грубый проход ${i}`, coarse[i] == golden.coarse[i], `${coarse[i]}\n  ждали ${golden.coarse[i]}`);

for (let c in golden.coarse) {
	let n = srch.neighbours(c), g = golden.neighbours[c];
	check(`соседи: число (${c})`, length(n) == length(g), `${length(n)} != ${length(g)}`);
	for (let i = 0; i < length(g); i++)
		check(`сосед ${i} (${c})`, n[i] == g[i], `${n[i]}\n  ждали ${g[i]}`);
}

for (let d in golden.cost) {
	let c = srch.cost(d);
	check(`цена (${d})`, c - golden.cost[d] < 0.001 && golden.cost[d] - c < 0.001, `${c} != ${golden.cost[d]}`);
	// Каждый кандидат допустим в обеих ролях поиска, а его фейки есть в пакете.
	for (let role in keys(srch.ROLE_GROUPS)) {
		let e = cat.validate({ id: 'x', roles: { [role]: d } }, roles);
		check(`допустим в ${role} (${d})`, e == null, e);
	}
	for (let m in match(d, /\{fake\}\/[A-Za-z0-9_.-]+\.bin/g) ?? [])
		check(`фейк есть: ${m[0]}`, stat(`${fake_dir}/${substr(m[0], 7)}`) != null);
}

// Описание для таблицы и выбор лучших.
check('описание', srch.describe('--dpi-desync=fake,multisplit --dpi-desync-fooling=ts --dpi-desync-split-pos=1', 'google')
	== 'fake + multisplit, ts, pos 1 — YouTube', srch.describe('--dpi-desync=fake,multisplit --dpi-desync-fooling=ts --dpi-desync-split-pos=1', 'google'));
let a = { share: 1, cost: 3, seconds: 1 }, b = { share: 1, cost: 8, seconds: 1 }, c = { share: 0.5, cost: 1, seconds: 0.2 };
check('лучшие: доля, затем тише', srch.best([c, b, a], 2)[0] === a && srch.best([c, b, a], 2)[1] === b);
check('лучше: тише при равной доле', srch.cand_better(a, b) && !srch.cand_better(b, a));
check('лучше: доля важнее', srch.cand_better(a, c) && !srch.cand_better(c, a));

print(`search: ${passed} прошло, ${failed} не прошло\n`);
exit(failed ? 1 : 0);
