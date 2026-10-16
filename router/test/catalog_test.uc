// Тесты сборщика: сверка с эталоном WinwsArgs.Build (golden.json) и проверки validate().
// Запуск: ucode -L <ucode-dir> catalog_test.uc <data-dir> <roles.json> <golden.json>
'use strict';

import * as cat from 'dpishell.catalog';

let data_dir = ARGV[0], roles = cat.load_json(ARGV[1]), golden = cat.load_json(ARGV[2]).strategies;
let catalog = cat.load_json(`${data_dir}/strategies.json`);
let failed = 0, passed = 0;

function check(name, ok, detail) {
	if (ok) { passed++; return; }
	failed++;
	print(`FAIL ${name}${detail ? ': ' + detail : ''}\n`);
}

const opts = { mode: 'lists', lists_dir: '{lists}', fake_dir: '{fake}', ipset_file: 'ipset-none.txt' };

// 1. Каждая стратегия каталога проходит проверку и собирается так же, как на Windows (без --wf-*: на роутере это порты в config).
for (let s in catalog.strategies) {
	check(`validate ${s.id}`, cat.validate(s, roles) == null, cat.validate(s, roles));
	let want = filter(golden[s.id] ?? [], t => !match(t, /^--wf-/));
	let got = cat.build(s, roles, opts);
	let same = length(want) == length(got);
	for (let i = 0; same && i < length(want); i++)
		if (want[i] != got[i]) {
			same = false;
			print(`  ${s.id}[${i}]: ждали ${want[i]}, получили ${got[i]}\n`);
		}
	check(`build ${s.id} == golden`, same && length(want) > 0, `${length(got)} vs ${length(want)}`);
}
check('golden: все стратегии каталога', length(keys(golden)) == length(catalog.strategies));
check('golden: порты = roles.ports', (() => {
	let g = golden[catalog.strategies[0].id];
	return g[0] == `--wf-tcp=${roles.ports.tcp}` && g[1] == `--wf-udp=${roles.ports.udp}`;
})());

// 2. Режим «Все сайты»: у general и quic нет включающих списков, исключения остаются; google по своему списку.
let alt = cat.find([catalog], 'alt');
let all = cat.build(alt, roles, { ...opts, mode: 'all' });
let joined = join(' ', all);
check('all: нет general.txt', index(joined, '{lists}/general.txt') < 0);
check('all: есть user-exclude', index(joined, '--hostlist-exclude={lists}/user-exclude.txt') >= 0);
check('all: google.txt остался', index(joined, '--hostlist={lists}/google.txt') >= 0);

// 3. validate отвергает опасное.
function bad(roles_obj) { return cat.validate({ id: 'u-test', roles: roles_obj }, roles, 'items[0]'); }
check('validate: неизвестная роль', index(bad({ nosuch: '--dpi-desync=fake' }) ?? '', 'items[0].roles.nosuch') == 0);
check('validate: чужой параметр', bad({ general: '--debug=@/etc/shadow' }) != null);
check('validate: путь к файлу', bad({ general: '--dpi-desync-fake-tls=/etc/passwd' }) != null);
check('validate: выход из папки', bad({ general: '--dpi-desync-fake-tls={fake}/../x.bin' }) != null);
check('validate: кавычка', bad({ general: '--dpi-desync="fake"' }) != null);
check('validate: $()', bad({ general: '--dpi-desync=$(id)' }) != null);
check('validate: встроенный фейк !', bad({ general: '--dpi-desync=fake --dpi-desync-fake-tls=!' }) == null);
check('validate: hex', bad({ general: '--dpi-desync-fake-tls=0x00000000' }) == null);

// 4. Конфиг: одна строка NFQWS_OPT в кавычках, порты из roles.json.
let cfg = cat.render_config(alt, roles, { ...opts, lists_dir: '/etc/dpishell/lists', fake_dir: '/usr/share/dpishell/fake' });
check('config: NFQWS_OPT', match(cfg, /\nNFQWS_OPT="--filter-udp=443 [^"\n]+"\n/) != null);
check('config: порты', index(cfg, `NFQWS_PORTS_UDP=${roles.ports.udp}\n`) >= 0);
check('config: IPv6 выкл', index(cfg, 'DISABLE_IPV6=1\n') >= 0);

// 5. Игровой режим: роли game_* только при включённой функции, порты расширяются.
let g = cat.find([catalog], 'alt');
if (g.roles.game_tcp) {
	check('game выкл: нет 1024-65535', index(join(' ', cat.build(g, roles, opts)), '1024-65535') < 0);
	let on = { ...opts, features: { game: true } };
	check('game вкл: профиль игр', index(join(' ', cat.build(g, roles, on)), '--filter-tcp=1024-65535') >= 0);
	check('game вкл: порты', index(cat.render_config(g, roles, on), `NFQWS_PORTS_TCP=${roles.ports.tcp},1024-65535\n`) >= 0);
	check('game: any-protocol только в game_*', cat.validate({ id: 'x', roles: { general: '--dpi-desync-any-protocol=1' } }, roles) != null);
}

// 6. Признак ts.
check('uses_ts alt9', cat.uses_ts(cat.find([catalog], 'alt9')));
check('uses_ts alt4 = нет', !cat.uses_ts(cat.find([catalog], 'alt4')));

print(`${passed} прошло, ${failed} не прошло\n`);
exit(failed ? 1 : 0);
