**Подсветка мыши 1.0.0**: подсветка курсора и круги при щелчке для Windows 10/11.

* Вокруг курсора всегда мягкая жёлтая подсветка, она начинает работать сразу после запуска.
* При щелчке расходится круг: красный для левой кнопки, синий для правой.
* При первом запуске сама включает автозапуск с Windows (выключается в меню значка).
* Щелчки проходят сквозь подсветку, фокус она не забирает, в Alt+Tab её нет.
* В настройках: цвет, размер и яркость подсветки, режим «только при движении», цвета, размер, длительность и толщина кругов. Изменения видны сразу.
* Без установки и без прав администратора, без Планировщика заданий (нет «ошибки оператора»).

Скачайте `PodsvetkaMyshi.exe`, положите в постоянную папку (например, `C:\Users\<имя>\Программы\`) и запустите. Если появится «Система Windows защитила ваш компьютер», нажмите «Подробнее» → «Выполнить в любом случае».


## 1.1.4 — 2026-11-09
- Added a direct “Анимация щелчков” submenu to the tray icon context menu.
- The three presets (single ring, double ripple, layered ripple) are selectable as radio items and apply immediately.
- The settings dialog retains the same selector.


## 1.1.5 — 2026-11-09
- Fixed clean application shutdown while the settings dialog or an error dialog is open.
- The shutdown routine now responds to active modal dialogs before quitting the main GTK loop, allowing the process to release its instance lock.
- CI now opens the settings dialog during the GUI smoke test and verifies that the application exits through --quit.


## 1.2.0 — 2026-11-09
- Replaced KWin's built-in Mouse Click Animation with a small native KWin effect for KDE Plasma Wayland.
- The new effect animates only the button-down transition and ignores button-up, guaranteeing one animation per physical click.
- The built-in mouseclick effect is explicitly disabled/unloaded to avoid overlapping effects.
- Preserves the three animation presets (1, 2, or 4 waves), colors, radius, thickness, and duration settings.
- The KWin plugin is built against Debian 13 / KWin 6.3.6 development files and shipped inside the amd64 Debian package.
- A Plasma logout/login is required once after installation so KWin scans the newly installed native effect plugin.
