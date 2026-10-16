# Пакет DpiShell для роутера (OpenWRT)

ТЗ — `docs/06-router-spec.md`, договор с Windows-приложением — `docs/07-router-api.md`.

## Раскладка
- `dpishell/files/` — файлы пакета как на роутере: модули ucode (`usr/share/ucode/dpishell/`), ubus-объект (`usr/share/rpcd/ucode/dpishell`), ACL, исполнитель фоновых заданий (`usr/libexec/dpishell/run`), init-скрипт, uci по умолчанию, `roles.json`, `core.json`. Данные (`strategies.json`, `targets.json`, списки, фейки) берутся из `data/` при сборке.
- `luci-app-dpishell/` — страницы LuCI.
- `test/` — тесты ucode; `test/golden.json` — эталон сборки из `WinwsArgs.Build` (генератор — `tools/golden/`).
- `tools/vm-deploy.sh` — разложить файлы в эмулятор; `tools/vm-test.sh` — тесты в эмуляторе (`LIVE=1` — с живыми запросами).

## Эмулятор
`C:\Dev\owrt-vm`: `run.sh` (QEMU из MSYS2, OpenWRT 25.12.5 x86_64; запуск в фоне), `ssh.sh '<команда>'` (порт 8022, ключ `vmkey`), LuCI — http://127.0.0.1:8080 (root / test). Сеть QEMU `user` пересобирает TCP — действие стратегий в эмуляторе не проверяется.

## Состояние (2026-10-17)
Готово и проверено в эмуляторе: сборщик config (совпадает с Windows для 20 стратегий), ubus-объект (статус, вкл/выкл, стратегии, режим, расписание, свои адреса/списки/стратегии, вход через /ubus), проверка целей (DoH, тело, редиректы), подбор (без обхода → перебор → автоприменение + запасные; ts глазами Windows), проверка по таймеру с запасными и автоподбором.

Также готово: `core_install` — установка ядра с роутера (наш zip, основной адрес → зеркало, хеши архива и nfqws, шаги install_easy без cron), замки заданий с PID (мёртвый замок не блокирует).

Также готово: `site_check` (адрес временно в nozapret), `diag`/`report`/`conflict_stop`, DNS (анализ подмены: NXDOMAIN/служебные адреса/проверка делом; https-dns-proxy по кнопке; возврат с перезапуском dnsmasq; временный откат при недоступности DoH).

Также готово: страницы LuCI (`luci-app-dpishell`); пакеты собираются в GitHub Actions (`.github/workflows/router.yml`, SDK `openwrt/sdk:x86-64-v25.12.5`); первая сборка поставлена в эмулятор через `apk add --allow-untrusted` — объект ubus, cron, страница на месте. Найдена и исправлена ошибка порядка функций (ucode разрешает имена при компиляции) — проверка `tools/check-order.py` в тестах и в сборке; дымовой тест API — `SMOKE=1 sh router/tools/vm-test.sh`.

## Где остановился (2026-10-17, ~01:50)
- Запушен `50b4b50`: подписанная витрина пакетов на GitHub Pages (https://slavdew.github.io/dpishell/feed/). Секрет `APK_SIGNING_KEY` задан; секретный ключ лежит в профиле пользователя (`.config/dpishell/apk-signing-key.pem`), открытый — `router/keys/dpishell.pem`. Сборка этого коммита не проверена: `gh run list -R Slavdew/dpishell`; если не прошли `apk mkndx`/`apk verify` в SDK — смотреть журнал шага «Сборка и подписанный индекс».
- Следующий шаг: в эмуляторе поставить пакеты с витрины как на роутере — ключ в `/etc/apk/keys/dpishell.pem`, строка `https://slavdew.github.io/dpishell/feed/packages.adb` в `/etc/apk/repositories.d/customfeeds.list`, `apk update && apk add dpishell luci-app-dpishell` без `--allow-untrusted`; затем `ubus call dpishell core_install` и `SMOKE=1 sh router/tools/vm-test.sh`.
- Эмулятор: QEMU запущен из прошлой сессии (порты 8080/8022); если не отвечает — `sh /c/Dev/owrt-vm/run.sh` в фоне.
- Связь с Windows-сессией — напрямую через SendMessage; договор согласуем в 07 §8/§9.

Дальше: витрина и установка с неё, инструкция установки на роутер (сторонняя прошивка OpenWrt → ключ и витрина → пакеты), глубокий поиск на роутере, действия flow offload/IPv6 в API, проверка на настоящем роутере.
