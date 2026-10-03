using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

namespace PodsvetkaMyshi
{
    internal static class Clock
    {
        private static readonly double Factor = 1000.0 / Stopwatch.Frequency;
        public static long NowMs { get { return (long)(Stopwatch.GetTimestamp() * Factor); } }
    }

    /// <summary>Чистая математика эффектов (без Windows), покрыта тестами.</summary>
    internal static class EffectMath
    {
        public static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        public static double SmoothStep(double e0, double e1, double x)
        {
            double t = Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        public static double EaseOutCubic(double p) { p = Clamp01(p); double q = 1 - p; return 1 - q * q * q; }

        /// <summary>Пиксели при масштабе 100 % → физические пиксели монитора.</summary>
        public static int Scale(int logicalPx, int dpi)
        {
            if (dpi <= 0) dpi = 96;
            return Math.Max(1, (int)Math.Round(logicalPx * dpi / 96.0, MidpointRounding.AwayFromZero));
        }

        /// <summary>Яркость подсветки на расстоянии d от центра (0 — центр, 1 — край): плавно гаснет к краю.</summary>
        public static double GlowFalloff(double d)
        {
            if (d < 0) d = -d;
            if (d >= 1) return 0;
            double s = SmoothStep(0.12, 1.0, d);
            return (1 - s) * (1 - s * 0.35);
        }

        /// <summary>Изображение подсветки: квадрат (2R+1)², центр — в пикселе (R,R).
        /// BGRA, строки сверху вниз, цвет умножен на альфу (premultiplied) — как требует UpdateLayeredWindow.</summary>
        public static byte[] RenderGlow(int radius, Color color, double opacity, out int size)
        {
            radius = Math.Max(1, radius);
            size = 2 * radius + 1;
            opacity = Clamp01(opacity);
            var buf = new byte[size * size * 4];
            double inv = 1.0 / (radius + 0.5);
            for (int y = 0; y < size; y++)
            {
                double dy = y - radius;
                for (int x = 0; x < size; x++)
                {
                    double dx = x - radius;
                    double a = opacity * GlowFalloff(Math.Sqrt(dx * dx + dy * dy) * inv);
                    if (a <= 0) continue;
                    int A = (int)Math.Round(a * 255);
                    if (A <= 0) continue;
                    int i = (y * size + x) * 4;
                    buf[i] = (byte)((color.B * A + 127) / 255);
                    buf[i + 1] = (byte)((color.G * A + 127) / 255);
                    buf[i + 2] = (byte)((color.R * A + 127) / 255);
                    buf[i + 3] = (byte)A;
                }
            }
            return buf;
        }

        public static double Progress(long elapsedMs, int durationMs)
        {
            if (durationMs <= 0) return 1;
            return Clamp01((double)elapsedMs / durationMs);
        }

        /// <summary>Радиус круга: быстро растёт и плавно замедляется.</summary>
        public static double RippleRadius(double p, double r0, double r1) { return r0 + (r1 - r0) * EaseOutCubic(p); }

        /// <summary>Прозрачность круга: почти полная в начале, к концу — 0.</summary>
        public static double RippleAlpha(double p) { p = Clamp01(p); return 1 - p * p; }

        /// <summary>Квадрат, в который гарантированно помещается круг за всё время анимации.</summary>
        public static Rectangle RippleBounds(Point center, double maxRadius, double thickness)
        {
            int half = (int)Math.Ceiling(maxRadius + thickness / 2.0 + 2);
            return new Rectangle(center.X - half, center.Y - half, 2 * half + 1, 2 * half + 1);
        }
    }

    internal enum MouseButton { Left, Right }

    internal sealed class Ripple
    {
        public Point Center;
        public long StartMs;
        public int DurationMs;
        public double R0, R1, Thickness;
        public Color Color;

        public Rectangle Bounds { get { return EffectMath.RippleBounds(Center, R1, Thickness); } }
        public double ProgressAt(long now) { return EffectMath.Progress(now - StartMs, DurationMs); }
        public bool DoneAt(long now) { return now - StartMs >= DurationMs; }

        public static Ripple Create(Point center, long now, AppSettings s, MouseButton b, int dpi)
        {
            double r1 = EffectMath.Scale(s.RippleSize, dpi) / 2.0;
            return new Ripple
            {
                Center = center, StartMs = now, DurationMs = s.RippleDuration,
                R1 = r1, R0 = Math.Max(2, r1 * 0.12),
                Thickness = Math.Max(1, EffectMath.Scale(s.RippleThickness, dpi)),
                Color = b == MouseButton.Right ? s.RippleRightColor : s.RippleLeftColor,
            };
        }
    }

    /// <summary>Набор активных кругов.</summary>
    internal sealed class RippleSet
    {
        public const int MaxCount = 16, MaxBoxSide = 2048;
        public readonly List<Ripple> Items = new List<Ripple>();

        public void Add(Ripple r)
        {
            Items.Add(r);
            while (Items.Count > MaxCount) Items.RemoveAt(0);
            // Щелчки в разных концах большого экрана: окно не должно разрастаться бесконечно — старые круги убираем
            while (Items.Count > 1)
            {
                Rectangle u = Union();
                if (u.Width <= MaxBoxSide && u.Height <= MaxBoxSide) break;
                Items.RemoveAt(0);
            }
        }

        public void RemoveDone(long now) { Items.RemoveAll(r => r.DoneAt(now)); }

        public Rectangle Union()
        {
            Rectangle u = Rectangle.Empty;
            for (int i = 0; i < Items.Count; i++) u = i == 0 ? Items[i].Bounds : Rectangle.Union(u, Items[i].Bounds);
            return u;
        }
    }

    /// <summary>Плавное появление/исчезание подсветки (режим «только при движении» и скрытый курсор).</summary>
    internal sealed class GlowFade
    {
        public const int FadeInMs = 120, FadeOutMs = 350, IdleBeforeHideMs = 900;
        public double Value;

        public GlowFade(double initial) { Value = EffectMath.Clamp01(initial); }

        public static bool Target(AppSettings s, long now, long lastMoveMs, bool cursorHidden)
        {
            if (!s.GlowEnabled) return false;
            if (s.HideWhenCursorHidden && cursorHidden) return false;
            if (s.GlowMode == GlowMode.WhileMoving) return now - lastMoveMs < IdleBeforeHideMs;
            return true;
        }

        /// <returns>true, если значение ещё меняется (нужны следующие кадры).</returns>
        public bool Step(double dtMs, bool visible)
        {
            if (dtMs < 0) dtMs = 0;
            if (visible) Value = Math.Min(1, Value + dtMs / FadeInMs);
            else Value = Math.Max(0, Value - dtMs / FadeOutMs);
            return visible ? Value < 1 : Value > 0;
        }

        public byte Alpha { get { return (byte)Math.Round(EffectMath.Clamp01(Value) * 255); } }
    }

    /// <summary>Запасное определение щелчков опросом кнопок (если перехватчик мыши молчит —
    /// например, над окнами администратора или если Windows сняла перехватчик). Не дублирует щелчки перехватчика.</summary>
    internal sealed class PollClickDetector
    {
        public const int DedupeMs = 400;
        private readonly bool[] prevDown = new bool[2];
        private readonly long[] lastHook = { long.MinValue / 2, long.MinValue / 2 };
        private readonly long[] lastPoll = { long.MinValue / 2, long.MinValue / 2 };

        /// <returns>true — показать круг (этот щелчок ещё не показан опросом).</returns>
        public bool OnHookDown(MouseButton b, long now)
        {
            int i = (int)b;
            lastHook[i] = now;
            prevDown[i] = true;
            return now - lastPoll[i] > DedupeMs;
        }

        /// <returns>true — опрос заметил нажатие, которое перехватчик не сообщил.</returns>
        public bool OnPoll(MouseButton b, bool down, long now)
        {
            int i = (int)b;
            bool edge = down && !prevDown[i];
            prevDown[i] = down;
            if (edge && now - lastHook[i] > DedupeMs) { lastPoll[i] = now; return true; }
            return false;
        }
    }
}
