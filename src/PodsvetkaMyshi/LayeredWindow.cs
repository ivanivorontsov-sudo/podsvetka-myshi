using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PodsvetkaMyshi
{
    /// <summary>Прозрачное для мыши окно поверх всех: не получает фокус, не видно в Alt+Tab и на панели задач,
    /// содержимое с попиксельной прозрачностью задаётся через UpdateLayeredWindow.</summary>
    internal sealed class LayeredWindow : NativeWindow, IDisposable
    {
        public const int ExStyles = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                                    Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;

        private IntPtr memDc, dib, oldBmp, bits;
        private int dibW, dibH;
        private Bitmap canvas;
        private Graphics graphics;
        private bool hasContent;
        public bool Visible { get; private set; }
        public Rectangle Bounds { get; private set; }
        public byte Alpha { get; private set; }
        /// <summary>Сменился DPI или конфигурация мониторов — пора перерисовать.</summary>
        public volatile bool DisplayChanged;

        public LayeredWindow(string title)
        {
            var cp = new CreateParams
            {
                Caption = title,
                Style = Native.WS_POPUP,
                ExStyle = ExStyles,
                X = -32000, Y = -32000, Width = 1, Height = 1,
            };
            CreateHandle(cp);
            memDc = Native.CreateCompatibleDC(IntPtr.Zero);
            if (memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC не удался");
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case Native.WM_NCHITTEST: m.Result = new IntPtr(Native.HTTRANSPARENT); return;   // щелчки проходят насквозь
                case Native.WM_MOUSEACTIVATE: m.Result = new IntPtr(Native.MA_NOACTIVATE); return; // и не активируют окно
                case Native.WM_DPICHANGED: DisplayChanged = true; m.Result = IntPtr.Zero; return;  // размер задаём сами
                case Native.WM_DISPLAYCHANGE: case Native.WM_SETTINGCHANGE: DisplayChanged = true; break;
            }
            base.WndProc(ref m);
        }

        /// <summary>Холст не меньше w×h (переиспользуется между кадрами).</summary>
        public Graphics Canvas(int w, int h, bool exact)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            bool fits = exact ? (w == dibW && h == dibH) : (w <= dibW && h <= dibH);
            if (!fits)
            {
                FreeDib();
                int nw = exact ? w : Math.Max(w, dibW), nh = exact ? h : Math.Max(h, dibH);
                var bmi = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                    biWidth = nw, biHeight = -nh, biPlanes = 1, biBitCount = 32, biCompression = 0,
                };
                dib = Native.CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                    throw new OutOfMemoryException("CreateDIBSection " + nw + "x" + nh + " не удался (код " + Marshal.GetLastWin32Error() + ")");
                oldBmp = Native.SelectObject(memDc, dib);
                dibW = nw; dibH = nh;
                canvas = new Bitmap(nw, nh, nw * 4, PixelFormat.Format32bppPArgb, bits);
                graphics = Graphics.FromImage(canvas);
            }
            return graphics;
        }

        public void CopyPixels(byte[] bgra, int w, int h)
        {
            Canvas(w, h, true);
            Marshal.Copy(bgra, 0, bits, w * h * 4);
        }

        /// <summary>Показать нарисованное (левый верхний угол холста w×h) в точке экрана x,y.</summary>
        public bool Commit(int x, int y, int w, int h, byte alpha)
        {
            Native.GdiFlush();
            var dst = new Native.POINT(x, y);
            var size = new Native.SIZE(w, h);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = alpha, AlphaFormat = Native.AC_SRC_ALPHA };
            if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
            {
                Log.Write("UpdateLayeredWindow: код " + Marshal.GetLastWin32Error());
                return false;
            }
            hasContent = true;
            Bounds = new Rectangle(x, y, w, h);
            Alpha = alpha;
            Show();
            return true;
        }

        /// <summary>Только сдвинуть/изменить общую прозрачность, без перерисовки.</summary>
        public bool Move(int x, int y, byte alpha)
        {
            if (!hasContent) return false;
            if (x == Bounds.X && y == Bounds.Y && alpha == Alpha && Visible) return true;
            var dst = new Native.POINT(x, y);
            var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = alpha, AlphaFormat = Native.AC_SRC_ALPHA };
            if (!Native.UpdateLayeredWindowMove(Handle, IntPtr.Zero, ref dst, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, Native.ULW_ALPHA))
                return false;
            Bounds = new Rectangle(x, y, Bounds.Width, Bounds.Height);
            Alpha = alpha;
            Show();
            return true;
        }

        private void Show()
        {
            if (Visible) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
            Visible = true;
        }

        public void Hide()
        {
            if (!Visible) return;
            Native.ShowWindow(Handle, Native.SW_HIDE);
            Visible = false;
        }

        /// <summary>Вернуть окно наверх (панель задач и другие «поверх всех» окна могли его перекрыть).</summary>
        public void BringToTop()
        {
            if (!Visible) return;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        /// <summary>Не попадать на скриншоты и запись экрана (Windows 10 2004+).</summary>
        public bool SetExcludeFromCapture(bool exclude)
        {
            try { return Native.SetWindowDisplayAffinity(Handle, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE); }
            catch { return false; }
        }

        /// <summary>Освободить большой холст, когда он не нужен (после кругов в разных концах экрана).</summary>
        public void TrimCanvas(int maxPixels)
        {
            if (!Visible && (long)dibW * dibH > maxPixels) { FreeDib(); hasContent = false; }
        }

        private void FreeDib()
        {
            if (graphics != null) { graphics.Dispose(); graphics = null; }
            if (canvas != null) { canvas.Dispose(); canvas = null; }
            if (dib != IntPtr.Zero)
            {
                Native.SelectObject(memDc, oldBmp);
                Native.DeleteObject(dib);
                dib = IntPtr.Zero; bits = IntPtr.Zero;
            }
            dibW = dibH = 0;
        }

        public void Dispose()
        {
            try { FreeDib(); } catch { }
            try { if (memDc != IntPtr.Zero) Native.DeleteDC(memDc); memDc = IntPtr.Zero; } catch { }
            try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { }
        }
    }
}
