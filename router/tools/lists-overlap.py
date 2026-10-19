#!/usr/bin/env python3
# Списки ролей с hostlist не пересекаются (mixed-strategies.md §1): профиль берётся по первому совпадению, и домен
# из двух списков ушёл бы не в свою роль. google.txt (YouTube) и discord.txt (Discord) — домен и его поддомены только
# в одном. general.txt с discord.txt пересекается намеренно: discord_tcp стоит раньше general и перекрывает его.
# Запуск из корня репозитория: python3 router/tools/lists-overlap.py
import sys
from pathlib import Path

PAIRS = [('google.txt', 'discord.txt')]
LISTS = Path(__file__).resolve().parents[2] / 'data' / 'lists'


def domains(name):
    out = set()
    for line in (LISTS / name).read_text(encoding='utf-8').splitlines():
        d = line.strip().lower().lstrip('^')
        if d and not d.startswith('#'):
            out.add(d)
    return out


def covers(a, b):
    """a закрывает b: тот же домен или b — поддомен a (так hostlist сравнивает имена)."""
    return b == a or b.endswith('.' + a)


bad = []
for x, y in PAIRS:
    dx, dy = domains(x), domains(y)
    for a in dx:
        for b in dy:
            if covers(a, b) or covers(b, a):
                bad.append(f'{x}: {a}  ↔  {y}: {b}')
for line in bad:
    print('пересечение:', line)
print(f'списки ролей: пересечений {len(bad)}')
sys.exit(1 if bad else 0)
