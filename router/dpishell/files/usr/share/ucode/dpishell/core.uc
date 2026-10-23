// Установка ядра zapret на роутер (architecture.md «Ядро на роутере»): наш zip по манифесту, проверка хешей, шаги install_easy.sh для OpenWRT
// без его вопросов и без cron-загрузки списков со сторонних адресов. Проверено вручную в эмуляторе 2026-10-17.
'use strict';

import { readfile, writefile, stat, lstat, popen, unlink, mkdir, symlink, readlink } from 'fs';
import * as cat from 'dpishell.catalog';
import * as st from 'dpishell.state';

const TMP = '/tmp/dpishell/core';
const ZIP = '/tmp/dpishell/core/zapret.zip';
const UPLOADED = '/tmp/dpishell-core.zip';

function q(s) {
	return `'${replace(`${s}`, "'", "'\\''")}'`;
}

function sh(cmd) {
	let p = popen(`${cmd} 2>&1`);
	let out = p ? p.read('all') : '';
	return { rc: p ? p.close() : -1, out };
}

function sha256(path) {
	let r = sh(`sha256sum ${q(path)}`);
	return r.rc == 0 ? substr(r.out, 0, 64) : null;
}

// Архитектура бинарника zapret по DISTRIB_ARCH OpenWRT (aarch64_cortex-a73 → arm64, mipsel_24kc → mipsel).
function arch() {
	let rel = readfile('/etc/openwrt_release') ?? '';
	let m = match(rel, /DISTRIB_ARCH='([^']+)'/);
	let a = m ? m[1] : '';
	if (match(a, /^aarch64/)) return 'arm64';
	if (match(a, /^arm/)) return 'arm';
	if (match(a, /^x86_64/)) return 'x86_64';
	if (match(a, /^i386|^i686|^x86/)) return 'x86';
	if (match(a, /^mipsel|^mips64el/)) return match(a, /^mips64/) ? 'mips64' : 'mipsel';
	if (match(a, /^mips64/)) return 'mips64';
	if (match(a, /^mips/)) return 'mips';
	return null;
}

function manifest() {
	return cat.load_json(`${st.P.share}/core.json`);
}

// Свободно, КБ (df -k): null — узнать не удалось (тогда не мешаем).
function free_kb(path) {
	let m = match(sh(`df -k ${q(path)}`).out, /\n[^ ]+ +[0-9]+ +[0-9]+ +([0-9]+)/);
	return m ? +m[1] : null;
}

// Скачать zip: основной адрес, затем зеркала; принимается только файл с хешем из манифеста.
function download(m, progress) {
	st.own_dir('/tmp/dpishell', 0o700);
	mkdir(TMP, 0o700);
	if (stat(ZIP) && sha256(ZIP) == m.zip_sha256)
		return null;
	// Архив ~8 МБ и распаковка — в ОЗУ: на роутере с 64 МБ загрузка могла упереться в память.
	let tf = free_kb('/tmp');
	if (tf != null && tf < 20 * 1024 && !st.root_file(UPLOADED))
		return `Мало свободной памяти для загрузки ядра: ${int(tf / 1024)} МБ, нужно 20 МБ. Перезагрузите роутер и повторите.`;
	// Архив, загруженный через веб-вход (cgi-upload пишет только в путь из ACL пакета, router-api.md §5.5).
	// Только обычный файл root (его пишет cgi-upload LuCI): ссылка или файл другого пользователя мог бы смениться после сверки.
	if (st.root_file(UPLOADED) && sha256(UPLOADED) == m.zip_sha256 && sh(`mv -f ${q(UPLOADED)} ${q(ZIP)}`).rc == 0 &&
	    sha256(ZIP) == m.zip_sha256)
		return null;
	let tried = [];
	for (let url in [m.zip_url, ...(m.mirror_urls ?? [])]) {
		progress?.(`Загрузка ядра: ${url}`);
		let r = sh(`curl -sSL --fail --connect-timeout 10 --max-time 300 -o ${q(ZIP)} ${q(url)}`);
		if (r.rc == 0 && sha256(ZIP) == m.zip_sha256)
			return null;
		push(tried, `${url}: ${r.rc == 0 ? 'хеш не совпал' : trim(r.out)}`);
		unlink(ZIP);
	}
	return `Не удалось скачать ядро:\n${join('\n', tried)}\nЕсли GitHub с роутера недоступен — положите ${split(m.zip_url, '/')[-1]} в ${ZIP} (scp) или поставьте офлайн-набором (router/tools/offline-bundle.sh).`;
}

// Разложить zapret в /opt/zapret. Свой config пишет state.write_zapret_config.
// Пакеты, которые сами владеют /opt/zapret и /etc/init.d/zapret (менеджеры zapret в сторонних прошивках ставят zapret от remittor):
// наше ядро и их файлы затирали бы друг друга при каждой установке и обновлении.
const FOREIGN_PACKAGES = ['zapret', 'luci-app-zapret', 'zapret-manager-beam', 'zapret2', 'luci-app-zapret2'];

function foreign_packages() {
	// Без запуска apk: база apk (/lib/apk/db/installed, строка «P:<имя>»; пакеты не из прошивки не всегда кладут
	// /lib/apk/packages/<имя>.list), список файлов пакета, opkg — /usr/lib/opkg/info/<имя>.control.
	let db = readfile('/lib/apk/db/installed') ?? '';
	return filter(FOREIGN_PACKAGES, n => index(db, `\nP:${n}\n`) >= 0 || index(db, `P:${n}\n`) == 0 ||
		stat(`/lib/apk/packages/${n}.list`) || stat(`/usr/lib/opkg/info/${n}.control`));
}

// Распаковать, сверить nfqws и разложить в /opt/zapret. null — успех.
function unpack_install(m, a, x, progress) {
	let paths = map(m.extract, p => q(`${m.zip_dir}${p}${substr(p, -1) == '/' ? '*' : ''}`));
	push(paths, q(`${m.zip_dir}binaries/linux-${a}/nfqws`));
	let r = sh(`unzip -q -o ${q(ZIP)} ${join(' ', paths)} -d ${q(x)}`);
	if (r.rc != 0)
		return `Не удалось распаковать: ${trim(r.out)}`;
	let src = `${x}/${m.zip_dir}`;
	let bin = `${src}binaries/linux-${a}/nfqws`;
	if (sha256(bin) != m.nfqws[a])
		return 'Файл nfqws не совпал с закреплённым хешем — установка отменена';

	progress?.('Установка');
	let Z = st.P.zapret;
	sh('/etc/init.d/zapret stop');
	// Сохраняем только наш config: всё остальное в /opt/zapret — из архива.
	r = sh(`set -e; rm -rf ${q(Z)}.new; mkdir -p ${q(Z)}.new/nfq ${q(Z)}.new/init.d;` +
		` cp -r ${q(src + 'common')} ${q(src + 'files')} ${q(src + 'ipset')} ${q(src + 'config.default')} ${q(Z + '.new/')};` +
		` cp -r ${q(src + 'init.d/openwrt')} ${q(Z + '.new/init.d/')};` +
		` cp ${q(bin)} ${q(Z + '.new/nfq/nfqws')}; chmod 755 ${q(Z + '.new/nfq/nfqws')};` +
		` [ -f ${q(Z + '/config')} ] && cp ${q(Z + '/config')} ${q(Z + '.new/config')} || true;` +
		` rm -rf ${q(Z)}.old; [ -d ${q(Z)} ] && mv ${q(Z)} ${q(Z)}.old || true; mv ${q(Z)}.new ${q(Z)}; rm -rf ${q(Z)}.old`);
	if (r.rc != 0)
		return `Не удалось установить: ${trim(r.out)}`;
	if (readlink('/etc/init.d/zapret') != `${Z}/init.d/openwrt/zapret`) {
		unlink('/etc/init.d/zapret');
		symlink(`${Z}/init.d/openwrt/zapret`, '/etc/init.d/zapret');
	}
	mkdir('/etc/hotplug.d/iface', 0o755);
	if (readlink('/etc/hotplug.d/iface/90-zapret') != `${Z}/init.d/openwrt/90-zapret`) {
		unlink('/etc/hotplug.d/iface/90-zapret');
		symlink(`${Z}/init.d/openwrt/90-zapret`, '/etc/hotplug.d/iface/90-zapret');
	}
	// Системный flow offloading несовместим с nfqws (research/router-openwrt.md §2) — как делает install_easy.sh.
	// Что было — в fw_offload_prev (если ещё не записано): удаление вернёт межсетевому экрану (uninstall.sh, шаг 4).
	if (trim(sh('uci -q get firewall.@defaults[0].flow_offloading').out) == '1') {
		let hw = trim(sh('uci -q get firewall.@defaults[0].flow_offloading_hw').out) == '1';
		if (!length(trim(sh('uci -q get dpishell.main.fw_offload_prev').out)))
			st.uci_set({ fw_offload_prev: hw ? 'hardware' : 'software' });
		sh('uci set firewall.@defaults[0].flow_offloading=0 && uci commit firewall && /etc/init.d/firewall reload');
	}
	st.write_atomic(`${st.P.etc}/core-installed.json`, sprintf('%J\n', { version: m.version, arch: a, nfqws_sha256: m.nfqws[a], at: time() }));
	return null;
}

function install(progress) {
	let fp = foreign_packages();
	if (length(fp))
		return `Установлен другой zapret (${join(', ', fp)}) — он занимает /opt/zapret. Удалите его (Система → Программное обеспечение, название может отличаться, или apk del ${join(' ', fp)}) и повторите.`;
	// zapret, поставленный вручную (install_easy.sh), — не наш: заменять его файлы не будем (отметка нашего ядра —
	// core-installed.json; удаление DpiShell убирает её вместе с ядром).
	if (stat(`${st.P.zapret}/nfq/nfqws`) && !stat(`${st.P.etc}/core-installed.json`))
		return `В ${st.P.zapret} уже стоит zapret, поставленный не DpiShell. Удалите его (его uninstall_easy.sh) и повторите.`;
	let m = manifest();
	let a = arch();
	if (!a || !m.nfqws[a])
		return `Архитектура роутера не поддерживается (${a ?? 'неизвестна'})`;
	let of = free_kb('/overlay') ?? free_kb('/');
	if (of != null && of < 1024)
		return `Мало места во флеше роутера: ${of} КБ, нужно 1 МБ`;
	let e = download(m, progress);
	if (e)
		return e;
	progress?.('Проверка и распаковка');
	let x = `${TMP}/x`;
	sh(`rm -rf ${q(x)}`);
	mkdir(x, 0o700);
	// Распакованное — убрать на любом выходе (zip остаётся для повтора; его убирает cleanup_tmp через час).
	e = unpack_install(m, a, x, progress);
	sh(`rm -rf ${q(x)}`);
	if (!e)
		unlink(ZIP);
	return e;
}

// Остатки установки ядра в ОЗУ (tick): zip после неудачи — через час, если установка не идёт; распаковка — всегда.
// Только свои пути в своём каталоге /tmp/dpishell (0700, root).
function cleanup_tmp() {
	if (st.job_running('core_install'))
		return;
	let z = lstat(ZIP);
	if (z && (z.type != 'file' || time() - z.mtime > 3600))
		unlink(ZIP);
	if (lstat(`${TMP}/x`))
		sh(`rm -rf ${q(TMP + '/x')}`);
}

// Хеш установленного nfqws совпадает с манифестом. status зовёт это на каждый опрос страницы — хеш (sha256sum, процесс)
// считается заново, если файл сменился (размер, время, inode) и не реже раза в сутки: размер и время подделываются,
// сверка в окне должна оставаться настоящей. В rpcd модуль живёт между вызовами.
const VERIFY_MAX_AGE = 86400;
let verify_memo = null;

function verify() {
	let f = `${st.P.zapret}/nfq/nfqws`, s = stat(f);
	let key = s ? `${s.size}:${s.mtime}:${s.inode}` : null, now = time();
	if (key && verify_memo?.key == key && now - verify_memo.at < VERIFY_MAX_AGE && now >= verify_memo.at)
		return verify_memo.ok;
	let m = manifest(), a = arch();
	let h = sha256(f);
	let ok = h != null && a != null && h == m.nfqws[a];
	verify_memo = key ? { key, ok, at: now } : null;
	return ok;
}

export { arch, install, verify, foreign_packages, cleanup_tmp, free_kb };
