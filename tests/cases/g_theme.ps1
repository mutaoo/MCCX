# GUI 实测：暗色模式开关（黑底白字）+ 设置记住 + 重启保持；顺带验启动默认仍是浅色。
# 只看不改：结束把开关拨回浅色、进程收干净，测试新建的设置文件删掉。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_theme.ps1
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXThemeShot {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$script:GuiFail = 0
function Check([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name  $detail"; $script:GuiFail++ }
}

# 弹层/主窗都按 AutomationId 找（x:Name 就是 AutomationId）
function Find-IdAnywhere([string]$Id) {
    $procId = Get-MainProcId
    if (-not $procId) { return $null }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::AutomationIdProperty, $Id)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)))
    return $script:AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Get-SwitchState($el) {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}

function Set-SwitchState($el, [bool]$On) {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    for ($i = 0; $i -lt 6; $i++) {
        if ((($tp.Current.ToggleState) -eq [System.Windows.Automation.ToggleState]::On) -eq $On) { return }
        $tp.Toggle()
        Start-Sleep -Milliseconds 250
    }
    if (((($tp.Current.ToggleState) -eq [System.Windows.Automation.ToggleState]::On)) -ne $On) {
        throw "开关没能切到 $On"
    }
}

# PrintWindow 抓图（不抢前台），返回平均亮度；$CenterOnly 只采样中间区域（弹窗位置）
function Save-WindowShot([string]$Path, [bool]$CenterOnly = $false) {
    $h = [IntPtr]$script:Hwnd
    $r = New-Object MccXThemeShot+RECT
    [void][MccXThemeShot]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L
    $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $null = [MccXThemeShot]::PrintWindow($h, $hdc, 3)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    $x0 = 0; $x1 = $w; $y0 = 0; $y1 = $ht
    if ($CenterOnly) {
        $x0 = [int]($w * 0.3); $x1 = [int]($w * 0.7)
        $y0 = [int]($ht * 0.25); $y1 = [int]($ht * 0.75)
    }

    $sum = 0.0
    $n = 0
    for ($y = $y0; $y -lt $y1; $y += [Math]::Max(1, [int](($y1 - $y0) / 60))) {
        for ($x = $x0; $x -lt $x1; $x += [Math]::Max(1, [int](($x1 - $x0) / 40))) {
            $c = $bmp.GetPixel($x, $y)
            $sum += (0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
            $n++
        }
    }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    if ($n -eq 0) { return 255.0 }
    return [Math]::Round($sum / $n, 1)
}

$settingsPath = Join-Path (Split-Path $script:AppExe) 'ui-settings.json'
$settingsBak = "$settingsPath.g_theme_bak"
$hadSettings = Test-Path $settingsPath
# 2026-10-05：上一轮跑测时若被强杀（进程残留/中途 Ctrl-C），ui-settings.json 会留下
# darkMode=true，下次开局就是暗色，而下面第一步断言的是"默认浅色"——必然假红。
# 所以先把旧设置挪走，让这次一定从默认（浅色）开始，收尾再原样还原。
if ($hadSettings) {
    Move-Item -Path $settingsPath -Destination $settingsBak -Force
    Write-Host "  [prep] 已把既有 ui-settings.json 挪走（本次从默认浅色开始）"
}
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) 'mccx-theme'
if (-not (Test-Path $tmp)) { New-Item -ItemType Directory -Path $tmp | Out-Null }

Write-Host '=== 启动（默认应为浅色）==='
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }

try {
    # 2026-10-05：暗色开关从 ToggleSwitch 换成"月亮/太阳"小开关，但语义是常规的
    #   勾选 = 开 = 暗色（滑块在右、月亮）；不勾 = 关 = 浅色（滑块在左、太阳）。
    #   提示文本也只显示当前状态（"暗色模式 开/关"），不再写长说明。
    $sw = $null
    for ($i = 0; $i -lt 10 -and -not $sw; $i++) {
        Start-Sleep -Milliseconds 300
        $sw = Find-IdAnywhere 'ThemeSwitch'
    }
    Check ($null -ne $sw) '账号卡片底部有暗色模式开关'
    if (-not $sw) { throw '找不到开关，后续步骤无意义' }

    $lightOn = Get-SwitchState $sw
    $lightShot = Save-WindowShot (Join-Path $tmp 'theme_light.png')
    Write-Host ("  初始开关 = " + $lightOn + "（False=关=浅色），浅色截图平均亮度 = " + $lightShot)
    Check ($lightShot -gt 60) '浅色截图不是黑的' "亮度=$lightShot"

    Write-Host "`n=== 切到暗色（勾选开关 = 开）==="
    Set-SwitchState $sw $true
    Start-Sleep -Seconds 2
    $darkShot = Save-WindowShot (Join-Path $tmp 'theme_dark.png')
    Write-Host ("  暗色截图平均亮度 = " + $darkShot)
    Check ($darkShot -lt $lightShot) '暗色比浅色明显更暗' "dark=$darkShot light=$lightShot"
    Check ($darkShot -lt 90) '暗色截图整体是黑底' "dark=$darkShot"
    Check (Test-Path $settingsPath) 'ui-settings.json 已写出'
    if (Test-Path $settingsPath) {
        $json = Get-Content $settingsPath -Raw -Encoding UTF8
        Write-Host ("  设置内容 = " + $json.Replace("`r`n", ' ').Replace("`n", ' '))
        Check ($json -match '"darkMode"\s*:\s*true') '设置里 darkMode=true'
    }

    Write-Host "`n=== 暗色下的弹窗（添加账号 ContentDialog，弹层主题要跟着变）==="
    try {
        Invoke-UiButton '添加账号'
        Start-Sleep -Seconds 2
        $dlgShot = Save-WindowShot (Join-Path $tmp 'theme_dark_dialog.png') $true
        Write-Host ("  弹窗中心区平均亮度 = " + $dlgShot)
        Check ($dlgShot -lt 60) '暗色下弹窗也是深色（不是白底）' "center=$dlgShot"

        $cancel = Find-UiElement $script:CT::Button '取消'
        if ($cancel) { Invoke-Element $cancel } else { Write-Host '  未找到弹窗「取消」按钮' }
        Start-Sleep -Seconds 1
    } catch {
        Check $false '暗色下打开添加账号弹窗' $_.Exception.Message
    }

    Write-Host "`n=== 重启后是否记住暗色 ==="
    Stop-App
    $null = Start-App
    Start-Sleep -Seconds 3
    $sw2 = $null
    for ($i = 0; $i -lt 10 -and -not $sw2; $i++) {
        Start-Sleep -Milliseconds 300
        $sw2 = Find-IdAnywhere 'ThemeSwitch'
    }
    if ($sw2) {
        $reOn = Get-SwitchState $sw2
        Check ($reOn -eq $true) '重启后开关仍是勾选（开=暗色被记住）' "state=$reOn"
        $darkShot2 = Save-WindowShot (Join-Path $tmp 'theme_dark_restart.png')
        Write-Host ("  重启后暗色截图平均亮度 = " + $darkShot2)
        Check ($darkShot2 -lt 90) '重启后仍是黑底' "dark=$darkShot2"
    } else {
        Check $false '重启后能找到开关' 'ThemeSwitch 未找到'
        $sw2 = $null
    }
}
finally {
    Write-Host "`n=== 收尾：拨回浅色并还原现场 ==="
    try {
        if ($null -eq $sw2) {
            $sw2 = $null
            for ($i = 0; $i -lt 10 -and -not $sw2; $i++) {
                Start-Sleep -Milliseconds 300
                $sw2 = Find-IdAnywhere 'ThemeSwitch'
            }
        }
        if ($sw2) { Set-SwitchState $sw2 $false }   # 不勾 = 关 = 浅色
    } catch { Write-Host ("  恢复浅色失败: " + $_.Exception.Message) }
    Start-Sleep -Seconds 1
    Stop-App

    if (Test-Path $settingsBak) {
        # 还原跑测前那份设置（跑测前的现场不能被测试改掉）
        try {
            Move-Item -Path $settingsBak -Destination $settingsPath -Force
            Write-Host '  已还原跑测前的 ui-settings.json'
        } catch {
            Write-Host ("  还原 ui-settings.json 失败: " + $_.Exception.Message)
        }
    } elseif (-not $hadSettings -and (Test-Path $settingsPath)) {
        try { Remove-Item -Path $settingsPath -Force } catch { }
        Write-Host '  已删除本次测试新建的 ui-settings.json（下次启动仍是默认浅色）'
    }
}

Write-Host "截图目录：$tmp"
Write-Host ("SUMMARY g_theme FAILURES=$script:GuiFail")
exit $(if ($script:GuiFail -eq 0) { 0 } else { 1 })
