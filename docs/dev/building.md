# Сборка, тесты, публикация

## Windows-приложение

- Требуется .NET 10 SDK. `dotnet build DpiShell.sln` — должно быть 0 предупреждений, 0 ошибок; `dotnet test tests/DpiShell.Tests` — все тесты зелёные.
- Тесты не запускают winws, службы и UAC: служба/реестр/сеть подменяются; окно проверяется в памяти (`WindowSmokeTests`: привязки, кнопки «(i)», снимки по переменной `DPISHELL_SNAPSHOTS`); роутер — `FakeRouter` по [router-api.md](router-api.md) §7.
- Правила, которые ловят тесты:
  - изменил файл в `data/` — обнови sha256 и `version` в `data/data.json` (`PackageManifest_CoversEveryDataFile_AndPassesUpdateRules`); хеш текстовых файлов — по тексту с LF;
  - тексты кнопок «(i)» — дословно из [../info-texts.md](../info-texts.md) (`InfoButtons_EveryDocSection_HasButton_WithDocText`);
  - белый список команд помощника — в [helper-protocol.md](helper-protocol.md) §3 (`Whitelist_matches_spec`);
  - каталог стратегий — только параметры из белого списка `WinwsArgs`; `^!` из `.bat` = `!` в winws.
- Portable-архив: `powershell -ExecutionPolicy Bypass -File packaging\make-portable.ps1` из корня — `dotnet publish` (Release, win-x64, self-contained, single-file), `data\` рядом с exe с проверкой по `data.json`, `README.txt`, `THIRD-PARTY-NOTICES.txt`, `LICENSE.txt` → `publish\DpiShell-<версия>-portable.zip` + sha256. Установщика и подписи exe нет; `publish\` в git не кладётся.
- Отладочная консоль — `src/DpiShell.Cli` (`dotnet run --project src/DpiShell.Cli -- …`), она же генерирует эталоны для роутера (`router/tools/golden`).
- Подписи `sc`/`netsh` переводятся на язык Windows — в коде ищется непереводимое (`RUNNING`, `RFC 1323`, `enabled`).

## Пакет для роутера

- Исходники — `router/dpishell/`, `router/luci-app-dpishell/`; данные (`strategies.json`, `targets.json`, списки, фейки) берутся из `data/` при сборке. Своих бинарников нет (`PKGARCH:=all`).
- Сборка — GitHub Actions `.github/workflows/router.yml`: контейнер `openwrt/sdk:x86-64-v25.12.5`, проверка порядка объявлений функций ucode (`router/tools/check-order.py` — ucode разрешает имена при компиляции), пакеты в дерево SDK, `apk mkndx --sign` своим ключом, артефакт `router-packages` и подписанный фид на GitHub Pages (`https://slavdew.github.io/dpishell/feed/`, ключ `dpishell.pem`). Пользователь один раз кладёт ключ в `/etc/apk/keys/` и ставит без `--allow-untrusted` — [../router/README.md](../router/README.md).
- Тесты ucode — `router/test/*.uc`: сборщик config (`golden.json` — тот же набор, что тесты `WinwsArgs`: одна стратегия → одинаковые профили), генератор глубокого поиска (`golden-search.json` — эталон `CandidateGenerator` ПК), проверка цели на записанных ответах curl, оценка подбора с контрольной группой. Эталоны обновляются консолью ПК (`dotnet run -- search` в `router/tools/golden`).
- Эмулятор: QEMU с образом OpenWrt 25.12 x86_64. Скрипты ожидают каталог эмулятора с `run.sh` и `ssh.sh` (переменная `VM`, по умолчанию `/c/Dev/owrt-vm`): `sh router/tools/vm-test.sh` — тесты ucode в эмуляторе (`LIVE=1` — с живыми запросами, `SMOKE=1` — дымовой тест API `router/test/api_smoke.sh`); `vm-deploy.sh`, `vm-deploy-luci.sh` — разложить файлы пакета и страницы. Сеть QEMU `user` пересобирает TCP — действие стратегий в эмуляторе не проверяется, только логика; для подбора есть имитатор DPI (`/tmp/dpishell/fake-dpi.json`, см. [architecture.md](architecture.md)).
- Грабли ucode: целые делятся нацело (`1.0 *` для долей); busybox `sleep` не понимает дробей — `sleep(мс)` ucode; после `zapret restart` nfqws поднимается асинхронно (1–2 с).
- Офлайн-набор для роутера без доступа к GitHub: `sh router/tools/offline-bundle.sh <папка> feeds.tgz` (нужен эмулятор) собирает пакеты DpiShell, недостающие пакеты прошивки (в т. ч. `kmod-nft-queue` под ядро роутера), архив ядра и `install.sh` (`router/tools/offline-install.sh`); подписи пакетов проверяются ключами самого роутера. Порядок для пользователя — [../router/README.md](../router/README.md).

## Публикация

- Один репозиторий, две независимые ленты релизов: `windows-vX.Y.Z` (portable-архив) и `router-vX.Y.Z` (пакеты и офлайн-набор), у каждой своя страница установки. Мост — [договор](router-api.md) с номером `api`: при расхождении приложение и роутер вежливо просят обновить другую сторону.
- **Выпуск программы для Windows**: поднять `<Version>` в `Directory.Build.props`, записать «что нового» для этой версии в `packaging/release-notes.json` (без записи тег не выпускается; тест `CurrentVersion_FromBuildProps` требует запись для текущей версии), закоммитить и поставить тег `windows-vX.Y.Z`. `.github/workflows/windows.yml`: сверка тега с версией (`packaging/release-json.py check`) → `dotnet build -warnaserror` и тесты на windows-latest → `make-portable.ps1` (имя архива — по версии exe, вторая сверка) → `release.json` (`release-json.py write`) → на ubuntu подпись `release.json.sig` (без секрета — отказ: неподписанный выпуск программа не примет), проверка подписи и sha256 → релиз GitHub как «последний». Релизы роутера публикуются с `--latest=false`, чтобы `releases/latest/download/` всегда вёл на выпуск программы. Запуск вручную (workflow_dispatch) — только сборка и артефакт.
- Обновления данных приложение берёт из `data/` по адресам из `data/update-sources.json` (зеркало — ещё одна строка): первый — копия на GitHub Pages с подписью манифеста, запасной — `raw.githubusercontent.com` без подписи.

### Подписи

- Описания выпусков (`release.json` обеих лент) и манифест данных (`data.json`) подписывает CI: `openssl pkeyutl -sign -rawin` ключом ed25519 из секрета репозитория `RELEASE_SIGNING_KEY` → рядом файл `<имя>.sig` — base64 подписи над **точными байтами** файла (без канонизации JSON: перевод строки в конце — тоже часть подписи).
- Проверочный ключ зашит в программу (`ReleaseSignature.PublicKeyPem`, `src/DpiShell.Core/ReleaseSignature.cs`) и лежит в `packaging/keys/release-ed25519.pem`; тест `Embedded_key_matches_repository_file` сверяет их, CI перед подписью сверяет файл с секретом. **Ключ меняется только вместе с секретом** — и это ломает проверку у всех вышедших программ: они перестанут принимать новые выпуски и данные (выпуски, подписанные старым ключом, нужно оставить доступными, пока программы не обновятся).
- **Подпись данных**: job `site` в `.github/workflows/router.yml` (master, при изменении `data/**`, `router/**`, `packaging/keys/**`) кладёт на Pages фид роутера (`/feed/`) и копию `data/` (`/data/`), подписывает `site/data/data.json` → `data.json.sig`, сверяет ключ секрета с `packaging/keys/release-ed25519.pem` и проверяет подпись `openssl pkeyutl -verify`. Почему так: Pages — один сайт на репозиторий и выкладывается целиком одной сборкой, поэтому манифест, файлы и подпись всегда из одного коммита (у raw.githubusercontent.com файлы кэшируются по отдельности, а коммит подписи обратно в master — лишний коммит на каждую правку и гонка). Без секрета данные выкладываются без подписи с предупреждением. Программа без подписи данные не принимает (обновление отклоняется), поэтому секрет `RELEASE_SIGNING_KEY` обязателен для рабочих обновлений данных. Подпись в репозиторий не кладётся.
- Закрытый ключ — только в секрете: `openssl genpkey -algorithm ed25519 -out key.pem` → содержимое в `RELEASE_SIGNING_KEY`, открытый (`openssl pkey -in key.pem -pubout`) — в `packaging/keys/release-ed25519.pem` и в `ReleaseSignature.PublicKeyPem`.
- Лицензии сторонних компонентов и их тексты — `packaging/THIRD-PARTY-NOTICES.txt`; откуда что взято — [../vetting.md](../vetting.md).

## Для участников

- Документы: пользователям — `docs/windows/`, `docs/router/`; разработчикам — `docs/dev/`; выводы исследований — `docs/research/`. В репозиторий не попадают сведения о конкретных сетях и устройствах (адреса, модели роутеров, результаты проверок у конкретного провайдера); в примерах адрес роутера — `192.168.1.1`.
- Договор с роутером меняется только с записью в [router-api.md](router-api.md); формат команд помощника — в [helper-protocol.md](helper-protocol.md).
- Код и тексты интерфейса — по-русски; тексты «(i)» — 3–6 коротких предложений без терминов.
- Файлы с обратным слешем в строках править редактором, не потоковыми утилитами с экранированием; не править через PowerShell `Get-Content`/`Set-Content` (портит кодировку).
