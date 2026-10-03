using System;
using System.Drawing;
using System.Threading;
using Xunit;

namespace PodsvetkaMyshi.Tests
{
    /// <summary>Тесты, которым нужен настоящий Windows с рабочим столом (в CI — windows-latest).</summary>
    public class WindowsTests
    {
        [Fact]
        public void LayeredWindow_IsClickThrough_NoActivate_NotInAltTab()
        {
            Sta.Run(() =>
            {
                Native.EnsureDpiAwareness();
                using (var w = new LayeredWindow("test"))
                {
                    int size;
                    byte[] px = EffectMath.RenderGlow(30, Color.Yellow, 1.0, out size);
                    w.CopyPixels(px, size, size);
                    Assert.True(w.Commit(200, 200, size, size, 255));
                    Assert.True(w.Visible);
                    int ex = Native.GetWindowLong(w.Handle, Native.GWL_EXSTYLE);
                    Assert.Equal(LayeredWindow.ExStyles, ex & LayeredWindow.ExStyles);
                    // Окно прозрачно для мыши: WindowFromPoint «видит» то, что под ним
                    Assert.NotEqual(w.Handle, Native.WindowFromPoint(new Native.POINT(230, 230)));
                    Assert.True(w.Move(260, 260, 128));
                    Assert.Equal(new Rectangle(260, 260, size, size), w.Bounds);
                    w.Hide();
                    Assert.False(w.Visible);
                }
            });
        }

        [Fact]
        public void Overlay_StartsHookAndRenders_ThenStopsCleanly()
        {
            var ov = new Overlay(new AppSettings());
            ov.Start();
            try
            {
                Assert.True(ov.HookInstalled, "перехватчик мыши установлен, код " + ov.HookError);
                Assert.NotEqual(IntPtr.Zero, ov.GlowHandle);
                Thread.Sleep(400);
                Assert.True(Interlocked.Read(ref ov.Frames) > 0);
                ov.Apply(new AppSettings { GlowSize = 200, GlowColor = Color.Cyan });
                Thread.Sleep(200);
            }
            finally { ov.Dispose(); }
            Assert.Equal(IntPtr.Zero, ov.GlowHandle);
        }
    }
}
