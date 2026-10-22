#!/usr/bin/env python3
"""Языковые файлы страницы LuCI: dpishell/lang/{ru,en}.json (docs/dev/architecture.md «Язык страниц»).

Сборка (по умолчанию): общие с окном ПК ключи (luci-app-dpishell/lang-src/shared-keys.txt) — тексты из языковых файлов
ПК src/DpiShell.Core/Lang/<язык>/*.json; ключа там ещё нет — остаётся прежний текст из собранного файла.
Свои строки web.* — из lang-src/web.json ([ru, en]).

--check (CI): собранные файлы совпадают со сборкой; каждый ключ из кода страниц есть в ru и en; подстановки в ru и en
одинаковые; общий ключ, который есть у ПК, совпадает с текстом ПК.
Запуск: python -I router/tools/lang-build.py [--check] (из корня репозитория или откуда угодно).
"""
import glob, json, os, re, sys

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
APP = os.path.join(ROOT, 'router', 'luci-app-dpishell')
RES = os.path.join(APP, 'htdocs', 'luci-static', 'resources')
OUT = os.path.join(RES, 'dpishell', 'lang')
LANGS = {'ru': {'language': 'Русский', 'code': 'ru', 'flag': 'ru'},
         'en': {'language': 'English', 'code': 'en', 'flag': 'gb'}}
PREFIXES = ('web', 'router', 'home', 'select', 'sites', 'dns', 'diag', 'about', 'common')
# Ключи, собираемые в коде из частей: 'router.schedule.opt.' + h[1] и т. п.
DYNAMIC = (['router.schedule.opt.' + x for x in ('30m', '1h', '3h', '6h', '12h', '24h')] +
           ['web.err.ubus'] + ['web.err.ubus_%d' % i for i in (2, 4, 5, 6, 7)] + ['web.dns.cons%d' % i for i in range(1, 7)] +
           ['router.dns.job.' + x for x in ('dns_analyze', 'dns_enable', 'dns_restore', 'dns_set')] +
           ['router.update.step.' + x for x in ('download', 'install', 'restart')] +
           ['web.xfer.sec.' + x for x in ('sites', 'optimize', 'exclude', 'strategies', 'advanced', 'subnets', 'dns', 'mode', 'apps', 'hosts')])


def read_json(p):
    with open(p, encoding='utf-8-sig') as f:
        return json.load(f)


def pc_texts(lang):
    d = {}
    for p in sorted(glob.glob(os.path.join(ROOT, 'src', 'DpiShell.Core', 'Lang', lang, '*.json'))):
        d.update({k: v for k, v in read_json(p).items() if not k.startswith('_')})
    return d


def used_keys():
    keys = set(DYNAMIC)
    # Ключи сообщений роутера (key в warnings, ошибках, ходе подбора) — тоже в словарь: их переводят страница и окно ПК.
    back = os.path.join(ROOT, 'router', 'dpishell', 'files')
    files = (glob.glob(os.path.join(RES, 'view', 'dpishell', '*.js')) + glob.glob(os.path.join(RES, 'dpishell', '*.js')) +
             glob.glob(os.path.join(back, 'usr', 'share', 'ucode', 'dpishell', '*.uc')) +
             [os.path.join(back, 'usr', 'share', 'rpcd', 'ucode', 'dpishell'), os.path.join(back, 'usr', 'libexec', 'dpishell', 'run')])
    for p in files:
        s = open(p, encoding='utf-8').read()
        for k in re.findall(r"'((?:%s)\.[\w.]+)'" % '|'.join(PREFIXES), s):
            # Начало составного ключа ('web.dns.cons' + i) — не ключ.
            # Имена файлов пакета настроек ('dns.json', 'sites.txt') — не ключи.
            if k.endswith(('.json', '.txt')):
                continue
            if not k.endswith('.') and not k.endswith('_') and not any(d != k and d.startswith(k) for d in DYNAMIC):
                keys.add(k)
    return keys


def holes(v):
    text = ' '.join(v.values()) if isinstance(v, dict) else str(v)
    return sorted(set(re.findall(r'\{\w+\}', text)))


def build():
    shared = [l.strip() for l in open(os.path.join(APP, 'lang-src', 'shared-keys.txt'), encoding='utf-8')
              if l.strip() and not l.startswith('#')]
    web = {k: v for k, v in read_json(os.path.join(APP, 'lang-src', 'web.json')).items() if not k.startswith('_')}
    out = {}
    for i, lang in enumerate(('ru', 'en')):
        prev_p = os.path.join(OUT, lang + '.json')
        prev = read_json(prev_p) if os.path.exists(prev_p) else {}
        pc = pc_texts(lang)
        d = {'_meta': dict(LANGS[lang], author='DpiShell', version=1)}
        for k in sorted(shared):
            v = pc.get(k, prev.get(k))
            if v is None:
                raise SystemExit(f'{lang}: общий ключ {k} — нет ни у ПК, ни в прежнем {lang}.json')
            d[k] = v
        for k in sorted(web):
            d[k] = web[k][i]
        out[lang] = d
    return out, shared


def dump(d):
    return json.dumps(d, ensure_ascii=False, indent=1) + '\n'


def main():
    check = '--check' in sys.argv
    out, shared = build()
    errors = []
    keys = used_keys()
    for lang, d in out.items():
        miss = sorted(k for k in keys if k not in d)
        if miss:
            errors.append(f'{lang}: нет ключей: ' + ', '.join(miss))
    for k in out['ru']:
        if k.startswith('_'):
            continue
        if holes(out['ru'][k]) != holes(out['en'].get(k, '')):
            errors.append(f'подстановки различаются: {k}: ru {holes(out["ru"][k])}, en {holes(out["en"].get(k, ""))}')
    for lang in out:
        pc = pc_texts(lang)
        for k in shared:
            if k in pc and pc[k] != out[lang][k]:
                errors.append(f'{lang}: {k} не совпадает с текстом ПК')
    # router.security.<id>[.<detail>] — ключи по ответу security_check (id и detail от роутера), в коде страницы не буквами.
    unused = sorted(k for k in out['ru'] if not k.startswith('_') and k not in keys and not k.startswith(('web.dns.preset.', 'web.dns.note.', 'router.security.')))
    if unused:
        print('не используются в коде (можно убрать):', ', '.join(unused))
    if check:
        for lang, d in out.items():
            p = os.path.join(OUT, lang + '.json')
            if not os.path.exists(p) or open(p, encoding='utf-8').read() != dump(d):
                errors.append(f'{lang}.json не собран заново: python -I router/tools/lang-build.py')
    else:
        os.makedirs(OUT, exist_ok=True)
        for lang, d in out.items():
            with open(os.path.join(OUT, lang + '.json'), 'w', encoding='utf-8', newline='\n') as f:
                f.write(dump(d))
    if errors:
        print('\n'.join(errors))
        sys.exit(1)
    print('язык: ок,', len(keys), 'ключей в коде,', len(out['ru']) - 1, 'в словаре')


main()
