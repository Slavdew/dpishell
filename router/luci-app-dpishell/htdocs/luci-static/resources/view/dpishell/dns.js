'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «DNS» (06-router-spec §3.8): анализ подмены, защищённый DNS для всей сети, возврат.

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return Promise.all([api.safe('dns_status'), api.safe('status')]);
	},

	render: function(data) {
		var self = this;
		this.dns = data[0];
		this.status = data[1].__error ? { jobs: {} } : data[1];
		this.waitTicks = 0;

		this.el = {
			state: E('div', { 'class': 'dps-state' }),
			body: E('div'),
			actions: E('div', { 'class': 'dps-actions' }),
			err: E('div', { 'class': 'dps-error' })
		};

		var view = E('div', {}, [
			E('h2', {}, 'DNS'),
			api.card(null, [
				api.line(['DNS переводит имя сайта в адрес. Некоторые провайдеры подменяют ответы, и тогда обход не помогает программам на телефонах, ТВ и компьютерах. Роутер раздаёт DNS всем устройствам, поэтому защищённый DNS включается здесь для всей сети.',
					api.info('«Проверить» сравнивает ответы DNS провайдера с эталонными (Google и Cloudflare по шифрованному каналу). Если ответы подменяются — включите защищённый DNS. Браузеры с собственным защищённым DNS это не затрагивает. Если после включения что-то пропало — «Вернуть как было».')], 'dps-muted'),
				this.el.state,
				this.el.body,
				this.el.actions,
				this.el.err
			]),
			api.card('Что изменится при включении защищённого DNS', [api.dnsConsequences()])
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
		var names = { dns_analyze: 'Проверка', dns_enable: 'Включение защищённого DNS', dns_restore: 'Возврат DNS' };
		var name = names[job.action] || job.action;
		if (job.running)
			return E('div', { 'class': 'spinning' }, name + '…');
		return api.line(name + (job.at ? ' ' + api.time(job.at) : '') + ': ' + (job.message || (job.ok ? 'готово' : 'не удалось')),
			job.ok === false ? 'dps-error' : 'dps-muted');
	},

	update: function() {
		var self = this, d = this.dns;
		var analyzing = this.waitTicks > 0 || !!(d.job && d.job.running);
		var kids = [], actions = [];

		if (d.__error) {
			this.el.state.textContent = 'Состояние DNS неизвестно';
			kids.push(api.errorLine(d.__error));
		} else {
			this.el.state.textContent = d.mode == 'doh' ? 'Включён защищённый DNS' : 'Обычный DNS провайдера';
			if (d.fallback_active)
				kids.push(E('div', { 'class': 'alert-message warning' }, 'Защищённые серверы недоступны — временно работает DNS провайдера. Роутер вернёт защищённый DNS, когда они ответят.'));
			if (d.spoofed === true)
				kids.push(E('div', { 'class': 'alert-message warning' }, 'Провайдер подменяет ответы DNS' + (d.checked_at ? ' (проверено ' + api.time(d.checked_at) + ')' : '')));
			else if (d.spoofed === false)
				kids.push(api.line('Подмены DNS не замечено' + (d.checked_at ? ' (проверено ' + api.time(d.checked_at) + ')' : ''), 'dps-ok'));
			else
				kids.push(api.line('Проверка ещё не выполнялась', 'dps-muted'));
			if (d.servers && d.servers.length) {
				var t = E('table', { 'class': 'table dps-table' }, [
					E('tr', { 'class': 'tr table-titles' }, [
						E('th', { 'class': 'th' }, 'Сервер'), E('th', { 'class': 'th' }, 'Доступен'), E('th', { 'class': 'th' }, 'Ответ')
					])
				]);
				d.servers.forEach(function(s) {
					t.appendChild(E('tr', { 'class': 'tr' }, [
						E('td', { 'class': 'td', 'data-title': 'Сервер' }, s.name),
						E('td', { 'class': 'td', 'data-title': 'Доступен' }, s.doh_ok ? 'да' : 'нет'),
						E('td', { 'class': 'td', 'data-title': 'Ответ' }, s.ms != null ? s.ms + ' мс' : '—')
					]));
				});
				kids.push(t);
			}
			if (d.job)
				kids.push(this.jobLine(d.job));
		}
		if (analyzing && !(d.job && d.job.running))
			kids.push(E('div', { 'class': 'spinning' }, 'Выполняется…'));

		actions.push(api.btn('Проверить', function() { return self.analyze(); }, 'cbi-button-action'));
		if (!d.__error && d.mode == 'doh')
			actions.push(api.btn('Вернуть как было', function() { return self.restore(); }, 'cbi-button-remove'));
		else
			actions.push(api.btn('Включить защищённый DNS', function() { api.dnsEnableDialog(function() { self.wait(); }); }, 'cbi-button-apply'));
		actions.forEach(function(b) { b.disabled = analyzing; });

		dom.content(this.el.body, kids);
		dom.content(this.el.actions, actions);
	},

	analyze: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('dns_analyze').then(function() {
			self.wait();
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	},

	restore: function() {
		var self = this;
		this.el.err.textContent = '';
		return api.call('dns_restore').then(function() {
			self.wait();
		}, function(e) {
			self.el.err.textContent = api.text(e);
		});
	}
});
