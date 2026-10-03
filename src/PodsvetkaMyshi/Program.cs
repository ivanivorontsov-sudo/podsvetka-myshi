using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PodsvetkaMyshi
{
    internal static class Program
    {
        private const string Id = "PodsvetkaMyshi-8b2e4c71";
        private static TrayApp app;

        internal sealed class Options
        {
            public string ConfigDir;
            public bool Quit, Autostart, Settings;
            public string Error;
        }

        internal static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "--config-dir":
                        if (i + 1 < args.Length) o.ConfigDir = args[++i]; else o.Error = "После --config-dir нужна папка";
                        break;
                    case "--quit": o.Quit = true; break;
                    case "--settings": o.Settings = true; break;
                    case "--autostart": o.Autostart = true; break;
                    default: o.Error = "Неизвестный параметр: " + args[i]; break;
                }
            }
            return o;
        }

        private static string ResolveConfigDir(string requested)
        {
            foreach (string dir in new[]
            {
                requested,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PodsvetkaMyshi"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodsvetkaMyshi"),
                Path.Combine(Path.GetTempPath(), "PodsvetkaMyshi"),
            })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                try { Directory.CreateDirectory(dir); return dir; } catch { }
            }
            return Path.GetTempPath();
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Native.EnsureDpiAwareness();
            Options opt = ParseArgs(args);
            string configDir = ResolveConfigDir(opt.ConfigDir);
            Log.FilePath = Path.Combine(configDir, "log.txt");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                if (app != null) app.ReportCrash("интерфейс", e.Exception); else Log.Error("ThreadException", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("UnhandledException", e.ExceptionObject as Exception);

            if (opt.Error != null)
            {
                Log.Write(opt.Error);
                try { if (Native.AttachConsole(-1)) Console.WriteLine(opt.Error); } catch { }
                return 2;
            }
            if (opt.Quit) { Signal("quit"); return 0; }

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\" + Id, out createdNew))
            {
                if (!createdNew)
                {
                    if (opt.Autostart) return 0; // уже работает — при автозапуске молча выходим
                    if (!Signal(opt.Settings ? "settings" : "show"))
                        Dialogs.Warn("«Подсветка мыши» уже запущена, но не отвечает (возможно, она запущена от имени администратора).\n\nЗакройте её через значок в трее или Диспетчер задач и запустите снова.");
                    return 0;
                }

                using (var evShow = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + Id + "-show"))
                using (var evQuit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + Id + "-quit"))
                using (var evSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + Id + "-settings"))
                {
                    try { app = new TrayApp(configDir, opt.Settings); }
                    catch (Exception ex)
                    {
                        Log.Error("Запуск", ex);
                        Dialogs.Error("«Подсветка мыши» не смогла запуститься: " + ErrorText.Friendly(ex) + "\n\nЖурнал: " + Log.FilePath);
                        return 1;
                    }
                    var waiter = new Thread(() =>
                    {
                        var handles = new WaitHandle[] { evShow, evQuit, evSettings };
                        while (true)
                        {
                            int i;
                            try { i = WaitHandle.WaitAny(handles); } catch { return; }
                            if (i == 0) app.Post(app.ShowRunning);
                            else if (i == 2) app.Post(app.OpenSettings);
                            else { app.Post(app.ExitThread); return; }
                        }
                    }) { IsBackground = true, Name = "signals" };
                    waiter.Start();
                    Application.Run(app);
                }
            }
            return 0;
        }

        private static bool Signal(string name)
        {
            try
            {
                try { Native.AllowSetForegroundWindow(-1 /*ASFW_ANY*/); } catch { } // чтобы окно настроек вышло на передний план
                using (var ev = EventWaitHandle.OpenExisting(@"Local\" + Id + "-" + name)) ev.Set();
                return true;
            }
            catch (Exception ex) { Log.Error("Сигнал " + name, ex); return false; }
        }
    }
}
