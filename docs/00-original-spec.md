# Исходное ТЗ (черновик пользователя, как было прислано)

Цель: CLI на Python 3 — обёртка над zapret (сборка Flowseal) с параллельным подбором стратегий
обхода DPI под указанные сайты и применением лучшей стратегии на всю Windows.

Структура: config.json (target_url, timeout, last_working_strategy), strategies.txt (флаги по строке),
main.py (меню), downloader.py (закреплённый релиз Flowseal + проверка SHA-256, распаковка в zapret-win/),
tester.py (tpws.exe --socks --port=10081+, параллельно через Popen, ThreadPoolExecutor, requests HEAD
через proxies, успех = статус < 500, время до сотых, taskkill /F /T, max_workers 5–6, проверка занятости
портов), service.py (IsUserAnAdmin + ShellExecuteW runas, nfqws.exe --wndivert=all + флаги стратегии,
Ctrl+C для закрытия).

Пример strategies.txt: `--split-pos=2`, `--disorder`, `--split-pos=2 --disorder`, `--hostcase`.

## Уже найденные проблемы (первичный разбор)
- tpws.exe под Windows нативно нет (только через WSL); у Flowseal в архиве winws.exe + WinDivert + cygwin1.dll.
- nfqws.exe и --wndivert=all в Windows нет; есть winws.exe с --wf-tcp/--wf-udp/--wf-raw.
- Флаги в strategies.txt — синтаксис tpws, не winws.
- HEAD со статусом < 500 даёт ложные успехи (обрыв ТСПУ после ~16 КБ, googlevideo отдельно от youtube.com).
- Админ нужен и для тестов (WinDivert).
- Идея параллельности: несколько winws с --wf-raw по диапазонам исходных портов, запросы привязаны к портам. Не проверено.
- У Flowseal в service.bat уже есть «Run Tests».
