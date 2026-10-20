'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Диагностика» (architecture.md «Диагностика роутера»): чужие обходы, flow offloading, IPv6, DNS, ядро, отчёт.
// Названия — как в окне ПК («Что мешает» на вкладке «Роутер»), матрица ПК×роутер п. 46–51.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.lang().then(function() {
			return Promise.all([api.safe('diag'), api.safe('status')]);
		});
	},

	render: function(data) {
		var self = this;
		this.diag = data[0];
		this.status = data[1].__error ? null : data[1];
		this.coreTicks = 0;

		this.el = {
			findings: E('div'),
			core: E('div'),
			err: E('div', { 'class': 'dps-error' })
		};

		var view = E('div', {}, [
			E('h2', {}, api.t('diag.title')),
			api.card(api.t('router.diag.title'), [
				api.line([api.t('web.diag.intro'), api.info(api.t('web.diag.intro_info'))], 'dps-muted'),
				this.el.findings,
				this.el.err
			]),
			api.card(api.t('web.core.title'), [this.el.core]),
			api.card(api.t('web.diag.report'), [
				api.line(api.t('web.diag.report_descr'), 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [api.btn(api.t('router.diag.save_report'), function() { return self.report(); }, 'cbi-button-action')])
			]),
			this.uninstallSection()
		]);

		this.renderFindings();
		this.renderCore();
		poll.add(L.bind(this.tick, this), 1);
		return view;
	},

	tick: function() {
		var self = this;
		if (this.coreTicks <= 0)
			return Promise.resolve();
		this.coreTicks--;
		return api.safe('status').then(function(s) {
			if (s.__error)
				return;
			self.status = s;
			if (!s.jobs.core_install)
				self.coreTicks = 0;
			self.renderCore();
		});
	},

	reload: function() {
		var self = this;
		return Promise.all([api.safe('diag'), api.safe('status')]).then(function(d) {
			self.diag = d[0];
			if (!d[1].__error)
				self.status = d[1];
			self.renderFindings();
			self.renderCore();
		});
	},

	renderFindings: function() {
		var self = this, d = this.diag, kids = [];
		if (d.__error) {
			dom.content(this.el.findings, api.errorLine(d.__error));
			return;
		}
		function finding(cls, text, action) {
			return E('div', { 'class': 'alert-message ' + cls }, E('div', { 'class': 'dps-warn' }, [E('span', {}, text), action || '']));
		}
		(d.conflicts || []).forEach(function(c) {
			if (!c.running)
				return;
			if (c.kind == 'package')
				return kids.push(finding('warning', api.t('web.diag.package', { name: c.name })));
			kids.push(finding('warning', api.t('web.diag.conflict', { name: c.name }),
				api.btn(api.t('router.diag.stop'), function() { return self.conflictStop(c.name); }, 'cbi-button-remove')));
		});
		if (d.flow_offload)
			kids.push(finding('warning', api.t('web.diag.flow'),
				api.btn(api.t('router.warn.fix'), function() { return self.act('flow_offload_fix'); }, 'cbi-button-apply')));
		if (d.ipv6_wan)
			kids.push(finding('notice', api.t('web.diag.ipv6'),
				api.btn(api.t('router.warn.fix'), function() { return self.act('ipv6_set', { enabled: true }); }, 'cbi-button-apply')));
		if (d.dns && d.dns.spoofed)
			kids.push(finding('warning', d.dns.checked_at ? api.t('router.dns.spoofed_at', { time: api.time(d.dns.checked_at) }) : api.t('router.dns.spoofed'),
				api.btn(api.t('router.dns.enable'), function() { api.dnsEnableDialog(function() { self.reload(); }); }, 'cbi-button-apply')));
		if (!kids.length)
			kids.push(api.line(api.t('web.diag.none'), 'dps-ok'));
		if (d.overlay_free_kb != null)
			kids.push(api.line(api.t('router.diag.space') + ': ' + (d.overlay_free_kb >= 1024 ? api.t('router.diag.space_value', { value: Math.round(d.overlay_free_kb / 1024) })
				: api.t('web.diag.space_kb', { value: d.overlay_free_kb })), 'dps-muted'));
		dom.content(this.el.findings, kids);
	},

	renderCore: function() {
		var self = this, s = this.status, kids = [];
		if (!s) {
			dom.content(this.el.core, api.line(api.t('web.diag.unknown'), 'dps-muted'));
			return;
		}
		var c = s.core || {};
		if (s.jobs && s.jobs.core_install)
			kids.push(E('div', { 'class': 'spinning' }, api.t('web.core.loading')));
		else if (!c.installed)
			kids.push(api.line(api.t('web.core.missing_long'), 'dps-error'));
		else if (c.hash_ok === false)
			kids.push(api.line(api.t('web.core.broken_long'), 'dps-error'));
		else
			kids.push(api.line(api.t('web.core.ok', { version: c.version || '' }), 'dps-ok'));
		if (!(s.jobs && s.jobs.core_install) && (!c.installed || c.hash_ok === false))
			kids.push(E('div', { 'class': 'dps-actions' }, [
				api.btn(api.t(c.installed ? 'web.core.restore' : 'web.core.download'), function() { return self.coreInstall(); }, 'cbi-button-apply')
			]));
		dom.content(this.el.core, kids);
	},

	coreInstall: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('core_install').then(function() {
			self.coreTicks = 600;
			self.status.jobs.core_install = true;
			self.renderCore();
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	},

	act: function(method, args) {
		var self = this;
		this.el.err.textContent = '';
		return api.call(method, args).then(function() {
			return self.reload();
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	},

	conflictStop: function(name) {
		var self = this;
		this.el.err.textContent = '';
		return api.call('conflict_stop', { name: name }).then(function() {
			return self.reload();
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	},

	// Удаление (router-api.md §5, uninstall): внизу, свёрнуто, спокойная кнопка; подтверждение — словом «удалить».
	uninstallSection: function() {
		var self = this;
		var keep = E('input', { type: 'radio', name: 'dps-uninst', value: 'keep', checked: '' });
		var full = E('input', { type: 'radio', name: 'dps-uninst', value: 'full' });
		var word = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: api.t('web.uninst.word'), autocomplete: 'off', style: 'max-width:12em' });
		var btn = api.btn(api.t('web.uninst.button'), function() { return self.uninstall(!full.checked); }, 'cbi-button-negative');
		btn.disabled = true;
		word.addEventListener('input', function() { btn.disabled = word.value.trim().toLowerCase() != api.t('web.uninst.word').toLowerCase(); });
		this.el.uninstall = E('div', {}, [
			api.line(api.t('web.uninst.intro'), 'dps-muted'),
			E('label', { 'class': 'dps-radio' }, [keep, api.t('router.uninstall.keep')]),
			E('label', { 'class': 'dps-radio' }, [full, api.t('router.uninstall.full')]),
			E('div', { 'class': 'dps-actions' }, [E('span', {}, api.t('web.uninst.confirm', { word: api.t('web.uninst.word') }) + ' '), word, btn])
		]);
		// Опасное — отдельной красной карточкой, не рядом с «Сохранить отчёт» (ux-audit-web п.29).
		return E('div', { 'class': 'dps-danger', 'style': 'margin-top:2em' }, E('details', {}, [
			E('summary', { 'style': 'font-weight:600' }, api.t('router.uninstall.title')),
			this.el.uninstall
		]));
	},

	uninstall: function(keepSettings) {
		var self = this;
		return api.call('uninstall', { keep_settings: keepSettings }).then(function() {
			dom.content(self.el.uninstall, E('div', { 'class': 'spinning' }, api.t('web.uninst.busy')));
			poll.stop();
			// Пакет и объект ubus исчезают по ходу удаления: пропали (или сброшена сессия) — значит, удалён.
			var tries = 0;
			var timer = window.setInterval(function() {
				tries++;
				api.safe('status').then(function(s) {
					if (s.__error || tries > 90) {
						window.clearInterval(timer);
						dom.content(self.el.uninstall, api.line(s.__error
							? api.t(keepSettings ? 'web.uninst.done_kept' : 'web.uninst.done')
							: api.t('web.uninst.too_long'), s.__error ? 'dps-ok' : 'dps-muted'));
					}
				});
			}, 2000);
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	},

	report: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('report').then(function(r) {
			var d = new Date();
			var name = 'dpishell-report-%04d%02d%02d-%02d%02d.txt'.format(d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes());
			api.download(name, r.text || '');
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	}
});
