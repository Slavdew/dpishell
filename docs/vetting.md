# Проверка заимствованного

Всё, что DpiShell берёт из открытых источников, проверяется до попадания в репозиторий и закрепляется хешами. Здесь — что откуда взято, как проверено и чем защищено на машине пользователя. Полные тексты лицензий — `packaging/THIRD-PARTY-NOTICES.txt`.

## Правила

- Ядро: только релиз `bol-van/zapret`; хеш zip и каждого нужного файла сверяются с `sha256sum.txt` релиза и зашиваются в манифест `data/core.json`; файлы не из списка не извлекаются.
- Стратегии Flowseal: `.bat` не исполняем и в репозиторий не кладём — из них вручную выписываются только аргументы winws, каждый сверяется с `winws --help` закреплённой версии; аргументы, запускающие внешние программы или указывающие на файлы вне наших `lists/`/`fake/`, отбрасываются. Разрешённые параметры — белый список в `Strategies.cs` (`WinwsArgs`) и `router/dpishell/files/usr/share/dpishell/roles.json`.
- Списки: только строки-домены/подсети, проверка регулярным выражением при загрузке; всё прочее отбрасывается.
- Фейки `.bin`: проверяем, что это то, чем назван файл (TLS ClientHello / QUIC Initial / STUN), и размер в разумных пределах.
- Код чужих проектов не копируем; NuGet-пакеты — только широко известные, с закреплённой версией, не моложе двух недель, с живым сопровождением и без открытых уязвимостей (раздел «Библиотеки NuGet»).
- На машине пользователя: хеши ядра проверяются перед каждым запуском; данные ставятся только при совпадении sha256 с манифестом `data/data.json`; IP-список проходит валидатор; hosts-кандидаты — белый список доменов и живая проверка сертификата.

## Ядро: bol-van/zapret v72.13

- `zapret-v72.13.zip`: sha256 `c493e33a…2e0c` совпал с хешем, который публикует GitHub для файла релиза (поле `digest` в API).
- Четыре нужных файла (`binaries/windows-x86_64/`: `winws.exe`, `WinDivert.dll`, `WinDivert64.sys`, `cygwin1.dll`) совпали с `sha256sum.txt` релиза; хеши — в `data/core.json`.
- `WinDivert.dll` и `WinDivert64.sys` побайтно совпали с официальным `basil00/WinDivert` v2.2.2-A. Драйвер подписан EV-сертификатом 成都密思听科技有限公司 (Mistiny) — спонсор подписи, указан автором WinDivert на reqrypt.org.
- Релиз собирает GitHub Actions (`.github/workflows/build.yml`): winws компилируется из исходников, `cygwin1.dll` из архивного зеркала Cygwin (2024-01-30), WinDivert качается с официального релиза.
- `winws.exe`, `WinDivert.dll`, `cygwin1.dll` без подписи — обычное дело для zapret; защищаемся закреплёнными хешами.
- Defender помечает официальный архив WinDivert как `Misleading:Win32/Malgent!AMTB` — известное ложное срабатывание на перехватчик пакетов. Приложение переживает удаление файлов антивирусом (проверка хешей + понятное сообщение, восстановление из поставки).
- Для роутера из того же архива берётся `binaries/linux-<arch>/nfqws`; `linux-arm64/nfqws` в zip побайтно тот же, что в `openwrt-embedded.tar.gz` (sha256 `75fc3d63…37a6`). Скрипты zapret (`common/`, `init.d/openwrt/`) исполняются как есть — часть того же архива; при смене версии — просмотр диффа.
- Резервная копия архива — `vendor/zapret-v72.13.zip` и форк `Slavdew/zapret` с тем же хешем (запасной адрес `mirror_urls` в `core.json`).

## Данные: Flowseal/zapret-discord-youtube @ `eb16c1419057e36b8a36fbe7c959483450e6ac8b` (MIT)

- 22 `general*.bat`: кроме вызовов `service.bat` (не берём) содержат одну команду `winws`. Все 33 параметра есть в документации zapret v72.13; пути только на `bin\*.bin` и `lists\*.txt`. В каталог (`data/strategies.json`) вошли 20: без `EXP` и `ALT5` (автор пометил NOT RECOMMENDED).
- `.bin` (16 шт.): по заголовкам — TLS ClientHello (SNI google.com, 4pda.to и др.), QUIC Initial v1, STUN с magic cookie; ни одного исполняемого. Это пакеты, которые winws отправляет в сеть как приманку, локально не исполняются. Взяты 8 используемых.
- Списки: только домены и подсети (`^домен` — синтаксис zapret «без поддоменов»). `lists/ipset-all.txt` в репозитории Flowseal — заглушка `203.0.113.113/32` (тестовая сеть); рабочий список лежит в `.service/`.
- Встроенный фейк `--dpi-desync-fake-tls=!`: в `.bat` записан как `^!` (экранирование cmd); в каталог идёт `!`.
- `bin/winws.exe` у Flowseal отличается размером от сборки bol-van — не используем, ядро только от bol-van.

### Кандидаты hosts («Починить адреса»)
- Источник: `Flowseal/.service/hosts` @ `eb16c14`, sha256 `e7dcca4f…a9a9`; совпадает с `main` (последняя правка файла — `ffec6c8` 2026-09-20). Только ASCII, 82 строки: 80 «IP домен» и 2 пустые.
- Все 56 доменов — из белого списка (github.com, `*.githubusercontent.com`, Telegram, `discord.com`, `updates.discord.com`); адреса: `140.82.121.3`, `146.75.22.132`, `2606:50c0:8000::154`, `149.154.167.220`, четыре `162.159.x` — все публичные.
- RDAP (rdap.org): `140.82.121.3` — GITHU; `146.75.22.132` — FASTLY; `149.154.167.220` — `Telegram_Messenger_Network` (MNT-TELEGRAM); `162.159.128.233` — CLOUDFLARENET (CLOUD14); `2606:50c0:8000::154`, `162.159.138.232` — по аудиту ([research/flowseal-features.md](research/flowseal-features.md) §4) GITHU и Cloudflare. На машине RDAP — вторичный признак; главное — живая проверка сертификата перед каждой записью.
- Положено своим форматом в `data/hosts/hosts-candidates.json` (поля `host`, `ips`, `owner_any_of`, `reason`, `since`, `source`), хеш — в `data/data.json`. Хеш не совпал или домен вне белого списка — функция не запускается.

### IP-список и игровой режим
- Файлы скачаны с raw.githubusercontent.com по коммиту `eb16c14` и сверены с git-хешами дерева коммита (GitHub API `git/trees`): совпали. Манифест данных — `data/data.json` (версия, у каждого файла sha256; у IP-списка ещё `url`, `lines`, `ipv4_share`, `updated`). Хеш текстовых файлов — по тексту с LF (CRLF при чтении приводится к LF, чтобы git autocrlf не ломал проверку), двоичных — побайтно.
- **IP-список** `data/lists/ipset-all.txt` = `.service/ipset-service.txt` (сборка V3nilla/IPSets-For-Bypass-in-Russia, последняя правка `249a704` 2026-09-26, PR #17737). sha256 `f4250118…21fb`, 539 879 байт, 33 048 строк: 26 038 IPv4 + 7 010 IPv6, только `адрес/длина`, без комментариев, дублей и CR. Самые широкие — `/8` (IPv4, напр. `3.0.0.0/8`) и `/24` (IPv6). Покрывает 10,32 % адресов IPv4. Пересечений с приватными/служебными сетями нет; `1.1.1.0/24` есть (Cloudflare — учтено: DoH-проверки в подборе идут и с заглушкой).
- Результат `IpsetValidator` (тот же код, что на машине; тест `RealList_PassesValidatorClean`): принят, отброшено 0, исправлено host-битов 0, дублей 0, доля IPv4 10,32 % ≤ 15 %. Независимая проверка Python `ipaddress` (strict) дала те же числа.
- **Фейк** `data/fake/ACTIVE_GAME_UDP.bin` (`bin/ACTIVE_GAME_UDP.bin`): sha256 `e065870c…a56e`, 1 250 байт; заголовок — QUIC long header Initial, версия 1, DCID 8 байт; не исполняемый (нет MZ/ELF).
- **Игровые профили**: из 20 взятых `general*.bat` перенесены строки `--filter-tcp=%GameFilterTCP%` и `--filter-udp=%GameFilterUDP%` как роли `game_tcp`/`game_udp` (фильтры и списки снимаются, их назначает `WinwsArgs.Build`). У 18 из 20 `game_tcp` = десинк general + `--dpi-desync-any-protocol=1 --dpi-desync-cutoff=nN` (+ `--dpi-desync-fake-unknown`); исключения у автора: ALT7 (`syndata` вместо `multisplit`), ALT9 (`fooling=ts` вместо `ts,md5sig`). Своё: в `fake-tls-auto` (`fooling=badseq` без смещения) добавлено `--dpi-desync-badseq-increment=0x80000000` (readme.md:349).
- Новые параметры: `--dpi-desync-fake-unknown` (файл: только `{fake}/…` или hex). Вместе с `--dpi-desync-any-protocol`, `--dpi-desync-cutoff`, `--dpi-desync-fake-unknown-udp` разрешены **только в ролях `game_*`**. Имена сверены с `nfq/nfqws.c` и `docs/readme.md` v72.13 (строки 243–261).
- Пустой include-лист в режиме «Все адреса»: по исходнику `nfq/ipset.c` v72.13 (`IpsetCheck_`, «all include lists are empty means check passes») — проверка проходит для любого адреса; поэтому в этом режиме подставляется **только** пустой `ipset-any.txt`, без `user-ipset.txt`.
- Манифест `data/data.json` перечисляет все файлы `data/` (кроме самого манифеста и `update-sources.json`) с sha256 — по нему помощник ставит обновления. Тест `PackageManifest_CoversEveryDataFile_AndPassesUpdateRules` ловит файл, не попавший в манифест, и несовпавший хеш: правишь данные — правь и `data.json` (и `version`).

## Библиотека SSH: SSH.NET 2026.0.0 (MIT)

Нужна окну для установки, удаления и обновления DpiShell на роутере без API (router-api.md §5.5): вход по паролю, `sh -s` со stdin, загрузка файлов, проброс `-R` к прокси ПК. Проверено 2026-10-17.
- Пакет NuGet `SSH.NET` (сборка `Renci.SshNet`), владельцы на nuget.org — `SSH.NET`, `drieseng`, `olegkap`, `wojtpl2` (исторические авторы и организация проекта); 345 млн загрузок. Исходники — `github.com/sshnet/SSH.NET` (4,4 тыс. звёзд, живой: последняя правка 2026-10-14); в `.nuspec` указан коммит сборки `7b2fd3db`.
- Версия 2026.0.0 — стабильная, выпущена 2026-08-09 (больше двух месяцев назад), есть сборка под `net10.0`. Ломающих изменений в заметках выпуска нет.
- Лицензия: MIT (`licenseExpression` пакета и `LICENSE` репозитория).
- Файл: `ssh.net.2026.0.0.nupkg`, sha256 `b2515de6…fecac2`; подпись репозитория nuget.org (`dotnet nuget verify` — «NuGet.org Repository by Microsoft»).
- Известные уязвимости: GHSA-q939-rpr3-3284 (CVE-2026-48798, запись файлов при рекурсивной загрузке `ScpClient`) и GHSA-mggc-4xg6-vcxf (CVE-2026-85756, выполнение команд через пути `ScpClient`) — обе «high», обе в `<= 2025.1.0`, исправлены в 2026.0.0. Мы `ScpClient` не используем вовсе (только `SshClient`, `SftpClient`, `ForwardedPortRemote`). У 2026.0.0 nuget.org уязвимостей не показывает.
- Зависимости под net10.0: `BouncyCastle.Cryptography` 2.7.0 (MIT, выпуск 2026-07-30, уязвимостей нет) и `Microsoft.Extensions.Logging.Abstractions` 8.0.3 (MIT, Microsoft).
- Как защищаемся сами: ключ роутера запоминается при первом входе (`IRouterHostKeyStore`, отпечаток SHA256 как у OpenSSH); другой ключ — отказ с текстом «Ключ роутера изменился (переустановили прошивку?)», а не молча. Сеанс SFTP принимает только тот же ключ. Сценарии передаются через stdin из ресурсов сборки (`router/tools/*.sh`, `uninstall.sh` — те же файлы, что в репозитории); загруженное (ядро, пакеты обновления) сверяется `sha256sum` на самом роутере.
- У OpenWrt по умолчанию dropbear без `sftp-server` (пакет `openssh-sftp-server`): тогда файл идёт потоком в `cat > файл` по тому же SSH, проверка хеша — та же.
## Библиотеки NuGet

### BouncyCastle.Cryptography 2.7.0 (проверка подписей ed25519)
- Зачем: в .NET 10 нет ed25519, а подписи выпусков (`release.json.sig`) и манифеста данных (`data.json.sig`) — ed25519 (`openssl pkeyutl -sign -rawin` в CI). Используется только проверка (`ReleaseSignature`), закрытого ключа в программе нет.
- Выбор: управляемый код без нативных библиотек — single-file exe не распаковывает лишнюю DLL рядом (антивирусы, права). NSec.Cryptography 26.4.0 (MIT) отклонена: тянет нативный libsodium.
- Пакет: `BouncyCastle.Cryptography` 2.7.0, опубликован на nuget.org 2026-07-30 (старше двух недель), MIT, без зависимостей (`net6.0`, `netstandard2.0`, `net461`); nuspec ссылается на `bcgit/bc-csharp`, ветка `release/v2.7`, коммит `4007498b`. sha256 `.nupkg` `f091ffcc…6241`.
- `BouncyCastle.Cryptography.dll` (5,2 МБ, версия файла 2.7.0.16391) подписан Authenticode: Legion of the Bouncy Castle Inc. (Get-AuthenticodeSignature — Valid).
- Сопровождение: репозиторий не в архиве, последние правки 2026-09, ~1,9 тыс. звёзд, выпуски 2.6.0–2.7.0 в 2025–2026. Известные уязвимости (GitHub Advisory, NuGet) — только `< 2.3.1` (бесконечный цикл на подписи/ключе, Marvin RSA, разбор сертификатов), в 2.7.0 исправлены.
- Совместимость с CI: тест `Openssl_signature_verifies` проверяет подпись, сделанную `openssl pkeyutl -rawin` (OpenSSL 3.5), тем же кодом.

## Лицензии в portable-архиве

`packaging/THIRD-PARTY-NOTICES.txt` — откуда что и полные тексты: zapret — MIT (`zapret-v72.13/docs/LICENSE.txt` из архива релиза в `vendor/`); Flowseal — MIT (`data/licenses/flowseal-MIT.txt`); WinDivert — по выбору LGPL v3 или GPL v2 (GitHub API: `NOASSERTION`, условия — reqrypt.org/windivert.html); cygwin1.dll — LGPL v3; .NET 10 (Microsoft.NETCore.App и WindowsDesktop) — MIT + THIRD-PARTY-NOTICES пакета среды; BouncyCastle.Cryptography — MIT (`LICENSE.md` пакета). Тексты GPL v2/v3 и LGPL v3 — канонические файлы FSF (sha256 LGPL v3 `a853c2ff…9d1c`, GPL v3 `8ceb4b9e…b903`, GPL v2 `8177f975…0643`). **IP-список V3nilla/IPSets-For-Bypass-in-Russia: лицензия у источника не объявлена** (GitHub API `license: null`) — перечень адресов сетей, идёт через Flowseal; отмечено в уведомлениях. DpiShell — MIT (`LICENSE`).
