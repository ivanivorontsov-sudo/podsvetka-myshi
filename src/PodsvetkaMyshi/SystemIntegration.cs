using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PodsvetkaMyshi
{
    /// <summary>Автозапуск только через HKCU\...\Run: без Планировщика заданий (там и возникает
    /// «Оператор или администратор отклонил запрос», 0x800710E0) и без прав администратора.</summary>
    internal static class Autostart
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        public const string ValueName = "PodsvetkaMyshi";

        public static string CommandFor(string exePath) { return "\"" + exePath + "\" --autostart"; }

        public static string ExePathFromCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : null;
            }
            int sp = cmd.IndexOf(' ');
            return sp > 0 ? cmd.Substring(0, sp) : cmd;
        }

        public static bool IsEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(ValueName) is string; }
            catch { return false; }
        }

        /// <summary>Отключён ли автозапуск в «Диспетчер задач → Автозагрузка» / «Параметры → Приложения → Автозагрузка».</summary>
        public static bool IsDisabledByWindows()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    var v = k == null ? null : k.GetValue(ValueName) as byte[];
                    return v != null && v.Length > 0 && (v[0] & 1) == 1;
                }
            }
            catch { return false; }
        }

        public static void Enable(string exePath)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                k.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
            // Пользователь явно включил — снимаем отметку «Отключено» из Диспетчера задач
            try { using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) if (k != null) k.DeleteValue(ValueName, false); } catch { }
        }

        public static void Disable()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (k != null) k.DeleteValue(ValueName, false);
        }

        /// <summary>Если exe перенесли — обновить путь в автозапуске.</summary>
        public static void RefreshPath(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string cur = k == null ? null : k.GetValue(ValueName) as string;
                    if (cur != null && !string.Equals(ExePathFromCommand(cur), exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        k.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
                        Log.Write("Путь автозапуска обновлён: " + exePath);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Обновление автозапуска", ex); }
        }

        public static bool IsUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            return (path.TrimEnd('\\') + "\\").StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Запуск из места, откуда автозапуск сломается (временная папка — например, прямо из архива).</summary>
        public static string RiskyLocationWarning(string exePath, string temp, Func<string, bool> isRemovable)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (IsUnder(exePath, temp))
                return "Программа запущена из временной папки (возможно, прямо из архива), поэтому автозапуск не включён. Распакуйте PodsvetkaMyshi.exe в постоянную папку и запустите оттуда.";
            if (isRemovable != null && isRemovable(exePath))
                return "Программа запущена со съёмного диска, поэтому автозапуск не включён. Скопируйте PodsvetkaMyshi.exe на компьютер и запустите оттуда.";
            return null;
        }

        public static bool IsRemovable(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Removable; } catch { return false; }
        }

        /// <summary>Включать ли автозапуск сам при этом запуске.</summary>
        public static bool ShouldAutoEnable(bool alreadyConfigured, string riskyWarning)
        {
            return !alreadyConfigured && riskyWarning == null;
        }
    }

    internal static class ErrorText
    {
        public const int ERROR_ACCESS_DENIED = 5, ERROR_OPERATOR = 4320 /*0x10E0*/;

        public static int Win32Code(Exception ex)
        {
            int hr = ex == null ? 0 : ex.HResult;
            if ((hr & 0xFFFF0000) == 0x80070000) return hr & 0xFFFF;
            return 0;
        }

        public static string Friendly(Exception ex)
        {
            if (ex == null) return "неизвестная ошибка.";
            int c = Win32Code(ex);
            if (c == ERROR_OPERATOR) return "Windows отклонила операцию («Оператор или администратор отклонил запрос»).";
            if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || c == ERROR_ACCESS_DENIED)
                return "нет прав (возможно, мешает антивирус или политика компьютера).";
            if (ex is OutOfMemoryException) return "не хватило памяти.";
            if (ex is IOException) return "ошибка записи на диск (" + ex.Message.TrimEnd('.') + ").";
            return "непредвиденная ошибка (" + ex.Message.TrimEnd('.') + ").";
        }
    }

    internal static class Shell
    {
        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Открытие " + target, ex); throw; }
        }
    }

    internal static class Dialogs
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        public const string Title = "Подсветка мыши";
        private const uint MB_OK = 0, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, MB_ICONINFO = 0x40, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;

        public static void Info(string text) { MessageBoxW(IntPtr.Zero, text, Title, MB_OK | MB_ICONINFO | MB_TOPMOST | MB_SETFOREGROUND); }
        public static void Warn(string text) { MessageBoxW(IntPtr.Zero, text, Title, MB_OK | MB_ICONWARNING | MB_TOPMOST | MB_SETFOREGROUND); }
        public static void Error(string text) { MessageBoxW(IntPtr.Zero, text, Title, MB_OK | MB_ICONERROR | MB_TOPMOST | MB_SETFOREGROUND); }
    }

    /// <summary>Временно переключает поток в режим «system aware» (окна WinForms .NET 4.8 корректно масштабируются).</summary>
    internal sealed class DpiScope : IDisposable
    {
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        private readonly IntPtr old;
        private readonly bool active;

        private DpiScope(IntPtr ctx)
        {
            try { old = SetThreadDpiAwarenessContext(ctx); active = old != IntPtr.Zero; } catch { active = false; }
        }

        public static DpiScope SystemAware() { return new DpiScope(new IntPtr(-2)); }

        public void Dispose()
        {
            if (active) try { SetThreadDpiAwarenessContext(old); } catch { }
        }
    }
}
