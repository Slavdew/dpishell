"""Страницы LuCI: строки — только текстом (docs security-audit-router №1–2).

E() и dom.content() LuCI кладут строку, переданную детьми, как HTML (innerHTML). В строки попадают названия стратегий,
адреса из редиректов, вывод команд, заметки выпуска — поэтому страницы пользуются своим E (api.js: строка → текстовый
узел) и api.content вместо dom.content. Проверка (CI, router.yml):
  1. api.js объявляет свой E, остальные файлы с E( — «var E = api.E;» в начале модуля;
  2. нет innerHTML/outerHTML/insertAdjacentHTML/document.write и прямых dom.content/append/create/parse, window.E, L.dom;
  3. первый аргумент E — имя тега строкой (строка с «<» LuCI разбирает как HTML);
  4. заголовок ui.showModal/addNotification (LuCI кладёт его как HTML) — текст по ключу без подстановок или null.
Запуск: python router/tools/xss-check.py
"""
import pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[1] / 'luci-app-dpishell' / 'htdocs' / 'luci-static' / 'resources'
API = ROOT / 'dpishell' / 'api.js'

FORBIDDEN = [
    (re.compile(r'\binnerHTML\b|\bouterHTML\b|\binsertAdjacentHTML\b|\bdocument\.write'), 'вставка HTML'),
    (re.compile(r'(?<![\w.$])dom\.(content|append|create|parse)\('), 'dom.* кладёт строки как HTML — api.content / E'),
    (re.compile(r'\bwindow\.E\b|\bL\.dom\b|\bL\.E\b'), 'E/dom LuCI напрямую'),
]
# В api.js — единственные разрешённые обращения к E и dom LuCI (обёртки).
API_ALLOWED = {'var rawE = window.E;', 'return dom.content(node, textKids(kids));'}
E_CALL = re.compile(r'(?<![\w.$])(?<!function )E\(\s*([^,)\s]*)')
TAG = re.compile(r"^'[a-z][a-z0-9]*'$")
MODAL = re.compile(r'\bui\.(showModal|addNotification)\(\s*([^,]*?)\s*,')
MODAL_TITLE = re.compile(r"^(null|(api|self|this)\.t\('[\w.]+'\)|'[^'<&]*')$")

sys.stdout.reconfigure(encoding='utf-8')
errors = []
files = sorted(p for p in ROOT.rglob('*.js') if 'dpishell' in p.parts)
if not files:
    sys.exit('xss-check: нет файлов страниц')
for f in files:
    text = f.read_text(encoding='utf-8')
    rel = f.relative_to(ROOT)
    lines = text.splitlines()
    code = [(i + 1, l) for i, l in enumerate(lines) if not l.lstrip().startswith('//')]
    if f == API:
        if not re.search(r'^function E\(', text, re.M):
            errors.append(f'{rel}: нет своего function E(…)')
    elif E_CALL.search(text) and 'var E = api.E;' not in lines[:20]:
        errors.append(f'{rel}: E( без «var E = api.E;» в начале модуля')
    for n, l in code:
        s = l.strip()
        if f == API and s in API_ALLOWED:
            continue
        for rx, why in FORBIDDEN:
            if rx.search(l):
                errors.append(f'{rel}:{n}: {why}: {s}')
        for m in E_CALL.finditer(l):
            if not TAG.match(m.group(1)):
                errors.append(f'{rel}:{n}: первый аргумент E — не имя тега строкой: {s}')
        for m in MODAL.finditer(l):
            if not MODAL_TITLE.match(m.group(2)):
                errors.append(f'{rel}:{n}: заголовок ui.{m.group(1)} — только текст по ключу без подстановок: {s}')

if errors:
    print('\n'.join(errors))
    sys.exit(1)
print(f'xss-check: {len(files)} файлов, строки идут текстом')
