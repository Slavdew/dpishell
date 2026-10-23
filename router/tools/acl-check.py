#!/usr/bin/env python3
# Методы ubus-объекта dpishell и доступ к ним: каждый метод rpcd есть в ACL (иначе окно ПК и страница по HTTP получают
# «нет доступа», а локальный ubus в тестах этого не видит), каждый метод, который зовёт страница, описан в PARAMS
# api.js (LuCI отбрасывает неописанные аргументы); личное и сведения о защите — только в группе записи (WRITE_ONLY). Запуск из корня репозитория: python3 router/tools/acl-check.py
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RPCD = ROOT / 'router/dpishell/files/usr/share/rpcd/ucode/dpishell'
ACL = ROOT / 'router/dpishell/files/usr/share/rpcd/acl.d/dpishell.json'
API = ROOT / 'router/luci-app-dpishell/htdocs/luci-static/resources/dpishell/api.js'
VIEWS = ROOT / 'router/luci-app-dpishell/htdocs/luci-static/resources/view/dpishell'

src = RPCD.read_text(encoding='utf-8')
methods = set(re.findall(r'^methods\.(\w+) = ', src, re.M))
body = src[src.index('const methods = {'):]
body = body[:body.index('\n};')]
methods |= set(re.findall(r'^\t(\w+): \{', body, re.M))
for group in re.findall(r"for \(let name in \[([^\]]*)\]\)", src):
    methods |= set(re.findall(r"'(\w+)'", group))

acl = json.loads(ACL.read_text(encoding='utf-8'))['dpishell']
allowed = set(acl['read']['ubus']['dpishell']) | set(acl['write']['ubus']['dpishell'])

api = API.read_text(encoding='utf-8')
params = set(re.findall(r"\b(\w+): \[", api[api.index('PARAMS'):api.index('};', api.index('PARAMS'))]))
called = set()
for f in [API, *VIEWS.glob('*.js')]:
    called |= set(re.findall(r"api\.(?:call|safe)\('(\w+)'", f.read_text(encoding='utf-8')))
    called |= set(re.findall(r"self\.act\('(\w+)'", f.read_text(encoding='utf-8')))

# Личное и сведения о защите — только группе записи (учётка LuCI «только чтение» их не видит): отчёт с адресами, связки с ПК (MAC),
# список изменений (MAC в pc:<id>), «Безопасность роутера» (пустой ли пароль root, открыт ли вход с WAN, открытый Wi-Fi).
WRITE_ONLY = {'report', 'pc_link_get', 'changes_list', 'security_check'}
read = set(acl['read']['ubus']['dpishell'])

bad = []
bad += [f'метод {m} нет в ACL ({ACL.name})' for m in sorted(methods - allowed)]
bad += [f'{m} — только в группе write (личное или сведения о защите)' for m in sorted(WRITE_ONLY & read)]
bad += [f'{m} из WRITE_ONLY нет в группе write' for m in sorted(WRITE_ONLY - set(acl['write']['ubus']['dpishell']))]
bad += [f'в ACL лишний {m} — такого метода нет' for m in sorted(allowed - methods)]
bad += [f'страница зовёт {m}, а в PARAMS api.js его нет' for m in sorted(called - params)]
for b in bad:
    print(b)
print(f'методов {len(methods)}, в ACL {len(allowed)}, зовёт страница {len(called)}; ошибок {len(bad)}')
sys.exit(1 if bad else 0)
