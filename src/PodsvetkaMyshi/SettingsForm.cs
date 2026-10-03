using System;
using System.Drawing;
using System.Windows.Forms;

namespace PodsvetkaMyshi
{
    /// <summary>Окно настроек. Изменения сразу видны вокруг настоящего курсора; «Отмена» возвращает как было.</summary>
    internal sealed class SettingsForm : Form
    {
        private readonly AppSettings original;
        private readonly Action<AppSettings> preview;
        private bool loading;

        private CheckBox chkGlow, chkLeft, chkRight, chkAutostart, chkHideCapture, chkHideCursor;
        private Button btnGlowColor, btnLeftColor, btnRightColor;
        private NumericUpDown numGlowSize, numOpacity, numRippleSize, numDuration, numThickness;
        private RadioButton rbAlways, rbMoving;

        public AppSettings Result { get; private set; }
        public bool AutostartResult { get; private set; }

        public SettingsForm(AppSettings current, bool autostart, Action<AppSettings> preview)
        {
            original = current.Clone();
            this.preview = preview;

            Text = "Подсветка мыши — настройки";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);
            try { Icon = TrayApp.LoadAppIcon(32); } catch { }

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            Controls.Add(root);

            // --- подсветка ---
            var g1 = Group("Подсветка вокруг курсора", out TableLayoutPanel t1);
            chkGlow = Check("Показывать подсветку"); Span(t1, chkGlow);
            btnGlowColor = ColorButton(); Row(t1, "Цвет:", btnGlowColor, null);
            numGlowSize = Num(AppSettings.GlowSizeMin, AppSettings.GlowSizeMax, 5); Row(t1, "Размер (диаметр):", numGlowSize, "пикс.");
            numOpacity = Num(AppSettings.OpacityMin, AppSettings.OpacityMax, 5); Row(t1, "Яркость в центре:", numOpacity, "%");
            rbAlways = new RadioButton { Text = "Показывать всегда", AutoSize = true, Margin = new Padding(3, 6, 3, 0) }; Span(t1, rbAlways);
            rbMoving = new RadioButton { Text = "Только когда мышь двигается", AutoSize = true }; Span(t1, rbMoving);
            root.Controls.Add(g1);

            // --- круги ---
            var g2 = Group("Круг при щелчке", out TableLayoutPanel t2);
            chkLeft = Check("Левая кнопка"); btnLeftColor = ColorButton(); Pair(t2, chkLeft, btnLeftColor);
            chkRight = Check("Правая кнопка"); btnRightColor = ColorButton(); Pair(t2, chkRight, btnRightColor);
            numRippleSize = Num(AppSettings.RippleSizeMin, AppSettings.RippleSizeMax, 4); Row(t2, "Размер (диаметр):", numRippleSize, "пикс.");
            numDuration = Num(AppSettings.DurationMin, AppSettings.DurationMax, 50); Row(t2, "Длительность:", numDuration, "мс");
            numThickness = Num(AppSettings.ThicknessMin, AppSettings.ThicknessMax, 1); Row(t2, "Толщина линии:", numThickness, "пикс.");
            root.Controls.Add(g2);

            // --- прочее ---
            var g3 = Group("Прочее", out TableLayoutPanel t3);
            chkAutostart = Check("Запускать вместе с Windows"); Span(t3, chkAutostart);
            chkHideCapture = Check("Не показывать на скриншотах и записи экрана"); Span(t3, chkHideCapture);
            chkHideCursor = Check("Прятать подсветку, когда курсор скрыт (видео, игры)"); Span(t3, chkHideCursor);
            root.Controls.Add(g3);

            root.Controls.Add(new Label
            {
                Text = "Изменения видны сразу: подвигайте мышью и пощёлкайте в этом окне.",
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6),
            });

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Anchor = AnchorStyles.Right, WrapContents = false };
            var btnDefaults = new Button { Text = "По умолчанию", AutoSize = true, MinimumSize = new Size(110, 0) };
            var btnOk = new Button { Text = "ОК", AutoSize = true, MinimumSize = new Size(90, 0) };
            var btnCancel = new Button { Text = "Отмена", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
            btnDefaults.Click += (s, e) => { var d = new AppSettings { AutostartConfigured = original.AutostartConfigured }; LoadValues(d, chkAutostart.Checked); Changed(); };
            btnOk.Click += (s, e) => { Result = Collect(); AutostartResult = chkAutostart.Checked; DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(btnDefaults);
            buttons.Controls.Add(new Label { Width = 30 });
            buttons.Controls.Add(btnOk);
            buttons.Controls.Add(btnCancel);
            root.Controls.Add(buttons);
            AcceptButton = btnOk;
            CancelButton = btnCancel;

            LoadValues(current, autostart);

            foreach (var c in new Control[] { chkGlow, chkLeft, chkRight, chkHideCapture, chkHideCursor, rbAlways, rbMoving })
            {
                if (c is CheckBox) ((CheckBox)c).CheckedChanged += (s, e) => Changed();
                else ((RadioButton)c).CheckedChanged += (s, e) => Changed();
            }
            foreach (var n in new[] { numGlowSize, numOpacity, numRippleSize, numDuration, numThickness }) n.ValueChanged += (s, e) => Changed();
            foreach (var b in new[] { btnGlowColor, btnLeftColor, btnRightColor }) b.Click += (s, e) => PickColor((Button)s);

            FormClosed += (s, e) =>
            {
                if (DialogResult != DialogResult.OK) SafePreview(original); // отмена — вернуть как было
            };
        }

        // ---------- построение ----------

        private static GroupBox Group(string title, out TableLayoutPanel table)
        {
            var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 6), Margin = new Padding(3, 3, 3, 6) };
            table = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            g.Controls.Add(table);
            return g;
        }

        private static CheckBox Check(string text) { return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 2) }; }

        private static NumericUpDown Num(int min, int max, int step)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Width = 80, TextAlign = HorizontalAlignment.Right, Margin = new Padding(3, 3, 3, 3) };
        }

        private static Button ColorButton()
        {
            var b = new Button { Width = 80, Height = 24, FlatStyle = FlatStyle.Flat, Text = "", Margin = new Padding(3, 3, 3, 3) };
            b.FlatAppearance.BorderColor = Color.Gray;
            return b;
        }

        private static void Span(TableLayoutPanel t, Control c)
        {
            int row = t.RowCount++;
            t.Controls.Add(c, 0, row);
            t.SetColumnSpan(c, 3);
        }

        private static void Row(TableLayoutPanel t, string label, Control c, string unit)
        {
            int row = t.RowCount++;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) }, 0, row);
            t.Controls.Add(c, 1, row);
            if (unit != null) t.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 3, 3) }, 2, row);
        }

        private static void Pair(TableLayoutPanel t, Control a, Control b)
        {
            int row = t.RowCount++;
            a.Anchor = AnchorStyles.Left;
            t.Controls.Add(a, 0, row);
            t.Controls.Add(b, 1, row);
        }

        // ---------- значения ----------

        private static decimal Clamp(NumericUpDown n, int v) { return Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }

        private void LoadValues(AppSettings s, bool autostart)
        {
            loading = true;
            try
            {
                chkGlow.Checked = s.GlowEnabled;
                btnGlowColor.BackColor = s.GlowColor;
                numGlowSize.Value = Clamp(numGlowSize, s.GlowSize);
                numOpacity.Value = Clamp(numOpacity, s.GlowOpacity);
                rbAlways.Checked = s.GlowMode == GlowMode.Always;
                rbMoving.Checked = s.GlowMode == GlowMode.WhileMoving;
                chkLeft.Checked = s.RippleLeft; btnLeftColor.BackColor = s.RippleLeftColor;
                chkRight.Checked = s.RippleRight; btnRightColor.BackColor = s.RippleRightColor;
                numRippleSize.Value = Clamp(numRippleSize, s.RippleSize);
                numDuration.Value = Clamp(numDuration, s.RippleDuration);
                numThickness.Value = Clamp(numThickness, s.RippleThickness);
                chkAutostart.Checked = autostart;
                chkHideCapture.Checked = s.HideFromCapture;
                chkHideCursor.Checked = s.HideWhenCursorHidden;
            }
            finally { loading = false; }
            UpdateEnabled();
        }

        private AppSettings Collect()
        {
            var s = original.Clone();
            s.GlowEnabled = chkGlow.Checked;
            s.GlowColor = btnGlowColor.BackColor;
            s.GlowSize = (int)numGlowSize.Value;
            s.GlowOpacity = (int)numOpacity.Value;
            s.GlowMode = rbMoving.Checked ? GlowMode.WhileMoving : GlowMode.Always;
            s.RippleLeft = chkLeft.Checked; s.RippleLeftColor = btnLeftColor.BackColor;
            s.RippleRight = chkRight.Checked; s.RippleRightColor = btnRightColor.BackColor;
            s.RippleSize = (int)numRippleSize.Value;
            s.RippleDuration = (int)numDuration.Value;
            s.RippleThickness = (int)numThickness.Value;
            s.HideFromCapture = chkHideCapture.Checked;
            s.HideWhenCursorHidden = chkHideCursor.Checked;
            s.Sanitize();
            return s;
        }

        private void UpdateEnabled()
        {
            bool g = chkGlow.Checked;
            btnGlowColor.Enabled = numGlowSize.Enabled = numOpacity.Enabled = rbAlways.Enabled = rbMoving.Enabled = g;
            btnLeftColor.Enabled = chkLeft.Checked;
            btnRightColor.Enabled = chkRight.Checked;
            bool r = chkLeft.Checked || chkRight.Checked;
            numRippleSize.Enabled = numDuration.Enabled = numThickness.Enabled = r;
        }

        private void Changed()
        {
            if (loading) return;
            UpdateEnabled();
            SafePreview(Collect());
        }

        private void SafePreview(AppSettings s)
        {
            try { if (preview != null) preview(s); } catch (Exception ex) { Log.Error("Предпросмотр", ex); }
        }

        private void PickColor(Button b)
        {
            try
            {
                using (var d = new ColorDialog { Color = b.BackColor, FullOpen = true, AnyColor = true })
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        b.BackColor = Color.FromArgb(255, d.Color);
                        Changed();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Выбор цвета", ex);
                Dialogs.Warn("Не удалось открыть выбор цвета: " + ErrorText.Friendly(ex));
            }
        }
    }
}
