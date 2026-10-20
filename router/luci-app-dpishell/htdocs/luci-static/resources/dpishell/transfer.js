'use strict';
'require baseclass';
'require ui';
'require dpishell.api as api';

// Выгрузка и загрузка своих настроек (docs/settings-export.md, формат 1 — общий с окном ПК). Всё в браузере: данные берутся
// и записываются обычными методами ubus (targets_set, lists_set, strategies_user_set, advanced_set, dns_set, mode_set) —
// роутер проверяет их сам, как при правке со страницы. Выгрузка — архив .zip с папкой; загрузка — папка или такой архив.
// Файлам не доверяем: размер и число строк — до разбора, строгий разбор записей, раздел с ошибкой — пропуск с причиной.

var FORMAT = 1;
var LIMITS = {
	manifest: 16 * 1024,
	sites: [1000, 100 * 1024], exclude: [200, 50 * 1024], optimize: [1000, 100 * 1024],
	strategies: [50, 16 * 1024], advanced: [20, 64 * 1024], subnets: [10000, 512 * 1024], dns: [1, 4 * 1024], mode: [1, 1024]
};
// Пределы самого роутера (rpcd): своих сайтов — 50, строк продвинутого режима — 20, своих стратегий — 50.
var ROUTER_MAX = { sites: 50, strategies: 50 };
var SECTIONS = ['sites', 'optimize', 'exclude', 'strategies', 'advanced', 'subnets', 'dns', 'mode'];
var NOT_HERE = ['apps', 'hosts'];
var DNS_IDS = { auto: 'provider', 'default': 'default', google: 'google', cloudflare: 'cloudflare', quad9: 'quad9', yandex: 'yandex' };

// ——— zip: запись (без сжатия) и чтение (без сжатия и deflate). Response — только window.Response: в модуле LuCI это имя занято своим классом. ———
var CRC = (function() {
	var t = [];
	for (var n = 0; n < 256; n++) {
		var c = n;
		for (var k = 0; k < 8; k++)
			c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
		t[n] = c >>> 0;
	}
	return t;
})();

function crc32(b) {
	var c = 0xFFFFFFFF;
	for (var i = 0; i < b.length; i++)
		c = CRC[(c ^ b[i]) & 0xFF] ^ (c >>> 8);
	return (c ^ 0xFFFFFFFF) >>> 0;
}

function zipStore(files) {
	var enc = new TextEncoder(), parts = [], central = [], offset = 0;
	var now = new Date();
	var dtime = (now.getHours() << 11) | (now.getMinutes() << 5) | (now.getSeconds() >> 1);
	var ddate = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();
	files.forEach(function(f) {
		var name = enc.encode(f.name), data = enc.encode(f.text), crc = crc32(data);
		var h = new DataView(new ArrayBuffer(30));
		h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x0800, true); h.setUint16(8, 0, true);
		h.setUint16(10, dtime, true); h.setUint16(12, ddate, true); h.setUint32(14, crc, true);
		h.setUint32(18, data.length, true); h.setUint32(22, data.length, true); h.setUint16(26, name.length, true); h.setUint16(28, 0, true);
		parts.push(new Uint8Array(h.buffer), name, data);
		var c = new DataView(new ArrayBuffer(46));
		c.setUint32(0, 0x02014b50, true); c.setUint16(4, 20, true); c.setUint16(6, 20, true); c.setUint16(8, 0x0800, true);
		c.setUint16(10, 0, true); c.setUint16(12, dtime, true); c.setUint16(14, ddate, true); c.setUint32(16, crc, true);
		c.setUint32(20, data.length, true); c.setUint32(24, data.length, true); c.setUint16(28, name.length, true);
		c.setUint32(42, offset, true);
		central.push(new Uint8Array(c.buffer), name);
		offset += 30 + name.length + data.length;
	});
	var csize = central.reduce(function(s, x) { return s + x.length; }, 0);
	var e = new DataView(new ArrayBuffer(22));
	e.setUint32(0, 0x06054b50, true); e.setUint16(8, files.length, true); e.setUint16(10, files.length, true);
	e.setUint32(12, csize, true); e.setUint32(16, offset, true);
	return new Blob(parts.concat(central, [new Uint8Array(e.buffer)]), { type: 'application/zip' });
}

// Распаковка с пределом: заголовок архива может врать о размере (zip-бомба) — читаем поток по кускам и бросаем при превышении.
var INFLATE_MAX = 1024 * 1024;

function inflateRaw(bytes, max) {
	max = max || INFLATE_MAX;
	var reader = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
	var chunks = [], total = 0;
	var pump = function() {
		return reader.read().then(function(r) {
			if (r.done) {
				var out = new Uint8Array(total), at = 0;
				chunks.forEach(function(c) { out.set(c, at); at += c.length; });
				return out;
			}
			total += r.value.length;
			if (total > max) {
				reader.cancel();
				throw new Error('too big');
			}
			chunks.push(r.value);
			return pump();
		});
	};
	return pump();
}

function deflateRaw(bytes) {
	var cs = new CompressionStream('deflate-raw');
	return new window.Response(new Blob([bytes]).stream().pipeThrough(cs)).arrayBuffer().then(function(b) { return new Uint8Array(b); });
}

// Архив → [{ name, size, bytes() }]: только обычные файлы, имя — путь внутри архива.
function zipRead(buf) {
	var u = new Uint8Array(buf), v = new DataView(buf);
	var eocd = -1;
	for (var i = u.length - 22; i >= Math.max(0, u.length - 65557); i--)
		if (v.getUint32(i, true) == 0x06054b50) { eocd = i; break; }
	if (eocd < 0)
		throw new Error('zip');
	var count = v.getUint16(eocd + 10, true), p = v.getUint32(eocd + 16, true), out = [];
	if (count > 200)
		throw new Error('zip');
	var dec = new TextDecoder('utf-8');
	for (var n = 0; n < count; n++) {
		if (v.getUint32(p, true) != 0x02014b50)
			throw new Error('zip');
		var method = v.getUint16(p + 10, true), csize = v.getUint32(p + 20, true), usize = v.getUint32(p + 24, true);
		var nlen = v.getUint16(p + 28, true), xlen = v.getUint16(p + 30, true), clen = v.getUint16(p + 32, true);
		var lho = v.getUint32(p + 42, true);
		var name = dec.decode(u.subarray(p + 46, p + 46 + nlen));
		p += 46 + nlen + xlen + clen;
		if (/\/$/.test(name))
			continue;
		var start = lho + 30 + v.getUint16(lho + 26, true) + v.getUint16(lho + 28, true);
		(function(name, method, data, usize) {
			out.push({ name: name, size: usize, method: method,
				bytes: function() { return method == 0 ? Promise.resolve(data) : method == 8 ? inflateRaw(data) : Promise.reject(new Error('method')); } });
		})(name, method, u.subarray(start, start + csize), usize);
	}
	return out;
}

// ——— строка стратегии dpishell:1:… (как StrategyExchange окна ПК) ———
function b64url(bytes) {
	var s = '';
	for (var i = 0; i < bytes.length; i++)
		s += String.fromCharCode(bytes[i]);
	return btoa(s).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
}

function unb64url(t) {
	var b = t.replace(/-/g, '+').replace(/_/g, '/');
	b += '===='.slice(0, (4 - b.length % 4) % 4);
	var s = atob(b), out = new Uint8Array(s.length);
	for (var i = 0; i < s.length; i++)
		out[i] = s.charCodeAt(i);
	return out;
}

function encodeStrategy(title, roles) {
	return deflateRaw(new TextEncoder().encode(JSON.stringify({ title: title, roles: roles }))).then(function(b) {
		return 'dpishell:1:' + b64url(b);
	});
}

function decodeStrategy(text) {
	var lines = text.split(/\r?\n/).map(function(l) { return l.trim(); }).filter(function(l) { return l && l.charAt(0) != '#'; });
	if (lines.length != 1 || !/^dpishell:1:[A-Za-z0-9_-]+$/.test(lines[0]) || lines[0].length > 8192)
		return Promise.reject(new Error(api.t('web.xfer.bad_strategy')));
	var packed;
	try { packed = unb64url(lines[0].slice(11)); } catch (e) { return Promise.reject(new Error(api.t('web.xfer.bad_strategy'))); }
	return inflateRaw(packed, 16 * 1024 + 1).then(function(raw) {
		if (raw.length > 16 * 1024)
			throw new Error(api.t('web.xfer.bad_strategy'));
		var o = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw));
		if (!o || typeof o != 'object' || Object.keys(o).some(function(k) { return k != 'title' && k != 'roles'; }) ||
				typeof o.title != 'string' || !o.roles || typeof o.roles != 'object')
			throw new Error(api.t('web.xfer.bad_strategy'));
		var roles = {};
		Object.keys(o.roles).forEach(function(r) {
			if (typeof o.roles[r] != 'string')
				throw new Error(api.t('web.xfer.bad_strategy'));
			roles[r] = o.roles[r];
		});
		if (!Object.keys(roles).length)
			throw new Error(api.t('web.xfer.bad_strategy'));
		return { title: cleanTitle(o.title), roles: roles };
	}, function() { throw new Error(api.t('web.xfer.bad_strategy')); });
}

function cleanTitle(t) {
	// Управляющие и разделители строк — пробел, невидимые (в том числе меняющие направление текста) — убрать.
	var out = '';
	String(t || '').split('').forEach(function(ch) {
		var c = ch.charCodeAt(0);
		if (c < 32 || (c >= 127 && c < 160) || c == 0x2028 || c == 0x2029)
			out += ' ';
		else if (!((c >= 0x200b && c <= 0x200f) || (c >= 0x202a && c <= 0x202e) || (c >= 0x2066 && c <= 0x2069) || c == 0xfeff))
			out += ch;
	});
	return out.trim().slice(0, 60);
}

function sameRoles(a, b) {
	var ka = Object.keys(a).sort(), kb = Object.keys(b).sort();
	return ka.length == kb.length && ka.every(function(k, i) { return k == kb[i] && a[k] == b[k]; });
}

// ——— записи ———
// Домен строго: как «Свои сайты» окна ПК; ^ — только в исключениях (сам домен без поддоменов).
function cleanDomain(line, allowCaret) {
	var caret = '';
	var s = line.trim().toLowerCase();
	if (allowCaret && s.charAt(0) == '^') { caret = '^'; s = s.slice(1); }
	s = s.replace(/^https?:\/\//, '').replace(/\/$/, '').replace(/^www\./, '');
	if (!s || /[\s*^/:?#@\\]/.test(s) || s.indexOf('.') < 0 || s.length > 253)
		return null;
	var host;
	try { host = new URL('http://' + s + '/').hostname; } catch (e) { return null; }
	if (!/^[a-z0-9.-]+$/.test(host) || /^\.|\.$|\.\./.test(host) || /^[0-9.]+$/.test(host))
		return null;
	return caret + host;
}

function ipv4(s) {
	var m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(s);
	if (!m) return null;
	var a = m.slice(1).map(Number);
	return a.every(function(x) { return x <= 255; }) ? a : null;
}

// Подсеть: IPv4 не шире /16, IPv6 не шире /32; служебные сети — нет.
function cleanSubnet(line) {
	var s = line.trim(), m = /^([^/]+)(?:\/(\d{1,3}))?$/.exec(s);
	if (!m) return null;
	var a = ipv4(m[1]);
	if (a) {
		var p = m[2] != null ? +m[2] : 32;
		if (p < 16 || p > 32) return null;
		if (a[0] == 0 || a[0] == 10 || a[0] == 127 || a[0] >= 224 || (a[0] == 169 && a[1] == 254) || (a[0] == 172 && a[1] >= 16 && a[1] < 32) ||
				(a[0] == 192 && a[1] == 168) || (a[0] == 100 && a[1] >= 64 && a[1] < 128))
			return null;
		var n = ((a[0] << 24) | (a[1] << 16) | (a[2] << 8) | a[3]) >>> 0;
		n = p ? (n & (0xFFFFFFFF << (32 - p))) >>> 0 : 0;
		return [n >>> 24, (n >>> 16) & 255, (n >>> 8) & 255, n & 255].join('.') + (p == 32 ? '' : '/' + p);
	}
	if (/^[0-9a-f:]+$/i.test(m[1]) && m[1].indexOf(':') >= 0) {
		var p6 = m[2] != null ? +m[2] : 128;
		var low = m[1].toLowerCase();
		if (p6 < 32 || p6 > 128 || /^(fc|fd|fe8|fe9|fea|feb|ff)/.test(low) || low == '::1' || low == '::')
			return null;
		return low + (p6 == 128 ? '' : '/' + p6);
	}
	return null;
}

// Текстовый раздел: строка на запись, # — комментарий; предел строк — записи + 200.
function readLines(text, max, clean) {
	var lines = text.split(/\r?\n/);
	if (lines.length > max + 200)
		throw new Error(api.t('web.xfer.too_many', { max: max }));
	var ok = [], bad = [];
	lines.forEach(function(l, i) {
		var t = l.trim();
		if (!t || t.charAt(0) == '#')
			return;
		var v = clean(t);
		if (v == null)
			bad.push(i + 1);
		else if (ok.indexOf(v) < 0)
			ok.push(v);
	});
	if (ok.length > max)
		throw new Error(api.t('web.xfer.too_many', { max: max }));
	return { items: ok, bad: bad };
}

function strictJson(text) {
	// Повтор ключа JSON.parse молча принимает — ловим заранее грубой проверкой по уровням не нужно: файлы маленькие,
	// а повтор в наших объектах безвреден (берётся последний). Главное — разбор и типы полей ниже.
	return JSON.parse(text);
}

function host(url) {
	return (String(url).match(/^https?:\/\/([^\/:]+)/i) || [])[1] || '';
}

function stamp() {
	var d = new Date();
	return '%04d%02d%02d-%02d%02d'.format(d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes());
}

var TRANSLIT = { 'а': 'a', 'б': 'b', 'в': 'v', 'г': 'g', 'д': 'd', 'е': 'e', 'ё': 'yo', 'ж': 'zh', 'з': 'z', 'и': 'i', 'й': 'y', 'к': 'k',
	'л': 'l', 'м': 'm', 'н': 'n', 'о': 'o', 'п': 'p', 'р': 'r', 'с': 's', 'т': 't', 'у': 'u', 'ф': 'f', 'х': 'h', 'ц': 'c', 'ч': 'ch',
	'ш': 'sh', 'щ': 'sch', 'ъ': '', 'ы': 'y', 'ь': '', 'э': 'e', 'ю': 'yu', 'я': 'ya' };

// Имя файла стратегии: латиница, цифры, «_», «-», «.» (формат пакета), без повторов.
function fileName(title, used) {
	var base = String(title || 'strategy').toLowerCase()
		.replace(/[а-яё]/g, function(c) { return TRANSLIT[c]; })
		.replace(/[^a-z0-9_.-]+/g, '-').replace(/^[^a-z0-9]+|[.-]+$/g, '').slice(0, 56) || 'strategy';
	var name = base, n = 2;
	while (used[name])
		name = base + '-' + n++;
	used[name] = true;
	return name;
}

return baseclass.extend({
	// Текущие свои настройки роутера в разделах формата: { sections: {имя: {count, files: [{name, text}], preview}} }.
	collect: function() {
		return Promise.all([
			api.call('targets_get'), api.call('lists_get', { name: 'user-general' }), api.call('lists_get', { name: 'user-exclude' }),
			api.call('lists_get', { name: 'user-ipset' }), api.call('lists_get', { name: 'user-ipset-exclude' }),
			api.call('strategies'), api.call('advanced_get'), api.call('dns_status'), api.call('dns_presets'), api.call('status')
		]).then(function(d) {
			var sec = {}, head = '# DpiShell settings: ';
			var targets = (d[0].items || []).filter(function(t) { return host(t.url); });
			var sites = [], important = [];
			targets.forEach(function(t) {
				var h = cleanDomain(host(t.url));
				if (h && sites.indexOf(h) < 0) { sites.push(h); if (t.important) important.push(h); }
			});
			if (sites.length) {
				var files = [{ name: 'sites.txt', text: head + 'sites\n' + sites.join('\n') + '\n' }];
				if (important.length)
					files.push({ name: 'sites-important.txt', text: head + 'sites (important)\n' + important.join('\n') + '\n' });
				sec.sites = { count: sites.length, files: files, preview: sites };
			}
			var lines = function(x) { return (x.lines || []).map(function(l) { return l.trim(); }).filter(function(l) { return l && l.charAt(0) != '#'; }); };
			var opt = lines(d[1]), exc = lines(d[2]), ips = lines(d[3]), ipx = lines(d[4]);
			if (opt.length)
				sec.optimize = { count: opt.length, files: [{ name: 'optimize.txt', text: head + 'optimize\n' + opt.join('\n') + '\n' }], preview: opt };
			if (exc.length)
				sec.exclude = { count: exc.length, files: [{ name: 'exclude.txt', text: head + 'exclude\n' + exc.join('\n') + '\n' }], preview: exc };
			if (ips.length || ipx.length) {
				var sf = [];
				if (ips.length) sf.push({ name: 'subnets.txt', text: head + 'subnets\n' + ips.join('\n') + '\n' });
				if (ipx.length) sf.push({ name: 'subnets-exclude.txt', text: head + 'subnets (exclude)\n' + ipx.join('\n') + '\n' });
				sec.subnets = { count: ips.length + ipx.length, files: sf, preview: ips.concat(ipx) };
			}
			// Свои и найденные поиском; смешанные (parts) собирает программа — не выгружаются.
			var own = (d[5].items || []).filter(function(s) { return s.source == 'user' && !s.parts && s.roles && Object.keys(s.roles).length; });
			var used = {}, byId = {};
			var stratJobs = own.map(function(s) {
				var name = fileName(api.stratTitle(s.title), used);
				byId[s.id] = name;
				return encodeStrategy(api.stratTitle(s.title), s.roles).then(function(line) {
					return { name: 'strategies/' + name + '.txt', text: '# DpiShell strategy: ' + api.stratTitle(s.title) + '\n' + line + '\n' };
				});
			});
			var adv = d[6];
			if (adv && adv.rows && adv.rows.length) {
				var catalog = {};
				(d[5].items || []).forEach(function(s) { catalog[s.id] = s.source != 'user'; });
				var rows = adv.rows.map(function(r) {
					var row = { domains: r.domains };
					if (r.strategy == 'auto' || r.strategy == 'direct' || catalog[r.strategy])
						row.strategy = r.strategy;
					else if (byId[r.strategy])
						row.strategy_file = byId[r.strategy];
					else
						row.strategy = 'auto';
					return row;
				});
				sec.advanced = { count: rows.length, files: [{ name: 'advanced.json',
					text: JSON.stringify({ enabled: !!adv.enabled, others: adv.others || 'lists', rows: rows }, null, 1) + '\n' }],
					preview: rows.map(function(r) { return r.domains.join(', '); }) };
			}
			var dn = d[7], pr = d[8];
			if (dn && dn.preset && dn.preset != 'provider') {
				var choice = null;
				Object.keys(DNS_IDS).forEach(function(k) { if (DNS_IDS[k] == dn.preset) choice = k; });
				var dj = null;
				if (dn.preset == 'custom' && pr.custom) {
					var r0 = (pr.custom.resolvers || [])[0] || {};
					dj = { choice: 'custom', ips: pr.custom.plain || [] };
					if (r0.doh) dj.doh = r0.doh;
					if ((r0.bootstrap || []).length) dj.bootstrap = r0.bootstrap;
				} else if (choice)
					dj = { choice: choice };
				if (dj)
					sec.dns = { count: 1, files: [{ name: 'dns.json', text: JSON.stringify(dj) + '\n' }], preview: [dj.choice] };
			}
			var st = d[9];
			sec.mode = { count: 1, files: [{ name: 'mode.json', text: JSON.stringify({ mode: st.mode == 'lists' ? 'lists' : 'all' }) + '\n' }],
				preview: [api.t(st.mode == 'lists' ? 'sites.mode.lists' : 'sites.mode.all')] };
			return Promise.all(stratJobs).then(function(sfiles) {
				if (sfiles.length)
					sec.strategies = { count: sfiles.length, files: sfiles, preview: own.map(function(s) { return api.stratTitle(s.title); }) };
				return { sections: sec, version: st.version || (st.packages || {}).dpishell || '' };
			});
		});
	},

	// Архив .zip: папка dpishell-settings-<дата>/ с manifest.json и файлами выбранных разделов.
	pack: function(data, chosen, provider, note) {
		var folder = 'dpishell-settings-' + stamp();
		var files = [], names = [];
		SECTIONS.forEach(function(s) {
			if (chosen.indexOf(s) < 0 || !data.sections[s])
				return;
			names.push(s);
			data.sections[s].files.forEach(function(f) { files.push({ name: folder + '/' + f.name, text: f.text }); });
		});
		var m = { format: FORMAT, app: 'DpiShell', app_version: 'router ' + data.version, created: new Date().toISOString().replace(/\.\d+Z$/, 'Z'), sections: names };
		if (provider) m.provider = cleanTitle(provider).slice(0, 60);
		if (note) m.note = String(note).replace(/[\u0000-\u001f]/g, ' ').trim().slice(0, 500);
		files.unshift({ name: folder + '/manifest.json', text: JSON.stringify(m, null, 1) + '\n' });
		return { blob: zipStore(files), name: folder + '.zip' };
	},

	save: function(p) {
		var a = E('a', { href: URL.createObjectURL(p.blob), download: p.name, style: 'display:none' });
		document.body.appendChild(a);
		a.click();
		window.setTimeout(function() { URL.revokeObjectURL(a.href); a.remove(); }, 2000);
	},

	// Диалог «Выгрузить настройки»: разделы с галочками и числом записей, что уйдёт — раскрываемым списком.
	exportDialog: function() {
		var self = this;
		return this.collect().then(function(data) {
			var boxes = {}, rows = [];
			SECTIONS.forEach(function(s) {
				var x = data.sections[s];
				if (!x)
					return;
				boxes[s] = E('input', { type: 'checkbox', checked: '' });
				// Один выбор (режим, DNS) — значением, списки — числом записей и раскрываемым «Что уйдёт».
				var single = s == 'mode' || s == 'dns';
				rows.push(E('div', { 'class': 'dps-xfer-row' }, [
					E('label', { 'class': 'dps-check' }, [boxes[s], api.t('web.xfer.sec.' + s) + ': ' + (single ? x.preview[0] : x.count)]),
					single ? '' : E('details', { 'class': 'dps-details' }, [E('summary', {}, api.t('web.xfer.what_goes')),
						E('pre', { 'class': 'dps-pre' }, x.preview.slice(0, 200).join('\n') + (x.preview.length > 200 ? '\n…' : ''))])
				]));
			});
			var provider = E('input', { type: 'text', 'class': 'cbi-input-text', maxlength: 60, placeholder: api.t('web.xfer.provider_ph') });
			var note = E('input', { type: 'text', 'class': 'cbi-input-text', maxlength: 500 });
			var err = E('div', { 'class': 'dps-error' });
			ui.showModal(api.t('web.xfer.export_title'), [
				E('p', { 'class': 'dps-muted' }, api.t('web.xfer.export_intro')),
				rows.length ? E('div', {}, rows) : E('p', {}, api.t('web.xfer.nothing')),
				api.row(api.t('web.xfer.provider'), provider),
				api.row(api.t('web.xfer.note'), note),
				err,
				E('div', { 'class': 'right' }, [
					E('button', { 'class': 'btn', 'click': ui.hideModal }, api.t('common.cancel')), ' ',
					E('button', { 'class': 'btn cbi-button cbi-button-positive important', 'click': function() {
						var chosen = Object.keys(boxes).filter(function(k) { return boxes[k].checked; });
						if (!chosen.length) { err.textContent = api.t('web.xfer.choose'); return; }
						self.save(self.pack(data, chosen, provider.value.trim(), note.value.trim()));
						ui.hideModal();
					} }, api.t('web.xfer.export_go'))
				])
			]);
		});
	},

	// Файлы пакета: из выбранной папки (webkitdirectory) или из .zip. Имена — относительно папки пакета.
	readPick: function(fileList) {
		var list = Array.prototype.slice.call(fileList || []);
		if (list.length == 1 && /\.zip$/i.test(list[0].name)) {
			if (list[0].size > 4 * 1024 * 1024)
				return Promise.reject(new Error(api.t('web.xfer.too_big')));
			return list[0].arrayBuffer().then(function(buf) {
				var entries;
				try { entries = zipRead(buf); } catch (e) { throw new Error(api.t('web.xfer.bad_zip')); }
				return entries;
			});
		}
		return Promise.resolve(list.map(function(f) {
			return { name: f.webkitRelativePath || f.name, size: f.size, bytes: function() { return f.arrayBuffer().then(function(b) { return new Uint8Array(b); }); } };
		}));
	},

	// Разбор пакета: { manifest, sections: {имя: {...}}, skipped: [{section, reason}] }. Ничего не пишет.
	parse: function(entries) {
		// Пакет — файлы в корне архива или в одной папке; путь — без «..», без абсолютных, вложенность — только strategies/.
		var map = {}, prefix = null;
		entries.forEach(function(e) {
			var n = String(e.name).replace(/\\/g, '/');
			var m = /^(?:([^/]+)\/)?manifest\.json$/.exec(n);
			if (m) prefix = m[1] ? m[1] + '/' : '';
		});
		if (prefix == null)
			return Promise.reject(new Error(api.t('web.xfer.no_manifest')));
		entries.forEach(function(e) {
			var n = String(e.name).replace(/\\/g, '/');
			if (n.indexOf(prefix) != 0)
				return;
			n = n.slice(prefix.length);
			if (/^(?:[A-Za-z0-9][A-Za-z0-9_.-]{0,63}|strategies\/[A-Za-z0-9][A-Za-z0-9_.-]{0,63})$/.test(n) && !/\.\.|\.$/.test(n))
				map[n] = e;
		});
		var text = function(name, max) {
			var e = map[name];
			if (!e)
				return Promise.resolve(null);
			if (e.size > max)
				return Promise.reject(new Error(api.t('web.xfer.file_big', { file: name })));
			return e.bytes().catch(function() {
				throw new Error(api.t('web.xfer.file_big', { file: name }));
			}).then(function(b) {
				if (b.length > max)
					throw new Error(api.t('web.xfer.file_big', { file: name }));
				var t = new TextDecoder('utf-8', { fatal: true }).decode(b);
				if (t.indexOf('\u0000') >= 0)
					throw new Error(api.t('web.xfer.bad_file', { file: name }));
				return t.replace(/^﻿/, '');
			});
		};
		return text('manifest.json', LIMITS.manifest).then(function(mt) {
			var m;
			try { m = strictJson(mt); } catch (e) { throw new Error(api.t('web.xfer.bad_manifest')); }
			if (!m || typeof m != 'object' || m.format == null || +m.format < 1)
				throw new Error(api.t('web.xfer.bad_manifest'));
			if (+m.format > FORMAT)
				throw new Error(api.t('web.xfer.newer'));
			var listed = Array.isArray(m.sections) ? m.sections : [];
			var out = { manifest: m, sections: {}, skipped: [] };
			NOT_HERE.forEach(function(s) { if (listed.indexOf(s) >= 0) out.skipped.push({ section: s, reason: api.t('web.xfer.not_on_router') }); });
			var jobs = [];
			var section = function(s, fn) {
				if (listed.indexOf(s) < 0)
					return;
				jobs.push(Promise.resolve().then(fn).then(function(r) {
					if (r) out.sections[s] = r;
				}, function(e) { out.skipped.push({ section: s, reason: e.message }); }));
			};
			section('sites', function() {
				return Promise.all([text('sites.txt', LIMITS.sites[1]), text('sites-important.txt', LIMITS.sites[1])]).then(function(t) {
					if (t[0] == null) throw new Error(api.t('web.xfer.no_file', { file: 'sites.txt' }));
					var a = readLines(t[0], LIMITS.sites[0], function(l) { return cleanDomain(l); });
					var imp = t[1] != null ? readLines(t[1], LIMITS.sites[0], function(l) { return cleanDomain(l); }).items : [];
					return { items: a.items, important: imp.filter(function(d) { return a.items.indexOf(d) >= 0; }), bad: a.bad };
				});
			});
			section('optimize', function() {
				return text('optimize.txt', LIMITS.optimize[1]).then(function(t) {
					if (t == null) throw new Error(api.t('web.xfer.no_file', { file: 'optimize.txt' }));
					return readLines(t, LIMITS.optimize[0], function(l) { return cleanDomain(l); });
				});
			});
			section('exclude', function() {
				return text('exclude.txt', LIMITS.exclude[1]).then(function(t) {
					if (t == null) throw new Error(api.t('web.xfer.no_file', { file: 'exclude.txt' }));
					return readLines(t, LIMITS.exclude[0], function(l) { return cleanDomain(l, true); });
				});
			});
			section('subnets', function() {
				return Promise.all([text('subnets.txt', LIMITS.subnets[1]), text('subnets-exclude.txt', LIMITS.subnets[1])]).then(function(t) {
					if (t[0] == null && t[1] == null) throw new Error(api.t('web.xfer.no_file', { file: 'subnets.txt' }));
					var a = t[0] != null ? readLines(t[0], LIMITS.subnets[0], cleanSubnet) : { items: [], bad: [] };
					var b = t[1] != null ? readLines(t[1], LIMITS.subnets[0], cleanSubnet) : { items: [], bad: [] };
					return { items: a.items, exclude: b.items, bad: a.bad.concat(b.bad) };
				});
			});
			section('strategies', function() {
				var names = Object.keys(map).filter(function(n) { return /^strategies\/.+\.txt$/.test(n); });
				if (names.length > LIMITS.strategies[0]) throw new Error(api.t('web.xfer.too_many', { max: LIMITS.strategies[0] }));
				var items = [], bad = [];
				return Promise.all(names.map(function(n) {
					return text(n, LIMITS.strategies[1]).then(decodeStrategy).then(function(s) {
						items.push({ file: n.replace(/^strategies\//, '').replace(/\.txt$/, ''), title: s.title, roles: s.roles });
					}, function(e) { bad.push(n.replace(/^strategies\//, '') + ': ' + e.message); });
				})).then(function() { return { items: items, bad: bad }; });
			});
			section('advanced', function() {
				return text('advanced.json', LIMITS.advanced[1]).then(function(t) {
					if (t == null) throw new Error(api.t('web.xfer.no_file', { file: 'advanced.json' }));
					var o;
					try { o = strictJson(t); } catch (e) { throw new Error(api.t('web.xfer.bad_file', { file: 'advanced.json' })); }
					var okKeys = ['enabled', 'others', 'rows'];
					if (!o || typeof o != 'object' || Object.keys(o).some(function(k) { return okKeys.indexOf(k) < 0; }) ||
							typeof o.enabled != 'boolean' || ['direct', 'lists', 'all'].indexOf(o.others) < 0 || !Array.isArray(o.rows) || o.rows.length > 20)
						throw new Error(api.t('web.xfer.bad_file', { file: 'advanced.json' }));
					var rows = [], bad = [];
					o.rows.forEach(function(r, i) {
						var keys = r && typeof r == 'object' ? Object.keys(r) : [];
						var doms = Array.isArray(r && r.domains) ? r.domains.map(function(d) { return cleanDomain(String(d)); }) : [];
						if (!doms.length || doms.length > 50 || doms.some(function(d) { return !d; }) ||
								keys.some(function(k) { return ['domains', 'strategy', 'strategy_file'].indexOf(k) < 0; }) ||
								(r.strategy != null) == (r.strategy_file != null))
							return bad.push(i + 1);
						rows.push({ domains: doms, strategy: r.strategy, strategy_file: r.strategy_file });
					});
					return { enabled: o.enabled, others: o.others, rows: rows, bad: bad };
				});
			});
			section('dns', function() {
				return text('dns.json', LIMITS.dns[1]).then(function(t) {
					if (t == null) throw new Error(api.t('web.xfer.no_file', { file: 'dns.json' }));
					var o;
					try { o = strictJson(t); } catch (e) { throw new Error(api.t('web.xfer.bad_file', { file: 'dns.json' })); }
					if (!o || typeof o != 'object')
						throw new Error(api.t('web.xfer.bad_file', { file: 'dns.json' }));
					if (o.choice == 'custom') {
						var ipsOk = function(v) { return Array.isArray(v) && v.length <= 4 && v.every(function(x) { return typeof x == 'string' && (ipv4(x) || /^[0-9a-f:]+$/i.test(x)); }); };
						if (!ipsOk(o.ips) || (o.bootstrap != null && !ipsOk(o.bootstrap)) || (o.doh != null && !/^https:\/\/[A-Za-z0-9.-]+(:\d{1,5})?(\/[A-Za-z0-9._~\/-]*)?$/.test(o.doh)) ||
								(!o.ips.length && !o.doh))
							throw new Error(api.t('web.xfer.bad_file', { file: 'dns.json' }));
						return { choice: 'custom', ips: o.ips, doh: o.doh || null, bootstrap: o.bootstrap || [], risky: true };
					}
					if (!DNS_IDS[o.choice] || o.ips != null || o.doh != null)
						throw new Error(api.t('web.xfer.bad_file', { file: 'dns.json' }));
					return { choice: o.choice };
				});
			});
			section('mode', function() {
				return text('mode.json', LIMITS.mode[1]).then(function(t) {
					if (t == null) throw new Error(api.t('web.xfer.no_file', { file: 'mode.json' }));
					var o;
					try { o = strictJson(t); } catch (e) { throw new Error(api.t('web.xfer.bad_file', { file: 'mode.json' })); }
					if (!o || ['all', 'lists'].indexOf(o.mode) < 0 || Object.keys(o).length != 1)
						throw new Error(api.t('web.xfer.bad_file', { file: 'mode.json' }));
					return { mode: o.mode };
				});
			});
			return Promise.all(jobs).then(function() { return out; });
		});
	},

	// План по текущему состоянию роутера: по разделу — добавится / уберётся / уже есть / пропущено (с причинами).
	plan: function(pkg, replace) {
		return Promise.all([
			api.call('targets_get'), api.call('lists_get', { name: 'user-general' }), api.call('lists_get', { name: 'user-exclude' }),
			api.call('lists_get', { name: 'user-ipset' }), api.call('lists_get', { name: 'user-ipset-exclude' }),
			api.call('strategies'), api.call('advanced_get'), api.call('dns_status'), api.call('status')
		]).then(function(d) {
			var plan = {}, S = pkg.sections;
			var lines = function(x) { return (x.lines || []).map(function(l) { return l.trim(); }).filter(function(l) { return l && l.charAt(0) != '#'; }); };
			var merge = function(cur, inc, max) {
				var res = replace ? [] : cur.slice(), added = 0, had = 0, over = 0;
				inc.forEach(function(v) {
					if (cur.indexOf(v) >= 0) { had++; if (replace) res.push(v); return; }
					if (res.length >= max) { over++; return; }
					res.push(v);
					added++;
				});
				return { result: res, added: added, had: had, over: over, removed: replace ? cur.filter(function(v) { return inc.indexOf(v) < 0; }).length : 0 };
			};
			if (S.sites) {
				var cur = (d[0].items || []).map(function(t) { return cleanDomain(host(t.url)); }).filter(function(x) { return x; });
				var mg = merge(cur, S.sites.items, ROUTER_MAX.sites);
				var impNow = {};
				(d[0].items || []).forEach(function(t) { if (t.important) impNow[cleanDomain(host(t.url))] = true; });
				plan.sites = Object.assign(mg, { bad: S.sites.bad, apply: function() {
					return api.call('targets_set', { items: mg.result.map(function(h) {
						return { url: 'https://' + h + '/', important: S.sites.important.indexOf(h) >= 0 || (!replace && !!impNow[h]) };
					}) });
				} });
			}
			[['optimize', 1, 'user-general', 5000], ['exclude', 2, 'user-exclude', 5000]].forEach(function(x) {
				if (!S[x[0]]) return;
				var mg = merge(lines(d[x[1]]), S[x[0]].items, x[3]);
				plan[x[0]] = Object.assign(mg, { bad: S[x[0]].bad, apply: function() { return api.call('lists_set', { name: x[2], lines: mg.result }); } });
			});
			if (S.subnets) {
				var a = merge(lines(d[3]), S.subnets.items, 5000), b = merge(lines(d[4]), S.subnets.exclude, 5000);
				plan.subnets = { added: a.added + b.added, had: a.had + b.had, over: a.over + b.over, removed: a.removed + b.removed, bad: S.subnets.bad,
					apply: function() {
						return api.call('lists_set', { name: 'user-ipset', lines: a.result }).then(function() {
							return api.call('lists_set', { name: 'user-ipset-exclude', lines: b.result });
						});
					} };
			}
			// Стратегии не удаляются и при «заменить» (на них ссылаются выбранная и запасные); новые — u-…, по параметрам без повторов.
			var all = d[5].items || [], fileToId = {};
			if (S.strategies) {
				var mine = all.filter(function(s) { return /^u-/.test(s.id); });
				var add = [], had = 0;
				S.strategies.items.forEach(function(s, i) {
					var same = all.filter(function(x) { return x.roles && sameRoles(x.roles, s.roles); })[0];
					if (same) { had++; fileToId[s.file] = same.id; return; }
					if (mine.length + add.length >= ROUTER_MAX.strategies) return;
					var id = 'u-imp-' + Date.now().toString(36) + '-' + i;
					fileToId[s.file] = id;
					add.push({ id: id, title: s.title || id, roles: s.roles });
				});
				plan.strategies = { added: add.length, had: had, over: S.strategies.items.length - add.length - had, removed: 0, bad: S.strategies.bad,
					apply: function() {
						if (!add.length) return Promise.resolve();
						var keep = mine.map(function(s) { return { id: s.id, title: s.title, roles: s.roles }; });
						return api.call('strategies_user_set', { items: keep.concat(add) });
					} };
			}
			if (S.advanced) {
				var bad = S.advanced.bad.slice();
				var rows = [];
				S.advanced.rows.forEach(function(r, i) {
					var sid = r.strategy;
					if (r.strategy_file != null) {
						sid = fileToId[r.strategy_file];
						if (!sid) return bad.push(i + 1);
					} else if (sid != 'auto' && sid != 'direct' && !all.filter(function(x) { return x.id == sid; }).length)
						return bad.push(i + 1);
					rows.push({ domains: r.domains, strategy: sid });
				});
				var curRows = d[6].rows || [];
				var key = function(r) { return r.domains.join(',') + '>' + r.strategy; };
				var resRows = replace ? rows : curRows.map(function(r) { return { domains: r.domains, strategy: r.strategy }; });
				var addedRows = 0, hadRows = 0;
				if (!replace)
					rows.forEach(function(r) {
						if (resRows.some(function(x) { return key(x) == key(r); })) { hadRows++; return; }
						if (resRows.length < 20) { resRows.push(r); addedRows++; }
					});
				var enabled = replace || (!curRows.length && !d[6].enabled) ? S.advanced.enabled : !!d[6].enabled;
				var others = replace || (!curRows.length && !d[6].enabled) ? S.advanced.others : (d[6].others || 'lists');
				plan.advanced = { added: replace ? rows.length : addedRows, had: hadRows, over: 0, removed: replace ? curRows.length : 0, bad: bad,
					apply: function() { return api.call('advanced_set', { enabled: enabled, others: others, rows: resRows }); } };
			}
			if (S.dns) {
				var want = S.dns.choice == 'custom' ? 'custom' : DNS_IDS[S.dns.choice];
				var same = want != 'custom' && d[7].preset == want;
				plan.dns = { added: same ? 0 : 1, had: same ? 1 : 0, over: 0, removed: 0, bad: [], risky: !!S.dns.risky,
					apply: function() {
						if (same) return Promise.resolve();
						if (want == 'custom') {
							var c = { title: api.t('web.xfer.dns_title'), plain: S.dns.ips };
							if (S.dns.doh) { c.doh = S.dns.doh; c.bootstrap = S.dns.bootstrap; }
							return api.call('dns_set', { custom: c });
						}
						return api.call('dns_set', { preset: want });
					} };
			}
			if (S.mode) {
				var sm = (d[8].mode == 'lists' ? 'lists' : 'all') == S.mode.mode;
				plan.mode = { added: sm ? 0 : 1, had: sm ? 1 : 0, over: 0, removed: 0, bad: [],
					apply: function() { return sm ? Promise.resolve() : api.call('mode_set', { mode: S.mode.mode }); } };
			}
			return plan;
		});
	},

	// Диалог «Загрузить настройки»: папка или .zip → разбор → план (добавить / заменить) → копия текущих файлом → запись.
	importDialog: function(onDone) {
		var self = this;
		var err = E('div', { 'class': 'dps-error' });
		var body = E('div');
		var pickDir = E('input', { type: 'file', webkitdirectory: '', directory: '', multiple: '', style: 'display:none' });
		var pickZip = E('input', { type: 'file', accept: '.zip,application/zip', style: 'display:none' });
		var picked = function(files) {
			err.textContent = '';
			body.textContent = '';
			self.readPick(files).then(function(entries) { return self.parse(entries); }).then(function(pkg) {
				self.showPlan(pkg, body, err, onDone);
			}).catch(function(e) { err.textContent = e.message; });
		};
		pickDir.addEventListener('change', function() { picked(pickDir.files); });
		pickZip.addEventListener('change', function() { picked(pickZip.files); });
		ui.showModal(api.t('web.xfer.import_title'), [
			E('p', { 'class': 'dps-muted' }, api.t('web.xfer.import_intro')),
			E('div', { 'class': 'dps-actions' }, [
				api.btn(api.t('web.xfer.pick_folder'), function() { pickDir.click(); }, 'cbi-button-action'),
				api.btn(api.t('web.xfer.pick_zip'), function() { pickZip.click(); }, 'cbi-button-neutral'),
				pickDir, pickZip
			]),
			body,
			err,
			E('div', { 'class': 'right' }, [E('button', { 'class': 'btn', 'click': ui.hideModal }, api.t('common.close'))])
		]);
	},

	showPlan: function(pkg, body, err, onDone) {
		var self = this, m = pkg.manifest, replace = false;
		var boxes = {};
		var planBox = E('div');
		var modeAdd = api.radio('dps-xfer-mode', 'add', api.t('web.xfer.mode_add'), true, function() { replace = false; refresh(); });
		var modeRep = api.radio('dps-xfer-mode', 'replace', api.t('web.xfer.mode_replace'), false, function() { replace = true; refresh(); });
		var current = null;
		function refresh() {
			dom(planBox, E('div', { 'class': 'spinning' }, api.t('web.xfer.planning')));
			self.plan(pkg, replace).then(function(plan) {
				current = plan;
				var rows = [];
				Object.keys(plan).forEach(function(s) {
					var p = plan[s];
					if (!boxes[s])
						boxes[s] = E('input', { type: 'checkbox', checked: p.risky ? null : '' });
					var parts = [];
					if (p.added) parts.push(api.t('web.xfer.p_added', { n: p.added }));
					if (p.removed) parts.push(api.t('web.xfer.p_removed', { n: p.removed }));
					if (p.had) parts.push(api.t('web.xfer.p_had', { n: p.had }));
					if (p.over) parts.push(api.t('web.xfer.p_over', { n: p.over }));
					if (p.bad && p.bad.length) parts.push(api.t('web.xfer.p_bad', { list: p.bad.slice(0, 10).map(function(b) {
						return typeof b == 'number' ? api.t('web.xfer.line', { n: b }) : b;
					}).join(', ') + (p.bad.length > 10 ? '…' : '') }));
					rows.push(E('div', { 'class': 'dps-xfer-row' }, [
						E('label', { 'class': 'dps-check' }, [boxes[s], E('b', {}, api.t('web.xfer.sec.' + s)), ': ' + (parts.join(' · ') || api.t('web.xfer.p_nothing'))]),
						p.risky ? E('div', { 'class': 'dps-error' }, api.t('web.xfer.dns_risky')) : ''
					]));
				});
				pkg.skipped.forEach(function(x) {
					rows.push(E('div', { 'class': 'dps-xfer-row dps-muted' }, (api.has('web.xfer.sec.' + x.section) ? api.t('web.xfer.sec.' + x.section) : x.section) + ': ' + api.t('web.xfer.skipped', { reason: x.reason })));
				});
				dom(planBox, rows.length ? rows : E('p', {}, api.t('web.xfer.nothing_in')));
			}, function(e) { err.textContent = api.text(e); });
		}
		function dom(el, kids) {
			while (el.firstChild) el.removeChild(el.firstChild);
			[].concat(kids).forEach(function(k) { el.appendChild(k); });
		}
		var go = api.btn(api.t('web.xfer.import_go'), function() {
			if (!current) return;
			var chosen = Object.keys(current).filter(function(s) { return boxes[s] && boxes[s].checked; });
			if (!chosen.length) { err.textContent = api.t('web.xfer.choose'); return; }
			err.textContent = '';
			// Копия текущих настроек — файлом до первой записи: на роутере нет журнала изменений, вернуть — загрузить её с «Заменить».
			return self.collect().then(function(data) {
				var p = self.pack(data, Object.keys(data.sections), '', api.t('web.xfer.backup_note'));
				p.name = p.name.replace(/^dpishell-settings-/, 'dpishell-before-import-');
				self.save(p);
				var order = ['strategies', 'sites', 'optimize', 'exclude', 'subnets', 'advanced', 'mode', 'dns'];
				var done = [], failed = [];
				return order.filter(function(s) { return chosen.indexOf(s) >= 0; }).reduce(function(pr, s) {
					return pr.then(function() {
						return current[s].apply().then(function() { done.push(api.t('web.xfer.sec.' + s)); },
							function(e) { failed.push(api.t('web.xfer.sec.' + s) + ': ' + api.text(e)); });
					});
				}, Promise.resolve()).then(function() {
					dom(body, [
						done.length ? E('div', { 'class': 'dps-ok' }, api.t('web.xfer.done', { list: done.join(', ') })) : '',
						failed.length ? E('div', { 'class': 'dps-error' }, failed.join('\n')) : '',
						E('div', { 'class': 'dps-muted' }, api.t('web.xfer.backup_saved', { file: p.name }))
					]);
					if (onDone) onDone();
				});
			});
		}, 'cbi-button-apply');
		var head = [];
		head.push(E('div', {}, api.t('web.xfer.from', { app: String(m.app_version || m.app || '?'), date: m.created ? api.time(m.created) : '?' })));
		if (m.provider) head.push(E('div', {}, api.t('web.xfer.provider') + ': ' + cleanTitle(m.provider)));
		if (m.note) head.push(E('div', { 'class': 'dps-muted' }, String(m.note).slice(0, 500)));
		dom(body, [E('div', { 'class': 'dps-line' }, head), E('div', {}, [modeAdd, modeRep]), planBox, E('div', { 'class': 'dps-actions' }, [go])]);
		refresh();
	}
});
