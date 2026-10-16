// Тесты проверки целей: same_site (как Probe.SameSite), разбор ответов curl на записанных файлах, живой запрос.
// Запуск: ucode -L <ucode-dir> probe_test.uc [live]
'use strict';

import { writefile, mkdir } from 'fs';
import * as pr from 'dpishell.probe';

let failed = 0, passed = 0;
function check(name, ok, detail) {
	if (ok) { passed++; return; }
	failed++;
	print(`FAIL ${name}${detail != null ? ': ' + detail : ''}\n`);
}

// same_site — те же случаи, что в тестах Windows.
check('same: равны', pr.same_site('youtube.com', 'youtube.com'));
check('same: www', pr.same_site('www.youtube.com', 'youtube.com'));
check('same: соседи', pr.same_site('www.youtube.com', 'm.youtube.com'));
check('same: заглушка', !pr.same_site('youtube.com', 'warning.rt.ru'));
check('same: co.uk', !pr.same_site('bbc.co.uk', 'stub.co.uk'));
check('same: IP', !pr.same_site('1.2.3.4', 'youtube.com'));

// classify на записанных ответах curl.
mkdir('/tmp/dpishell', 0o700);
let seq = 0;
function fake(w, h, e) {
	let p = `/tmp/dpishell/t${++seq}`;
	writefile(`${p}.w`, w); writefile(`${p}.h`, h ?? ''); writefile(`${p}.e`, e ?? '');
	return p;
}
let T = { url: 'https://www.youtube.com/', min_bytes: 20480 };
let c = (w, h, e, t) => pr.classify(t ?? T, fake(w, h, e));
check('ok: полное тело', c('200 500000 0 0.8 ', 'HTTP/2 200\r\ncontent-length: 500000\r\n').ok);
check('обрыв на 16 КБ (exit 18)', c('200 16384 18 2.1 ', 'HTTP/2 200\r\ncontent-length: 500000\r\n').outcome == 'truncated');
check('обрыв: меньше Content-Length', c('200 16384 0 2.1 ', 'HTTP/2 200\r\ncontent-length: 500000\r\n').outcome == 'truncated');
check('мало байт', c('200 1000 0 0.3 ', 'HTTP/2 200\r\n').outcome == 'truncated');
check('сброс', c('000 0 56 0.2 ', '', 'Recv failure: Connection reset by peer').outcome == 'reset');
check('TLS', c('000 0 35 0.2 ', '', 'SSL connect error').outcome == 'tls_failed');
check('таймаут', c('000 0 28 9.0 ', '').outcome == 'timeout');
check('чужой редирект', c('302 0 0 0.2 http://warning.rt.ru/', 'HTTP/1.1 302\r\nlocation: http://warning.rt.ru/\r\n').outcome == 'foreign_redirect');
check('свой редирект', c('301 0 0 0.2 https://m.youtube.com/', 'HTTP/1.1 301\r\n').ok);
check('400', c('400 100 0 0.2 ', 'HTTP/1.1 400\r\n').outcome == 'bad_status');
check('503', c('503 100 0 0.2 ', 'HTTP/1.1 503\r\n').outcome == 'bad_status');
check('рукопожатие: 404 ок', c('404 0 0 0.2 ', '', '', { url: 'https://gateway.discord.gg/', handshake_only: true }).ok);
check('рукопожатие: сброс', !c('000 0 56 0.2 ', '', '', { url: 'https://gateway.discord.gg/', handshake_only: true }).ok);
check('1 МБ и таймаут = ок', c('200 1048576 28 9.0 ', 'HTTP/2 200\r\ncontent-length: 9000000\r\n').ok);

// Живой запрос (эмулятор с интернетом): DoH и проверка обычного сайта.
if (ARGV[0] == 'live') {
	let ips = pr.doh_resolve('example.com', {});
	check('DoH example.com', length(ips) > 0, ips);
	let r = pr.check_batch([
		{ target: { url: 'https://example.com/', handshake_only: false }, tls: '1.3' },
		{ target: { url: 'https://example.com/', handshake_only: false }, tls: '1.2' },
		{ target: { url: 'https://nonexistent.invalid/' }, tls: '1.3' },
	], { parallel: 3 });
	check('example.com TLS1.3', r[0].ok, r[0]);
	check('example.com TLS1.2', r[1].ok, r[1]);
	check('несуществующий — не ок', !r[2].ok, r[2]);
	print(sprintf('%.3J\n', r));
}

print(`${passed} прошло, ${failed} не прошло\n`);
exit(failed ? 1 : 0);
