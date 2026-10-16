// Установка ядра zapret на роутер (06 §2.1, §6): наш zip по манифесту, проверка хешей, шаги install_easy.sh для OpenWRT
// без его вопросов и без cron-загрузки списков со сторонних адресов. Проверено вручную в эмуляторе 2026-10-17.
'use strict';

import { readfile, writefile, stat, popen, unlink, mkdir, symlink, readlink } from 'fs';
import * as cat from 'dpishell.catalog';
import * as st from 'dpishell.state';

const TMP = '/tmp/dpishell/core';
const ZIP = '/tmp/dpishell/core/zapret.zip';

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

// Скачать zip: основной адрес, затем зеркала; принимается только файл с хешем из манифеста.
function download(m, progress) {
	mkdir('/tmp/dpishell', 0o700);
	mkdir(TMP, 0o700);
	if (stat(ZIP) && sha256(ZIP) == m.zip_sha256)
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
	return `Не удалось скачать ядро:\n${join('\n', tried)}`;
}

// Разложить zapret в /opt/zapret. Свой config пишет state.write_zapret_config.
function install(progress) {
	let m = manifest();
	let a = arch();
	if (!a || !m.nfqws[a])
		return `Архитектура роутера не поддерживается (${a ?? 'неизвестна'})`;
	let e = download(m, progress);
	if (e)
		return e;
	progress?.('Проверка и распаковка');
	let x = `${TMP}/x`;
	sh(`rm -rf ${q(x)}`);
	mkdir(x, 0o700);
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
	// Системный flow offloading несовместим с nfqws (05-router-audit §2) — как делает install_easy.sh.
	sh("[ \"$(uci -q get firewall.@defaults[0].flow_offloading)\" = 1 ] && uci set firewall.@defaults[0].flow_offloading=0 && uci commit firewall && /etc/init.d/firewall reload");
	writefile(`${st.P.etc}/core-installed.json`, sprintf('%J\n', { version: m.version, arch: a, nfqws_sha256: m.nfqws[a], at: time() }));
	unlink(ZIP);
	sh(`rm -rf ${q(x)}`);
	return null;
}

// Хеш установленного nfqws совпадает с манифестом.
function verify() {
	let m = manifest(), a = arch();
	let h = sha256(`${st.P.zapret}/nfq/nfqws`);
	return h != null && a != null && h == m.nfqws[a];
}

export { arch, install, verify };
