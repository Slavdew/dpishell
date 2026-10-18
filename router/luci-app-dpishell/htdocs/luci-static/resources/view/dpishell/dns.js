'use strict';
'require view';
'require ui';
'require poll';
'require dom';
'require dpishell.api as api';

// Вкладка «DNS» (architecture.md «DNS для всей сети»): анализ подмены, выбор DNS для всей сети, возврат.

// Кто держит сервер — подпись к варианту (перед заметкой из dns-presets.json).
var OWNERS = {
	provider: 'Адреса выдаёт провайдер, ответы он может подменять',
	'default': 'Google и Cloudflare, США',
	google: 'Google, США',
	cloudflare: 'Cloudflare, США',
	quad9: 'Quad9, некоммерческий фонд, Швейцария',
	yandex: 'Яндекс'
};

// Копирование и по http: navigator.clipboard есть только в защищённом контексте, а LuCI обычно открыт по http.
function copyText(text) {
	if (navigator.clipboard && window.isSecureContext)
		return navigator.clipboard.writeText(text);
	var ta = E('textarea', { style: 'position:fixed;left:-1000px;top:0' }, text);
	document.body.appendChild(ta);
	ta.select();
	try { document.execCommand('copy'); } catch (e) {}
	document.body.removeChild(ta);
}

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return Promise.all([api.safe('dns_status'), api.safe('status'), api.safe('dns_presets')]);
	},

	render: function(data) {
		var self = this;
		this.dns = data[0];
		this.status = data[1].__error ? { jobs: {} } : data[1];
		this.presets = data[2];
		this.choice = null;
		this.waitTicks = 0;

		this.el = {
			state: E('div', { 'class': 'dps-state' }),
			body: E('div'),
			actions: E('div', { 'class': 'dps-actions' }),
			err: E('div', { 'class': 'dps-error' }),
			presets: E('div'),
			presetErr: E('div', { 'class': 'dps-error' })
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
			api.card('Какой DNS раздавать сети', [this.el.presets, this.el.presetErr]),
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
		var names = { dns_analyze: 'Проверка', dns_enable: 'Включение защищённого DNS', dns_restore: 'Возврат DNS', dns_set: 'Смена DNS' };
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
			var cur = this.presetTitle(d.preset);
			this.el.state.textContent = d.mode == 'doh' ? 'Защищённый DNS' + (cur ? ': ' + cur : '')
				: d.mode == 'plain' ? 'Обычный DNS' + (cur ? ': ' + cur : '') : 'DNS провайдера';
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
		this.renderPresets(analyzing);
	},

	presetTitle: function(id) {
		var p = this.presets;
		if (!id || !p || p.__error)
			return null;
		if (id == 'custom')
			return p.custom ? p.custom.title : 'свой';
		var f = (p.presets || []).filter(function(x) { return x.id == id; })[0];
		return f ? f.title : id;
	},

	// Выбор варианта DNS (router-api §5.1 dns_presets, §5.2 dns_set): адреса, DoH, кто держит; «Копировать» — для своих устройств.
	renderPresets: function(busy) {
		var self = this, p = this.presets;
		if (!p || p.__error) {
			dom.content(this.el.presets, p && p.__error ? api.errorLine(p.__error) : '');
			return;
		}
		var current = (this.dns && !this.dns.__error && this.dns.preset) || p.current || 'provider';
		if (this.choice == null)
			this.choice = current;
		var list = (p.presets || []).slice();
		var c = p.custom || {};
		var cres = (c.resolvers || [])[0] || {};
		list.push({ id: 'custom', title: 'Свой', note: null, plain: c.plain || [], resolvers: c.resolvers || [] });

		var rows = list.map(function(x) {
			var doh = (x.resolvers || []).map(function(v) { return v.doh; }).join(', ');
			var plain = x.plain || [];
			var info = [];
			var who = [OWNERS[x.id], x.note].filter(function(t) { return t; }).join(' · ');
			if (who)
				info.push(E('div', { 'class': 'dps-muted' }, who));
			if (plain.length)
				info.push(E('div', {}, 'Адреса: ' + plain.join(', ')));
			if (doh)
				info.push(E('div', {}, 'DoH: ' + doh));
			var head = [api.radio('dps-dns-preset', x.id, x.title + (x.id == current ? ' — сейчас' : ''), x.id == self.choice, function() {
				self.choice = x.id;
				self.renderPresets(busy);
			})];
			if (plain.length || doh)
				head.push(' ', api.btn('Копировать', function(ev) {
					var text = [plain.length ? 'DNS: ' + plain.join(', ') : null, doh ? 'DoH: ' + doh : null].filter(function(t) { return t; }).join('\n');
					copyText(text);
					ev.target.textContent = 'Скопировано';
					window.setTimeout(function() { ev.target.textContent = 'Копировать'; }, 1500);
				}));
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
				api.row('Название', this.cf.title),
				api.row('Адреса DNS', this.cf.plain, 'До 4, через запятую'),
				api.row('Адрес DoH', this.cf.doh, 'Необязательно'),
				api.row('Адреса сервера DoH', this.cf.boot, 'Нужны, если в адресе DoH имя, а не IP')
			]);
		}
		var apply = api.btn('Применить', function() { return self.applyPreset(); }, 'cbi-button-apply');
		apply.disabled = busy || (this.choice == current && this.choice != 'custom');
		dom.content(this.el.presets, rows.concat(form ? [form] : [], [E('div', { 'class': 'dps-actions' }, [apply])]));
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
			self.el.presetErr.textContent = api.text(e);
		});
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
