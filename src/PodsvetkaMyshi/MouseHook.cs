using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PodsvetkaMyshi
{
    /// <summary>Перехватчик мыши WH_MOUSE_LL в отдельном потоке: обработчик только запоминает событие
    /// и будит поток отрисовки, поэтому никогда не тормозит мышь (иначе Windows снимает перехватчик).</summary>
    internal sealed class MouseHook : IDisposable
    {
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, int threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_MOUSE_LL = 14, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204;
        private const int MsgReinstall = Native.WM_APP + 1;

        private readonly HookProc proc;
        private readonly Action<int, int> onMove;
        private readonly Action<MouseButton, int, int> onDown;
        private Thread thread;
        private volatile int threadId;
        private IntPtr hook;
        private readonly ManualResetEvent started = new ManualResetEvent(false);
        public volatile bool Installed;
        public int LastError;
        private long lastEventMs;
        public long LastEventMs { get { return Interlocked.Read(ref lastEventMs); } }

        public MouseHook(Action<int, int> onMove, Action<MouseButton, int, int> onDown)
        {
            this.onMove = onMove;
            this.onDown = onDown;
            proc = Callback; // держим ссылку, чтобы делегат не собрал сборщик мусора
        }

        public bool Start(int timeoutMs = 3000)
        {
            thread = new Thread(ThreadMain) { IsBackground = true, Name = "mouse-hook", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            started.WaitOne(timeoutMs);
            return Installed;
        }

        private void Install()
        {
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            LastError = hook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            Installed = hook != IntPtr.Zero;
            Interlocked.Exchange(ref lastEventMs, Clock.NowMs); // отсчёт «молчания» — с момента установки
            if (!Installed) Log.Write("Перехватчик мыши не установлен, код " + LastError);
        }

        private void Uninstall()
        {
            if (hook != IntPtr.Zero) { try { UnhookWindowsHookEx(hook); } catch { } hook = IntPtr.Zero; }
            Installed = false;
        }

        private void ThreadMain()
        {
            try
            {
                Native.MSG msg;
                Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); // создаём очередь сообщений до сигнала «готово»
                threadId = Native.GetCurrentThreadId();
                Install();
                started.Set();
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero && msg.message == MsgReinstall)
                    {
                        Uninstall(); Install();
                        Log.Write("Перехватчик мыши переустановлен: " + (Installed ? "успешно" : "ошибка " + LastError));
                        continue;
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
            }
            catch (Exception ex) { Log.Error("Поток перехватчика мыши", ex); }
            finally { Uninstall(); started.Set(); }
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int m = wParam.ToInt32();
                    if (m == WM_MOUSEMOVE || m == WM_LBUTTONDOWN || m == WM_RBUTTONDOWN)
                    {
                        int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
                        Interlocked.Exchange(ref lastEventMs, Clock.NowMs);
                        if (m == WM_MOUSEMOVE) onMove(x, y);
                        else onDown(m == WM_LBUTTONDOWN ? MouseButton.Left : MouseButton.Right, x, y);
                    }
                }
                catch { /* ни при каких условиях не мешаем мыши */ }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        /// <summary>Переустановить (если Windows сняла перехватчик из-за задержки).</summary>
        public void Reinstall()
        {
            int id = threadId;
            if (id != 0) Native.PostThreadMessage(id, MsgReinstall, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            int id = threadId;
            if (id != 0) Native.PostThreadMessage(id, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (thread != null) thread.Join(2000);
        }
    }
}
