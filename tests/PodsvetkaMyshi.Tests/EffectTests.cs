using System;
using System.Drawing;
using Xunit;

namespace PodsvetkaMyshi.Tests
{
    public class EffectTests
    {
        [Theory]
        [InlineData(80, 96, 80)]
        [InlineData(80, 120, 100)]
        [InlineData(80, 144, 120)]
        [InlineData(80, 192, 160)]
        [InlineData(1, 96, 1)]
        [InlineData(0, 96, 1)]
        [InlineData(80, 0, 80)]
        public void Scale_ByDpi(int px, int dpi, int expected) { Assert.Equal(expected, EffectMath.Scale(px, dpi)); }

        [Fact]
        public void GlowFalloff_BrightCenter_ZeroEdge_Monotonic()
        {
            Assert.Equal(1.0, EffectMath.GlowFalloff(0), 6);
            Assert.Equal(0.0, EffectMath.GlowFalloff(1), 6);
            Assert.Equal(0.0, EffectMath.GlowFalloff(1.5), 6);
            double prev = 2;
            for (double d = 0; d <= 1.0001; d += 0.01)
            {
                double v = EffectMath.GlowFalloff(d);
                Assert.InRange(v, 0, 1);
                Assert.True(v <= prev + 1e-12, "должно убывать: d=" + d);
                prev = v;
            }
            Assert.True(EffectMath.GlowFalloff(0.5) > 0.3 && EffectMath.GlowFalloff(0.9) < 0.1, "мягкий край");
        }

        [Fact]
        public void RenderGlow_Premultiplied_CenteredAndTransparentCorners()
        {
            int size;
            var c = Color.FromArgb(255, 242, 0);
            byte[] px = EffectMath.RenderGlow(40, c, 0.5, out size);
            Assert.Equal(81, size);
            Assert.Equal(81 * 81 * 4, px.Length);
            int ci = (40 * size + 40) * 4;
            Assert.Equal(128, px[ci + 3]); // альфа в центре = 50 %
            Assert.Equal(128, px[ci + 2]); // R = 255·a
            Assert.Equal(0, px[ci]);       // B = 0
            Assert.Equal(0, px[3]);        // угол прозрачный
            Assert.Equal(0, px[(size * size - 1) * 4 + 3]);
            for (int i = 0; i < px.Length; i += 4)
                Assert.True(px[i] <= px[i + 3] && px[i + 1] <= px[i + 3] && px[i + 2] <= px[i + 3], "premultiplied: цвет ≤ альфы");
            // симметрия
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    Assert.Equal(px[(y * size + x) * 4 + 3], px[(y * size + (size - 1 - x)) * 4 + 3]);
        }

        [Fact]
        public void RenderGlow_OpacityAndRadiusAreClamped()
        {
            int size;
            byte[] px = EffectMath.RenderGlow(0, Color.White, 5, out size);
            Assert.Equal(3, size);
            Assert.Equal(255, px[(1 * 3 + 1) * 4 + 3]);
        }

        [Fact]
        public void Ripple_GrowsFromSmallAndFades()
        {
            Assert.Equal(5, EffectMath.RippleRadius(0, 5, 36), 6);
            Assert.Equal(36, EffectMath.RippleRadius(1, 5, 36), 6);
            Assert.Equal(1, EffectMath.RippleAlpha(0), 6);
            Assert.Equal(0, EffectMath.RippleAlpha(1), 6);
            double pr = -1, pa = 2;
            for (double p = 0; p <= 1.0001; p += 0.02)
            {
                double r = EffectMath.RippleRadius(p, 5, 36), a = EffectMath.RippleAlpha(p);
                Assert.True(r >= pr - 1e-9); Assert.True(a <= pa + 1e-9);
                pr = r; pa = a;
            }
            Assert.True(EffectMath.RippleRadius(0.5, 5, 36) > 30, "ease-out: к середине круг почти вырос");
        }

        [Fact]
        public void Progress_Clamps()
        {
            Assert.Equal(0, EffectMath.Progress(-10, 500));
            Assert.Equal(0.5, EffectMath.Progress(250, 500), 6);
            Assert.Equal(1, EffectMath.Progress(900, 500));
            Assert.Equal(1, EffectMath.Progress(0, 0));
        }

        [Fact]
        public void Ripple_Create_UsesButtonColorAndDpi()
        {
            var s = new AppSettings { RippleSize = 72, RippleThickness = 2, RippleDuration = 500 };
            var l = Ripple.Create(new Point(10, 10), 1000, s, MouseButton.Left, 96);
            var r = Ripple.Create(new Point(10, 10), 1000, s, MouseButton.Right, 192);
            Assert.Equal(s.RippleLeftColor.ToArgb(), l.Color.ToArgb());
            Assert.Equal(s.RippleRightColor.ToArgb(), r.Color.ToArgb());
            Assert.Equal(36, l.R1, 6);
            Assert.Equal(72, r.R1, 6);
            Assert.Equal(4, r.Thickness, 6);
            Assert.True(l.R0 < l.R1 / 4);
            Assert.False(l.DoneAt(1499)); Assert.True(l.DoneAt(1500));
        }

        [Fact]
        public void RippleBounds_ContainsWholeCircle()
        {
            var b = EffectMath.RippleBounds(new Point(100, 200), 36, 3);
            Assert.True(b.Left <= 100 - 36 - 1.5 && b.Right >= 100 + 36 + 1.5);
            Assert.True(b.Top <= 200 - 36 - 1.5 && b.Bottom >= 200 + 36 + 1.5);
            Assert.Equal(b.Width, b.Height);
            Assert.Equal(100, b.X + b.Width / 2); // центр
        }

        [Fact]
        public void RippleSet_UnionAndLimits()
        {
            var s = new AppSettings();
            var set = new RippleSet();
            set.Add(Ripple.Create(new Point(0, 0), 0, s, MouseButton.Left, 96));
            set.Add(Ripple.Create(new Point(300, 100), 10, s, MouseButton.Right, 96));
            var u = set.Union();
            Assert.True(u.Contains(set.Items[0].Bounds) && u.Contains(set.Items[1].Bounds));
            // слишком далеко — старые круги убираются, окно не разрастается
            set.Add(Ripple.Create(new Point(20000, 0), 20, s, MouseButton.Left, 96));
            Assert.Single(set.Items);
            Assert.True(set.Union().Width <= RippleSet.MaxBoxSide);
            for (int i = 0; i < 40; i++) set.Add(Ripple.Create(new Point(i, i), 30 + i, s, MouseButton.Left, 96));
            Assert.Equal(RippleSet.MaxCount, set.Items.Count);
            set.RemoveDone(30 + 39 + s.RippleDuration);
            Assert.Empty(set.Items);
        }

        [Fact]
        public void GlowFade_AlwaysMode_VisibleAfterFadeIn_AndStaysVisibleWhenIdle()
        {
            var s = new AppSettings();
            var f = new GlowFade(0);
            Assert.True(GlowFade.Target(s, 100000, 0, false)); // давно не двигалась — всё равно видна
            bool more = f.Step(GlowFade.FadeInMs / 2.0, true);
            Assert.True(more);
            more = f.Step(GlowFade.FadeInMs, true);
            Assert.False(more);
            Assert.Equal(255, f.Alpha);
        }

        [Fact]
        public void GlowFade_MovingMode_HidesAfterIdle()
        {
            var s = new AppSettings { GlowMode = GlowMode.WhileMoving };
            Assert.True(GlowFade.Target(s, 1000, 900, false));
            Assert.False(GlowFade.Target(s, 1000 + GlowFade.IdleBeforeHideMs, 1000, false));
            var f = new GlowFade(1);
            f.Step(GlowFade.FadeOutMs / 2.0, false);
            Assert.InRange(f.Value, 0.4, 0.6);
            Assert.False(f.Step(GlowFade.FadeOutMs, false));
            Assert.Equal(0, f.Alpha);
        }

        [Fact]
        public void GlowFade_DisabledOrHiddenCursor()
        {
            Assert.False(GlowFade.Target(new AppSettings { GlowEnabled = false }, 0, 0, false));
            Assert.False(GlowFade.Target(new AppSettings { HideWhenCursorHidden = true }, 0, 0, true));
            Assert.True(GlowFade.Target(new AppSettings { HideWhenCursorHidden = false }, 0, 0, true));
        }

        [Fact]
        public void PollClicks_DoNotDuplicateHookClicks()
        {
            var d = new PollClickDetector();
            // перехватчик сообщил, опрос видит ту же нажатую кнопку — дубля нет
            Assert.True(d.OnHookDown(MouseButton.Left, 1000));
            Assert.False(d.OnPoll(MouseButton.Left, true, 1050));
            Assert.False(d.OnPoll(MouseButton.Left, false, 1150));
            // перехватчик молчит (окно администратора) — опрос замечает нажатие
            Assert.True(d.OnPoll(MouseButton.Left, true, 3000));
            Assert.False(d.OnPoll(MouseButton.Left, true, 3100)); // удержание — не новый щелчок
            Assert.False(d.OnPoll(MouseButton.Left, false, 3200));
            // опрос успел раньше, перехватчик сообщил позже — тоже без дубля
            Assert.True(d.OnPoll(MouseButton.Right, true, 5000));
            Assert.False(d.OnHookDown(MouseButton.Right, 4990));
            // двойной щелчок через перехватчик — два круга
            Assert.True(d.OnHookDown(MouseButton.Left, 8000));
            Assert.True(d.OnHookDown(MouseButton.Left, 8150));
        }
    }
}
