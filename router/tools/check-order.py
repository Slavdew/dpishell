"""Ищет в файлах ucode вызовы функций, объявленных ниже по файлу, и деление целых без 1.0 *.

ucode разрешает имена при компиляции: функция, объявленная после места использования, — «undeclared variable»
во время выполнения (так упал write_status_json → iso). Запуск: python router/tools/check-order.py
"""
import pathlib, re, sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
files = [p for p in ROOT.rglob('*') if p.is_file() and (p.suffix == '.uc' or p.parent.name in ('ucode',) or p.name == 'run')
         and 'luci-app' not in p.parts]
bad = 0
for f in files:
    text = f.read_text(encoding='utf-8')
    lines = text.split('\n')
    defs = {}
    for i, l in enumerate(lines):
        m = re.match(r'^(?:export\s+)?function\s+([A-Za-z_]\w*)\s*\(', l)
        if m:
            defs.setdefault(m.group(1), i)
    for i, l in enumerate(lines):
        code = l.split('//')[0]
        for name, at in defs.items():
            if at > i and re.search(r'(?<![\w.])' + name + r'\s*\(', code) and not re.match(r'^\s*(export\s+)?function\s+' + name, code):
                print(f'{f.relative_to(ROOT)}:{i + 1}: {name}() используется до объявления (строка {at + 1})')
                bad += 1
    # В ucode деление целых — целое (2/3 == 0): доли считать как 1.0 * a / b (так обнулялись доли групп при подборе).
    for i, l in enumerate(lines):
        code = l.split('//')[0]
        if re.search(r'\s/\s', code) and '1.0 *' not in code and not re.search(r'int\([^)]*/ \d+\)', code):
            print(f'{f.relative_to(ROOT)}:{i + 1}: деление без 1.0 * — в ucode целые делятся нацело')
            bad += 1
# Управляющие байты и голый CR в текстах router/ — след правки через heredoc, где «\1», «\r» превратились в символы
# (2026-10-17: так сломались sed и tr в offline-bundle.sh). Такие правки делать инструментом Edit/Write.
TEXT = ('.uc', '.sh', '.js', '.json', '.md', '.py', '.pem', '')
for f in ROOT.rglob('*'):
    if not f.is_file() or f.suffix not in TEXT or {'offline-bundle', 'bin', 'obj'} & set(f.parts):
        continue
    data = f.read_bytes()
    for i, line in enumerate(data.split(b'\n')):
        if re.search(rb'[\x00-\x08\x0b-\x1f]', line.rstrip(b'\r').replace(b'\t', b'')):
            print(f'{f.relative_to(ROOT)}:{i + 1}: управляющий символ — потерянный обратный слеш?')
            bad += 1
# Ключ витрины, встроенный в установщик окна ПК, — тот же, что в router/keys (иначе установка откажет по sha256).
key = (ROOT / 'keys/dpishell.pem').read_text(encoding='utf-8').strip()
inst = (ROOT / 'tools/dpishell-install.sh').read_text(encoding='utf-8')
if key not in inst:
    print('tools/dpishell-install.sh: встроенный ключ не совпадает с keys/dpishell.pem')
    bad += 1
import hashlib
sha = hashlib.sha256((ROOT / 'keys/dpishell.pem').read_bytes().replace(b'\r\n', b'\n')).hexdigest()
if f'KEY_SHA={sha}' not in inst:
    print(f'tools/dpishell-install.sh: KEY_SHA не равен sha256 keys/dpishell.pem ({sha})')
    bad += 1
print(f'{len(files)} файлов, нарушений: {bad}')
sys.exit(1 if bad else 0)
