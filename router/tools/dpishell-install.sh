#!/bin/sh
# Установка DpiShell из окна ПК по SSH (router-api.md §5): ssh root@<роутер> 'sh -s' < dpishell-install.sh
# Без интернета на роутере — через туннель к прокси окна ПК (ssh -R <порт>:127.0.0.1:<порт>):
#   ssh -R 3128:127.0.0.1:3128 root@<роутер> 'https_proxy=http://127.0.0.1:3128 http_proxy=http://127.0.0.1:3128 sh -s' < dpishell-install.sh
# apk и curl (загрузка ядра) идут через этот прокси; подписи пакетов и хеш ядра проверяются как обычно.
# Архив ядра (8 МБ) GitHub у части провайдеров отдаёт очень медленно — окну лучше скачать zapret-v72.13.zip самому
# (хеш — router/dpishell/files/usr/share/dpishell/core.json) и положить по SFTP в /tmp/dpishell/core/zapret.zip
# до запуска: тогда шаг «ядро» берёт его без сети (проверено в эмуляторе: через туннель 8 МБ не успели за 5 мин).
# Шаги: ключ витрины (встроен, сверяется по sha256) → строка витрины → apk add dpishell luci-app-dpishell → ядро → status.
# Ответ — последняя строка, JSON: { schema, ok, step, message, version, api, core }. Перед ним — строки хода «# …».
KEY_SHA=e3d98812b312b8c1475f7c98c72cb850ea87c90fdcd81bcd803dad7f9f9a1543
FEED=https://slavdew.github.io/dpishell/feed/packages.adb

js() { printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | tr -d '\000-\037')"; }
done_() {
	printf '{"schema":1,"ok":%s,"step":%s,"message":%s,"version":%s,"api":%s,"core":%s}\n' \
		"$1" "$(js "$2")" "$(js "$3")" "${4:-null}" "${5:-null}" "${6:-null}"
	exit 0
}
step() { STEP=$1; echo "# $2"; }

[ -x /usr/bin/apk ] || done_ false check 'Нужен OpenWrt с apk (25.12+); сборка для opkg (24.10) — позже'

step key 'Ключ витрины DpiShell'
mkdir -p /etc/apk/keys
cat > /etc/apk/keys/dpishell.pem <<'EOF'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJbV/9TQPQ6YZ6zjRzg9z+YYlpuDc
6dMA6fLLvRLjFsfrHQ4e9gPjmkGCELSLQPb0XqVQqKgLV7nQUt9gh4b34g==
-----END PUBLIC KEY-----
EOF
[ "$(sha256sum /etc/apk/keys/dpishell.pem | cut -c1-64)" = "$KEY_SHA" ] || { rm -f /etc/apk/keys/dpishell.pem; done_ false key 'Ключ витрины не совпал по sha256 — установка отменена'; }

step feed 'Витрина пакетов'
mkdir -p /etc/apk/repositories.d
grep -qs "^$FEED" /etc/apk/repositories.d/customfeeds.list || echo "$FEED" >> /etc/apk/repositories.d/customfeeds.list

step packages 'Пакеты DpiShell и зависимости'
# Сбой постороннего фида прошивки не мешает: решает apk add.
apk update >/dev/null 2>&1
out=$(apk add dpishell luci-app-dpishell 2>&1) || done_ false packages "$(echo "$out" | tail -5)"

step core 'Ядро обхода (zapret) — загрузка и проверка хеша'
# На переднем плане: задание наследует https_proxy этого сеанса (фоновый core_install из ubus — нет).
/usr/libexec/dpishell/run core_install >/dev/null 2>&1
core=$(jsonfilter -i /tmp/dpishell/core_install.json -e '@.ok' 2>/dev/null)
[ "$core" = true ] || done_ false core "$(jsonfilter -i /tmp/dpishell/core_install.json -e '@.message' 2>/dev/null)"

step status 'Проверка'
st=$(ubus call dpishell status 2>/dev/null)
[ -n "$st" ] || { /etc/init.d/rpcd restart; sleep 2; st=$(ubus call dpishell status 2>/dev/null); }
[ -n "$st" ] || done_ false status 'DpiShell установлен, но не отвечает — перезагрузите роутер'
ver=$(echo "$st" | jsonfilter -e '@.version')
api=$(echo "$st" | jsonfilter -e '@.api')
done_ true done 'DpiShell установлен. Дальше — «Подобрать»' "$(js "$ver")" "$(js "$api")" true
