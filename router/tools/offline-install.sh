#!/bin/sh
# Установка DpiShell на роутер без интернета — из набора, собранного на компьютере (tools/offline-bundle.sh).
# Запуск на роутере: sh /tmp/dpishell-offline/install.sh
# Ставит DpiShell и только те зависимости, которых нет в прошивке; кладёт архив ядра туда,
# откуда «Загрузить ядро» берёт его без сети, и ставит ядро.
set -e
D=$(cd "$(dirname "$0")" && pwd)
cd "$D"

echo 'Проверка файлов набора…'
sha256sum -c SHA256SUMS >/dev/null 2>&1 || { sha256sum -c SHA256SUMS | grep -v ': OK$'; echo 'Файлы повреждены — скопируйте набор заново.'; exit 1; }

arch=$(sed -n "s/^DISTRIB_ARCH='\(.*\)'/\1/p" /etc/openwrt_release)
[ "$arch" = "$(cat ARCH)" ] || { echo "Набор собран для $(cat ARCH), а роутер — $arch. Соберите набор для этого роутера."; exit 1; }

file_of() { awk -v n="$1" '$1 == n { print $2 }' packages.txt; }

# Ядро: модули из набора (kmod-nft-queue…) загрузятся только в то ядро, под которое собраны, — версия и хеш
# конфигурации (vermagic) пакета kernel из набора должны совпасть с установленным. Нет записи в базе apk — по uname -r.
want=$(file_of kernel | sed 's/^kernel-//; s/\.apk$//')
have=$(awk '/^P:kernel$/ { f = 1; next } f && /^V:/ { print substr($0, 3); exit } /^$/ { f = 0 }' /lib/apk/db/installed 2>/dev/null)
if [ -n "$want" ]; then
	# Без $(… && …): под set -e ложное условие в подстановке молча обрывает сценарий.
	ok=
	if [ -n "$have" ]; then
		[ "$want" = "$have" ] && ok=1
	else
		have=$(uname -r)
		[ "${want%%~*}" = "$have" ] && ok=1
	fi
	[ -n "$ok" ] || {
		echo "Набор собран для ядра ${want%%-r[0-9]*}, а на роутере — ${have%%-r[0-9]*}."
		echo 'Модули ядра из набора здесь не загрузятся, обход не заработает. Ничего не установлено.'
		echo 'Соберите набор под эту прошивку: скопируйте с роутера /etc/apk/repositories.d/customfeeds.list'
		echo 'и на компьютере: sh router/tools/offline-bundle.sh router/offline-bundle customfeeds.list'
		exit 1
	}
fi

# Что ставить, решает apk роутера: начинаем с DpiShell и добавляем из набора то, чего ему не хватает.
# Набор собран в пустом корне — в нём есть и то, что в прошивке уже стоит или заменено (nftables-json/-nojson).
set -- "$D/$(file_of dpishell)" "$D/$(file_of luci-app-dpishell)"
i=0
while ! out=$(apk add --simulate --no-network --allow-untrusted "$@" 2>&1); do
	# Нехватку apk называет двумя способами: «имя (no such package)» — индексов нет совсем,
	# «имя-версия:» + «masked in: --no-network» — пакет известен по старому индексу, но сети нет.
	miss=$(echo "$out" | awk '
		/^  [^ ]+ \(no such package\):/ { print $1; next }
		/^  [^ ]+:$/ { c = $1; sub(/:$/, "", c); next }
		/masked in: --no-network/ && c != "" { print c; c = "" }' | sort -u)
	if [ -z "$miss" ] || [ $i -ge 15 ]; then echo "$out"; exit 1; fi
	for n in $miss; do
		# Сначала точный файл «имя-версия.apk», потом — по имени пакета (версия в наборе может быть другой).
		if [ -f "$D/$n.apk" ]; then
			f="$n.apk"
		else
			f=$(file_of "$n")
			[ -n "$f" ] || f=$(file_of "$(echo "$n" | sed -E 's/-[0-9][^-]*-r[0-9]+$//')")
		fi
		[ -n "$f" ] || { echo "$out"; echo "Пакета $n нет в наборе — соберите набор для этой прошивки."; exit 1; }
		case " $* " in *" $D/$f "*) echo "$out"; exit 1 ;; esac
		set -- "$@" "$D/$f"
	done
	i=$((i + 1))
done
echo "Установка пакетов: $#"
# Пакеты из набора проверены при сборке по подписанным индексам (прошивки и DpiShell) и здесь — по SHA256SUMS.
apk add --no-network --allow-untrusted "$@"

# Ключ и адрес витрины — для будущих обновлений через apk, когда GitHub доступен.
cp dpishell.pem /etc/apk/keys/dpishell.pem
grep -qs slavdew.github.io /etc/apk/repositories.d/customfeeds.list ||
	echo https://slavdew.github.io/dpishell/feed/packages.adb >> /etc/apk/repositories.d/customfeeds.list

# Ядро: установщик берёт /tmp/dpishell/core/zapret.zip, если хеш совпадает с манифестом пакета, и ничего не скачивает.
mkdir -p /tmp/dpishell/core
chmod 700 /tmp/dpishell /tmp/dpishell/core
cp zapret-*.zip /tmp/dpishell/core/zapret.zip
echo 'Установка ядра…'
ubus call dpishell core_install >/dev/null
i=0
while [ $i -lt 60 ]; do
	sleep 2
	i=$((i + 1))
	grep -qs '"running": false' /tmp/dpishell/core_install.json && break
done
cat /tmp/dpishell/core_install.json
echo
echo 'Дальше — страница http://<адрес роутера>/dpi : «Подобрать».'
