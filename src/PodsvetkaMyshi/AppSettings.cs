using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace PodsvetkaMyshi
{
    internal enum GlowMode { Always, WhileMoving }

    /// <summary>Настройки. Неизменяемые снимки передаются потоку отрисовки через Clone().</summary>
    internal sealed class AppSettings
    {
        public const int GlowSizeMin = 20, GlowSizeMax = 400, OpacityMin = 5, OpacityMax = 100,
            RippleSizeMin = 16, RippleSizeMax = 400, DurationMin = 150, DurationMax = 3000, ThicknessMin = 1, ThicknessMax = 10;

        public static readonly Color DefaultGlowColor = Color.FromArgb(255, 242, 0);
        public static readonly Color DefaultLeftColor = Color.FromArgb(229, 32, 32);
        public static readonly Color DefaultRightColor = Color.FromArgb(30, 120, 230);

        public bool GlowEnabled = true;
        public Color GlowColor = DefaultGlowColor;
        /// <summary>Диаметр подсветки в пикселях при масштабе 100 %.</summary>
        public int GlowSize = 80;
        /// <summary>Непрозрачность в центре, %.</summary>
        public int GlowOpacity = 50;
        public GlowMode GlowMode = GlowMode.Always;

        public bool RippleLeft = true;
        public Color RippleLeftColor = DefaultLeftColor;
        public bool RippleRight = true;
        public Color RippleRightColor = DefaultRightColor;
        /// <summary>Конечный диаметр круга в пикселях при масштабе 100 %.</summary>
        public int RippleSize = 72;
        public int RippleDuration = 500;
        public int RippleThickness = 2;

        public bool HideFromCapture = false;
        public bool HideWhenCursorHidden = false;

        /// <summary>Автозапуск уже настраивали (включили при первом запуске или пользователь менял сам).
        /// Пока false — при запуске автозапуск включается автоматически.</summary>
        public bool AutostartConfigured = false;

        public readonly List<string> LoadWarnings = new List<string>();

        public AppSettings Clone()
        {
            var c = (AppSettings)MemberwiseClone();
            return c;
        }

        /// <summary>Совпадают ли параметры эффекта (для сравнения в окне настроек).</summary>
        public bool SameEffect(AppSettings o)
        {
            return o != null && Serialize() == o.Serialize();
        }

        // ---------- цвет ----------

        public static string ColorToText(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public static bool TryParseColor(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            string[] parts = s.Split(',', ';');
            if (parts.Length == 3)
            {
                int r, g, b;
                if (int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) &&
                    int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out g) &&
                    int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out b) &&
                    r >= 0 && r <= 255 && g >= 0 && g <= 255 && b >= 0 && b <= 255)
                { c = Color.FromArgb(r, g, b); return true; }
                return false;
            }
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            if (s.Length != 6) return false;
            int v;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
            return true;
        }

        // ---------- текст ----------

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Подсветка мыши — настройки. Файл можно удалить: будут настройки по умолчанию.");
            sb.AppendLine("GlowEnabled=" + B(GlowEnabled));
            sb.AppendLine("GlowColor=" + ColorToText(GlowColor));
            sb.AppendLine("GlowSize=" + I(GlowSize));
            sb.AppendLine("GlowOpacity=" + I(GlowOpacity));
            sb.AppendLine("GlowMode=" + (GlowMode == GlowMode.WhileMoving ? "moving" : "always"));
            sb.AppendLine("RippleLeft=" + B(RippleLeft));
            sb.AppendLine("RippleLeftColor=" + ColorToText(RippleLeftColor));
            sb.AppendLine("RippleRight=" + B(RippleRight));
            sb.AppendLine("RippleRightColor=" + ColorToText(RippleRightColor));
            sb.AppendLine("RippleSize=" + I(RippleSize));
            sb.AppendLine("RippleDuration=" + I(RippleDuration));
            sb.AppendLine("RippleThickness=" + I(RippleThickness));
            sb.AppendLine("HideFromCapture=" + B(HideFromCapture));
            sb.AppendLine("HideWhenCursorHidden=" + B(HideWhenCursorHidden));
            sb.AppendLine("AutostartConfigured=" + B(AutostartConfigured));
            return sb.ToString();
        }

        private static string B(bool v) { return v ? "true" : "false"; }
        private static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        private static bool TryBool(string s, out bool v)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "да" || s == "on") { v = true; return true; }
            if (s == "false" || s == "0" || s == "no" || s == "нет" || s == "off") { v = false; return true; }
            v = false; return false;
        }

        public static AppSettings Parse(string text)
        {
            var s = new AppSettings();
            if (text == null) return s;
            foreach (string raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { s.LoadWarnings.Add("Непонятная строка в настройках пропущена: " + line); continue; }
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "GlowEnabled": Bool(s, key, val, ref s.GlowEnabled); break;
                    case "GlowColor": Col(s, "Цвет подсветки", val, ref s.GlowColor); break;
                    case "GlowSize": Int(s, "Размер подсветки", val, ref s.GlowSize); break;
                    case "GlowOpacity": Int(s, "Непрозрачность подсветки", val, ref s.GlowOpacity); break;
                    case "GlowMode":
                        if (val.Equals("always", StringComparison.OrdinalIgnoreCase)) s.GlowMode = GlowMode.Always;
                        else if (val.Equals("moving", StringComparison.OrdinalIgnoreCase)) s.GlowMode = GlowMode.WhileMoving;
                        else s.LoadWarnings.Add("Неизвестный режим подсветки «" + val + "», подсветка показывается всегда.");
                        break;
                    case "RippleLeft": Bool(s, key, val, ref s.RippleLeft); break;
                    case "RippleLeftColor": Col(s, "Цвет круга для левой кнопки", val, ref s.RippleLeftColor); break;
                    case "RippleRight": Bool(s, key, val, ref s.RippleRight); break;
                    case "RippleRightColor": Col(s, "Цвет круга для правой кнопки", val, ref s.RippleRightColor); break;
                    case "RippleSize": Int(s, "Размер круга", val, ref s.RippleSize); break;
                    case "RippleDuration": Int(s, "Длительность круга", val, ref s.RippleDuration); break;
                    case "RippleThickness": Int(s, "Толщина круга", val, ref s.RippleThickness); break;
                    case "HideFromCapture": Bool(s, key, val, ref s.HideFromCapture); break;
                    case "HideWhenCursorHidden": Bool(s, key, val, ref s.HideWhenCursorHidden); break;
                    case "AutostartConfigured": Bool(s, key, val, ref s.AutostartConfigured); break;
                    default: break; // неизвестные ключи (от будущих версий) пропускаем молча
                }
            }
            s.Sanitize();
            return s;
        }

        private static void Bool(AppSettings s, string key, string val, ref bool field)
        {
            bool b;
            if (TryBool(val, out b)) field = b;
            else s.LoadWarnings.Add("Неверное значение «" + val + "» для " + key + ", оставлено по умолчанию.");
        }

        private static void Int(AppSettings s, string what, string val, ref int field)
        {
            int i;
            if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) field = i;
            else s.LoadWarnings.Add(what + ": неверное число «" + val + "», оставлено по умолчанию.");
        }

        private static void Col(AppSettings s, string what, string val, ref Color field)
        {
            Color c;
            if (TryParseColor(val, out c)) field = c;
            else s.LoadWarnings.Add(what + ": неверный цвет «" + val + "», оставлен цвет по умолчанию.");
        }

        private int Fix(string what, int v, int min, int max, string unit)
        {
            if (v >= min && v <= max) return v;
            int f = Math.Max(min, Math.Min(max, v));
            LoadWarnings.Add(what + " должно быть от " + min + " до " + max + unit + " — установлено " + f + unit + ".");
            return f;
        }

        /// <summary>Исправляет недопустимые значения (защита от ручной правки файла).</summary>
        public void Sanitize()
        {
            GlowSize = Fix("Размер подсветки", GlowSize, GlowSizeMin, GlowSizeMax, " пикс.");
            GlowOpacity = Fix("Непрозрачность подсветки", GlowOpacity, OpacityMin, OpacityMax, " %");
            RippleSize = Fix("Размер круга", RippleSize, RippleSizeMin, RippleSizeMax, " пикс.");
            RippleDuration = Fix("Длительность круга", RippleDuration, DurationMin, DurationMax, " мс");
            RippleThickness = Fix("Толщина круга", RippleThickness, ThicknessMin, ThicknessMax, " пикс.");
            GlowColor = Opaque(GlowColor); RippleLeftColor = Opaque(RippleLeftColor); RippleRightColor = Opaque(RippleRightColor);
        }

        private static Color Opaque(Color c) { return Color.FromArgb(255, c.R, c.G, c.B); }

        public static AppSettings Load(string path)
        {
            if (!File.Exists(path)) return new AppSettings();
            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex)
            {
                Log.Error("Чтение настроек", ex);
                var d = new AppSettings { AutostartConfigured = true }; // не трогаем автозапуск, раз файл был
                d.LoadWarnings.Add("Не удалось прочитать настройки, используются настройки по умолчанию.");
                return d;
            }
            if (text.IndexOf('\0') >= 0)
            {
                try { File.Copy(path, path + ".broken", true); } catch { }
                var d = new AppSettings { AutostartConfigured = true };
                d.LoadWarnings.Add("Файл настроек был повреждён и сброшен (копия: settings.ini.broken).");
                return d;
            }
            return Parse(text);
        }

        /// <summary>Атомарное сохранение: сначала во временный файл, затем замена.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; } catch (Exception ex) { Log.Error("File.Replace настроек", ex); }
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
            else File.Move(tmp, path);
        }
    }
}
