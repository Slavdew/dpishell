DpiShell — оптимизация соединения с YouTube, Discord и другими сайтами (оболочка над zapret)
=================================================================================

Как запустить
1. Распакуйте архив в любую папку, например C:\Programs\DpiShell (не во «Временные файлы»).
2. Запустите DpiShell.exe. Окно открывается без прав администратора. Программа не подписана:
   SmartScreen может предупредить — «Подробнее» → «Выполнить в любом случае». Если такой кнопки
   нет, запуск, возможно, ограничивает Smart App Control или политика организации.
3. Нажмите «Включить». Первый раз программа попросит права администратора — это установка
   помощника DpiShell (служба Windows); обычно это один раз, но при обновлении или удалении
   помощника Windows спросит снова.
4. Сайты не открылись — «Подобрать»: программа сама найдёт подходящую стратегию и включит её.

Язык окна (русский, английский) переключается значком с флагом внизу меню слева.

Папку data рядом с DpiShell.exe не удаляйте и не меняйте — без неё программа не запустится.
Ядро zapret (winws, WinDivert от bol-van/zapret) программа скачивает сама и проверяет по хешам.
Обновления списков — в окне (вкладка «Информация и обновления»); новая версия программы -
кнопка «Обновить до …» там же (после «Проверить обновления»), «Вернуть версию …» откатывает.
Если кнопки нет, архив новой версии - на странице https://github.com/Slavdew/dpishell/releases
(распаковать поверх, папку data не трогать)

Если не работает
«Диагностика» → «Сохранить отчёт»: один текстовый файл, его можно прочитать перед отправкой.
Адреса и имена вашего ПК и сети, пароли и токены в нём скрыты. Приложите файл к обращению:
https://github.com/Slavdew/dpishell/issues
Частые вопросы и «Как вернуть как было» (в том числе командами, без окна и без интернета):
https://github.com/Slavdew/dpishell/blob/master/docs/FAQ.md

Как удалить полностью
Одной кнопкой: «Настройки» → «Очистка и удаление» → «Удалить DpiShell с этого ПК…».
Окно отменит все изменения, удалит службу, помощника и %ProgramData%\DpiShell,
затем покажет, какие две папки удалить вручную. Шаги ниже — то же вручную.
0. «Настройки» → «Изменения DpiShell» → «Отменить всё».
1. Вкладка «Сеть»: если включали «Починить адреса» — нажмите там «Вернуть как было»;
   если меняли DNS или прокси — верните прежние значения в их разделах.
2. «Настройки» → снять галочку «Запускать с Windows (в трее)».
3. Там же («Очистка и удаление»): «Удалить службу оптимизации» (служба оптимизации и драйвер WinDivert).
4. Там же: «Удалить помощника» (один запрос прав администратора).
5. Закройте программу: значок в трее → «Выход».
6. Удалите три папки (окно этого не делает): папку с программой и
     %LocalAppData%\DpiShell   — настройки окна
     %ProgramData%\DpiShell    — данные и ядро (нужны права администратора)

Совместимость
Гарантируется работа версий одного выпуска: окно на ПК, помощник и пакеты на роутере. Если долго не
обновлять одно (например, окно на ПК), а обновлять другое (роутер), полная работа не гарантируется —
обновляйте оба. По возможности совместимость сохраняется.

Лицензии сторонних компонентов — THIRD-PARTY-NOTICES.txt.

=================================================================================
English
=================================================================================

DpiShell optimizes the connection to YouTube, Discord and other sites (a shell over zapret).
It is a beta; results depend on the ISP and the network.

How to start
1. Unpack the archive to any folder, for example C:\Programs\DpiShell (not into Temp or OneDrive).
2. Run DpiShell.exe. The window opens without administrator rights. The program is not signed,
   so Windows SmartScreen may warn: "More info" > "Run anyway". If there is no such button,
   launch may be restricted by Smart App Control or an organization policy.
3. Press "Turn on". The first time Windows asks for administrator rights: this installs the
   DpiShell helper (a Windows service). Usually that happens once; when the helper is updated
   or removed, Windows asks again.
4. If sites do not open, press "Find strategy": the program finds a suitable strategy and turns it on.

The window language (Russian, English) is switched with the flag icon at the bottom of the left menu.

Do not delete or edit the "data" folder next to DpiShell.exe: the program will not start without it.
The zapret core (winws, WinDivert from bol-van/zapret) is downloaded by the program and verified by hashes.
Updates: "About & updates" tab > "Check for updates" > "Update to ...", "Restore version ..." rolls back.
If there is no such button, download the new archive from https://github.com/Slavdew/dpishell/releases
and unpack it over the old one, leaving the data folder alone.

Compatibility
Versions of one release are guaranteed to work together: the PC app, the helper and the router
packages. If you leave one of them without updates for long (for example the PC app) while updating
the other (the router), full operation is not guaranteed: update both. Compatibility is kept
where possible.

If something does not work
"Diagnostics" > "Save report": one text file you can read before sending. Addresses and names of
your PC and network, passwords and tokens are hidden in it. Attach it to an issue:
https://github.com/Slavdew/dpishell/issues
FAQ, including how to restore the previous state for every feature (also without the window and
without internet): https://github.com/Slavdew/dpishell/blob/master/docs/FAQ.en.md

How to uninstall completely
0. Easiest: "Settings" > "DpiShell changes" > "Undo all". The steps below do the same by hand.
1. "Network" tab: if you used "Fix addresses", press "Revert" there; if you changed DNS or the
   proxy, restore the previous values in their sections.
2. "Settings": clear "Start with Windows (in tray)".
3. The same tab ("Cleanup and removal"): "Remove optimization service" (the optimization service
   and the WinDivert driver).
4. The same place: "Remove helper" (one administrator prompt).
5. Close the program: tray icon > "Exit".
6. Delete three folders (the window does not do this): the program folder,
     %LocalAppData%\DpiShell   - window settings
     %ProgramData%\DpiShell    - data and the core (needs administrator rights)

Third-party licenses: THIRD-PARTY-NOTICES.txt.
