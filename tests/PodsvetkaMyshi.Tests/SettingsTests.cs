using System.Drawing;
using System.IO;
using System.Text;
using Xunit;

namespace PodsvetkaMyshi.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void Defaults_AreAlwaysOnYellowGlowAndRedLeftRipple()
        {
            var s = new AppSettings();
            Assert.True(s.GlowEnabled);
            Assert.Equal(GlowMode.Always, s.GlowMode);
            Assert.True(s.RippleLeft);
            Assert.True(s.RippleRight);
            Assert.True(s.RippleLeftColor.R > 200 && s.RippleLeftColor.G < 80 && s.RippleLeftColor.B < 80);
            Assert.True(s.RippleRightColor.B > 200 && s.RippleRightColor.R < 80);
            Assert.True(s.GlowColor.R > 240 && s.GlowColor.G > 220 && s.GlowColor.B < 40);
            Assert.False(s.AutostartConfigured); // при первом запуске автозапуск включится сам
            Assert.False(s.HideFromCapture);
        }

        [Fact]
        public void Serialize_Parse_RoundTrip()
        {
            var s = new AppSettings
            {
                GlowEnabled = false, GlowColor = Color.FromArgb(10, 20, 30), GlowSize = 150, GlowOpacity = 70, GlowMode = GlowMode.WhileMoving,
                RippleLeft = false, RippleLeftColor = Color.FromArgb(1, 2, 3), RippleRight = true, RippleRightColor = Color.FromArgb(200, 100, 50),
                RippleSize = 90, RippleDuration = 800, RippleThickness = 4, HideFromCapture = true, HideWhenCursorHidden = true, AutostartConfigured = true,
            };
            var p = AppSettings.Parse(s.Serialize());
            Assert.Empty(p.LoadWarnings);
            Assert.Equal(s.Serialize(), p.Serialize());
            Assert.Equal(GlowMode.WhileMoving, p.GlowMode);
            Assert.Equal(Color.FromArgb(200, 100, 50).ToArgb(), p.RippleRightColor.ToArgb());
        }

        [Fact]
        public void Parse_EmptyOrNull_GivesDefaults()
        {
            Assert.Equal(new AppSettings().Serialize(), AppSettings.Parse(null).Serialize());
            Assert.Equal(new AppSettings().Serialize(), AppSettings.Parse("").Serialize());
            Assert.Equal(new AppSettings().Serialize(), AppSettings.Parse("# только комментарий\n; и ещё\n\n").Serialize());
        }

        [Fact]
        public void Parse_OutOfRange_IsClampedWithWarning()
        {
            var s = AppSettings.Parse("GlowSize=99999\nGlowOpacity=0\nRippleDuration=10\nRippleThickness=50\nRippleSize=-5");
            Assert.Equal(AppSettings.GlowSizeMax, s.GlowSize);
            Assert.Equal(AppSettings.OpacityMin, s.GlowOpacity);
            Assert.Equal(AppSettings.DurationMin, s.RippleDuration);
            Assert.Equal(AppSettings.ThicknessMax, s.RippleThickness);
            Assert.Equal(AppSettings.RippleSizeMin, s.RippleSize);
            Assert.Equal(5, s.LoadWarnings.Count);
        }

        [Fact]
        public void Parse_Garbage_KeepsDefaultsWithWarnings()
        {
            var s = AppSettings.Parse("GlowSize=много\nGlowColor=жёлтый\nGlowEnabled=может быть\nGlowMode=sometimes\nэто не настройка\nFutureKey=1");
            var d = new AppSettings();
            Assert.Equal(d.GlowSize, s.GlowSize);
            Assert.Equal(d.GlowColor.ToArgb(), s.GlowColor.ToArgb());
            Assert.True(s.GlowEnabled);
            Assert.Equal(GlowMode.Always, s.GlowMode);
            Assert.Equal(5, s.LoadWarnings.Count); // неизвестный ключ — молча
        }

        [Fact]
        public void Parse_AcceptsRussianAndNumericBooleans_AndBom()
        {
            var s = AppSettings.Parse("\uFEFFGlowEnabled=нет\r\nRippleLeft=0\r\nRippleRight=да\r\nHideFromCapture=1");
            Assert.False(s.GlowEnabled); Assert.False(s.RippleLeft); Assert.True(s.RippleRight); Assert.True(s.HideFromCapture);
            Assert.Empty(s.LoadWarnings);
        }

        [Theory]
        [InlineData("#FFF200", 255, 242, 0)]
        [InlineData("fff200", 255, 242, 0)]
        [InlineData("#F00", 255, 0, 0)]
        [InlineData("10, 20, 30", 10, 20, 30)]
        [InlineData(" #0a0B0c ", 10, 11, 12)]
        public void Color_Parse(string text, int r, int g, int b)
        {
            Color c;
            Assert.True(AppSettings.TryParseColor(text, out c));
            Assert.Equal(Color.FromArgb(r, g, b).ToArgb(), c.ToArgb());
        }

        [Theory]
        [InlineData("")]
        [InlineData("#12345")]
        [InlineData("#GGGGGG")]
        [InlineData("300,0,0")]
        [InlineData("red")]
        public void Color_Parse_Rejects(string text)
        {
            Color c;
            Assert.False(AppSettings.TryParseColor(text, out c));
        }

        [Fact]
        public void Color_ToText_IsHex()
        {
            Assert.Equal("#FFF200", AppSettings.ColorToText(Color.FromArgb(255, 242, 0)));
        }

        [Fact]
        public void Clone_IsIndependent()
        {
            var a = new AppSettings();
            var b = a.Clone();
            b.GlowSize = 200;
            Assert.Equal(80, a.GlowSize);
        }

        [Fact]
        public void Load_MissingFile_Defaults_AutostartNotConfigured()
        {
            using (var t = new TempDir())
            {
                var s = AppSettings.Load(t.Sub("нет.ini"));
                Assert.False(s.AutostartConfigured);
                Assert.Empty(s.LoadWarnings);
            }
        }

        [Fact]
        public void Load_Corrupted_ResetsBacksUp_AndDoesNotReenableAutostart()
        {
            using (var t = new TempDir())
            {
                string p = t.Sub("settings.ini");
                File.WriteAllBytes(p, new byte[] { 0, 0, 0, 65, 0 });
                var s = AppSettings.Load(p);
                Assert.Single(s.LoadWarnings);
                Assert.True(s.AutostartConfigured);
                Assert.True(File.Exists(p + ".broken"));
            }
        }

        [Fact]
        public void Save_IsAtomic_OverwritesAndLeavesNoTemp()
        {
            using (var t = new TempDir())
            {
                string p = Path.Combine(t.Sub("вложенная папка"), "settings.ini");
                var s = new AppSettings { GlowSize = 120 };
                s.Save(p);
                s.GlowSize = 130;
                s.Save(p);
                Assert.Equal(130, AppSettings.Load(p).GlowSize);
                Assert.False(File.Exists(p + ".tmp"));
                var bytes = File.ReadAllBytes(p);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB); // без BOM
                Assert.Contains("Подсветка мыши", Encoding.UTF8.GetString(bytes));
            }
        }
    }
}
