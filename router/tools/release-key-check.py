"""Ключ выпуска в пакете роутера (usr/share/dpishell/release-key.pub, формат usign) — тот же ed25519, что
packaging/keys/release-ed25519.pem (им CI подписывает release.json; роутер проверяет подпись через usign, update.uc).

Формат usign: строка «untrusted comment: …», затем base64 от «Ed» + отпечаток (8 байт) + открытый ключ (32 байта).
Отпечаток — первые 8 байт sha512 ключа (usign сверяет его лишь с подписью, а подпись роутер собирает сам).
Запуск: python router/tools/release-key-check.py [--write]
"""
import base64, hashlib, pathlib, sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
PEM = ROOT / 'packaging' / 'keys' / 'release-ed25519.pem'
PUB = ROOT / 'router' / 'dpishell' / 'files' / 'usr' / 'share' / 'dpishell' / 'release-key.pub'

sys.stdout.reconfigure(encoding='utf-8')
der = base64.b64decode(''.join(l for l in PEM.read_text(encoding='ascii').splitlines() if not l.startswith('-----')))
# SubjectPublicKeyInfo ed25519: 12 байт заголовка (302a300506032b6570032100) + 32 байта ключа.
if len(der) != 44 or der[:12] != bytes.fromhex('302a300506032b6570032100'):
    sys.exit(f'{PEM}: не открытый ключ ed25519')
key = der[12:]
want = 'untrusted comment: DpiShell release key (packaging/keys/release-ed25519.pem)\n' + \
       base64.b64encode(b'Ed' + hashlib.sha512(key).digest()[:8] + key).decode() + '\n'
if '--write' in sys.argv:
    PUB.write_text(want, encoding='ascii', newline='\n')
    print(f'записан {PUB}')
elif not PUB.exists() or PUB.read_text(encoding='ascii') != want:
    sys.exit(f'{PUB} не совпадает с {PEM} — python router/tools/release-key-check.py --write')
else:
    print('release-key-check: ключ выпуска в пакете совпадает')
