'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Диагностика» (06-router-spec §3.6): чужие обходы, flow offloading, IPv6, DNS, ядро, отчёт.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return Promise.all([api.safe('diag'), api.safe('status')]);
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
			E('h2', {}, 'Диагностика'),
			api.card('Что мешает обходу', [
				api.line(['Здесь роутер проверяет, что мешает обходу: другие программы обхода, ускорение межсетевого экрана, подмена DNS. Ничего не меняется без вашей кнопки.',
					api.info('Зайдите сюда, если обход «Работает», а сайты не открываются, или перед тем, как писать о проблеме. «Сохранить отчёт» собирает сведения о роутере и обходе в файл — без паролей — его можно прикрепить к вопросу.')], 'dps-muted'),
				this.el.findings,
				this.el.err
			]),
			api.card('Ядро обхода', [this.el.core]),
			api.card('Отчёт', [
				api.line('Версии, настройки обхода и DNS, хвост журнала роутера — текстовым файлом.', 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [api.btn('Сохранить отчёт', function() { return self.report(); }, 'cbi-button-action')])
			])
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
				return kids.push(finding('warning', 'Установлен другой zapret (пакет ' + c.name + ') — он занимает те же файлы. Удалите его: Сервисы → Дополнения или apk del ' + c.name + '. До этого включение и подбор заблокированы.'));
			kids.push(finding('warning', 'Работает другой обход: ' + c.name + '. Пока он работает, включение и подбор заблокированы.',
				api.btn('Остановить', function() { return self.conflictStop(c.name); }, 'cbi-button-remove')));
		});
		if (d.flow_offload)
			kids.push(finding('warning', 'Включено ускорение межсетевого экрана (flow offloading) — с ним обход не работает. Ускорение можно передать zapret: скорость та же, обход работает.',
				api.btn('Передать zapret', function() { return self.act('flow_offload_fix'); }, 'cbi-button-apply')));
		if (d.ipv6_wan)
			kids.push(finding('notice', 'У роутера есть адрес IPv6 от провайдера. Обход по IPv6 сейчас выключен — сайты могут открываться в обход него.',
				api.btn('Включить обход по IPv6', function() { return self.act('ipv6_set', { enabled: true }); }, 'cbi-button-apply')));
		if (d.dns && d.dns.spoofed)
			kids.push(finding('warning', 'Провайдер подменяет ответы DNS' + (d.dns.checked_at ? ' (проверено ' + api.time(d.dns.checked_at) + ')' : ''),
				api.btn('Включить защищённый DNS', function() { api.dnsEnableDialog(function() { self.reload(); }); }, 'cbi-button-apply')));
		if (!kids.length)
			kids.push(api.line('Помех не найдено', 'dps-ok'));
		if (d.overlay_free_kb != null)
			kids.push(api.line('Свободно на роутере: ' + (d.overlay_free_kb >= 1024 ? Math.round(d.overlay_free_kb / 1024) + ' МБ' : d.overlay_free_kb + ' КБ'), 'dps-muted'));
		dom.content(this.el.findings, kids);
	},

	renderCore: function() {
		var self = this, s = this.status, kids = [];
		if (!s) {
			dom.content(this.el.core, api.line('Состояние неизвестно', 'dps-muted'));
			return;
		}
		var c = s.core || {};
		if (s.jobs && s.jobs.core_install)
			kids.push(E('div', { 'class': 'spinning' }, 'Загружаем ядро обхода…'));
		else if (!c.installed)
			kids.push(api.line('Ядро обхода не установлено — без него обход не работает.', 'dps-error'));
		else if (c.hash_ok === false)
			kids.push(api.line('Файлы ядра изменены — переустановите.', 'dps-error'));
		else
			kids.push(api.line('Ядро ' + (c.version || '') + ' установлено, файлы целы', 'dps-ok'));
		if (!(s.jobs && s.jobs.core_install) && (!c.installed || c.hash_ok === false))
			kids.push(E('div', { 'class': 'dps-actions' }, [
				api.btn(c.installed ? 'Переустановить ядро' : 'Загрузить ядро', function() { return self.coreInstall(); }, 'cbi-button-apply')
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
