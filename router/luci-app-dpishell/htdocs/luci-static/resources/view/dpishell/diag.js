'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «Диагностика» (architecture.md «Диагностика роутера»): чужие обходы, flow offloading, IPv6, DNS, ядро, отчёт.

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
				api.line(['Здесь роутер проверяет, что мешает обходу: другие программы обхода, ускорение сети, подмена DNS. Ничего не меняется без вашей кнопки.',
					api.info('Зайдите сюда, если обход «Работает», а сайты не открываются, или перед тем, как писать о проблеме. «Сохранить отчёт» собирает сведения о роутере и обходе в файл — без паролей — его можно прикрепить к вопросу.')], 'dps-muted'),
				this.el.findings,
				this.el.err
			]),
			api.card('Ядро обхода', [this.el.core]),
			api.card('Отчёт', [
				api.line('Версии, настройки обхода и DNS, хвост журнала роутера — текстовым файлом.', 'dps-muted'),
				E('div', { 'class': 'dps-actions' }, [api.btn('Сохранить отчёт', function() { return self.report(); }, 'cbi-button-action')])
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
				return kids.push(finding('warning', 'Установлен другой zapret (пакет ' + c.name + ') — он занимает те же файлы. Удалите его: Сервисы → Дополнения или apk del ' + c.name + '. До этого включение и подбор заблокированы.'));
			kids.push(finding('warning', 'Работает другой обход: ' + c.name + '. Пока он работает, включение и подбор заблокированы.',
				api.btn('Остановить', function() { return self.conflictStop(c.name); }, 'cbi-button-remove')));
		});
		if (d.flow_offload)
			kids.push(finding('warning', 'Включено ускорение сети (flow offloading) — с ним обход не работает. «Исправить» передаст ускорение обходу: скорость та же, обход работает.',
				api.btn('Исправить', function() { return self.act('flow_offload_fix'); }, 'cbi-button-apply')));
		if (d.ipv6_wan)
			kids.push(finding('notice', 'У роутера есть IPv6 от провайдера, а обход работает только для IPv4 — часть сайтов может открываться мимо него. «Исправить» включит обход и для IPv6.',
				api.btn('Исправить', function() { return self.act('ipv6_set', { enabled: true }); }, 'cbi-button-apply')));
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
			kids.push(api.line('Файлы обхода повреждены — нажмите «Восстановить».', 'dps-error'));
		else
			kids.push(api.line('Ядро ' + (c.version || '') + ' установлено, файлы целы', 'dps-ok'));
		if (!(s.jobs && s.jobs.core_install) && (!c.installed || c.hash_ok === false))
			kids.push(E('div', { 'class': 'dps-actions' }, [
				api.btn(c.installed ? 'Восстановить' : 'Загрузить ядро', function() { return self.coreInstall(); }, 'cbi-button-apply')
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

	// Удаление (07 §9, uninstall): внизу, свёрнуто, спокойная кнопка; подтверждение — словом «удалить».
	uninstallSection: function() {
		var self = this;
		var keep = E('input', { type: 'radio', name: 'dps-uninst', value: 'keep', checked: '' });
		var full = E('input', { type: 'radio', name: 'dps-uninst', value: 'full' });
		var word = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'удалить', autocomplete: 'off', style: 'max-width:12em' });
		var btn = api.btn('Удалить DpiShell', function() { return self.uninstall(!full.checked); }, 'cbi-button-neutral');
		btn.disabled = true;
		word.addEventListener('input', function() { btn.disabled = word.value.trim().toLowerCase() != 'удалить'; });
		this.el.uninstall = E('div', {}, [
			api.line('Обход выключится, всё, что DpiShell менял в настройках роутера (ускорение сети, DNS, расписание), вернётся как было, пакеты DpiShell и ядро обхода удалятся. Модули ядра и другие пакеты роутера останутся.', 'dps-muted'),
			E('label', { 'class': 'dps-radio' }, [keep, 'Сохранить настройки — свои адреса, исключения, найденные стратегии; при повторной установке они вернутся']),
			E('label', { 'class': 'dps-radio' }, [full, 'Удалить полностью, вместе с настройками']),
			E('div', { 'class': 'dps-actions' }, [E('span', {}, 'Чтобы подтвердить, введите слово «удалить»: '), word, btn])
		]);
		return E('details', { 'style': 'margin-top:2em' }, [
			E('summary', { 'class': 'dps-muted' }, 'Удаление DpiShell'),
			this.el.uninstall
		]);
	},

	uninstall: function(keepSettings) {
		var self = this;
		return api.call('uninstall', { keep_settings: keepSettings }).then(function() {
			dom.content(self.el.uninstall, E('div', { 'class': 'spinning' }, 'Удаляю DpiShell… страницу пока не закрывайте'));
			poll.stop();
			// Пакет и объект ubus исчезают по ходу удаления: пропали (или сброшена сессия) — значит, удалён.
			var tries = 0;
			var timer = window.setInterval(function() {
				tries++;
				api.safe('status').then(function(s) {
					if (s.__error || tries > 90) {
						window.clearInterval(timer);
						dom.content(self.el.uninstall, api.line(s.__error
							? 'DpiShell удалён' + (keepSettings ? ', настройки сохранены' : '') + '. Страницу можно закрыть.'
							: 'Удаление идёт дольше обычного — обновите страницу через минуту.', s.__error ? 'dps-ok' : 'dps-muted'));
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
