'use strict';
'require view';
'require ui';
'require poll';
'require dpishell.api as api';

// Строки — только текстом: свой E вместо LuCI (api.js, tools/xss-check.py).
var E = api.E;

// Вкладка «DNS» (architecture.md «DNS для всей сети»): анализ подмены, выбор DNS для всей сети, возврат.
// Названия — как в окне ПК (вкладка «DNS» и DNS роутера на вкладке «Роутер»), матрица ПК×роутер п. 41–45.

// Кто держит сервер — подпись к варианту (перед заметкой из dns-presets.json).
// Названия, владельцы и заметки — по id варианта ключами словаря (как в окне ПК, dns.preset.*); текст роутера — только для
// неизвестного id (замечания владельца 2026-10-20, п. 7: «От провайдера», «Яндекс» оставались в английском).
var OWNERS = { provider: 'web.dns.owner.provider', 'default': 'router.dns.owner_default', google: 'dns.preset.owner_google',
	cloudflare: 'dns.preset.owner_cloudflare', quad9: 'dns.preset.owner_quad9', yandex: 'dns.preset.owner_yandex' };
var NOTES = { provider: 'web.dns.note.provider', 'default': 'web.dns.note.default', quad9: 'dns.preset.note_quad9', yandex: 'dns.preset.note_yandex' };

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.lang().then(function() {
			return Promise.all([api.safe('dns_status'), api.safe('status'), api.safe('dns_presets')]);
		});
	},

	render: function(data) {
		this.dns = data[0];
		this.status = data[1].__error ? { jobs: {} } : data[1];
		this.presets = data[2];
		this.choice = null;
		this.waitTicks = 0;
		this.speed = {};

		this.el = {
			state: E('div', { 'class': 'dps-state' }),
			body: E('div'),
			actions: E('div', { 'class': 'dps-actions' }),
			err: E('div', { 'class': 'dps-error' }),
			presets: E('div'),
			presetErr: E('div', { 'class': 'dps-error' })
		};

		var view = E('div', {}, [
			E('h2', {}, api.t('common.nav.net')),
			api.card(api.t('dns.title'), [
				api.line([api.t('web.dns.intro'), api.info(api.t('web.dns.intro_info'))], 'dps-muted'),
				this.el.state,
				this.el.body,
				this.el.actions,
				this.el.err
			]),
			api.card(api.t('router.dns.title'), [
				E('details', { 'class': 'dps-details' }, [E('summary', {}, api.t('web.dns.what_changes')), api.dnsConsequences()]),
				this.el.presets, this.el.presetErr])
		]);

		this.update();
		poll.add(L.bind(this.tick, this), 1);
		return view;
	},

	// Пока идёт задание (проверка, включение, возврат) — опрос раз в 1 с; задания DNS в status.jobs.dns_analyze.
	tick: function() {
		var self = this;
		if (this.waitTicks <= 0)
			return Promise.resolve();
		this.waitTicks--;
		return api.safe('status').then(function(s) {
			if (!s.__error)
				self.status = s;
			if (s.__error || !s.jobs.dns_analyze || self.waitTicks <= 0) {
				self.waitTicks = 0;
				return self.reload();
			}
		});
	},

	wait: function() {
		this.waitTicks = 180;
		this.update();
	},

	reload: function() {
		var self = this;
		return api.safe('dns_status').then(function(d) {
			self.dns = d;
			if (d.job && d.job.running)
				self.waitTicks = self.waitTicks || 180;
			self.update();
		});
	},

	jobLine: function(job) {
		var known = ['dns_analyze', 'dns_enable', 'dns_restore', 'dns_set'];
		var name = known.indexOf(job.action) >= 0 ? api.t('router.dns.job.' + job.action) : job.action;
		if (job.running)
			return E('div', { 'class': 'spinning' }, api.t('router.dns.job.running', { action: name }));
		// При успехе роутер пишет в message «Готово» по-русски — на странице вместо него web.done.
		var m = job.ok === false ? api.msg(job.message_msg || job.message) : null;
		var text = job.ok === false
			? (m ? api.t('router.dns.job.failed_msg', { action: name, message: m }) : api.t('router.dns.job.failed', { action: name }))
			: name + ': ' + (m || api.t('web.done'));
		text += job.at ? ' · ' + api.time(job.at) : '';
		// Неудача — с «Подробнее»: там сырой вывод apk (job.detail).
		if (job.ok === false)
			return api.errorLine(api.error(text, job.action, null, job.detail));
		return api.line(text, 'dps-muted');
	},

	update: function() {
		var self = this, d = this.dns;
		var analyzing = this.waitTicks > 0 || !!(d.job && d.job.running);
		var kids = [], actions = [];

		if (d.__error) {
			this.el.state.textContent = api.t('web.dns.state_unknown');
			kids.push(api.errorLine(d.__error));
		} else {
			var cur = this.presetTitle(d.preset);
			this.el.state.textContent = d.mode == 'doh' ? api.t('router.dns.now_doh', { title: cur || '—' })
				: d.mode == 'plain' ? api.t('router.dns.now_plain', { title: cur || '—' }) : api.t('router.dns.now', { title: api.t('web.dns.provider') });
			if (d.fallback_active)
				kids.push(E('div', { 'class': 'alert-message warning' }, api.t('router.dns.unavailable') + '. ' + api.t('router.dns.fallback_hint')));
			if (d.spoofed === true)
				kids.push(E('div', { 'class': 'alert-message warning' }, d.checked_at ? api.t('router.dns.spoofed_at', { time: api.time(d.checked_at) }) : api.t('router.dns.spoofed')));
			else if (d.spoofed === false)
				kids.push(api.line(d.checked_at ? api.t('router.dns.clean_at', { time: api.time(d.checked_at) }) : api.t('router.dns.clean'), 'dps-ok'));
			else
				kids.push(api.line(api.t('web.dns.not_checked'), 'dps-muted'));
			(d.servers || []).forEach(function(s) {
				// Имя от роутера — русское название варианта; на странице — по id, в языке страницы.
				s = { id: s.id, doh_ok: s.doh_ok, ms: s.ms, name: self.presetTitle(s.id) || s.name };
				kids.push(api.line(!s.doh_ok ? api.t('router.dns.server_fail', { name: s.name })
					: s.ms != null ? api.t('router.dns.server_ok_ms', { name: s.name, ms: s.ms }) : api.t('router.dns.server_ok', { name: s.name }),
					s.doh_ok ? 'dps-muted' : 'dps-error'));
			});
			if (d.job)
				kids.push(this.jobLine(d.job));
		}
		if (analyzing && !(d.job && d.job.running))
			kids.push(E('div', { 'class': 'spinning' }, api.t('web.dns.running')));

		actions.push(api.btn(api.t('dns.check'), function() { return self.analyze(); }, 'cbi-button-action'));
		// После неудачного задания «Вернуть как было» есть всегда: на него ссылается инструкция в тексте ошибки.
		if (!d.__error && (d.mode != 'provider' || (d.job && d.job.ok === false)))
			actions.push(api.btn(api.t('router.dns.restore'), function() { return self.restore(); }, 'cbi-button-remove'));
		else if (!d.__error && d.spoofed === true)
			actions.push(api.btn(api.t('router.dns.enable'), function() { api.dnsEnableDialog(function() { self.wait(); }); }, 'cbi-button-apply'));
		actions.forEach(function(b) { b.disabled = analyzing; });

		api.content(this.el.body, kids);
		api.content(this.el.actions, actions);
		this.renderPresets(analyzing);
	},

	presetTitle: function(id) {
		var p = this.presets;
		if (!id || !p || p.__error)
			return null;
		if (id == 'custom')
			return p.custom ? p.custom.title : api.t('router.dns.custom');
		if (api.has('web.dns.preset.' + id))
			return api.t('web.dns.preset.' + id);
		var f = (p.presets || []).filter(function(x) { return x.id == id; })[0];
		return f ? f.title : id;
	},

	// Выбор варианта DNS (router-api §5.1 dns_presets, §5.2 dns_set): адреса, защищённый (DoH), кто держит; «Скопировать
	// адреса» — для своих устройств; «Сравнить скорость» — время ответа защищённого DNS каждого варианта (dns_compare).
	renderPresets: function(busy) {
		var self = this, p = this.presets;
		if (!p || p.__error) {
			api.content(this.el.presets, p && p.__error ? api.errorLine(p.__error) : '');
			return;
		}
		var current = (this.dns && !this.dns.__error && this.dns.preset) || p.current || 'provider';
		if (this.choice == null)
			this.choice = current;
		var list = (p.presets || []).slice();
		var c = p.custom || {};
		var cres = (c.resolvers || [])[0] || {};
		list.push({ id: 'custom', title: api.t('dns.picker.custom'), note: null, plain: c.plain || [], resolvers: c.resolvers || [] });

		// Названия и заметки вариантов приходят из dns-presets.json по-русски — на странице берутся из словаря, если там есть.
		list = list.map(function(x) {
			return Object.assign({}, x, {
				title: x.id != 'custom' && api.has('web.dns.preset.' + x.id) ? api.t('web.dns.preset.' + x.id) : x.title,
				note: NOTES[x.id] ? api.t(NOTES[x.id]) : x.note
			});
		});

		var rows = list.map(function(x) {
			var doh = (x.resolvers || []).map(function(v) { return v.doh; }).join(', ');
			var plain = x.plain || [];
			var info = [];
			var who = [OWNERS[x.id] ? api.t(OWNERS[x.id]) : null, x.note].filter(function(t) { return t; }).join(' · ');
			var sp = self.speed[x.id];
			if (sp !== undefined)
				who = (who ? who + ' · ' : '') + (sp == null ? api.t('dns.no_answer') : api.t('dns.picker.ping', { ms: sp }));
			if (who)
				info.push(E('div', { 'class': 'dps-muted' }, who));
			// Адреса и защищённый — свёрнуто (ux-audit-web п.23): видно название, кто держит и заметку; раскрытое не сворачивается при опросе.
			var addr = [];
			if (plain.length)
				addr.push(E('div', {}, api.t('web.dns.addresses', { ips: plain.join(', ') })));
			if (doh)
				addr.push(E('div', {}, api.t('web.dns.doh', { url: doh })));
			self.openAddr = self.openAddr || {};
			var head = [api.radio('dps-dns-preset', x.id, x.id == current ? api.t('web.dns.current_mark', { title: x.title }) : x.title, x.id == self.choice, function() {
				self.choice = x.id;
				self.renderPresets(busy);
			})];
			if (plain.length || doh) {
				var b = api.copyBtn(api.t('dns.picker.copy_ips'), function() {
					return [plain.length ? 'DNS: ' + plain.join(', ') : null, doh ? 'DoH: ' + doh : null].filter(function(t) { return t; }).join('\n');
				});
				b.title = api.t('dns.picker.copy_ips.tip');
				addr.push(b);
			}
			if (addr.length) {
				var det = E('details', { 'class': 'dps-details', open: self.openAddr[x.id] ? '' : null,
					toggle: function() { self.openAddr[x.id] = det.open; } }, [E('summary', {}, api.t('web.dns.addresses_title'))].concat(addr));
				info.push(det);
			}
			return E('div', { 'class': 'dps-preset' }, [E('div', {}, head), E('div', { 'class': 'dps-preset-info' }, info)]);
		});

		var form = null;
		if (this.choice == 'custom') {
			this.cf = this.cf || {
				title: E('input', { type: 'text', 'class': 'cbi-input-text', maxlength: 40, value: c.title || '' }),
				plain: E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: '9.9.9.9, 149.112.112.112', value: (c.plain || []).join(', ') }),
				doh: E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'https://dns.example/dns-query', value: cres.doh || '' }),
				boot: E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: '9.9.9.9', value: (cres.bootstrap || []).join(', ') })
			};
			form = E('div', {}, [
				api.row(api.t('dns.picker.name'), this.cf.title),
				api.row(api.t('dns.picker.ips_field'), this.cf.plain, api.t('web.dns.ips_hint')),
				api.row(api.t('dns.picker.doh'), this.cf.doh, api.t('web.dns.optional')),
				api.row(api.t('web.dns.boot'), this.cf.boot, api.t('web.dns.boot_hint'))
			]);
		}
		var apply = api.btn(api.t('dns.picker.apply'), function() { return self.applyPreset(); }, 'cbi-button-apply');
		apply.disabled = busy || (this.choice == current && this.choice != 'custom');
		var compare = api.btn(this.comparing ? api.t('dns.picker.comparing') : api.t('dns.picker.compare'), function() { return self.compare(); }, 'cbi-button-neutral');
		compare.title = api.t('dns.picker.compare.tip');
		compare.disabled = busy || !!this.comparing;
		var hint = apply.disabled && !busy ? E('span', { 'class': 'dps-muted' }, api.t('web.dns.choose_other')) : '';
		api.content(this.el.presets, rows.concat(form ? [form] : [], [E('div', { 'class': 'dps-actions' }, [apply, compare, hint])]));
	},

	compare: function() {
		var self = this;
		this.comparing = true;
		this.el.presetErr.textContent = '';
		this.renderPresets(false);
		return this.compareCall().then(function(r) {
			self.speed = {};
			(r.items || []).forEach(function(it) { self.speed[it.id] = it.ms; });
		}, function(e) {
			api.fail(self.el.presetErr, e);
		}).then(function() {
			self.comparing = false;
			self.renderPresets(false);
		});
	},

	// Роутер с dns_compare_start (0.2.2): замер фоном, итог — опросом dns_compare_result раз в 1 с (до 20 с), rpcd не
	// ждёт curl и остальная LuCI не стоит. Старый роутер — прежний синхронный dns_compare.
	compareCall: function() {
		var m = this.status && this.status.methods;
		if (!Array.isArray(m) || m.indexOf('dns_compare_start') < 0 || m.indexOf('dns_compare_result') < 0)
			return api.call('dns_compare');
		var tries = 20;
		var poll = function() {
			return new Promise(function(ok) { window.setTimeout(ok, 1000); }).then(function() {
				return api.call('dns_compare_result');
			}).then(function(r) {
				if (!r.running && r.items)
					return r;
				if (!r.running || --tries <= 0)
					throw api.error(api.t('web.dns.compare_failed'), 'dns_compare_result');
				return poll();
			});
		};
		return api.call('dns_compare_start').then(poll);
	},

	applyPreset: function() {
		var self = this, args;
		this.el.presetErr.textContent = '';
		if (this.choice == 'custom') {
			var ips = function(v) { return v.split(/[\s,;]+/).filter(function(x) { return x; }); };
			var cu = { title: this.cf.title.value.trim(), plain: ips(this.cf.plain.value) };
			if (this.cf.doh.value.trim()) {
				cu.doh = this.cf.doh.value.trim();
				cu.bootstrap = ips(this.cf.boot.value);
			}
			args = { custom: cu };
		} else
			args = { preset: this.choice };
		return api.call('dns_set', args).then(function() {
			return api.safe('dns_presets').then(function(p) {
				if (!p.__error)
					self.presets = p;
				self.wait();
			});
		}, function(e) {
			api.fail(self.el.presetErr, e);
		});
	},

	analyze: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('dns_analyze').then(function() {
			self.wait();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	},

	restore: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('dns_restore').then(function() {
			self.wait();
		}, function(e) {
			api.fail(self.el.err, e);
		});
	}
});
