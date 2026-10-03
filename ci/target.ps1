# Тестовое окно «под» подсветкой: белое, записывает все щелчки в файл.
param([Parameter(Mandatory = $true)][string]$Log)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$f = New-Object System.Windows.Forms.Form
$f.Text = 'PM-Target'
$f.BackColor = [System.Drawing.Color]::White
$f.FormBorderStyle = 'None'
$f.StartPosition = 'Manual'
$f.Location = New-Object System.Drawing.Point($wa.X, $wa.Y)
$f.Size = New-Object System.Drawing.Size($wa.Width, $wa.Height)
$lbl = New-Object System.Windows.Forms.Label
$lbl.Text = 'Тестовое окно: щелчки должны проходить сквозь подсветку'
$lbl.AutoSize = $true
$lbl.ForeColor = [System.Drawing.Color]::Gray
$lbl.Font = New-Object System.Drawing.Font('Segoe UI', 11)
$lbl.Location = New-Object System.Drawing.Point(16, 12)
$f.Controls.Add($lbl)
$f.Add_MouseDown({ param($s, $e)
    $p = [System.Windows.Forms.Cursor]::Position
    Add-Content -Path $Log -Value "$($e.Button) $($p.X) $($p.Y)" })
$f.Add_Shown({ $f.Activate() })
[System.Windows.Forms.Application]::Run($f)
