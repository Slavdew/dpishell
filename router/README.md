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

Дальше: Makefile пакетов и сборка apk (OpenWRT SDK), глубокий поиск, проверка на настоящем роутере. Страницы LuCI пишет отдельный агент — его заметки: scratchpad `router/luci-notes.md`.
