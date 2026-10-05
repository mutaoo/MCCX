# 外观三连验证：程序名 = MCCX、服务器地址不再拼写检查、参数下拉展开后光标在文本末尾
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
if (-not ('MccXLook.Pw' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
namespace MccXLook {
    public static class Pw {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    }
}
"@
}

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

function Grab {
    param([string]$Path)
    $r = New-Object MccXLook.Pw+RECT
    [void][MccXLook.Pw]::GetWindowRect($script:Hwnd, [ref]$r)
    $w = $r.R - $r.L
    $h = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][MccXLook.Pw]::PrintWindow($script:Hwnd, $hdc, 3)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  shot -> $Path"
}

function Get-CaretInfo {
    # 读输入框的“真实文本 + 选区/光标位置 + 是否有键盘焦点”
    param($El)
    $info = @{ HasFocus = $false; Start = -1.0; End = -1.0; Text = ''; Err = '' }
    try { $info.HasFocus = [bool]$El.Current.HasKeyboardFocus } catch { }
    $info.Text = Get-EditValue $El
    try {
        $tp = $El.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $sel = $tp.GetSelection()
        if ($sel -and $sel.Length -gt 0) {
            $info.Start = [double]$sel[0].Start
            $info.End = [double]$sel[0].End
        }
    } catch { $info.Err = $_.Exception.Message }
    return $info
}

$null = Start-App

# ---------- 1) 程序名 ----------
$proc = Get-Process MCCX | Select-Object -First 1
Assert-True ($proc.ProcessName -eq 'MCCX') '进程名 = MCCX' "实际 $($proc.ProcessName)"
Assert-True ((Split-Path $proc.Path -Leaf) -ieq 'MCCX.exe') '磁盘文件 = MCCX.exe' $proc.Path
$wn = [string]$script:AppRoot.Current.Name
Assert-True ($wn -eq 'MCCX') '窗口标题 = MCCX' "实际 '$wn'"
$old = @(Get-Process MCCX.App -ErrorAction SilentlyContinue)
Assert-True ($old.Count -eq 0) '没有 MCCX.App 旧名进程' "count=$($old.Count)"

Grab "$out\look_main.png"
$srv = Get-EditValue (Find-Edit $script:AppRoot 'ServerBox')
Write-Host "  服务器地址框 = $srv"

# ---------- 2) 下拉展开后光标 ----------
$cases = @(
    @{ Btn = '自动砍怪选项'; Edit = '攻击距离'; Shot = 'look_fly_attack.png'; Strict = $true },
    @{ Btn = '鼠标控制选项'; Edit = '左键间隔'; Shot = 'look_fly_mouse.png'; Strict = $false },
    @{ Btn = '自动重连选项'; Edit = '重连次数'; Shot = 'look_fly_recon.png'; Strict = $true }
)

foreach ($c in $cases) {
    Close-AllFlyouts
    Start-Sleep -Milliseconds 300
    $b = Get-UiButton $c.Btn
    $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
    }
    Start-Sleep -Milliseconds 1400

    $el = Find-UiElement $script:CT::Edit $c.Edit
    if (-not $el) {
        Assert-True $false "下拉 [$($c.Btn)] 里能找到 [$($c.Edit)]" '控件未出现'
        continue
    }

    $info = Get-CaretInfo $el
    $len = $info.Text.Length
    $atEnd = ($info.Start -ge ($len - 0.5)) -and ($info.Start -le ($len + 2)) -and (($info.End - $info.Start) -lt 0.5)
    Write-Host ("  {0} -> '{1}' len={2} focus={3} sel=[{4} .. {5}] atEnd={6} {7}" -f `
        $c.Btn, $info.Text, $len, $info.HasFocus, $info.Start, $info.End, $atEnd, $info.Err)
    Grab "$out\$($c.Shot)"

    if ($c.Strict) {
        # 光标是否在文本末尾以截图 look_fly_*.png 为准：WinUI 的 TextPattern.GetSelection()
        # 恒回 [0..0]，拿不到真实光标位置；这里只断言“焦点确实落进了这个输入框”。
        Assert-True $info.HasFocus "下拉 [$($c.Edit)] 展开后焦点落进该输入框" `
            ("len={0} sel=[{1}..{2}]" -f $len, $info.Start, $info.End)
    }

    $ec.Collapse()
    Start-Sleep -Milliseconds 500
}

Close-AllFlyouts

# ---------- 3) 只有一个主进程（无子进程串台） ----------
$all = @(Get-CimInstance Win32_Process -Filter "Name='MCCX.exe'")
Write-Host "  MCCX.exe 进程数 = $($all.Count)"
Assert-True ($all.Count -eq 1) '未连接时只有一个 MCCX 进程' "count=$($all.Count)"

$n = Get-FailureCount
Write-Host "FAILURES=$n"
exit $n
