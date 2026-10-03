using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;

namespace PodsvetkaMyshi
{
    /// <summary>Отрисовка подсветки и кругов в собственном потоке. В покое поток спит (проверка раз в 100 мс),
    /// во время движения и анимации — до ~60 кадров в секунду.</summary>
    internal sealed class Overlay : IDisposable
    {
        private const int IdleTickMs = 100, FrameMs = 16;

        private struct Click { public MouseButton Button; public int X, Y; public long Ms; }

        private volatile AppSettings settings;
        private int settingsVersion;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly object clickSync = new object();
        private readonly List<Click> clicks = new List<Click>();
        private long hookPos, hookMoveMs;
        private Thread thread;
        private volatile bool stopping;
        private MouseHook hook;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);

        /// <summary>Сообщение для пользователя (вызывается из потока отрисовки, не чаще раза в минуту).</summary>
        public event Action<string> Problem;
        public volatile IntPtr GlowHandle, RippleHandle;
        public bool HookInstalled { get { return hook != null && hook.Installed; } }
        public int HookError { get { return hook == null ? 0 : hook.LastError; } }
        public long Frames;

        public Overlay(AppSettings s) { settings = s.Clone(); }

        public void Start()
        {
            hook = new MouseHook(OnHookMove, OnHookDown);
            if (!hook.Start())
                RaiseProblem("Windows не дала подключиться к мыши (код " + hook.LastError + "). Подсветка работает, а круги при щелчке могут появляться с задержкой.");
            thread = new Thread(ThreadMain) { IsBackground = true, Name = "overlay" };
            thread.Start();
            ready.WaitOne(3000);
        }

        public void Apply(AppSettings s)
        {
            settings = s.Clone();
            Interlocked.Increment(ref settingsVersion);
            wake.Set();
        }

        // ---------- события перехватчика (поток перехватчика: только запомнить и разбудить) ----------

        private static long Pack(int x, int y) { return ((long)x << 32) | (uint)y; }
        private static Point Unpack(long v) { return new Point((int)(v >> 32), (int)(v & 0xFFFFFFFF)); }

        private void OnHookMove(int x, int y)
        {
            Interlocked.Exchange(ref hookPos, Pack(x, y));
            Interlocked.Exchange(ref hookMoveMs, Clock.NowMs);
            wake.Set();
        }

        private void OnHookDown(MouseButton b, int x, int y)
        {
            lock (clickSync)
            {
                if (clicks.Count < 64) clicks.Add(new Click { Button = b, X = x, Y = y, Ms = Clock.NowMs });
            }
            wake.Set();
        }

        // ---------- поток отрисовки ----------

        private LayeredWindow glow, ripple;
        private IntPtr timer;
        private readonly RippleSet ripples = new RippleSet();
        private readonly GlowFade fade = new GlowFade(0);
        private readonly PollClickDetector poll = new PollClickDetector();
        private Point lastPos = new Point(int.MinValue, int.MinValue);
        private long lastMoveMs, lastHookSeenMs, lastZ, lastCursorCheck, lastSwapCheck, lastHookHealth, lastReinstall, lastProblem = long.MinValue / 2;
        private IntPtr lastMon;
        private int dpi = 96, appliedVersion = -1;
        private bool cursorHidden, swapped, displayDirty = true;
        private string spriteKey;
        private int spriteRadius;
        private Rectangle virt;

        private void ThreadMain()
        {
            int failures = 0;
            long firstFailure = 0;
            while (!stopping)
            {
                try
                {
                    CreateWindows();
                    ready.Set();
                    Loop();
                }
                catch (Exception ex)
                {
                    Log.Error("Отрисовка", ex);
                    long now = Clock.NowMs;
                    if (failures == 0 || now - firstFailure > 60000) { failures = 0; firstFailure = now; }
                    failures++;
                    if (failures == 3) RaiseProblem("Подсветка несколько раз сбоила и перезапускается. Подробности — в журнале.");
                    Thread.Sleep(failures > 3 ? 2000 : 300);
                }
                finally
                {
                    DestroyWindows();
                    ready.Set();
                }
            }
        }

        private void CreateWindows()
        {
            glow = new LayeredWindow("PodsvetkaMyshi.Glow");
            ripple = new LayeredWindow("PodsvetkaMyshi.Ripple");
            GlowHandle = glow.Handle; RippleHandle = ripple.Handle;
            if (timer == IntPtr.Zero)
            {
                timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
                if (timer == IntPtr.Zero) timer = Native.CreateWaitableTimerExW(IntPtr.Zero, null, 0, Native.TIMER_ALL_ACCESS);
            }
            spriteKey = null; appliedVersion = -1; displayDirty = true;
            ripples.Items.Clear();
        }

        private void DestroyWindows()
        {
            GlowHandle = IntPtr.Zero; RippleHandle = IntPtr.Zero;
            if (glow != null) { glow.Dispose(); glow = null; }
            if (ripple != null) { ripple.Dispose(); ripple = null; }
        }

        private void Loop()
        {
            long last = Clock.NowMs;
            bool animating = true;
            while (!stopping)
            {
                if (animating) WaitFrame(); else WaitIdle();
                if (stopping) return;
                long now = Clock.NowMs;
                double dt = Math.Min(100, Math.Max(0, now - last));
                last = now;
                animating = Frame(now, dt);
                Interlocked.Increment(ref Frames);
            }
        }

        private bool Pump()
        {
            Native.MSG msg;
            int n = 0;
            while (n++ < 200 && Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
            {
                if (msg.message == Native.WM_QUIT) { stopping = true; return false; }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            return true;
        }

        private void WaitOn(IntPtr handle, int timeoutMs)
        {
            long deadline = Clock.NowMs + timeoutMs;
            var handles = new[] { handle };
            while (!stopping)
            {
                int left = (int)Math.Max(0, deadline - Clock.NowMs);
                int r = Native.MsgWaitForMultipleObjectsEx(1, handles, left, Native.QS_ALLINPUT, Native.MWMO_INPUTAVAILABLE);
                if (r == 1) { if (!Pump()) return; if (left == 0) return; continue; } // пришло сообщение окну
                Pump();
                return; // сработал таймер/событие, истекло время или ошибка
            }
        }

        private void WaitFrame()
        {
            if (timer != IntPtr.Zero)
            {
                long due = -FrameMs * 10000L - 6667; // ≈16,7 мс, относительное время
                if (Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) { WaitOn(timer, 50); return; }
            }
            Thread.Sleep(FrameMs);
            Pump();
        }

        private void WaitIdle() { WaitOn(wake.SafeWaitHandle.DangerousGetHandle(), IdleTickMs); }

        private Point Clamp(Point p)
        {
            if (virt.Width <= 0) return p;
            return new Point(Math.Max(virt.Left, Math.Min(virt.Right - 1, p.X)), Math.Max(virt.Top, Math.Min(virt.Bottom - 1, p.Y)));
        }

        private int DpiAt(Point p)
        {
            IntPtr mon = Native.MonitorAt(p.X, p.Y);
            return mon == lastMon ? dpi : Native.DpiOfMonitor(mon);
        }

        private bool Frame(long now, double dt)
        {
            AppSettings s = settings;
            int ver = Volatile.Read(ref settingsVersion);
            if (ver != appliedVersion)
            {
                appliedVersion = ver;
                spriteKey = null;
                bool g1 = glow.SetExcludeFromCapture(s.HideFromCapture), g2 = ripple.SetExcludeFromCapture(s.HideFromCapture);
                if (s.HideFromCapture && !(g1 && g2)) Log.Write("SetWindowDisplayAffinity не поддерживается этой версией Windows");
            }
            if (glow.DisplayChanged || ripple.DisplayChanged || displayDirty)
            {
                glow.DisplayChanged = ripple.DisplayChanged = false;
                displayDirty = false;
                virt = Native.VirtualScreen();
                lastMon = IntPtr.Zero;
                spriteKey = null;
            }

            // --- где курсор ---
            Point pos;
            long hm = Interlocked.Read(ref hookMoveMs);
            if (hm != lastHookSeenMs) { lastHookSeenMs = hm; pos = Clamp(Unpack(Interlocked.Read(ref hookPos))); }
            else
            {
                Native.POINT p;
                pos = Native.GetCursorPos(out p) ? new Point(p.X, p.Y) : lastPos;
            }
            bool moved = pos != lastPos;
            if (moved) { lastPos = pos; lastMoveMs = now; }

            IntPtr mon = Native.MonitorAt(pos.X, pos.Y);
            if (mon != lastMon) { lastMon = mon; dpi = Native.DpiOfMonitor(mon); }

            // --- щелчки ---
            Click[] pending = null;
            lock (clickSync) { if (clicks.Count > 0) { pending = clicks.ToArray(); clicks.Clear(); } }
            if (pending != null)
                foreach (var c in pending)
                    if (poll.OnHookDown(c.Button, c.Ms)) AddRipple(s, c.Button, Clamp(new Point(c.X, c.Y)), now);

            if (now - lastSwapCheck > 2000) { swapped = Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0; lastSwapCheck = now; }
            bool lDown = Native.GetAsyncKeyState(swapped ? Native.VK_RBUTTON : Native.VK_LBUTTON) < 0;
            bool rDown = Native.GetAsyncKeyState(swapped ? Native.VK_LBUTTON : Native.VK_RBUTTON) < 0;
            if (poll.OnPoll(MouseButton.Left, lDown, now)) AddRipple(s, MouseButton.Left, pos, now);
            if (poll.OnPoll(MouseButton.Right, rDown, now)) AddRipple(s, MouseButton.Right, pos, now);

            CheckHookHealth(now, moved);

            // --- подсветка ---
            if (s.HideWhenCursorHidden)
            {
                if (now - lastCursorCheck > 250) { lastCursorCheck = now; cursorHidden = Native.CursorShowing() == false; }
            }
            else cursorHidden = false;
            bool target = GlowFade.Target(s, now, lastMoveMs, cursorHidden);
            bool fading = fade.Step(dt, target);
            if (fade.Value > 0 && s.GlowEnabled)
            {
                int radius = Math.Max(2, EffectMath.Scale(s.GlowSize, dpi) / 2);
                string key = radius + "|" + s.GlowColor.ToArgb() + "|" + s.GlowOpacity;
                if (key != spriteKey)
                {
                    int size;
                    byte[] px = EffectMath.RenderGlow(radius, s.GlowColor, s.GlowOpacity / 100.0, out size);
                    glow.CopyPixels(px, size, size);
                    spriteKey = key; spriteRadius = radius;
                    glow.Commit(pos.X - radius, pos.Y - radius, size, size, fade.Alpha);
                }
                else if (!glow.Move(pos.X - spriteRadius, pos.Y - spriteRadius, fade.Alpha))
                    glow.Commit(pos.X - spriteRadius, pos.Y - spriteRadius, 2 * spriteRadius + 1, 2 * spriteRadius + 1, fade.Alpha);
            }
            else glow.Hide();

            // --- круги ---
            ripples.RemoveDone(now);
            if (ripples.Items.Count > 0) DrawRipples(now);
            else { ripple.Hide(); ripple.TrimCanvas(512 * 512); }

            bool animating = ripples.Items.Count > 0 || fading || now - lastMoveMs < 80;
            if (now - lastZ > (animating ? 250 : 1000))
            {
                lastZ = now;
                glow.BringToTop();
                ripple.BringToTop(); // круги поверх подсветки
            }
            return animating;
        }

        private void AddRipple(AppSettings s, MouseButton b, Point p, long now)
        {
            if (b == MouseButton.Left ? !s.RippleLeft : !s.RippleRight) return;
            ripples.Add(Ripple.Create(p, now, s, b, DpiAt(p)));
        }

        private void DrawRipples(long now)
        {
            Rectangle box = ripples.Union();
            Graphics g = ripple.Canvas(box.Width, box.Height, false);
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, 0, 0, box.Width, box.Height);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            foreach (var r in ripples.Items)
            {
                double p = r.ProgressAt(now);
                double rad = EffectMath.RippleRadius(p, r.R0, r.R1);
                int a = (int)Math.Round(EffectMath.RippleAlpha(p) * 255);
                if (a <= 0) continue;
                using (var pen = new Pen(Color.FromArgb(a, r.Color), (float)r.Thickness))
                    g.DrawEllipse(pen, (float)(r.Center.X - box.X - rad), (float)(r.Center.Y - box.Y - rad), (float)(2 * rad), (float)(2 * rad));
            }
            g.Flush(FlushIntention.Sync);
            ripple.Commit(box.X, box.Y, box.Width, box.Height, 255);
        }

        /// <summary>Windows молча снимает перехватчик, если он хоть раз «завис». Курсор двигается, а перехватчик
        /// давно молчит — переустанавливаем (не чаще раза в 5 секунд).</summary>
        private void CheckHookHealth(long now, bool moved)
        {
            if (hook == null || !moved || now - lastHookHealth < 1000) return;
            lastHookHealth = now;
            if (now - hook.LastEventMs > 2000 && now - lastReinstall > 5000)
            {
                lastReinstall = now;
                hook.Reinstall();
            }
        }

        private void RaiseProblem(string text)
        {
            long now = Clock.NowMs;
            if (now - lastProblem < 60000) return;
            lastProblem = now;
            Log.Write("Проблема: " + text);
            var h = Problem;
            if (h != null) try { h(text); } catch { }
        }

        public void Dispose()
        {
            stopping = true;
            wake.Set();
            if (thread != null) thread.Join(2000);
            if (hook != null) hook.Dispose();
            if (timer != IntPtr.Zero) { Native.CloseHandle(timer); timer = IntPtr.Zero; }
        }
    }
}
