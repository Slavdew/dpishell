"""Установка DpiShell через веб-вход роутера (логин и пароль, как в браузере, без SSH) — эталон для окна ПК.

    python router/tools/web-install.py http://192.168.1.1 <файл с паролем root> [<zapret-v72.13.zip>]

Только штатные права LuCI для root (router-api.md §5.5):
  1. session.login                                   — вход (JSON-RPC /ubus)
  2. file.write /etc/apk/keys/dpishell.pem           — ключ витрины (luci-mod-system-repokeys), sha256 сверяется
  3. file.read/write /etc/apk/repositories.d/customfeeds.list — строка витрины (luci-app-package-manager)
  4. file.exec /usr/libexec/package-manager-call update | install dpishell luci-app-dpishell
  5. вход заново (пакет перезапускает rpcd) и ожидание объекта dpishell
  6. архив ядра (если дан): cgi-upload в /tmp/dpishell-core.zip (право — в ACL пакета), иначе роутер качает сам
  7. dpishell.core_install, ожидание, dpishell.status
Пароль читается из файла и нигде не выводится. Роутеру нужен доступ к GitHub для пакетов (туннель через ПК — только SSH).
"""
import hashlib, json, pathlib, sys, time, urllib.request, uuid

ROOT = pathlib.Path(__file__).resolve().parents[2]
FEED = 'https://slavdew.github.io/dpishell/feed/packages.adb'
KEY = (ROOT / 'router/keys/dpishell.pem').read_bytes().replace(b'\r\n', b'\n').decode()
KEY_SHA = 'e3d98812b312b8c1475f7c98c72cb850ea87c90fdcd81bcd803dad7f9f9a1543'
NO_SESSION = '00000000000000000000000000000000'

base = sys.argv[1].rstrip('/')
password = pathlib.Path(sys.argv[2]).read_text(encoding='utf-8').strip()
zip_path = pathlib.Path(sys.argv[3]) if len(sys.argv) > 3 else None
step = lambda s: print(f'# {s}', flush=True)


def rpc(sid, obj, method, args=None, timeout=120):
    body = json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'call', 'params': [sid, obj, method, args or {}]}).encode()
    req = urllib.request.Request(f'{base}/ubus', body, {'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        res = json.load(r)
    if 'error' in res:
        raise RuntimeError(f'{obj}.{method}: {res["error"].get("message")}')
    code, *data = res['result']
    if code != 0:
        raise RuntimeError(f'{obj}.{method}: код ubus {code}')
    return data[0] if data else {}


def login():
    return rpc(NO_SESSION, 'session', 'login', {'username': 'root', 'password': password})['ubus_rpc_session']


def done(ok, msg, **extra):
    print(json.dumps({'schema': 1, 'ok': ok, 'message': msg, **extra}, ensure_ascii=False))
    sys.exit(0 if ok else 1)


step('Вход')
try:
    sid = login()
except Exception as e:
    done(False, f'Не удалось войти: {e}')

step('Ключ витрины')
if hashlib.sha256(KEY.encode()).hexdigest() != KEY_SHA:
    done(False, 'Ключ в программе не совпал по sha256')
rpc(sid, 'file', 'write', {'path': '/etc/apk/keys/dpishell.pem', 'data': KEY})

step('Строка витрины')
lst = '/etc/apk/repositories.d/customfeeds.list'
try:
    cur = rpc(sid, 'file', 'read', {'path': lst}).get('data', '')
except RuntimeError:
    cur = ''
if FEED not in cur:
    rpc(sid, 'file', 'write', {'path': lst, 'data': (cur if cur.endswith('\n') or not cur else cur + '\n') + FEED + '\n'})

step('Пакеты DpiShell (менеджер пакетов LuCI)')
pm = '/usr/libexec/package-manager-call'


def pm_call(*args):
    """Код ubus 7 — тайм-аут rpcd (30 с): команда продолжает выполняться на роутере. None — «ещё идёт»."""
    try:
        r = rpc(sid, 'file', 'exec', {'command': pm, 'params': list(args)}, timeout=60)
    except RuntimeError as e:
        if 'код ubus 7' in str(e):
            return None
        raise
    except (TimeoutError, OSError, ValueError):
        # Postinst пакета перезапускает rpcd — ответ на install обрывается; установка идёт дальше.
        return None
    out = r.get('stdout') or ''
    return json.loads(out) if out.startswith('{') else {'code': r.get('code', 0), 'stderr': r.get('stderr', '')}


pm_call('update')
installed_in_background = False
for attempt in range(60):
    res = pm_call('install', 'dpishell', 'luci-app-dpishell')
    if res is None:
        installed_in_background = True   # идёт дольше 30 с — дождёмся объекта dpishell ниже
        break
    if res.get('code') == 0:
        break
    if 'lock' in (res.get('stderr') or '').lower():   # база apk занята (update ещё идёт)
        time.sleep(5)
        continue
    done(False, 'Не удалось поставить пакеты: ' + (res.get('stderr') or '')[-400:])
else:
    done(False, 'Менеджер пакетов занят дольше 5 минут — повторите позже')

step('Ожидание объекта dpishell (rpcd перезапускается)')
st = None
for _ in range(150 if installed_in_background else 30):
    time.sleep(2)
    try:
        sid = login()
        st = rpc(sid, 'dpishell', 'status')
        break
    except Exception:
        continue
if not st:
    done(False, 'DpiShell установлен, но не отвечает — перезагрузите роутер')

if zip_path:
    step('Загрузка архива ядра')
    boundary = uuid.uuid4().hex
    parts = []
    for name, val in (('sessionid', sid), ('filename', '/tmp/dpishell-core.zip'), ('filemode', '0600')):
        parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{val}\r\n'.encode())
    parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="filedata"; filename="zapret.zip"\r\n'
                 f'Content-Type: application/octet-stream\r\n\r\n'.encode() + zip_path.read_bytes() + b'\r\n')
    parts.append(f'--{boundary}--\r\n'.encode())
    req = urllib.request.Request(f'{base}/cgi-bin/cgi-upload', b''.join(parts), {'Content-Type': f'multipart/form-data; boundary={boundary}'})
    with urllib.request.urlopen(req, timeout=300) as resp:
        up = json.load(resp)
    if 'failure' in up or not up.get('size'):
        done(False, f'Архив ядра не загрузился: {up}')

step('Ядро обхода')
rpc(sid, 'dpishell', 'core_install')
for _ in range(400):
    time.sleep(3)
    st = rpc(sid, 'dpishell', 'status')
    if not st['jobs'].get('core_install'):
        break
if not st['core'].get('installed'):
    done(False, 'Ядро не установилось — см. «Диагностика»')
done(True, 'DpiShell установлен. Дальше — «Подобрать»', version=st.get('version'), api=st.get('api'), core=True)
