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
print(f'{len(files)} файлов, нарушений: {bad}')
sys.exit(1 if bad else 0)
