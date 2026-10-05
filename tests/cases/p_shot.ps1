# 截屏：主界面 / 砍怪下拉 / 鼠标下拉（对比弹层锚点）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

function Grab {
    param([string]$Path)
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Path"
}

function Open-Flyout {
    param([string]$BtnName)
    $b = Get-UiButton $BtnName
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $b }
    Start-Sleep -Milliseconds 800
}

function Close-Flyout {
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500
}

$null = Start-App
Set-Foreground
Start-Sleep -Milliseconds 800
Grab "$out\shot1_main.png"

# 记录开关/按钮的屏幕位置，用于核对弹层锚点
foreach ($n in @('自动砍怪选项', '鼠标控制选项', '自动重连选项')) {
    try {
        $e = Get-UiButton $n
        $r = $e.Current.BoundingRectangle
        Write-Host ("BTN {0}: x={1} y={2} w={3} h={4}" -f $n, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    } catch { Write-Host "BTN ${n}: NOT FOUND" }
}

Open-Flyout '自动砍怪选项'
try {
    $e = Get-UiEdit '攻击距离'
    $r = $e.Current.BoundingRectangle
    Write-Host ("POP 咆怪: x={0} y={1} w={2} h={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
} catch { Write-Host 'POP 咆怪: content not found' }
Grab "$out\shot2_attack_flyout.png"
Close-Flyout

Open-Flyout '鼠标控制选项'
try {
    $e = Get-UiCombo '鼠标模式'
    $r = $e.Current.BoundingRectangle
    Write-Host ("POP 鼠标: x={0} y={1} w={2} h={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
} catch { Write-Host 'POP 鼠标: content not found' }
Grab "$out\shot3_mouse_flyout.png"
Close-Flyout

# 主窗口位置
$root = Get-UiButton '连接'
$wr = $root.Current.BoundingRectangle
Write-Host ("WIN: x={0} y={1}" -f [int]$wr.X, [int]$wr.Y)
Write-Host "done"
exit 0
