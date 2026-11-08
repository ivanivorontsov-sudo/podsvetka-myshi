#!/usr/bin/python3
# Подсветка мыши — Linux/X11.
# Copyright (c) 2026 Ivan Vorontsov. MIT license.
import argparse
import fcntl
import json
import logging
import math
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

# On KDE Wayland, render the transparent overlay through XWayland while KWin
# supplies the real global cursor coordinates over the session D-Bus.
WAYLAND_SESSION = os.environ.get("XDG_SESSION_TYPE", "").lower() == "wayland"
if WAYLAND_SESSION:
    os.environ["GDK_BACKEND"] = "x11"

import cairo
import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk
from Xlib import X, display as xdisplay

APP = "podsvetka-myshi"
VERSION = "1.0.5"
DEFAULTS = {
    "enabled": True, "only_moving": False, "glow_color": "#ffd54f",
    "glow_size": 150, "glow_alpha": 35, "click_effect": True,
    "left_color": "#ff5252", "right_color": "#448aff",
    "ring_radius": 100, "ring_duration": 550, "ring_width": 3,
}
RUNTIME = Path(os.environ.get("XDG_RUNTIME_DIR") or (Path.home() / ".cache" / APP))
CONFIG = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / APP
STATE = Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local" / "state")) / APP
SOCKET = RUNTIME / "control.sock"
LOCK = RUNTIME / "instance.lock"


def log(message, exc=None):
    try:
        STATE.mkdir(parents=True, exist_ok=True)
        logging.basicConfig(filename=str(STATE / "app.log"), level=logging.INFO,
                            format="%(asctime)s %(levelname)s %(message)s")
        logging.exception("%s: %s", message, exc) if exc else logging.info("%s", message)
    except Exception:
        pass


def clamp(value, low, high, default):
    try:
        return max(low, min(high, int(value)))
    except (TypeError, ValueError):
        return default


def color_ok(value, default):
    if isinstance(value, str) and len(value) == 7 and value[0] == "#":
        try:
            int(value[1:], 16)
            return value.lower()
        except ValueError:
            pass
    return default


def rgb(value):
    value = color_ok(value, "#ffffff")
    return tuple(int(value[i:i + 2], 16) / 255.0 for i in (1, 3, 5))


def load_settings():
    data = dict(DEFAULTS)
    path = CONFIG / "settings.json"
    if path.exists():
        try:
            loaded = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(loaded, dict):
                data.update(loaded)
        except Exception as exc:
            log("Ошибка чтения настроек", exc)
            try:
                path.replace(path.with_suffix(".json.broken"))
            except Exception:
                pass
    for key in ("enabled", "only_moving", "click_effect"):
        data[key] = bool(data.get(key, DEFAULTS[key]))
    for key in ("glow_color", "left_color", "right_color"):
        data[key] = color_ok(data.get(key), DEFAULTS[key])
    for key, lo, hi in (("glow_size", 20, 400), ("glow_alpha", 5, 100),
                        ("ring_radius", 10, 300), ("ring_duration", 150, 3000),
                        ("ring_width", 1, 12)):
        data[key] = clamp(data.get(key), lo, hi, DEFAULTS[key])
    data["autostart"] = bool(data.get("autostart", False))
    return data


def save_settings(data):
    CONFIG.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="settings-", suffix=".tmp", dir=str(CONFIG))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
            f.write("\n")
            f.flush()
            os.fsync(f.fileno())
        os.replace(name, CONFIG / "settings.json")
    finally:
        try:
            os.unlink(name)
        except FileNotFoundError:
            pass


def set_autostart(enabled):
    target = Path.home() / ".config" / "autostart" / (APP + ".desktop")
    if not enabled:
        try:
            target.unlink()
        except FileNotFoundError:
            pass
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    candidates = [
        Path("/usr/bin/podsvetka-myshi-launch"),
        Path.home() / ".local" / "bin" / "podsvetka-myshi-launch",
    ]
    launcher = next((candidate for candidate in candidates if candidate.is_file()), None)
    if launcher:
        executable = str(launcher).replace("\\", "\\\\").replace('"', '\\"')
        command = '"' + executable + '" --autostart'
    else:
        py = (sys.executable or "/usr/bin/python3").replace("\\", "\\\\").replace('"', '\\"')
        script = str(Path(__file__).resolve()).replace("\\", "\\\\").replace('"', '\\"')
        command = '"' + py + '" "' + script + '" --autostart'
    target.write_text(
        "[Desktop Entry]\nType=Application\nName=Подсветка мыши\n"
        "Comment=Подсветка курсора и анимация щелчков\n"
        "Exec=" + command + "\n"
        "Icon=input-mouse\nTerminal=false\nNoDisplay=true\n",
        encoding="utf-8",
    )


def send_command(command, retries=1):
    """
    Send a command to the active instance. A just-started GTK app may have
    created its overlay before its IPC thread binds the socket, so allow a
    short bounded retry window for menu actions and shutdown.
    """
    attempts = max(1, min(20, int(retries)))
    for attempt in range(attempts):
        try:
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
                s.settimeout(0.4)
                s.connect(str(SOCKET))
                s.sendall(command.encode("ascii"))
            return True
        except OSError:
            if attempt + 1 < attempts:
                time.sleep(0.1)
    return False


def acquire_lock():
    RUNTIME.mkdir(parents=True, exist_ok=True)
    handle = open(LOCK, "a+", encoding="utf-8")
    try:
        fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return handle
    except BlockingIOError:
        handle.close()
        return None


def ease_out(t):
    t = max(0.0, min(1.0, float(t)))
    return 1.0 - (1.0 - t) ** 3


def draw_glow(ctx, x, y, size, color, alpha):
    r, g, b = rgb(color)
    radius = size / 2.0
    grad = cairo.RadialGradient(x, y, 0, x, y, radius)
    grad.add_color_stop_rgba(0, r, g, b, alpha / 100.0 * 0.42)
    grad.add_color_stop_rgba(0.5, r, g, b, alpha / 100.0 * 0.2)
    grad.add_color_stop_rgba(1, r, g, b, 0)
    ctx.set_source(grad)
    ctx.arc(x, y, radius, 0, math.pi * 2)
    ctx.fill()


class App:
    def __init__(self, open_settings=False):
        self.cfg = load_settings()
        self.rings = []
        self.position = None
        self.glow_pos = None
        self.glow_on = False
        self.last_move = time.monotonic()
        self.last_mask = 0
        self.running = True
        self.stop = threading.Event()
        self.server = None
        self.wayland_mode = WAYLAND_SESSION
        self.dbus_bus = None
        self.dbus_name = None
        self.kwin_receiver = None

        display = Gdk.Display.get_default()
        display_name = display.get_name() if display else ""
        if self.wayland_mode and not os.environ.get("DISPLAY"):
            raise RuntimeError(
                "Для подсветки в KDE Wayland нужен XWayland. "
                "Перезапустите сеанс Plasma с установленным пакетом xwayland."
            )
        if not self.wayland_mode and "wayland" in display_name.lower():
            raise RuntimeError(
                "GTK подключился напрямую к Wayland вместо XWayland. "
                "Проверьте наличие XWayland и переменную DISPLAY."
            )
        self.xdisplay = xdisplay.Display()
        self.root = self.xdisplay.screen().root
        visual = Gdk.Screen.get_default().get_rgba_visual()
        if visual is None:
            raise RuntimeError("Нет RGBA-визуала: для прозрачной подсветки нужен композитный менеджер окон.")

        self.window = Gtk.Window(type=Gtk.WindowType.TOPLEVEL)
        self.window.set_title("Подсветка мыши")
        self.window.set_decorated(False)
        self.window.set_app_paintable(True)
        self.window.set_visual(visual)
        self.window.set_accept_focus(False)
        self.window.set_focus_on_map(False)
        self.window.set_skip_taskbar_hint(True)
        self.window.set_skip_pager_hint(True)
        self.window.set_type_hint(Gdk.WindowTypeHint.DOCK)
        self.window.connect("draw", self.on_draw)
        self.resize_window()
        self.window.realize()
        gdk_window = self.window.get_window()
        if not self.wayland_mode:
            gdk_window.set_override_redirect(True)
        else:
            # A managed XWayland surface can be placed in KWin's above layer;
            # override-redirect would bypass KWin's stacking policy.
            self.window.set_keep_above(True)
        gdk_window.set_pass_through(True)
        self.window.input_shape_combine_region(cairo.Region())
        self.window.show_all()

        self.create_tray()
        self.start_ipc()
        if self.wayland_mode:
            self.start_kwin_bridge()
        GLib.timeout_add(8, self.tick)
        if open_settings:
            GLib.idle_add(self.settings_dialog)
        log("Linux/X11 version started")

    def resize_window(self):
        geo = self.root.get_geometry()
        self.window.set_default_size(int(geo.width), int(geo.height))
        self.window.move(0, 0)
        self.window.resize(int(geo.width), int(geo.height))

    def create_tray(self):
        try:
            self.tray = Gtk.StatusIcon.new_from_icon_name("input-mouse")
            self.tray.set_tooltip_text("Подсветка мыши")
            self.tray.set_visible(True)
            self.tray.connect("activate", lambda *_: self.settings_dialog())
            self.tray.connect("popup-menu", self.tray_menu)
        except Exception as exc:
            self.tray = None
            log("Значок системного лотка недоступен", exc)

    def tray_menu(self, icon, button, timestamp):
        menu = Gtk.Menu()
        for label, callback in (
            ("Настройки…", lambda: self.settings_dialog()),
            ("Переключить подсветку", lambda: self.handle("toggle")),
            ("Выход", lambda: self.shutdown()),
        ):
            item = Gtk.MenuItem.new_with_label(label)
            item.connect("activate", lambda _item, cb=callback: cb())
            menu.append(item)
        menu.show_all()
        menu.popup(None, None, None, None, button, timestamp)

    def start_ipc(self):
        try:
            try:
                SOCKET.unlink()
            except FileNotFoundError:
                pass
            self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            self.server.bind(str(SOCKET))
            os.chmod(SOCKET, 0o600)
            self.server.listen(4)
            self.server.settimeout(0.5)
            threading.Thread(target=self.ipc_loop, daemon=True, name="podsvetka-ipc").start()
            log("Управляющий сокет запущен: " + str(SOCKET))
        except Exception as exc:
            log("Не удалось открыть управляющий сокет", exc)
            raise RuntimeError("Не удалось открыть локальный управляющий сокет " + str(SOCKET) + ": " + str(exc)) from exc

    def start_kwin_bridge(self):
        """Bridge KWin's compositor-owned cursor coordinates into the XWayland overlay."""
        try:
            import dbus
            import dbus.service
            from dbus.mainloop.glib import DBusGMainLoop

            DBusGMainLoop(set_as_default=True)
            self.dbus_bus = dbus.SessionBus()
            self.dbus_name = dbus.service.BusName(
                "org.podsvetka.Myshi", bus=self.dbus_bus, do_not_queue=True
            )
            app = self

            class CursorReceiver(dbus.service.Object):
                @dbus.service.method(
                    "org.podsvetka.Myshi", in_signature="ii", out_signature=""
                )
                def SetCursorPosition(self, x, y):
                    GLib.idle_add(app.receive_cursor_position, int(x), int(y))

            self.kwin_receiver = CursorReceiver(self.dbus_bus, "/org/podsvetka/Myshi")
            self.configure_kwin_script(True)
            log("Мост KDE/KWin Wayland запущен через D-Bus")
        except Exception as exc:
            log("Не удалось запустить мост KDE/KWin Wayland", exc)
            raise RuntimeError(
                "Не удалось включить поддержку KDE Wayland. Нужны KDE Plasma/KWin, "
                "XWayland и пакет python3-dbus. Подробности: "
                + str(STATE / "app.log")
            ) from exc

    def configure_kwin_script(self, enabled):
        """Install the per-user KWin script and toggle it only while the app runs."""
        source = Path("/usr/share/kwin/scripts/podsvetka-myshi")
        if not source.is_dir():
            source = Path(__file__).resolve().parent / "kwin-script"
        if not (source / "metadata.json").is_file():
            raise RuntimeError("Не найден пакет KWin script: " + str(source))

        data_home = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
        target = data_home / "kwin" / "scripts" / "podsvetka-myshi"
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(source, target, dirs_exist_ok=True)

        kwriteconfig = shutil.which("kwriteconfig6") or shutil.which("kwriteconfig5")
        if kwriteconfig:
            subprocess.run(
                [kwriteconfig, "--file", "kwinrc", "--group", "Plugins",
                 "--key", "podsvetka-myshiEnabled", "true" if enabled else "false"],
                check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            )
        else:
            # Minimal KConfig-compatible fallback, preserving unrelated entries.
            config = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config")) / "kwinrc"
            lines = config.read_text(encoding="utf-8").splitlines() if config.exists() else []
            section_start = next((i for i, line in enumerate(lines)
                                  if line.strip().lower() == "[plugins]"), None)
            key_line = "podsvetka-myshiEnabled=" + ("true" if enabled else "false")
            if section_start is None:
                lines.extend(["", "[Plugins]", key_line])
            else:
                section_end = next((i for i in range(section_start + 1, len(lines))
                                    if lines[i].strip().startswith("[") and lines[i].strip().endswith("]")),
                                   len(lines))
                found = next((i for i in range(section_start + 1, section_end)
                              if lines[i].split("=", 1)[0].strip() == "podsvetka-myshiEnabled"), None)
                if found is None:
                    lines.insert(section_end, key_line)
                else:
                    lines[found] = key_line
            config.parent.mkdir(parents=True, exist_ok=True)
            fd, temp_name = tempfile.mkstemp(prefix="kwinrc-", dir=str(config.parent))
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                stream.write("\n".join(lines) + "\n")
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temp_name, config)

        if self.dbus_bus is not None:
            obj = self.dbus_bus.get_object("org.kde.KWin", "/KWin")
            self.dbus.Interface(obj, "org.kde.KWin").reconfigure()

    def receive_cursor_position(self, x, y):
        now = time.monotonic()
        if self.position != (x, y):
            self.last_move = now
        self.position = (x, y)
        return False

    def ipc_loop(self):
        while not self.stop.is_set():
            try:
                conn, _ = self.server.accept()
            except socket.timeout:
                continue
            except (OSError, AttributeError):
                break
            try:
                cmd = conn.recv(64).decode("ascii", errors="ignore").strip()
                GLib.idle_add(self.handle, cmd)
            except Exception as exc:
                log("IPC error", exc)
            finally:
                conn.close()

    def handle(self, cmd):
        if cmd == "settings":
            self.settings_dialog()
        elif cmd == "quit":
            self.shutdown()
        elif cmd == "toggle":
            self.cfg["enabled"] = not self.cfg["enabled"]
            save_settings(self.cfg)
            self.window.queue_draw()
        return False

    def settings_dialog(self):
        cfg = dict(self.cfg)
        dlg = Gtk.Dialog(title="Настройки — Подсветка мыши", transient_for=None, flags=0)
        dlg.set_resizable(False)
        dlg.add_button("Отмена", Gtk.ResponseType.CANCEL)
        dlg.add_button("Сохранить", Gtk.ResponseType.OK)
        dlg.set_default_response(Gtk.ResponseType.OK)
        grid = Gtk.Grid(column_spacing=14, row_spacing=9, border_width=16)
        dlg.get_content_area().add(grid)

        def add(label, widget, y):
            grid.attach(Gtk.Label(label=label, xalign=0), 0, y, 1, 1)
            widget.set_hexpand(True)
            grid.attach(widget, 1, y, 1, 1)

        def scale(low, high, value):
            w = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, low, high, 1)
            w.set_digits(0)
            w.set_value(value)
            w.set_size_request(230, -1)
            return w

        def color(value):
            rgba = Gdk.RGBA()
            rgba.parse(value)
            w = Gtk.ColorButton.new_with_rgba(rgba)
            w.set_use_alpha(False)
            return w

        enabled = Gtk.CheckButton.new_with_label("Подсветка включена")
        enabled.set_active(cfg["enabled"])
        grid.attach(enabled, 0, 0, 2, 1)
        moving = Gtk.CheckButton.new_with_label("Показывать только при движении мыши")
        moving.set_active(cfg["only_moving"])
        grid.attach(moving, 0, 1, 2, 1)
        glow_size = scale(20, 400, cfg["glow_size"])
        glow_alpha = scale(5, 100, cfg["glow_alpha"])
        glow_color = color(cfg["glow_color"])
        add("Размер свечения (пикс.)", glow_size, 2)
        add("Яркость (%)", glow_alpha, 3)
        add("Цвет подсветки", glow_color, 4)
        clicks = Gtk.CheckButton.new_with_label("Анимировать щелчки мыши")
        clicks.set_active(cfg["click_effect"])
        if self.wayland_mode:
            clicks.set_sensitive(False)
            clicks.set_tooltip_text(
                "В KDE Wayland KWin передаёт координаты курсора, но не события кнопок мыши."
            )
        grid.attach(clicks, 0, 5, 2, 1)
        left = color(cfg["left_color"])
        right = color(cfg["right_color"])
        ring_size = scale(10, 300, cfg["ring_radius"])
        duration = scale(150, 3000, cfg["ring_duration"])
        width = scale(1, 12, cfg["ring_width"])
        add("Цвет левого щелчка", left, 6)
        add("Цвет правого щелчка", right, 7)
        add("Радиус круга", ring_size, 8)
        add("Длительность (мс)", duration, 9)
        add("Толщина линии", width, 10)
        autostart = Gtk.CheckButton.new_with_label("Запускать при входе в систему")
        autostart.set_active(cfg["autostart"])
        grid.attach(autostart, 0, 11, 2, 1)
        dlg.show_all()

        if dlg.run() == Gtk.ResponseType.OK:
            cfg.update({
                "enabled": enabled.get_active(),
                "only_moving": moving.get_active(),
                "click_effect": clicks.get_active(),
                "glow_size": int(glow_size.get_value()),
                "glow_alpha": int(glow_alpha.get_value()),
                "glow_color": self.to_hex(glow_color.get_rgba()),
                "left_color": self.to_hex(left.get_rgba()),
                "right_color": self.to_hex(right.get_rgba()),
                "ring_radius": int(ring_size.get_value()),
                "ring_duration": int(duration.get_value()),
                "ring_width": int(width.get_value()),
                "autostart": autostart.get_active(),
            })
            try:
                save_settings(cfg)
                set_autostart(cfg["autostart"])
                self.cfg = cfg
                self.rings.clear()
                self.glow_pos = None
                self.glow_on = False
                self.window.queue_draw()
            except Exception as exc:
                log("Ошибка сохранения настроек", exc)
                self.message_error("Не удалось сохранить настройки: " + str(exc))
        dlg.destroy()
        return False

    @staticmethod
    def to_hex(value):
        return "#{:02x}{:02x}{:02x}".format(round(value.red * 255),
                                             round(value.green * 255),
                                             round(value.blue * 255))

    def message_error(self, message):
        dlg = Gtk.MessageDialog(transient_for=None, flags=Gtk.DialogFlags.MODAL,
                                message_type=Gtk.MessageType.ERROR,
                                buttons=Gtk.ButtonsType.OK, text="Подсветка мыши")
        dlg.format_secondary_text(message)
        dlg.run()
        dlg.destroy()

    def invalidate(self, x, y, radius):
        m = int(radius + self.cfg["ring_width"] + 5)
        self.window.queue_draw_area(int(x - m), int(y - m), 2 * m + 1, 2 * m + 1)

    def on_draw(self, widget, ctx):
        ctx.set_operator(cairo.OPERATOR_CLEAR)
        ctx.paint()
        ctx.set_operator(cairo.OPERATOR_OVER)
        if self.glow_on and self.glow_pos:
            draw_glow(ctx, self.glow_pos[0], self.glow_pos[1],
                      self.cfg["glow_size"], self.cfg["glow_color"], self.cfg["glow_alpha"])
        now = time.monotonic()
        for ring in self.rings:
            t = max(0.0, min(1.0, (now - ring["start"]) / (ring["duration"] / 1000.0)))
            radius = ring["radius"] * (0.12 + ease_out(t) * 0.88)
            r, g, b = rgb(ring["color"])
            ctx.set_source_rgba(r, g, b, (1.0 - t) * 0.95)
            ctx.set_line_width(ring["width"])
            ctx.arc(ring["x"], ring["y"], radius, 0, math.pi * 2)
            ctx.stroke()
        return False

    def add_ring(self, x, y, color, now):
        self.rings.append({
            "x": x, "y": y, "color": color, "start": now,
            "duration": self.cfg["ring_duration"], "radius": self.cfg["ring_radius"],
            "width": self.cfg["ring_width"], "drawn": 0,
        })

    def tick(self):
        if not self.running:
            return False
        now = time.monotonic()
        if self.wayland_mode:
            if self.position is None:
                return True
            x, y = self.position
            mask = 0  # KWin's supported scripting API exposes position, not button state.
        else:
            try:
                pointer = self.root.query_pointer()
                x, y, mask = int(pointer.root_x), int(pointer.root_y), int(pointer.mask)
            except Exception as exc:
                log("Не удалось получить позицию указателя", exc)
                return True

        if self.position != (x, y):
            self.last_move = now
        show = self.cfg["enabled"] and (not self.cfg["only_moving"] or now - self.last_move < 0.8)
        if self.glow_on and self.glow_pos:
            self.invalidate(*self.glow_pos, self.cfg["glow_size"] / 2)
        if show:
            self.invalidate(x, y, self.cfg["glow_size"] / 2)
            self.glow_pos = (x, y)
        else:
            self.glow_pos = None
        self.glow_on = show

        if self.cfg["enabled"] and self.cfg["click_effect"]:
            if (mask & X.Button1Mask) and not (self.last_mask & X.Button1Mask):
                self.add_ring(x, y, self.cfg["left_color"], now)
            if (mask & X.Button3Mask) and not (self.last_mask & X.Button3Mask):
                self.add_ring(x, y, self.cfg["right_color"], now)
            self.last_mask = mask

        alive = []
        for ring in self.rings:
            elapsed = (now - ring["start"]) * 1000
            t = max(0.0, min(1.0, elapsed / ring["duration"]))
            radius = ring["radius"] * (0.12 + ease_out(t) * 0.88)
            self.invalidate(ring["x"], ring["y"], max(radius, ring["drawn"]) + ring["width"] + 4)
            ring["drawn"] = radius
            if elapsed < ring["duration"]:
                alive.append(ring)
        self.rings = alive
        self.position = (x, y)
        return True

    def shutdown(self):
        if not self.running:
            return False
        self.running = False
        self.stop.set()
        try:
            self.server.close()
        except Exception:
            pass
        try:
            SOCKET.unlink()
        except FileNotFoundError:
            pass
        if self.wayland_mode and self.dbus_bus is not None:
            try:
                self.configure_kwin_script(False)
            except Exception as exc:
                log("Не удалось выключить KWin script при выходе", exc)
            try:
                if self.kwin_receiver is not None:
                    self.kwin_receiver.remove_from_connection()
            except Exception:
                pass
            self.kwin_receiver = None
            self.dbus_name = None
            self.dbus_bus = None
        try:
            if self.tray:
                self.tray.set_visible(False)
        except Exception:
            pass
        self.xdisplay.close()
        self.window.destroy()
        Gtk.main_quit()
        return False


def set_autostart(enabled):
    target = Path.home() / ".config" / "autostart" / (APP + ".desktop")
    if not enabled:
        try:
            target.unlink()
        except FileNotFoundError:
            pass
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    py = (sys.executable or "/usr/bin/python3").replace("\\", "\\\\").replace('"', '\\"')
    script = str(Path(__file__).resolve()).replace("\\", "\\\\").replace('"', '\\"')
    target.write_text(
        "[Desktop Entry]\nType=Application\nName=Подсветка мыши\n"
        "Comment=Подсветка курсора и анимация щелчков\n"
        'Exec="' + py + '" "' + script + '" --autostart\n'
        "Icon=input-mouse\nTerminal=false\nNoDisplay=true\n",
        encoding="utf-8",
    )


def self_test():
    assert ease_out(0) == 0 and ease_out(1) == 1
    assert 0 < ease_out(0.5) < 1
    assert clamp("120", 20, 400, 150) == 120
    assert clamp("bad", 20, 400, 150) == 150
    assert color_ok("#a1B2c3", "#ffffff") == "#a1b2c3"
    assert color_ok("bad", "#ffffff") == "#ffffff"
    print("Linux core self-tests: OK")


def gtk_init_checked():
    """
    GTK 3 bindings may return either bool or (bool, argv).
    Never treat a non-empty tuple as success: (False, []) is still truthy.
    """
    result = Gtk.init_check()
    if isinstance(result, tuple):
        return bool(result[0])
    return bool(result)


def print_diagnostics():
    print("Подсветка мыши — диагностика Linux")
    print("Python:", sys.version.split()[0])
    print("DISPLAY:", os.environ.get("DISPLAY", "(не задан)"))
    print("WAYLAND_DISPLAY:", os.environ.get("WAYLAND_DISPLAY", "(не задан)"))
    print("XDG_SESSION_TYPE:", os.environ.get("XDG_SESSION_TYPE", "(не задан)"))
    print("KDE Wayland mode:", "да (через XWayland + KWin D-Bus)" if WAYLAND_SESSION else "нет")
    print("Control socket:", str(SOCKET), "(есть)" if SOCKET.exists() else "(пока нет)")
    try:
        result = Gtk.init_check()
        ok = bool(result[0]) if isinstance(result, tuple) else bool(result)
        print("GTK init:", "OK" if ok else "ОШИБКА")
        display = Gdk.Display.get_default() if ok else None
        print("GDK display:", display.get_name() if display else "(нет)")
        if display:
            screen = Gdk.Screen.get_default()
            visual = screen.get_rgba_visual() if screen else None
            print("RGBA visual:", "OK" if visual else "НЕТ — нужен композитор окон")
        if ok and display and "wayland" not in display.get_name().lower():
            probe = xdisplay.Display()
            pointer = probe.screen().root.query_pointer()
            print("X11 pointer:", int(pointer.root_x), int(pointer.root_y))
            print("X11 shape extension:", "OK" if probe.has_extension("SHAPE") else "НЕТ")
            probe.close()
    except Exception as exc:
        print("Диагностика GTK/X11 завершилась ошибкой:", repr(exc))
        return 1
    return 0 if ok else 2


def main():
    parser = argparse.ArgumentParser(description="Подсветка мыши для Linux/X11 и KDE Wayland")
    parser.add_argument("--settings", action="store_true")
    parser.add_argument("--quit", action="store_true")
    parser.add_argument("--toggle", action="store_true")
    parser.add_argument("--diagnose", action="store_true", help="проверить окружение GTK/X11")
    parser.add_argument("--autostart", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--self-test", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--version", action="version", version=VERSION)
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if args.diagnose:
        return print_diagnostics()

    cmd = "quit" if args.quit else "settings" if args.settings else "toggle" if args.toggle else "show"
    if send_command(cmd, retries=10 if (args.quit or args.toggle or args.settings) else 1):
        return 0
    if args.quit or args.toggle:
        print("Подсветка мыши сейчас не запущена или управляющий сокет недоступен:", SOCKET)
        return 2

    lock_handle = acquire_lock()
    if lock_handle is None:
        if send_command(cmd):
            return 0
        print("Подсветка мыши уже запускается. Повторите команду.", file=sys.stderr)
        return 1

    gtk_ready = False
    try:
        gtk_ready = gtk_init_checked()
        if not gtk_ready:
            raise RuntimeError(
                "GTK не смог подключиться к графическому сеансу. "
                "Проверьте DISPLAY, WAYLAND_DISPLAY и запуск из меню рабочего стола."
            )
        app = App(open_settings=args.settings)
        app.lock_handle = lock_handle
        Gtk.main()
    except Exception as exc:
        log("Ошибка запуска Linux-версии", exc)
        message = "Не удалось запустить «Подсветку мыши»:\n\n" + str(exc) + (
            "\n\nПодробности журнала: " + str(STATE / "app.log")
        )
        print(message, file=sys.stderr)
        if gtk_ready:
            try:
                dialog = Gtk.MessageDialog(
                    transient_for=None, flags=Gtk.DialogFlags.MODAL,
                    message_type=Gtk.MessageType.ERROR,
                    buttons=Gtk.ButtonsType.OK, text="Подсветка мыши",
                )
                dialog.format_secondary_text(message)
                dialog.run()
                dialog.destroy()
            except Exception as dialog_exc:
                log("Не удалось показать окно ошибки", dialog_exc)
        return 1
    finally:
        lock_handle.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
