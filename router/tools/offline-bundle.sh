#!/bin/sh
# Набор для установки DpiShell на роутер без интернета (запасной путь, если GitHub с роутера недоступен).
# Запуск на компьютере, где интернет работает (обход включён), из корня репозитория:
#   sh router/tools/offline-bundle.sh <папка> <feeds.tgz с роутера>
# feeds.tgz — фиды, ключи и версия прошивки самого роутера (там адрес модулей ядра под его хеш):
#   на роутере: tar -czf /tmp/feeds.tgz /etc/apk/repositories.d /etc/apk/keys /etc/openwrt_release
# Пакеты разрешает и качает apk в эмуляторе (C:\Dev\owrt-vm) в отдельном пустом корне — система эмулятора не меняется;
# подписи индексов проверяются ключами роутера и ключом DpiShell (router/keys/dpishell.pem).
# На роутер: scp -O -r <папка> root@<роутер>:/tmp/dpishell-offline && ssh root@<роутер> sh /tmp/dpishell-offline/install.sh
set -e
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${1:-}
FEEDS=${2:-}
[ -n "$OUT" ] && [ -f "$FEEDS" ] || { sed -n '3,6p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

mkdir -p "$OUT"
rm -f "$OUT"/*.apk "$OUT"/*.zip "$OUT"/SHA256SUMS "$OUT"/packages.txt
FT=$(mktemp -d)
# Через поток: GNU tar принимает «C:» в пути Windows за адрес удалённой машины.
gzip -dc < "$FEEDS" | tar -xf - -C "$FT"

# Фиды прошивки (незакомментированные строки всех списков) + витрина DpiShell; архитектура — из openwrt_release.
cat "$FT"/etc/apk/repositories.d/*.list 2>/dev/null | tr -d '\r' | grep -E '^https?://' > "$OUT/repos" || true
[ -s "$OUT/repos" ] || { echo "В $FEEDS нет фидов apk (/etc/apk/repositories.d) — роутер без apk пока не поддерживается"; exit 1; }
echo https://slavdew.github.io/dpishell/feed/packages.adb >> "$OUT/repos"
ARCH=$(sed -n "s/^DISTRIB_ARCH='\(.*\)'/\1/p" "$FT/etc/openwrt_release")
[ -n "$ARCH" ] || { echo "Не удалось узнать архитектуру роутера из $FEEDS"; exit 1; }
echo "$ARCH" > "$OUT/ARCH"

# Архив ядра zapret — с тех же адресов и с тем же хешем, что у установщика на роутере (core.json).
CJ=$ROOT/router/dpishell/files/usr/share/dpishell/core.json
SHA=$(sed -n 's/.*"zip_sha256": *"\([0-9a-f]*\)".*/\1/p' "$CJ")
ZIPNAME=$(sed -n 's#.*"zip_url": *"[^"]*/\([^"/]*\.zip\)".*#\1#p' "$CJ")
# Сначала — локальная копия релиза в vendor/ (architecture.md «Ядро и служба обхода»), потом адреса из манифеста.
[ -f "$ROOT/vendor/$ZIPNAME" ] && [ "$(sha256sum "$ROOT/vendor/$ZIPNAME" | cut -c1-64)" = "$SHA" ] && cp "$ROOT/vendor/$ZIPNAME" "$OUT/$ZIPNAME"
[ -f "$OUT/$ZIPNAME" ] || for url in $(sed -n 's#.*"\(https://[^"]*\.zip\)".*#\1#p' "$CJ"); do
	echo "Ядро: $url"
	curl -sSL --fail --retry 3 -C - -o "$OUT/$ZIPNAME" "$url" || true
	[ "$(sha256sum "$OUT/$ZIPNAME" 2>/dev/null | cut -c1-64)" = "$SHA" ] && break
	rm -f "$OUT/$ZIPNAME"
done
[ -f "$OUT/$ZIPNAME" ] || { echo "Не удалось скачать $ZIPNAME с проверенным хешем"; exit 1; }

# Пакеты с зависимостями — apk в эмуляторе, пустой корень, архитектура роутера.
KT=$(mktemp -d); cp "$FT"/etc/apk/keys/*.pem "$KT/"; cp -f "$ROOT/router/keys/dpishell.pem" "$KT/"
tar -C "$KT" -cf - . | "$VM/ssh.sh" 'R=/tmp/dpibundle; rm -rf $R; mkdir -p $R/keys $R/out $R/root/lib/apk/db $R/root/etc/apk; tar -C $R/keys -xf -'
"$VM/ssh.sh" "cat > /tmp/dpibundle/repos; echo $ARCH > /tmp/dpibundle/root/etc/apk/arch" < "$OUT/repos"
"$VM/ssh.sh" 'set -e; R=/tmp/dpibundle; touch $R/root/lib/apk/db/installed $R/root/etc/apk/world
	A="--root $R/root --arch $(cat $R/root/etc/apk/arch) --keys-dir $R/keys --repositories-file $R/repos"
	ok=; for i in 1 2 3; do apk $A update >/dev/null 2>&1 && { ok=1; break; }; sleep 2; done
	[ -n "$ok" ] || { apk $A update; exit 1; }
	ok=; for i in 1 2 3 4 5; do apk $A fetch --recursive -o $R/out dpishell luci-app-dpishell >/dev/null 2>&1 && { ok=1; break; }; sleep 3; done
	[ -n "$ok" ] || apk $A fetch --recursive -o $R/out dpishell luci-app-dpishell
	cd $R/out; for f in *.apk; do echo "$(apk adbdump "$f" | sed -n "s/^  name: //p" | head -1) $f"; done > ../packages.txt; mv ../packages.txt .'
# busybox tar понимает -C только в начале — packages.txt уже лежит рядом с пакетами.
"$VM/ssh.sh" 'tar -C /tmp/dpibundle/out -cf - .' | tar -C "$OUT" -xf -
"$VM/ssh.sh" 'rm -rf /tmp/dpibundle'

[ "$(ls "$OUT"/*.apk 2>/dev/null | wc -l)" -gt 0 ] && grep -q '^dpishell ' "$OUT/packages.txt" || { echo "Пакеты не скачались (архитектура $ARCH, фиды — см. выше)"; exit 1; }
cp "$ROOT/router/keys/dpishell.pem" "$OUT/dpishell.pem"
tr -d '\r' < "$ROOT/router/tools/offline-install.sh" > "$OUT/install.sh"
rm -rf "$OUT/repos" "$FT" "$KT"
( cd "$OUT" && sha256sum *.apk "$ZIPNAME" packages.txt ARCH dpishell.pem install.sh | sed 's/ \*/  /' > SHA256SUMS )
echo "Готово: $OUT ($(ls "$OUT"/*.apk | wc -l) пакетов, $(du -sh "$OUT" | cut -f1))"
