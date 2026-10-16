# Проверка заимствованного (2026-10-16)

## Ядро: bol-van/zapret v72.13
- `zapret-v72.13.zip`: sha256 `c493e33a…2e0c` совпал с хешем, который публикует GitHub для файла релиза (поле `digest` в API).
- Четыре нужных файла (`binaries/windows-x86_64/`) совпали с `sha256sum.txt` релиза; хеши — в `data/core.json`.
- `WinDivert.dll` и `WinDivert64.sys` побайтно совпали с официальным `basil00/WinDivert` v2.2.2-A (проверено в памяти, без записи на диск). Драйвер подписан EV-сертификатом 成都密思听科技有限公司 (Mistiny) — спонсор подписи, указан автором WinDivert на reqrypt.org.
- Релиз собирает GitHub Actions (`.github/workflows/build.yml`): winws компилируется из исходников, `cygwin1.dll` из архивного зеркала Cygwin (2024-01-30), WinDivert качается с официального релиза.
- `winws.exe`, `WinDivert.dll`, `cygwin1.dll` без подписи — обычное дело для zapret; защищаемся закреплёнными хешами.
- Defender удаляет официальный архив WinDivert (`Misleading:Win32/Malgent!AMTB`) — известное ложное срабатывание на перехватчик пакетов; копию в архиве zapret не тронул. Приложение должно переживать удаление файлов антивирусом (проверка хешей + понятное сообщение).

## Данные: Flowseal/zapret-discord-youtube @ eb16c1419057e36b8a36fbe7c959483450e6ac8b (MIT)
- 22 `general*.bat`: кроме вызовов `service.bat` (не берём) содержат одну команду `winws`. Все 33 параметра есть в документации zapret v72.13; пути только на `bin\*.bin` и `lists\*.txt`. В каталог (`data/strategies.json`) вошли 20: без `EXP` и `ALT5` (сам автор пометил NOT RECOMMENDED). Профили game filter не взяты (v2).
- `.bin` (16 шт.): по заголовкам — TLS ClientHello (SNI google.com, 4pda.to и др.), QUIC Initial v1, STUN с magic cookie; ни одного исполняемого. Это пакеты, которые winws отправляет в сеть как приманку, локально не исполняются. Взяты 8 используемых.
- Списки: только домены и подсети (`^домен` — синтаксис zapret «без поддоменов»). `ipset-all.txt` — заглушка `203.0.113.113/32` (тестовая сеть), т. е. профили ipset по умолчанию ничего не ловят.
- `.service/hosts` Flowseal (подмена адресов github/telegram в системном hosts) — **не используем**: правку hosts ТЗ исключает.
- `bin/winws.exe` у Flowseal отличается размером от сборки bol-van — не используем, ядро только от bol-van.
