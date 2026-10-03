# Дымовой тест на настоящем Windows: запускает программу, двигает мышью, щёлкает,
# делает снимки экрана и проверяет пиксели (подсветка, круги, прозрачность для щелчков).
param([Parameter(Mandatory = $true)][string]$Exe)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path $Exe).Path
$root = Join-Path ($env:RUNNER_TEMP ?? $env:TEMP) ("pm-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 6))
$shots = Join-Path $root 'ui-shots'
New-Item -ItemType Directory -Force $root, $shots | Out-Null
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class In {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static int PidAt(int x, int y) { var p = new POINT { X = x, Y = y }; int pid; GetWindowThreadProcessId(WindowFromPoint(p), out pid); return pid; }
  public static int ForegroundPid() { int pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
  public static List<string> VisibleTitles(int pid) {
    var r = new List<string>();
    EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); r.Add(sb.ToString()); } return true; }, IntPtr.Zero);
    return r;
  }
  public static List<int> VisibleExStyles(int pid) {
    var r = new List<int>();
    EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(GetWindowLong(h, -20)); return true; }, IntPtr.Zero);
    return r;
  }
  public static void MoveAbs(int x, int y) {
    int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
    mouse_event(0x8001, (int)Math.Round(x * 65535.0 / (w - 1)), (int)Math.Round(y * 65535.0 / (h - 1)), 0, UIntPtr.Zero);
  }
  public static void Glide(int x0, int y0, int x1, int y1, int steps) {
    for (int i = 1; i <= steps; i++) { MoveAbs(x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps); System.Threading.Thread.Sleep(12); }
  }
  public static void LeftClick() { mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
  [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
  [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int x1, int y1, int rop);
  // Снимок экрана с CAPTUREBLT (включая прозрачные layered-окна)
  public static IntPtr GrabHbitmap(int w, int h) {
    IntPtr sdc = GetDC(IntPtr.Zero), mdc = CreateCompatibleDC(sdc), bmp = CreateCompatibleBitmap(sdc, w, h);
    IntPtr old = SelectObject(mdc, bmp);
    BitBlt(mdc, 0, 0, w, h, sdc, 0, 0, 0x00CC0020 | 0x40000000);
    SelectObject(mdc, old); DeleteDC(mdc); ReleaseDC(IntPtr.Zero, sdc);
    return bmp;
  }
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr e);
  public static void Keys(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void RightClick() { mouse_event(8, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(40); mouse_event(16, 0, 0, 0, UIntPtr.Zero); }
}
"@
$failures = New-Object System.Collections.Generic.List[string]
function Check($cond, $msg) { if ($cond) { Write-Host "  OK  $msg" } else { Write-Host "  FAIL $msg" -ForegroundColor Red; $failures.Add($msg) } }
$W = [In]::GetSystemMetrics(0); $H = [In]::GetSystemMetrics(1)
Write-Host "Экран: ${W}x${H}, Windows $([Environment]::OSVersion.Version)"

function Grab() {
  $hb = [In]::GrabHbitmap($W, $H)
  try { $tmp = [System.Drawing.Image]::FromHbitmap($hb); $b = New-Object System.Drawing.Bitmap $tmp; $tmp.Dispose() } finally { [In]::DeleteObject($hb) | Out-Null }
  return $b
}
# Снимок экрана не содержит курсор — дорисовываем стрелку в точке курсора (только для наглядности картинок)
function WithCursor($bmp) {
  $c = New-Object System.Drawing.Bitmap $bmp
  $g = [System.Drawing.Graphics]::FromImage($c)
  $p = [System.Windows.Forms.Cursor]::Position
  [System.Windows.Forms.Cursors]::Arrow.Draw($g, (New-Object System.Drawing.Rectangle($p.X, $p.Y, 32, 32)))
  $g.Dispose(); return $c
}
function Save($bmp, $name) { $c = WithCursor $bmp; $c.Save((Join-Path $shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $c.Dispose() }
function SaveCrop($bmp, $cx, $cy, $half, $name) {
  $c = WithCursor $bmp
  $side = 2 * $half; $z = 3
  $out = New-Object System.Drawing.Bitmap(($side * $z), ($side * $z))
  $g = [System.Drawing.Graphics]::FromImage($out)
  $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
  $g.DrawImage($c, (New-Object System.Drawing.Rectangle(0, 0, ($side * $z), ($side * $z))), (New-Object System.Drawing.Rectangle(($cx - $half), ($cy - $half), $side, $side)), 'Pixel')
  $g.Dispose(); $out.Save((Join-Path $shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $out.Dispose(); $c.Dispose()
}
function Px($bmp, $x, $y) { $p = $bmp.GetPixel($x, $y); return "($($p.R),$($p.G),$($p.B))" }
function IsYellow($bmp, $x, $y) { $p = $bmp.GetPixel($x, $y); return ($p.R -ge 235 -and $p.G -ge 215 -and $p.B -le 215) }
function IsWhite($bmp, $x, $y) { $p = $bmp.GetPixel($x, $y); return ($p.R -ge 250 -and $p.G -ge 250 -and $p.B -ge 250) }
function CountPx($bmp, $cx, $cy, $half, [scriptblock]$pred) {
  $n = 0
  for ($y = [Math]::Max(0, $cy - $half); $y -lt [Math]::Min($H, $cy + $half); $y++) {
    for ($x = [Math]::Max(0, $cx - $half); $x -lt [Math]::Min($W, $cx + $half); $x++) { if (& $pred ($bmp.GetPixel($x, $y))) { $n++ } }
  }
  return $n
}
$isRed = { param($p) $p.R -ge 170 -and $p.G -le 120 -and $p.B -le 120 }
$isBlue = { param($p) $p.B -ge 150 -and $p.B -ge $p.R + 60 -and $p.B -ge $p.G + 20 }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function RunValue() { try { return (Get-ItemProperty -Path $runKey -Name PodsvetkaMyshi -ErrorAction Stop).PodsvetkaMyshi } catch { return $null } }
function StartApp($cfg) { return Start-Process $exe -ArgumentList @('--config-dir', "`"$cfg`"") -PassThru }
function Quit($app) { Start-Process $exe -ArgumentList @('--quit') -Wait | Out-Null; $app.WaitForExit(8000) | Out-Null; return $app.HasExited }
function WriteIni($dir, $text) { New-Item -ItemType Directory -Force $dir | Out-Null; [IO.File]::WriteAllText((Join-Path $dir 'settings.ini'), $text, [Text.UTF8Encoding]::new($false)) }
Remove-ItemProperty -Path $runKey -Name PodsvetkaMyshi -ErrorAction SilentlyContinue

Write-Host "0) тестовое окно"
$clickLog = Join-Path $root 'clicks.txt'; Set-Content $clickLog ''
$target = Start-Process pwsh -ArgumentList @('-NoProfile', '-File', "`"$PSScriptRoot\target.ps1`"", '-Log', "`"$clickLog`"") -PassThru
Start-Sleep -Seconds 4
$target.Refresh()
[In]::SetForegroundWindow($target.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
[In]::MoveAbs(400, 400); Start-Sleep -Milliseconds 300
$b = Grab; Check (IsWhite $b 400 400) "без программы под курсором белый фон $(Px $b 400 400)"; $b.Dispose()

Write-Host "1) первый запуск: эффект сразу, автозапуск включается сам"
$cfgA = Join-Path $root 'cfgA'
$app = StartApp $cfgA
Start-Sleep -Seconds 3
Check (-not $app.HasExited) "программа работает"
$log = Get-Content (Join-Path $cfgA 'log.txt') -Raw -ErrorAction SilentlyContinue
Check ($log -match 'перехватчик мыши: да') "перехватчик мыши установлен"
$rv = RunValue
Check ($rv -ne $null -and $rv -like "*$([IO.Path]::GetFileName($exe))*--autostart*") "автозапуск в HKCU\...\Run: $rv"
Check ((Get-Content (Join-Path $cfgA 'settings.ini') -Raw) -match 'AutostartConfigured=true') "выбор автозапуска запомнен"
Check ([In]::ForegroundPid() -eq $target.Id) "запуск не забрал фокус у активного окна"
$styles = [In]::VisibleExStyles($app.Id)
$need = 0x80000 -bor 0x20 -bor 0x80 -bor 0x08000000 -bor 0x8
Check ($styles.Count -ge 1) "видимых окон подсветки: $($styles.Count)"
Check (@($styles | Where-Object { ($_ -band $need) -ne $need }).Count -eq 0) "все видимые окна: LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE|TOPMOST (нет в Alt+Tab)"

Write-Host "2) подсветка вокруг курсора и движение за мышью"
[In]::Glide(400, 400, 410, 405, 5); [In]::Glide(410, 405, 400, 400, 5)
Start-Sleep -Milliseconds 400
$b = Grab
Check (IsYellow $b 400 400) "под курсором жёлтая подсветка $(Px $b 400 400)"
Check (IsYellow $b 415 400) "подсветка вокруг курсора $(Px $b 415 400)"
Check (IsWhite $b 470 400) "за краем подсветки белый фон $(Px $b 470 400)"
Save $b '01-glow'; SaveCrop $b 400 400 60 '01-glow-zoom'; $b.Dispose()
Start-Sleep -Seconds 2   # мышь стоит — подсветка не пропадает (режим «всегда»)
$b = Grab; Check (IsYellow $b 400 400) "мышь не двигается 2 с — подсветка остаётся"; $b.Dispose()
[In]::Glide(400, 400, 640, 460, 25)
Start-Sleep -Milliseconds 400
$b = Grab
Check (IsYellow $b 640 460) "подсветка переехала за курсором $(Px $b 640 460)"
Check (IsWhite $b 400 400) "на старом месте подсветки нет $(Px $b 400 400)"
Save $b '02-glow-moved'; $b.Dispose()
Check ([In]::PidAt(640, 460) -eq $target.Id) "окно под курсором — тестовое, не подсветка (WindowFromPoint)"

Write-Host "3) левый щелчок: красный круг и щелчок проходит насквозь"
$before = @(Get-Content $clickLog | Where-Object { $_ }).Count
[In]::LeftClick()
Start-Sleep -Milliseconds 60
$b1 = Grab
Start-Sleep -Milliseconds 130
$b2 = Grab
$red1 = CountPx $b1 640 460 70 $isRed; $red2 = CountPx $b2 640 460 70 $isRed
Check (($red1 + $red2) -ge 30) "красный круг виден (красных пикселей: $red1 и $red2)"
Save $b1 '03-ripple-left-early'; Save $b2 '04-ripple-left'; SaveCrop $b2 640 460 60 '04-ripple-left-zoom'; SaveCrop $b1 640 460 60 '03-ripple-left-early-zoom'
$b1.Dispose(); $b2.Dispose()
Start-Sleep -Milliseconds 700
$b = Grab; $red3 = CountPx $b 640 460 70 $isRed
Check ($red3 -lt 5) "через 0,9 с круг исчез (красных: $red3), подсветка осталась: $(IsYellow $b 640 460)"; $b.Dispose()
$clicks = @(Get-Content $clickLog | Where-Object { $_ })
Check ($clicks.Count -eq $before + 1 -and $clicks[-1] -eq 'Left 640 460') "щелчок попал в окно под подсветкой: '$($clicks[-1])'"
Check ([In]::ForegroundPid() -ne $app.Id) "после щелчка активна не подсветка"

Write-Host "4) правый щелчок: синий круг"
[In]::Glide(640, 460, 450, 320, 15); Start-Sleep -Milliseconds 200
[In]::RightClick()
Start-Sleep -Milliseconds 90
$b = Grab
$blue = CountPx $b 450 320 70 $isBlue
Check ($blue -ge 20) "синий круг при правом щелчке (синих пикселей: $blue)"
Save $b '05-ripple-right'; SaveCrop $b 450 320 60 '05-ripple-right-zoom'; $b.Dispose()
Start-Sleep -Milliseconds 300
$clicks = @(Get-Content $clickLog | Where-Object { $_ })
Check ($clicks[-1] -eq 'Right 450 320') "правый щелчок тоже прошёл насквозь: '$($clicks[-1])'"

Write-Host "5) серия быстрых щелчков (ошибки оператора) — без сбоев"
for ($i = 0; $i -lt 25; $i++) { [In]::MoveAbs(200 + 20 * $i, 500 + ($i % 5) * 15); [In]::LeftClick(); if ($i % 3 -eq 0) { [In]::RightClick() } }
Start-Sleep -Milliseconds 150
$b = Grab; Save $b '06-many-clicks'; $b.Dispose()
Start-Sleep -Seconds 1
Check (-not $app.HasExited) "программа не упала"
Check ((@(Get-Content $clickLog | Where-Object { $_ }).Count) -ge $before + 2 + 25 + 9) "все щелчки дошли до окна"

Write-Host "6) над панелью задач"
$tbY = $H - 20
[In]::Glide(450, 500, 300, $tbY, 20); Start-Sleep -Milliseconds 700
$b = Grab
$tbYellow = IsYellow $b 300 $tbY
Write-Host "  пиксель над панелью задач: $(Px $b 300 $tbY)"
Check ($tbYellow -or ([System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Height -eq $H)) "подсветка видна поверх панели задач"
Save $b '07-over-taskbar'; SaveCrop $b 300 ($H - 60) 60 '07-over-taskbar-zoom'; $b.Dispose()
[In]::Glide(300, $tbY, 520, 400, 20); Start-Sleep -Milliseconds 300

Write-Host "7) нагрузка в покое"
Start-Sleep -Milliseconds 500
$app.Refresh(); $cpu0 = $app.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Seconds 5
$app.Refresh(); $cpu1 = $app.TotalProcessorTime.TotalMilliseconds
$pct = ($cpu1 - $cpu0) / 5000 * 100
Check ($pct -lt 2) ("мышь стоит 5 с — загрузка процессора {0:N2} %" -f $pct)
$app.Refresh(); Write-Host ("  память: {0:N1} МБ" -f ($app.WorkingSet64 / 1MB))

Write-Host "8) окно настроек и повторный запуск"
$p2 = Start-Process $exe -ArgumentList @('--config-dir', "`"$cfgA`"", '--settings') -PassThru
$p2.WaitForExit(5000) | Out-Null
Check ($p2.HasExited) "второй экземпляр сразу завершился (передал команду первому)"
Start-Sleep -Seconds 2
$sw = @([In]::VisibleTitles($app.Id) | Where-Object { $_ -like '*настройки*' })
Check ($sw.Count -eq 1) "окно настроек открылось"
[In]::Glide(520, 400, 512, 330, 10); Start-Sleep -Milliseconds 300
$b = Grab; Save $b '08-settings'; $b.Dispose()
Check ([In]::PidAt(512, 330) -eq $app.Id) "окно настроек поверх остальных"
[In]::Glide(512, 330, 640, 335, 10); Start-Sleep -Milliseconds 200   # пустое место окна (не кнопка цвета)
[In]::LeftClick(); Start-Sleep -Milliseconds 120
$b = Grab; Save $b '08-settings-click'; $b.Dispose()
Start-Sleep -Milliseconds 400
Write-Host "  активно окно процесса: $([In]::ForegroundPid()) (программа: $($app.Id))"
[In]::Keys(0x1B); Start-Sleep -Milliseconds 800
$sw = @([In]::VisibleTitles($app.Id) | Where-Object { $_ -like '*настройки*' })
Check ($sw.Count -eq 0) "Esc закрыл настройки"
Check (-not $app.HasExited) "после закрытия настроек программа работает"
Check (@(Get-Process PodsvetkaMyshi -ErrorAction SilentlyContinue).Count -eq 1) "запущен ровно один экземпляр"
$p3 = Start-Process $exe -ArgumentList @('--autostart') -PassThru; $p3.WaitForExit(5000) | Out-Null
Check ($p3.HasExited -and $p3.ExitCode -eq 0) "--autostart при уже запущенной программе молча выходит"

Write-Host "9) выход"
Check (Quit $app) "--quit закрыл программу"
Start-Sleep -Milliseconds 300
[In]::SetForegroundWindow($target.MainWindowHandle) | Out-Null
[In]::Glide(560, 420, 400, 400, 10); Start-Sleep -Milliseconds 300
$b = Grab; Check (IsWhite $b 400 400) "после выхода подсветки нет $(Px $b 400 400)"; $b.Dispose()

Write-Host "10) автозапуск выключили — программа не включает его снова"
Remove-ItemProperty -Path $runKey -Name PodsvetkaMyshi -ErrorAction SilentlyContinue
$app = StartApp $cfgA; Start-Sleep -Seconds 3
Check ((RunValue) -eq $null) "автозапуск остался выключенным"
[In]::Glide(400, 400, 405, 400, 3); Start-Sleep -Milliseconds 300
$b = Grab; Check (IsYellow $b 405 400) "и подсветка снова работает сразу при запуске"; $b.Dispose()
Check (Quit $app) "выход"

Write-Host "11) режим «только при движении»"
$cfgC = Join-Path $root 'cfgC'; WriteIni $cfgC "GlowMode=moving`nAutostartConfigured=true`n"
$app = StartApp $cfgC; Start-Sleep -Seconds 3
$b = Grab; Check (IsWhite $b 405 400) "мышь стоит — подсветки нет $(Px $b 405 400)"; $b.Dispose()
[In]::Glide(405, 400, 520, 430, 20)
$b = Grab; Check (IsYellow $b 520 430) "мышь двигается — подсветка есть $(Px $b 520 430)"; Save $b '09-moving-mode'; $b.Dispose()
Check (Quit $app) "выход"

Write-Host "12) «не показывать на скриншотах»"
$cfgB = Join-Path $root 'cfgB'; WriteIni $cfgB "HideFromCapture=true`nAutostartConfigured=true`nRippleLeftColor=#00AA00`n"
$app = StartApp $cfgB; Start-Sleep -Seconds 3
[In]::Glide(520, 430, 500, 420, 5); Start-Sleep -Milliseconds 300
$b = Grab
if ([Environment]::OSVersion.Version.Build -ge 19041) { Check (IsWhite $b 500 420) "подсветка не попала на снимок экрана $(Px $b 500 420)" }
$b.Dispose()
Check (-not $app.HasExited) "программа работает"
Check (Quit $app) "выход"

Write-Host "13) испорченный файл настроек"
$cfgD = Join-Path $root 'cfgD'; New-Item -ItemType Directory -Force $cfgD | Out-Null
[IO.File]::WriteAllBytes((Join-Path $cfgD 'settings.ini'), [byte[]](0, 0, 71, 0, 0))
$app = StartApp $cfgD; Start-Sleep -Seconds 3
Check (-not $app.HasExited) "запустилась с испорченными настройками"
Check (Test-Path (Join-Path $cfgD 'settings.ini.broken')) "копия испорченного файла сохранена"
[In]::Glide(500, 420, 505, 425, 3); Start-Sleep -Milliseconds 300
$b = Grab; Check (IsYellow $b 505 425) "подсветка работает с настройками по умолчанию"; $b.Dispose()
Check (Quit $app) "выход"

Write-Host "14) неверные параметры"
$p = Start-Process $exe -ArgumentList @('--bogus') -Wait -PassThru
Check ($p.ExitCode -eq 2) "неизвестный параметр → код 2"

foreach ($d in @($cfgA, $cfgB, $cfgC, $cfgD)) {
  $l = Join-Path $d 'log.txt'
  if (Test-Path $l) { Write-Host "---- журнал $([IO.Path]::GetFileName($d)) ----"; Get-Content $l | Write-Host }
}
Remove-ItemProperty -Path $runKey -Name PodsvetkaMyshi -ErrorAction SilentlyContinue
try { $target.Kill() } catch { }
Copy-Item $shots -Destination (Join-Path ($env:GITHUB_WORKSPACE ?? '.') 'ui-shots') -Recurse -Force

if ($failures.Count -gt 0) { Write-Host "ПРОВАЛЕНО: $($failures.Count)" -ForegroundColor Red; $failures | ForEach-Object { Write-Host " - $_" }; exit 1 }
Write-Host "Все проверки пройдены" -ForegroundColor Green
