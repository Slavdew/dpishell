'use strict';
'require view';
'require ui';
'require dom';
'require dpishell.api as api';

// Вкладка «Сайты» (architecture.md «Режимы роутера»; порядок и названия — как на вкладке «Сайты» окна ПК, матрица ПК×роутер):
// для каких сайтов оптимизация, «Сайт не открывается», свои настройки для сайтов, исключения, «Оптимизировать»
// (только в режиме «Только из списков»), встроенные списки, свои сайты (адреса для подбора).

return view.extend({
	handleSaveApply: null,
	handleSave: null,
	handleReset: null,

	load: function() {
		return api.lang().then(function() {
			return Promise.all([
				api.safe('status'),
				api.safe('lists_get', { name: 'user-exclude' }),
				api.safe('lists_get', { name: 'exclude' }),
				api.safe('lists_get', { name: 'user-general' }),
				api.safe('targets_get'),
				api.safe('advanced_get'),
				api.safe('strategies')
			]);
		});
	},

	render: function(data) {
		var self = this;
		var status = data[0].__error ? {} : data[0];
		var listsMode = status.mode == 'lists';
		this.excludeArea = null;
		this.generalArea = null;
		this.advOn = !data[5].__error && !!data[5].enabled;

		return E('div', {}, [
			E('h2', {}, api.t('common.nav.sites')),
			this.modeCard(status),
			api.siteCheckCard('sites', function(host) { self.addToExcludeArea(host); }, function(host) { self.addToArea(self.generalArea, host); }),
			this.advancedEditor(data[5], data[6].__error ? { items: [] } : data[6]),
			this.listEditor('user-exclude', api.t('sites.router.lists.exclude_name'), data[1],
				[api.t('web.sites.exclude_descr'), api.info(api.t('web.sites.exclude_info'))]),
			listsMode ? this.listEditor('user-general', api.t('sites.router.lists.general_name'), data[3],
				[api.t('web.sites.general_descr'), api.info(api.t('web.sites.general_info'))]) : '',
			this.readOnlyList(api.t('web.sites.builtin'), data[2], api.t('web.sites.builtin_descr')),
			this.targetsEditor(data[4])
		]);
	},

	// Строка исключений «^домен» (только сам домен, без поддоменов) показывается как «домен (только сам сайт)»;
	// хранение прежнее. Разбор принимает обе подписи (ru/en) и «^» руками.
	showLine: function(x) {
		return x.charAt(0) == '^' ? x.slice(1) + ' (' + api.t('web.sites.only_self') + ')' : x;
	},

	storeLine: function(x) {
		var m = /^\s*(\S+)\s*\((?:только сам сайт|this site only)\)\s*$/i.exec(x);
		return m ? '^' + m[1].replace(/^\^/, '') : x;
	},

	// «Для каких сайтов оптимизация» (перенесено с главной, матрица п. 12): смена режима перезапускает оптимизацию — «Применить».
	modeCard: function(status) {
		var msg = E('div', { 'class': 'dps-line' });
		var cur = status.mode == 'lists' ? 'lists' : 'all';
		var all = api.radio('dps-mode', 'all', api.t('sites.mode.all'), cur == 'all', changed);
		var lists = api.radio('dps-mode', 'lists', api.t('sites.mode.lists'), cur == 'lists', changed);
		var hint = E('div', { 'class': 'dps-muted' });
		var locked = E('div', { 'class': 'dps-muted' }, api.t('web.sites.mode_locked'));
		this.modeRadios = [all, lists];
		this.modeLocked = locked;
		var apply = E('div', { 'class': 'dps-actions', 'style': 'display:none' }, [
			api.btn(api.t('web.apply'), function() {
				var mode = lists.firstChild.checked ? 'lists' : 'all';
				msg.textContent = '';
				return api.call('mode_set', { mode: mode }).then(function() {
					cur = mode;
					apply.style.display = 'none';
					msg.className = 'dps-line dps-ok';
					msg.textContent = api.t(mode == 'lists' ? 'web.sites.mode_lists_done' : 'web.sites.mode_all_done');
					// «Оптимизировать» видно только в режиме «Только из списков» — перерисовать вкладку.
					window.setTimeout(function() { window.location.reload(); }, 1500);
				}, function(e) {
					msg.className = 'dps-line dps-error';
					msg.textContent = api.text(e);
				});
			}, 'cbi-button-apply'),
			E('span', { 'class': 'dps-muted' }, api.t('web.main.restart_note'))
		]);
		function changed() {
			var mode = lists.firstChild.checked ? 'lists' : 'all';
			hint.textContent = api.t(mode == 'lists' ? 'router.mode.hint_lists' : 'sites.mode.all_warn');
			apply.style.display = mode != cur ? '' : 'none';
		}
		changed();
		var busy = status.jobs && (status.jobs.select || status.jobs.core_install);
		this.modeBusy = !!busy;
		this.syncMode(this.advOn);
		return api.card(api.t('sites.mode.title'), [
			E('div', {}, [all, lists]),
			locked,
			hint,
			apply,
			msg
		]);
	},

	// Пока включены «Свои настройки для сайтов», режим берётся оттуда («Остальные сайты») — переключатель здесь неактивен.
	syncMode: function(advOn) {
		var off = this.modeBusy || advOn;
		this.modeRadios.forEach(function(r) { r.firstChild.disabled = off; });
		this.modeLocked.style.display = advOn ? '' : 'none';
	},

	// Сайт добавлен в исключения из «Сайт не открывается» — дописать в поле ниже, если там нет несохранённых правок.
	addToExcludeArea: function(host) {
		this.addToArea(this.excludeArea, host);
	},

	// Сайт добавлен с карточки проверки — дописать его в поле списка (если там нет несохранённых правок).
	addToArea: function(ta, host) {
		if (!ta || ta.dataset.dirty)
			return;
		var lines = ta.value.split('\n').filter(function(x) { return x.trim(); });
		if (lines.indexOf(host) < 0)
			lines.push(host);
		ta.value = lines.join('\n');
	},

	// Редактор своего списка: текстовое поле, строка на домен, «Сохранить».
	listEditor: function(name, title, got, descr) {
		var self = this;
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var ta = E('textarea', { 'class': 'cbi-input-textarea dps-textarea', rows: 8, spellcheck: 'false',
			input: function() { ta.dataset.dirty = '1'; } }, (got.lines || []).map(function(x) { return self.showLine(x); }).join('\n'));
		if (name == 'user-exclude')
			this.excludeArea = ta;
		if (name == 'user-general')
			this.generalArea = ta;
		var msg = E('div', { 'class': 'dps-line' });
		var save = api.btn(api.t('web.save'), function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			var lines = ta.value.split('\n').map(function(x) { return self.storeLine(x); });
			return api.call('lists_set', { name: name, lines: lines }).then(function() {
				delete ta.dataset.dirty;
				msg.className = 'dps-line dps-ok';
				msg.textContent = api.t('web.saved');
			}, function(e) {
				msg.className = 'dps-line dps-error';
				var m = /^lines\[(\d+)\]$/.exec(e.field || '');
				if (m) {
					var i = parseInt(m[1], 10);
					msg.textContent = api.t('web.line_err', { n: i + 1, error: api.text(e) });
					self.selectLine(ta, i);
				} else
					msg.textContent = api.text(e);
			});
		}, 'cbi-button-save');
		return api.card(title, [
			api.line(descr, 'dps-muted'),
			ta,
			E('div', { 'class': 'dps-actions' }, [save]),
			msg
		]);
	},

	// Подсветить строку i (с нуля): выделить её в поле и прокрутить к ней.
	selectLine: function(ta, i) {
		var lines = ta.value.split('\n'), start = 0;
		for (var k = 0; k < i && k < lines.length; k++)
			start += lines[k].length + 1;
		var end = start + (lines[i] != null ? lines[i].length : 0);
		ta.focus();
		ta.setSelectionRange(start, end);
		var lineHeight = parseFloat(getComputedStyle(ta).lineHeight) || 18;
		ta.scrollTop = Math.max(0, (i - 2) * lineHeight);
	},

	readOnlyList: function(title, got, descr) {
		var self = this;
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var lines = got.lines || [];
		return api.card(title, [
			api.line(descr, 'dps-muted'),
			E('details', {}, [
				E('summary', { style: 'cursor:pointer' }, api.t('web.sites.show_list', { n: lines.length })),
				E('textarea', { 'class': 'cbi-input-textarea dps-textarea', rows: 10, readonly: '' }, lines.map(function(x) { return self.showLine(x); }).join('\n'))
			])
		]);
	},

	// Свои настройки для сайтов (продвинутый режим, router-api §5.2 advanced_set): строки «сайты → стратегия» и что делать
	// с остальными. Выключено — всё как сейчас, строки хранятся. «Отменить правки» — к сохранённому на роутере,
	// «Вернуть как было» — к состоянию до последнего сохранения (advanced_revert).
	advancedEditor: function(got, strategies) {
		var self = this;
		var title = api.t('sites.adv.title');
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var items = strategies.items || [];
		var name = function(id) {
			var s = items.filter(function(x) { return x.id == id; })[0];
			return s ? api.stratTitle(s.title) : id;
		};
		var on = E('input', { type: 'checkbox', change: function() { showOthers(); } });
		var othersHead = E('p', { 'style': 'margin:.6em 0 .2em' });
		var rowsBox = E('div');
		var msg = E('div', { 'class': 'dps-line' });
		var others = {};
		var othersBox = E('div', {}, [['lists', api.t('web.sites.others_lists')], ['all', api.t('sites.mode.all')], ['direct', api.t('sites.adv.others.direct')]]
			.map(function(o) { others[o[0]] = api.radio('dps-adv-others', o[0], o[1], false); return others[o[0]]; }));

		// «Остальные сайты» действуют только при включённых своих настройках — иначе блок скрыт (режим выбирается в карточке выше).
		function showOthers() {
			othersHead.style.display = othersBox.style.display = on.checked ? '' : 'none';
		}

		function addRow(r) {
			var row = E('div', { 'class': 'dps-target dps-adv-row' });
			var doms = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'example.com, cdn.example.com', value: r ? (r.domains || []).join(', ') : '' });
			var sel = E('select', { 'class': 'cbi-input-select' }, [
				E('option', { value: 'auto' }, api.t('sites.adv.choice.auto')), E('option', { value: 'direct' }, api.t('sites.adv.choice.direct'))
			].concat(items.map(function(s) { return E('option', { value: s.id }, api.stratTitle(s.title)); })));
			sel.value = r ? r.strategy : 'auto';
			var note = E('div', { 'class': 'dps-muted', 'style': 'font-size:.9em;width:100%' },
				r && r.strategy == 'auto' ? (r.resolved ? api.t('sites.adv.hint.auto_now', { strategy: name(r.resolved) }) : api.t('sites.adv.hint.auto_pending'))
					: r && r.strategy == 'direct' ? api.t('sites.adv.hint.direct') : '');
			row.appendChild(doms);
			row.appendChild(sel);
			row.appendChild(E('button', { 'class': 'btn cbi-button cbi-button-remove', title: api.t('sites.adv.row.remove'), click: function(ev) { ev.preventDefault(); row.remove(); } }, '×'));
			row.appendChild(note);
			rowsBox.appendChild(row);
			return doms;
		}

		function fill(g) {
			on.checked = !!g.enabled;
			while (rowsBox.firstChild)
				rowsBox.removeChild(rowsBox.firstChild);
			(g.rows || []).forEach(addRow);
			Object.keys(others).forEach(function(k) { others[k].firstChild.checked = (g.others || 'lists') == k; });
			showOthers();
		}
		othersHead.appendChild(E('b', {}, api.t('sites.adv.others.title')));
		othersHead.appendChild(api.info(api.t('web.adv.others_info')));
		fill(got);

		function reload(text, cls) {
			return api.call('advanced_get').then(function(g) {
				got = g;
				fill(g);
				self.syncMode(!!g.enabled);
				msg.className = 'dps-line ' + (cls || '');
				msg.textContent = text || '';
			});
		}

		var add = api.btn(api.t('sites.adv.add'), function() { addRow().focus(); }, 'cbi-button-add');
		var save = api.btn(api.t('web.save'), function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			var rows = rowsBox.querySelectorAll('.dps-adv-row'), sent = [], map = [];
			rows.forEach(function(r, i) {
				r.classList.remove('dps-bad');
				var d = r.querySelector('input').value.split(/[\s,;]+/).filter(function(x) { return x; });
				if (d.length) { sent.push({ domains: d, strategy: r.querySelector('select').value }); map.push(i); }
			});
			var oth = Object.keys(others).filter(function(k) { return others[k].firstChild.checked; })[0] || 'lists';
			return api.call('advanced_set', { enabled: on.checked, others: oth, rows: sent }).then(function() {
				return reload(on.checked ? api.t('sites.adv.done.on', { n: sent.length }) : api.t('sites.adv.done.off'), 'dps-ok');
			}, function(e) {
				msg.className = 'dps-line dps-error';
				var m = /^rows\[(\d+)\]/.exec(e.field || '');
				if (m && map[parseInt(m[1], 10)] != null) {
					var r = rows[map[parseInt(m[1], 10)]];
					r.classList.add('dps-bad');
					r.querySelector('input').focus();
					msg.textContent = api.t('web.line_err', { n: map[parseInt(m[1], 10)] + 1, error: api.text(e) });
				} else
					msg.textContent = api.text(e);
			});
		}, 'cbi-button-save');
		var discard = api.btn(api.t('sites.adv.discard'), function() { fill(got); msg.textContent = ''; }, 'cbi-button-neutral');
		var revert = api.btn(api.t('sites.adv.revert'), function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			return api.call('advanced_revert').then(function(r) {
				return reload(r && r.nothing ? api.t('sites.adv.nothing_to_revert') : api.t('web.sites.adv_reverted'), r && r.nothing ? 'dps-muted' : 'dps-ok');
			}, function(e) {
				msg.className = 'dps-line dps-error';
				msg.textContent = api.text(e);
			});
		}, 'cbi-button-neutral');

		return api.card(title, [
			E('label', { 'class': 'dps-check' }, [on, api.t('sites.adv.on'),
				api.info(api.t('web.adv.info'))]),
			rowsBox,
			E('div', { 'class': 'dps-actions' }, [add]),
			othersHead,
			othersBox,
			E('div', { 'class': 'dps-actions' }, [save, discard, revert]),
			msg
		]);
	},

	// «Свои сайты» — адреса для подбора и проверки: адрес + флаг «важный».
	targetsEditor: function(got) {
		var title = api.t('select.group.user');
		if (got.__error)
			return api.card(title, [api.errorLine(got.__error)]);
		var rowsBox = E('div');
		var msg = E('div', { 'class': 'dps-line' });

		function addRow(item) {
			var row = E('div', { 'class': 'dps-target' });
			var url = E('input', { type: 'text', 'class': 'cbi-input-text', placeholder: 'https://example.com/', value: item ? item.url : '' });
			var imp = E('input', { type: 'checkbox', checked: item && item.important ? '' : null });
			row.appendChild(url);
			row.appendChild(E('label', { style: 'white-space:nowrap' }, [imp, ' ' + api.t('sites.router.target.important')]));
			row.appendChild(E('button', { 'class': 'btn cbi-button cbi-button-remove', title: api.t('sites.router.target.remove'), click: function(ev) { ev.preventDefault(); row.remove(); } }, '×'));
			rowsBox.appendChild(row);
			return url;
		}
		(got.items || []).forEach(addRow);

		var add = api.btn(api.t('web.sites.add_target'), function() { addRow().focus(); }, 'cbi-button-add');
		var save = api.btn(api.t('web.save'), function() {
			msg.className = 'dps-line';
			msg.textContent = '';
			var rows = rowsBox.querySelectorAll('.dps-target'), items = [];
			rows.forEach(function(r) {
				r.classList.remove('dps-bad');
				var u = r.querySelector('input[type=text]').value.trim();
				if (u && !/^https?:\/\//i.test(u))
					u = 'https://' + u;
				items.push({ url: u, important: r.querySelector('input[type=checkbox]').checked });
			});
			// Пустые строки — не отправляем, но индексы ошибок считаем по отправленному.
			var sent = [], map = [];
			items.forEach(function(it, i) { if (it.url) { sent.push(it); map.push(i); } });
			return api.call('targets_set', { items: sent }).then(function() {
				msg.className = 'dps-line dps-ok';
				msg.textContent = api.t('web.saved');
				rows.forEach(function(r) {
					var inp = r.querySelector('input[type=text]');
					if (!inp.value.trim()) r.remove();
				});
			}, function(e) {
				msg.className = 'dps-line dps-error';
				var m = /^items\[(\d+)\]/.exec(e.field || '');
				if (m && map[parseInt(m[1], 10)] != null) {
					var r = rows[map[parseInt(m[1], 10)]];
					r.classList.add('dps-bad');
					r.querySelector('input[type=text]').focus();
					msg.textContent = api.t('web.sites.addr_err', { n: map[parseInt(m[1], 10)] + 1, error: api.text(e) });
				} else
					msg.textContent = api.text(e);
			});
		}, 'cbi-button-save');

		return api.card(title, [
			api.line([api.t('web.sites.targets_descr'), api.info(api.t('web.sites.targets_info'))], 'dps-muted'),
			rowsBox,
			E('div', { 'class': 'dps-actions' }, [add, save]),
			msg
		]);
	}
});
