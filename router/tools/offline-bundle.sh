#!/bin/sh
# Набор для установки DpiShell на роутер без интернета (запасной путь, если GitHub с роутера недоступен).
# Запуск на компьютере, где интернет работает (обход включён), из корня репозитория:
#   sh router/tools/offline-bundle.sh [папка] [customfeeds.list с роутера]
# Без второго аргумента — фиды сторонняя прошивка OpenWrt 1.4.8 (роутер); KEYS_DIR=<папка> — ключи других фидов. Для другой прошивки взять с роутера
# /etc/apk/repositories.d/customfeeds.list (там адрес модулей ядра под её хеш) и передать путь к нему.
# Пакеты разрешает и качает apk в эмуляторе (C:\Dev\owrt-vm) в отдельном пустом корне — система эмулятора не меняется;
# подписи индексов проверяются ключами router/keys (сторонняя прошивка OpenWrt — из образа 1.4.8, DpiShell — наш).
# На роутер: scp -r <папка> root@<роутер>:/tmp/dpishell-offline && ssh root@<роутер> sh /tmp/dpishell-offline/install.sh
set -e
VM=${VM:-/c/Dev/owrt-vm}
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT=${1:-$ROOT/router/offline-bundle}
FEEDS=${2:-}
ARCH=aarch64_cortex-a73
B=https://example.org/firmware-feed

mkdir -p "$OUT"
rm -f "$OUT"/*.apk "$OUT"/*.zip "$OUT"/SHA256SUMS "$OUT"/packages.txt

# Фиды: прошивки (с роутера или 1.4.8) + витрина DpiShell.
if [ -n "$FEEDS" ]; then
	grep -E '^https?://' "$FEEDS" | tr -d '\r' > "$OUT/repos"
	# Архитектура пакетов — из адресов фидов (aarch64_cortex-a73, mipsel_24kc, x86_64…), не из targets/<цель>/<подцель>.
	a=$(grep -o -E '/(aarch64_[a-z0-9_-]+|arm_[a-z0-9_-]+|mips[a-z0-9]*_[a-z0-9_-]+|x86_64|i386_[a-z0-9_-]+)/' "$OUT/repos" | head -1 | tr -d /)
	[ -n "$a" ] && ARCH=$a
else
	{
		for d in base packages luci; do echo "$B/main-d958caf/$ARCH/$d/packages.adb"; done
		echo "$B/targets/SoC/SoC/<vermagic>/packages/packages.adb"
	} > "$OUT/repos"
fi
echo https://slavdew.github.io/dpishell/feed/packages.adb >> "$OUT/repos"
echo "$ARCH" > "$OUT/ARCH"

# Архив ядра zapret — с тех же адресов и с тем же хешем, что у установщика на роутере (core.json).
CJ=$ROOT/router/dpishell/files/usr/share/dpishell/core.json
SHA=$(sed -n 's/.*"zip_sha256": *"\([0-9a-f]*\)".*/\1/p' "$CJ")
ZIPNAME=$(sed -n 's#.*"zip_url": *"[^"]*/\([^"/]*\.zip\)".*#\1#p' "$CJ")
# Сначала — локальная копия релиза в vendor/ (02-spec §9.10), потом адреса из манифеста.
[ -f "$ROOT/vendor/$ZIPNAME" ] && [ "$(sha256sum "$ROOT/vendor/$ZIPNAME" | cut -c1-64)" = "$SHA" ] && cp "$ROOT/vendor/$ZIPNAME" "$OUT/$ZIPNAME"
[ -f "$OUT/$ZIPNAME" ] || for url in $(sed -n 's#.*"\(https://[^"]*\.zip\)".*#\1#p' "$CJ"); do
	echo "Ядро: $url"
	curl -sSL --fail --retry 3 -C - -o "$OUT/$ZIPNAME" "$url" || true
	[ "$(sha256sum "$OUT/$ZIPNAME" 2>/dev/null | cut -c1-64)" = "$SHA" ] && break
	rm -f "$OUT/$ZIPNAME"
done
[ -f "$OUT/$ZIPNAME" ] || { echo "Не удалось скачать $ZIPNAME с проверенным хешем"; exit 1; }

# Пакеты с зависимостями — apk в эмуляторе, пустой корень, архитектура роутера.
# KEYS_DIR — доп. ключи фидов (для другой прошивки — /etc/apk/keys с роутера).
KT=$(mktemp -d); cp "$ROOT/router/keys/сторонняя прошивка OpenWrt.pem" "$ROOT/router/keys/dpishell.pem" "$KT/"; [ -n "$KEYS_DIR" ] && cp "$KEYS_DIR"/*.pem "$KT/"
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
rm -f "$OUT/repos"
( cd "$OUT" && sha256sum *.apk "$ZIPNAME" packages.txt ARCH dpishell.pem install.sh | sed 's/ \*/  /' > SHA256SUMS )
echo "Готово: $OUT ($(ls "$OUT"/*.apk | wc -l) пакетов, $(du -sh "$OUT" | cut -f1))"
