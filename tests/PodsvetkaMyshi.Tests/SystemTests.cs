using Xunit;

namespace PodsvetkaMyshi.Tests
{
    public class SystemTests
    {
        [Fact]
        public void Autostart_Command_QuotedWithFlag()
        {
            Assert.Equal("\"C:\\Program Files\\x\\PodsvetkaMyshi.exe\" --autostart", Autostart.CommandFor(@"C:\Program Files\x\PodsvetkaMyshi.exe"));
            Assert.Equal(@"C:\Program Files\x\PodsvetkaMyshi.exe", Autostart.ExePathFromCommand("\"C:\\Program Files\\x\\PodsvetkaMyshi.exe\" --autostart"));
            Assert.Equal(@"C:\x\a.exe", Autostart.ExePathFromCommand(@"C:\x\a.exe --autostart"));
            Assert.Null(Autostart.ExePathFromCommand(""));
        }

        [Fact]
        public void Autostart_RiskyLocations()
        {
            string temp = @"C:\Users\Ivan\AppData\Local\Temp\";
            Assert.NotNull(Autostart.RiskyLocationWarning(@"C:\Users\Ivan\AppData\Local\Temp\Temp1_x.zip\PodsvetkaMyshi.exe", temp, p => false));
            Assert.NotNull(Autostart.RiskyLocationWarning(@"E:\PodsvetkaMyshi.exe", temp, p => true));
            Assert.Null(Autostart.RiskyLocationWarning(@"C:\Users\Ivan\Downloads\PodsvetkaMyshi.exe", temp, p => false));
            Assert.Null(Autostart.RiskyLocationWarning(@"C:\Users\Ivan\AppData\Local\TempX\a.exe", temp, p => false));
        }

        [Fact]
        public void Autostart_FirstRunDecision()
        {
            Assert.True(Autostart.ShouldAutoEnable(false, null));    // первый запуск — включаем
            Assert.False(Autostart.ShouldAutoEnable(true, null));    // пользователь уже выбирал — не трогаем
            Assert.False(Autostart.ShouldAutoEnable(false, "из архива"));
        }

        [Fact]
        public void Args_Parse()
        {
            var o = Program.ParseArgs(new[] { "--config-dir", @"C:\cfg", "--settings", "--AUTOSTART" });
            Assert.Equal(@"C:\cfg", o.ConfigDir);
            Assert.True(o.Settings); Assert.True(o.Autostart); Assert.Null(o.Error);
            Assert.NotNull(Program.ParseArgs(new[] { "--bogus" }).Error);
            Assert.NotNull(Program.ParseArgs(new[] { "--config-dir" }).Error);
            Assert.True(Program.ParseArgs(new[] { "--quit" }).Quit);
        }

        [Fact]
        public void ErrorText_Operator()
        {
            var ex = new System.ComponentModel.Win32Exception(4320);
            var io = new System.IO.IOException("x", unchecked((int)0x800710E0));
            Assert.Contains("Оператор или администратор", ErrorText.Friendly(io));
            Assert.NotNull(ErrorText.Friendly(ex));
        }
    }
}
