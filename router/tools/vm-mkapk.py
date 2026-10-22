#!/usr/bin/env python3
"""Пакеты dpishell и luci-app-dpishell для эмулятора — без SDK OpenWRT, формат apk v2 без подписи (apk 3 ставит их
с --allow-untrusted). Только для проверки обновления и удаления в эмуляторе: настоящие пакеты собирает CI (router.yml).

    python -I router/tools/vm-mkapk.py <папка-вывода> [<git-ref>]

Без ref — из рабочей папки, с ref (тег, коммит) — из git: так собирается «прежняя версия» для проверки обновления.
RELEASE_ADD=N — номер сборки больше на N (следующая версия из той же папки: проверка отката версии).
Состав — как в Makefile: файлы пакета, данные из data/ (без ipset-all.txt, если его не кладёт сборка этой версии),
keep.d; сценарии postinst/prerm/postrm — из Makefile (postinst идёт и как post-upgrade, как у SDK).
"""
import gzip, hashlib, io, os, pathlib, re, subprocess, sys, tarfile, time

ROOT = pathlib.Path(__file__).resolve().parents[2]
out = pathlib.Path(sys.argv[1])
ref = sys.argv[2] if len(sys.argv) > 2 else None
out.mkdir(parents=True, exist_ok=True)
NOW = int(time.time())


def read(path):
    if ref:
        return subprocess.check_output(['git', '-C', str(ROOT), 'show', f'{ref}:{path}'])
    return (ROOT / path).read_bytes()


def ls(prefix):
    if ref:
        r = subprocess.check_output(['git', '-C', str(ROOT), 'ls-tree', '-r', '--name-only', ref, prefix]).decode('utf-8')
        return [l for l in r.split('\n') if l]
    base = ROOT / prefix
    return sorted(p.relative_to(ROOT).as_posix() for p in base.rglob('*') if p.is_file())


def makefile(path):
    t = read(path).decode('utf-8')
    # RELEASE_ADD — к номеру сборки прибавить N: «следующая версия» из той же рабочей папки (проверка отката версии).
    rel = int(re.search(r'^PKG_RELEASE:=(\d+)', t, re.M).group(1)) + int(os.environ.get('RELEASE_ADD', '0') or 0)
    ver = re.search(r'^PKG_VERSION:=(\S+)', t, re.M).group(1)
    dep = re.search(r'^\s*DEPENDS:=(.*)$', t, re.M).group(1).replace('+', '').split()
    scripts = {}
    for name, key in (('postinst', '.post-install'), ('prerm', '.pre-deinstall'), ('postrm', '.post-deinstall')):
        m = re.search(r'define Package/[^/]+/%s\n(.*?)\nendef' % name, t, re.S)
        if m:
            scripts[key] = m.group(1).replace('$${', '${').replace('$$$$', '$$') + '\n'
            if key == '.post-install':
                scripts['.post-upgrade'] = scripts[key]
    return f'{ver}-r{rel}', dep, scripts


def tarbytes(entries, eof=True):
    bio = io.BytesIO()
    with tarfile.open(fileobj=bio, mode='w', format=tarfile.PAX_FORMAT) as tf:
        seen = set()
        for name, data, mode in entries:
            parts = name.split('/')[:-1]
            for k in range(1, len(parts) + 1):
                d = '/'.join(parts[:k])
                if d not in seen and not name.startswith('.'):
                    seen.add(d)
                    di = tarfile.TarInfo(d)
                    di.type = tarfile.DIRTYPE
                    di.mode = 0o755
                    di.uname = di.gname = 'root'
                    di.mtime = NOW
                    tf.addfile(di)
            ti = tarfile.TarInfo(name)
            ti.size = len(data)
            ti.mode = mode
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = 'root'
            ti.mtime = NOW
            if not name.startswith('.'):
                ti.pax_headers = {'APK-TOOLS.checksum.SHA1': hashlib.sha1(data).hexdigest()}
            tf.addfile(ti, io.BytesIO(data))
    z = bytes(1)
    b = bio.getvalue().rstrip(z)
    b += z * (-len(b) % 512)
    return b + (z * 1024 if eof else b'')


def gz(b):
    return gzip.compress(b, 9, mtime=0)


def build(name, files, dep, ver, scripts):
    data_gz = gz(tarbytes(files))
    size = sum(len(d) for _, d, _ in files)
    info = (f'pkgname = {name}\npkgver = {ver}\npkgdesc = {name} test build\nurl = https://example.invalid\n'
            f'builddate = {NOW}\npackager = test\nsize = {size}\narch = noarch\norigin = {name}\nlicense = MIT\n')
    for d in dep:
        info += f'depend = {d}\n'
    info += f'datahash = {hashlib.sha256(data_gz).hexdigest()}\n'
    ctl = [('.PKGINFO', info.encode('utf-8'), 0o644)]
    for k, v in scripts.items():
        ctl.append((k, v.encode('utf-8'), 0o755))
    fn = out / f'{name}-{ver}.apk'
    fn.write_bytes(gz(tarbytes(ctl, eof=False)) + data_gz)
    print(fn.name)


EXEC = ('usr/libexec/', 'etc/init.d/', 'etc/hotplug.d/')

ver, dep, scripts = makefile('router/dpishell/Makefile')
skip_ipset_all = b'rm -f $D/lists/ipset-all.txt' in read('.github/workflows/router.yml')
files = []
for p in ls('router/dpishell/files'):
    rel = p[len('router/dpishell/files/'):]
    files.append((rel, read(p), 0o755 if rel.startswith(EXEC) else 0o644))
for p in ('data/strategies.json', 'data/targets.json'):
    files.append(('usr/share/dpishell/' + os.path.basename(p), read(p), 0o644))
for p in ls('data/lists'):
    if p.endswith('.txt') and not (skip_ipset_all and p.endswith('/ipset-all.txt')):
        files.append(('usr/share/dpishell/lists/' + os.path.basename(p), read(p), 0o644))
for p in ls('data/fake'):
    if p.endswith('.bin'):
        files.append(('usr/share/dpishell/fake/' + os.path.basename(p), read(p), 0o644))
files.append(('lib/upgrade/keep.d/dpishell', b'/etc/dpishell/\n', 0o644))
build('dpishell', files, dep, ver, scripts)

ver, dep, scripts = makefile('router/luci-app-dpishell/Makefile')
files = []
for p in ls('router/luci-app-dpishell/htdocs'):
    files.append(('www/' + p[len('router/luci-app-dpishell/htdocs/'):], read(p), 0o644))
for p in ls('router/luci-app-dpishell/root'):
    files.append((p[len('router/luci-app-dpishell/root/'):], read(p), 0o644))
build('luci-app-dpishell', files, dep, ver, scripts)
