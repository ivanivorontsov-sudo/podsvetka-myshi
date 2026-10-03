using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PodsvetkaMyshi
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly string settingsPath;
        private AppSettings settings;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem miAutostart;
        private readonly Control invoker;
        private readonly Overlay overlay;
        private SettingsForm settingsForm;
        private DateTime lastCrashNote = DateTime.MinValue;

        public TrayApp(string configDir, bool openSettings)
        {
            settingsPath = Path.Combine(configDir, "settings.ini");
            settings = AppSettings.Load(settingsPath);

            invoker = new Control();
            invoker.CreateControl();
            var h = invoker.Handle; // окно для BeginInvoke в UI-потоке

            // Эффект включается сразу при запуске — никаких переключателей
            overlay = new Overlay(settings);
            overlay.Problem += text => Post(() => Balloon("Подсветка мыши", text, ToolTipIcon.Warning));
            overlay.Start();

            menu = new ContextMenuStrip { ShowImageMargin = false };
            var miSettings = new ToolStripMenuItem("Настройки…", null, (s, e) => OpenSettings()) { Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            miAutostart = new ToolStripMenuItem("Автозапуск с Windows", null, (s, e) => Safe("Автозапуск", ToggleAutostart));
            menu.Items.Add(miSettings);
            menu.Items.Add(miAutostart);
            menu.Items.Add(new ToolStripMenuItem("Открыть журнал", null, (s, e) => Safe("Журнал", OpenLog)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Выход", null, (s, e) => ExitThread()));
            menu.Opening += (s, e) => miAutostart.Checked = Autostart.IsEnabled() && !Autostart.IsDisabledByWindows();

            tray = new NotifyIcon { Icon = LoadAppIcon(SystemInformation.SmallIconSize.Width), ContextMenuStrip = menu, Text = "Подсветка мыши — работает", Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };

            invoker.BeginInvoke(new Action(() => Safe("Запуск", () => Startup(openSettings))));
        }

        public static Icon LoadAppIcon(int size)
        {
            using (var st = typeof(TrayApp).Assembly.GetManifestResourceStream("PodsvetkaMyshi.app.ico"))
                return new Icon(st, new Size(size, size));
        }

        public void Post(Action a)
        {
            try { invoker.BeginInvoke(new Action(() => Safe("команда", a))); } catch (Exception ex) { Log.Error("Post", ex); }
        }

        private void Startup(bool openSettings)
        {
            Log.Write("Запуск. Версия " + Application.ProductVersion + ", exe: " + Application.ExecutablePath +
                      ", перехватчик мыши: " + (overlay.HookInstalled ? "да" : "нет (код " + overlay.HookError + ")"));
            if (settings.LoadWarnings.Count > 0)
            {
                Balloon("Настройки исправлены", string.Join("\n", settings.LoadWarnings), ToolTipIcon.Warning);
                TrySaveSettings();
            }
            EnsureAutostartOnFirstRun();
            Autostart.RefreshPath(Application.ExecutablePath);
            if (Autostart.IsEnabled() && Autostart.IsDisabledByWindows())
                Balloon("Автозапуск выключен в Windows", "Windows отключила автозапуск программы («Диспетчер задач → Автозагрузка»). Включите его там или выберите «Автозапуск с Windows» в меню значка.", ToolTipIcon.Warning);
            if (openSettings) OpenSettings();
        }

        /// <summary>При первом запуске автозапуск включается сам, чтобы подсветка работала после каждой загрузки.
        /// Если потом его выключили в меню — больше не включаем.</summary>
        private void EnsureAutostartOnFirstRun()
        {
            if (settings.AutostartConfigured) return;
            string warn = Autostart.RiskyLocationWarning(Application.ExecutablePath, Path.GetTempPath(), Autostart.IsRemovable);
            if (!Autostart.ShouldAutoEnable(settings.AutostartConfigured, warn))
            {
                if (warn != null) Balloon("Автозапуск не включён", warn, ToolTipIcon.Warning);
                return;
            }
            try
            {
                Autostart.Enable(Application.ExecutablePath);
                settings.AutostartConfigured = true;
                TrySaveSettings();
                Log.Write("Автозапуск включён при первом запуске");
                Balloon("Подсветка мыши работает", "Программа будет запускаться вместе с Windows. Настройки и выход — в меню значка возле часов.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Error("Автозапуск при первом запуске", ex);
                Balloon("Автозапуск не включён", "Не удалось включить автозапуск: " + ErrorText.Friendly(ex) + " Включите его в меню значка.", ToolTipIcon.Warning);
            }
        }

        public void ShowRunning()
        {
            Balloon("Подсветка мыши уже работает", "Значок программы — возле часов (может быть спрятан под стрелкой «^»). Щелчок по нему открывает настройки.", ToolTipIcon.Info);
        }

        // ---------- настройки ----------

        public void OpenSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                try
                {
                    settingsForm.WindowState = FormWindowState.Normal;
                    settingsForm.TopMost = true; settingsForm.Activate(); settingsForm.TopMost = false;
                }
                catch { }
                return;
            }
            Safe("Настройки", () =>
            {
                using (DpiScope.SystemAware())
                {
                    var f = new SettingsForm(settings, Autostart.IsEnabled() && !Autostart.IsDisabledByWindows(), s => overlay.Apply(s));
                    settingsForm = f;
                    f.FormClosed += (s, e) => Safe("Настройки", () =>
                    {
                        settingsForm = null;
                        if (f.DialogResult == DialogResult.OK && f.Result != null)
                        {
                            f.Result.AutostartConfigured |= settings.AutostartConfigured;
                            settings = f.Result;
                            overlay.Apply(settings);
                            bool on = Autostart.IsEnabled() && !Autostart.IsDisabledByWindows();
                            if (f.AutostartResult != on) SetAutostart(f.AutostartResult);
                            else TrySaveSettings();
                        }
                        else overlay.Apply(settings);
                    });
                    // Поверх остальных окон, даже если Windows не даёт активировать окно другой программы
                    f.Shown += (s, e) => Safe("Настройки", () =>
                    {
                        f.TopMost = true;
                        f.Activate();
                        Native.SetForegroundWindow(f.Handle);
                        invoker.BeginInvoke(new Action(() => { try { if (!f.IsDisposed) f.TopMost = false; } catch { } }));
                    });
                    f.Show();
                }
            });
        }

        private void TrySaveSettings()
        {
            try { settings.Save(settingsPath); }
            catch (Exception ex)
            {
                Log.Error("Сохранение настроек", ex);
                Balloon("Настройки не сохранены", "Не удалось записать «" + settingsPath + "»: " + ErrorText.Friendly(ex) + " Они действуют до выхода из программы.", ToolTipIcon.Warning);
            }
        }

        private void ToggleAutostart() { SetAutostart(!(Autostart.IsEnabled() && !Autostart.IsDisabledByWindows())); }

        private void SetAutostart(bool on)
        {
            try
            {
                if (on)
                {
                    string warn = Autostart.RiskyLocationWarning(Application.ExecutablePath, Path.GetTempPath(), Autostart.IsRemovable);
                    if (warn != null) { Dialogs.Warn(warn); return; }
                    Autostart.Enable(Application.ExecutablePath);
                    Balloon("Автозапуск включён", "Подсветка будет включаться вместе с Windows (без прав администратора и без Планировщика заданий).", ToolTipIcon.Info);
                }
                else
                {
                    Autostart.Disable();
                    Balloon("Автозапуск выключен", "Программа больше не будет запускаться вместе с Windows.", ToolTipIcon.Info);
                }
                settings.AutostartConfigured = true; // выбор пользователя запоминаем и больше не трогаем
                TrySaveSettings();
            }
            catch (Exception ex)
            {
                Log.Error("Автозапуск", ex);
                Balloon("Автозапуск не изменён", ErrorText.Friendly(ex), ToolTipIcon.Error);
            }
        }

        private void OpenLog()
        {
            if (!File.Exists(Log.FilePath)) Log.Write("Журнал создан");
            Shell.Open(Log.FilePath);
        }

        // ---------- уведомления и ошибки ----------

        private void Balloon(string title, string text, ToolTipIcon icon)
        {
            try { tray.ShowBalloonTip(8000, title, text.Length > 250 ? text.Substring(0, 250) + "…" : text, icon); }
            catch (Exception ex) { Log.Error("Уведомление", ex); }
        }

        private void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { ReportCrash(what, ex); }
        }

        /// <summary>Любая ошибка — запись в журнал и короткое уведомление; программа продолжает работать.</summary>
        public void ReportCrash(string what, Exception ex)
        {
            Log.Error(what, ex);
            if (invoker.InvokeRequired) { Post(() => ReportCrash(what, null)); return; }
            if ((DateTime.Now - lastCrashNote).TotalSeconds < 30) return;
            lastCrashNote = DateTime.Now;
            Balloon("Что-то пошло не так", "«" + what + "»: " + (ex == null ? "ошибка" : ErrorText.Friendly(ex)) + " Программа продолжает работать. Подробности — в журнале (меню значка).", ToolTipIcon.Error);
        }

        protected override void ExitThreadCore()
        {
            try { if (settingsForm != null) settingsForm.Close(); } catch { }
            try { overlay.Dispose(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            Log.Write("Выход");
            base.ExitThreadCore();
        }
    }
}
