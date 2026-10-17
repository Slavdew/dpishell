"""release.json программы для Windows (architecture.md «Обновление программы»): версия, тег, min_os, изменения, файлы с sha256.

Запуск в CI:
  python packaging/release-json.py check [<тег>]           — сверка версии и заметок до сборки (быстрый отказ)
  python packaging/release-json.py write <папка> [<тег>]   — после make-portable.ps1: пишет <папка>/release.json

Версия — <Version> в Directory.Build.props (единый источник). Тег (windows-vX.Y.Z) должен совпадать с ней,
для версии должна быть запись в packaging/release-notes.json (словами пользователя) — иначе выход с ошибкой.
В папке должен лежать DpiShell-<версия>-portable.zip (имя пишет make-portable.ps1 по версии exe — вторая сверка).
"""
import hashlib, json, pathlib, re, sys

# Консоль Windows-раннера — не UTF-8: без этого print кириллицы падает с UnicodeEncodeError.
for stream in (sys.stdout, sys.stderr):
    stream.reconfigure(encoding='utf-8')

ROOT = pathlib.Path(__file__).resolve().parents[1]
mode = sys.argv[1] if len(sys.argv) > 1 else ''
if mode not in ('check', 'write'):
    sys.exit(__doc__)
args = sys.argv[2:]
out = pathlib.Path(args.pop(0)) if mode == 'write' else None
tag = args[0] if args and args[0] else None

props = (ROOT / 'Directory.Build.props').read_text(encoding='utf-8')
version = re.search(r'<Version>([^<]+)</Version>', props).group(1).strip()
notes = json.loads((ROOT / 'packaging/release-notes.json').read_text(encoding='utf-8'))
changes = notes.get(version) or []
min_os = notes.get('min_os')

errors = []
if not re.fullmatch(r'\d+\.\d+\.\d+', version):
    errors.append(f'версия {version} в Directory.Build.props — не X.Y.Z')
if tag and tag != f'windows-v{version}':
    errors.append(f'тег {tag} не совпадает с версией {version} в Directory.Build.props')
if tag and not changes:
    errors.append(f'нет заметок к {version} в packaging/release-notes.json — выпуск без них не выпускаем')
if len(changes) > 15:
    errors.append(f'заметок к {version} больше 15 строк')
if min_os is not None and not re.fullmatch(r'\d+\.\d+\.\d+', min_os):
    errors.append(f'min_os {min_os} — не версия Windows X.Y.Z')
if errors:
    sys.exit('\n'.join(errors))
if mode == 'check':
    print(f'версия {version}, тег {tag or "-"}, заметок {len(changes)}')
    sys.exit(0)

zip_name = f'DpiShell-{version}-portable.zip'
if not (out / zip_name).is_file():
    found = ', '.join(p.name for p in out.glob('*.zip')) or 'нет архивов'
    sys.exit(f'нет {zip_name} в {out} ({found}) — версия exe не совпала с Directory.Build.props?')
assets = [{'name': zip_name, 'sha256': hashlib.sha256((out / zip_name).read_bytes()).hexdigest()}]
doc = {
    'schema': 1, 'product': 'dpishell-windows', 'version': version, 'tag': f'windows-v{version}',
    'min_os': min_os, 'changes': changes, 'assets': assets,
}
(out / 'release.json').write_bytes((json.dumps(doc, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))
print(f'release.json: {version}, файлов {len(assets)}, изменений {len(changes)}, sha256 {assets[0]["sha256"]}')
