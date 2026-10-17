"""release.json пакетов роутера: версия, api, min_app, изменения, файлы с sha256 (07 §9).

Запуск в CI после сборки: python3 router/tools/release-json.py <папка с .apk> [<тег>]
Пишет <папка>/release.json. Заодно сверяет версии: PKG_VERSION обоих Makefile, VERSION в state.uc и тег
(router-vX.Y.Z) должны совпадать — иначе выход с ошибкой.
"""
import hashlib, json, pathlib, re, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
out = pathlib.Path(sys.argv[1])
tag = sys.argv[2] if len(sys.argv) > 2 else None


def mk(name, var):
    m = re.search(rf'^{var}:=(.+)$', (ROOT / 'router' / name / 'Makefile').read_text(encoding='utf-8'), re.M)
    return m.group(1).strip()


state = (ROOT / 'router/dpishell/files/usr/share/ucode/dpishell/state.uc').read_text(encoding='utf-8')
version = re.search(r"^const VERSION = '([^']+)';", state, re.M).group(1)
api = re.search(r"^const API = '([^']+)';", state, re.M).group(1)
min_app = re.search(r"^const MIN_APP = '([^']+)';", state, re.M).group(1)

errors = []
for pkg in ('dpishell', 'luci-app-dpishell'):
    if mk(pkg, 'PKG_VERSION') != version:
        errors.append(f'{pkg}/Makefile PKG_VERSION={mk(pkg, "PKG_VERSION")}, а state.uc VERSION={version}')
if not re.fullmatch(r'\d+\.\d+\.\d+', version):
    errors.append(f'версия {version} — не SemVer X.Y.Z')
if not re.fullmatch(r'\d+\.\d+', api):
    errors.append(f'api {api} — не «мажор.минор»')
if tag and tag != f'router-v{version}':
    errors.append(f'тег {tag} не совпадает с версией пакета {version}')
if errors:
    sys.exit('\n'.join(errors))


def git(*args):
    try:
        return subprocess.run(['git', '-C', str(ROOT), *args], capture_output=True, text=True, encoding='utf-8', check=True).stdout.strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return ''


# Изменения — заголовки коммитов роутерной части с прошлого тега router-v*.
prev = git('describe', '--tags', '--abbrev=0', '--match', 'router-v*', *(['HEAD^'] if tag else []))
rng = f'{prev}..HEAD' if prev else 'HEAD'
log = git('log', '--format=%s', '--no-merges', '-n', '50', rng, '--', 'router', 'data/router-releases.json')
changes = [l.removeprefix('Роутер: ') for l in log.splitlines() if l]

assets = [{'name': f.name, 'sha256': hashlib.sha256(f.read_bytes()).hexdigest()}
          for f in sorted(out.glob('*.apk'))]
doc = {
    'schema': 1, 'product': 'dpishell-router', 'version': version, 'tag': f'router-v{version}',
    'api': api, 'min_app': min_app, 'pkg': 'apk', 'changes': changes, 'assets': assets,
}
(out / 'release.json').write_text(json.dumps(doc, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(f'release.json: {version}, api {api}, файлов {len(assets)}, изменений {len(changes)}')
