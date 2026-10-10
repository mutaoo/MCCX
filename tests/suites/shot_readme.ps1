# README 配套截图 v2：裁剪到 MCCX 窗口可见帧（DWM，不带桌面/背后窗口），输出 docs/images/*.png
# 账号库安全（本轮重点）：
#   1) 把三处账号库全部备份两份（stash + %TEMP%）后移除：exe 目录、旧版 %APPDATA%\MCCX、%APPDATA%\MccX；
#      只删 exe 目录不够——AccountStore.MigrateLegacyDataIfAbsent 会在 exe 库缺失时把 %APPDATA% 旧库自动迁回；
#   2) 空库启动，走 UI 新建 3 个测试账号 + 测试服务器（不暴露任何真实账号/服务器）；
#   3) 全部截图完成后：关进程 -> 删测试库 -> 逐库还原备份 -> MD5 逐字节校验，任一失败则 exit 2；
#   4) 中途任何异常，finally 同样还原。用户原账号库一个字节不动。
# 状态：添加账号(填好测试服务器) / 主界面 / 砍怪参数 / 鼠标(间隔点击) / 鼠标(间隔长按) / 生物过滤 / 重连参数 / 连接成功
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TopHelper {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fUnknown);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
}
"@

$imgDir = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\docs\images')"
New-Item -ItemType Directory -Path $imgDir -Force | Out-Null

# ---------- 账号库备份/还原（exe 目录 + 两处旧版 %APPDATA% 目录） ----------
$exeDir = Split-Path $script:AppExe
$stashDir = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\accounts-stash')"
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$libSwapped = $false
$libRestored = $false

function Get-FileMd5([string]$Path) { (Get-FileHash $Path -Algorithm MD5).Hash }

# 三处缺一不可：只删 exe 库的话，程序启动会把 %APPDATA% 旧库（含真实账号）迁回列表
$swapSpecs = @(
    # 2026-10-05：账号库在程序目录的 UserData 子目录里（UserData 为空时也扫一遍 exe 根，
    # 兼容升级前遗留的散落文件）
    @{ Label = 'exe-userdata'; Dir = (Join-Path $exeDir 'UserData') }
    @{ Label = 'exe';          Dir = $exeDir }
    @{ Label = 'appdata';      Dir = (Join-Path $env:APPDATA 'MCCX') }
    @{ Label = 'mccx-old';     Dir = (Join-Path $env:APPDATA 'MccX') }
)
$swaps = @()
$seenDat = @{}
foreach ($spec in $swapSpecs) {
    $d = Join-Path $spec.Dir 'accounts.dat'
    $k = Join-Path $spec.Dir 'accounts.key'
    if (-not (Test-Path $d)) { continue }
    # Windows 路径大小写不敏感：MCCX 与 MccX 是同一目录，必须去重，否则二次移除会报错
    $dk = $d.ToLowerInvariant()
    if ($seenDat.ContainsKey($dk)) { continue }
    $seenDat[$dk] = $true
    $keyMd5 = $null
    if (Test-Path $k) { $keyMd5 = Get-FileMd5 $k }
    $swaps += [pscustomobject]@{
        Dat      = $d
        Key      = $k
        DatMd5   = Get-FileMd5 $d
        KeyMd5   = $keyMd5
        StashDat = Join-Path $stashDir "readme-$ts-$($spec.Label).dat"
        StashKey = Join-Path $stashDir "readme-$ts-$($spec.Label).key"
        TempDat  = Join-Path $env:TEMP "mccx-accounts-$ts-$($spec.Label).dat"
        TempKey  = Join-Path $env:TEMP "mccx-accounts-$ts-$($spec.Label).key"
        Restored = $false
    }
}

function Find-NamedAny {
    param([string]$Name)
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if (-not $el) {
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.AndCondition(
            $nameCond,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, (Get-MainProcId))))
        $el = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    return $el
}

function Expand-Flyout {
    param([string]$EntryName)
    $btn = Find-NamedAny $EntryName
    if (-not $btn) { throw "找不到入口: $EntryName" }
    $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
    }
    Start-Sleep -Milliseconds 800
}

# SwitchToThisWindow + SetWindowPos 组合提升前台（普通 SetForegroundWindow 会被前台锁拒掉）
function Raise-Mccx {
    $h = [IntPtr]$script:Hwnd
    $null = [TopHelper]::ShowWindow($h, 9)
    [void][TopHelper]::SetWindowPos($h, [TopHelper]::HWND_NOTOPMOST, 0, 0, 0, 0,
        [TopHelper]::SWP_NOMOVE -bor [TopHelper]::SWP_NOSIZE -bor [TopHelper]::SWP_SHOWWINDOW)
    [TopHelper]::SwitchToThisWindow($h, $true)
    Start-Sleep -Milliseconds 400
    [void][TopHelper]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 400
}

# 裁剪到 DWM 可见帧（不含不可见 resize 边框与阴影区，避免把窗口后面的
# 桌面/终端内容裁进来），再内收 6px 消掉圆角外的小三角；光标先挪出窗口避免悬停提示入镜
function CropGrab {
    param([string]$Path)
    [void][TopHelper]::SetCursorPos(0, 0)
    Start-Sleep -Milliseconds 600
    # 连接后会多出 --runner 子进程（同名 MCCX.exe）：必须只取有主窗口的那个句柄
    $p = Get-Process -Name MCCX -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw '找不到 MCCX 主窗口' }
    $hwnd = $p.MainWindowHandle
    if ([TopHelper]::GetForegroundWindow() -ne $hwnd) {
        Raise-Mccx
        if ([TopHelper]::GetForegroundWindow() -ne $hwnd) {
            throw ("前台仍不是 MCCX（0x{0:X}），中止" -f [TopHelper]::GetForegroundWindow().ToInt64())
        }
    }
    $rect = New-Object TopHelper+RECT
    $hr = [TopHelper]::DwmGetWindowAttribute($hwnd, [TopHelper]::DWMWA_EXTENDED_FRAME_BOUNDS, [ref]$rect, 16)
    if ($hr -eq 0 -and ($rect.R - $rect.L) -gt 100) {
        $inset = 6
        $cx = $rect.L + $inset; $cy = $rect.T + $inset
        $cw = ($rect.R - $rect.L) - 2 * $inset; $ch = ($rect.B - $rect.T) - 2 * $inset
    } else {
        $r = $script:AppRoot.Current.BoundingRectangle
        $inset = 14
        $cx = [int]$r.X + $inset; $cy = [int]$r.Y + $inset
        $cw = [int]$r.Width - 2 * $inset; $ch = [int]$r.Height - 2 * $inset
    }
    if ($cw -le 100 -or $ch -le 100) { throw "裁剪矩形异常: $cw x $ch" }
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()
    $crop = $bmp.Clone([System.Drawing.Rectangle]::new($cx, $cy, $cw, $ch), $bmp.PixelFormat)
    $bmp.Dispose()
    $crop.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose()
    Write-Host "saved $Path"
}

# ---------- UI 新建测试账号（走真实添加对话框；立即连接必须取消，避免误连） ----------
function Add-AccountUi {
    param([string]$Srv, [string]$Port, [string]$User, [switch]$Shot)
    Invoke-UiButton '添加账号'
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗出现' -Probe {
        $e = $null; try { $e = Find-ById $script:AppRoot 'NewServerBox' } catch { $e = $null }
        if ($e) { 'open' } else { $null }
    }
    $cb = $null
    for ($i = 0; $i -lt 10 -and -not $cb; $i++) {
        $cb = Find-NamedAny '立即连接'
        if (-not $cb) { Start-Sleep -Milliseconds 300 }
    }
    if ($cb) {
        $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
            $tp.Toggle()
            Start-Sleep -Milliseconds 300
        }
    }
    Set-Edit (Find-Edit $script:AppRoot 'NewServerBox') $Srv
    Set-Edit (Find-Edit $script:AppRoot 'NewPortBox') $Port
    Set-Edit (Find-Edit $script:AppRoot 'NewUserBox') $User
    Set-Edit (Find-Edit $script:AppRoot 'NewVersionBox') 'auto'
    if ($Shot) { CropGrab (Join-Path $imgDir '02-add-account.png') }
    $addBtn = Find-Button $script:AppRoot '添加'
    if (-not $addBtn) { throw '找不到弹窗的 [添加] 按钮' }
    Invoke-Element $addBtn
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗关闭' -Probe {
        $e = $null; try { $e = Find-ById $script:AppRoot 'NewServerBox' } catch { $e = $null }
        if ($e) { $null } else { 'closed' }
    }
    Start-Sleep -Milliseconds 300
}

# 列表项显示文本（截图内容的 UIA 真值）
function Get-ListTexts {
    $list = Find-ById $script:AppRoot 'AccountList'
    $out = @()
    foreach ($item in (Get-ListItems $list)) {
        $ct = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)
        $parts = @()
        foreach ($t in $item.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ct)) { $parts += $t.Current.Name }
        $out += ($parts -join ' / ')
    }
    return $out
}

# 真实账号/服务器敏感词——任何一处出现都说明截图会泄密，直接失败。
# 大小写敏感 + 字母边界：精确抓真实账号名（Player/Play/Yuki_Suou 等实名），
# 既不被普通英文（ping 回显 Players: 0/10）误伤，也不会因下划线（Yuki_Suou 里的 \b 失效）漏抓
$sensitive = @('simpfun', 'mcyyy', 'gugugaga', 'lemon', 'Yuki', 'Player', 'Play', 'TestBob', '24840', '11724')
function Assert-NoSensitive([string]$Where, [string]$Text) {
    foreach ($w in $sensitive) {
        $pat = '(?<![A-Za-z])' + [regex]::Escape($w) + '(?![A-Za-z])'
        if ($Text -cmatch $pat) { throw "敏感信息泄露（$Where 含 '$w'）: $Text" }
    }
}

$libFail = $false

try {
    # ---- 1) 三处库全部备份（stash + TEMP 双份，MD5 记账）→ 全部成功后才删 ----
    if ($swaps.Count -gt 0) {
        New-Item -ItemType Directory -Path $stashDir -Force | Out-Null
        foreach ($s in $swaps) {
            Copy-Item $s.Dat $s.StashDat -Force
            Copy-Item $s.Dat $s.TempDat -Force
            if ($s.KeyMd5) {
                Copy-Item $s.Key $s.StashKey -Force
                Copy-Item $s.Key $s.TempKey -Force
            }
            if ((Get-FileMd5 $s.StashDat) -ne $s.DatMd5) { throw "stash 备份校验失败($($s.Dat))，中止（未动原库）" }
            if ((Get-FileMd5 $s.TempDat) -ne $s.DatMd5) { throw "temp 备份校验失败($($s.Dat))，中止（未动原库）" }
        }
        $libSwapped = $true
        foreach ($s in $swaps) {
            if (Test-Path $s.Dat) { Remove-Item $s.Dat -Force }
            if (Test-Path $s.Key) { Remove-Item $s.Key -Force }
            if (Test-Path $s.Dat) { throw "移除失败（文件仍在，会泄露真实账号）: $($s.Dat)" }
            Write-Host "LIB 已备份并移除: $($s.Dat)  md5=$($s.DatMd5)"
        }
    } else {
        Write-Host 'LIB 未发现任何账号库（全新环境）'
    }

    # ---- 2) 空库启动 + UI 新建测试账号 ----
    $null = Start-App
    Save-Foreground
    Raise-Mccx
    Start-Sleep -Milliseconds 800

    Add-AccountUi -Srv '127.0.0.1' -Port '25599' -User 'TestAlpha' -Shot   # 02：填好测试服务器的添加弹窗
    Add-AccountUi -Srv 'mc.example.com' -Port '25565' -User 'TestBravo'
    Add-AccountUi -Srv 'demo.example.org' -Port '25565' -User 'TestCharlie'

    # ---- 3) 内容断言：列表只能是这 3 个测试号 ----
    $texts = @(Get-ListTexts)
    Write-Host ('LIST: ' + ($texts -join ' | '))
    if ($texts.Count -ne 3) { throw "列表应为 3 个测试账号，实际 $($texts.Count)" }
    foreach ($t in $texts) { Assert-NoSensitive '账号列表' $t }
    foreach ($t in $texts) { if ($t -notmatch '^Test(Alpha|Bravo|Charlie)( /|$)') { throw "列表含非预期账号: $t" } }

    # ---- 4) 选中 TestAlpha（本地测试服），清掉启动日志（含本机路径行） ----
    $list = Find-ById $script:AppRoot 'AccountList'
    $item = Find-ListItemByText $list '^TestAlpha( /|$)'
    if (-not $item) { throw '找不到 TestAlpha' }
    Select-Item $item
    Wait-For -TimeoutMs 10000 -What '面板切到 TestAlpha' -Probe {
        $s = Get-WindowStateText $script:AppRoot
        if ($s -eq '未连接') { $s }
    } | Out-Null
    $srvVal = Get-EditValue (Find-Edit $script:AppRoot 'ServerBox')
    $usrVal = Get-EditValue (Find-Edit $script:AppRoot 'UserBox')
    if ($srvVal -ne '127.0.0.1' -or $usrVal -ne 'TestAlpha') { throw "面板字段异常: srv=$srvVal user=$usrVal" }
    Assert-NoSensitive '服务器框' $srvVal
    Assert-NoSensitive '用户名框' $usrVal
    Invoke-UiButton '清空日志'
    Start-Sleep -Milliseconds 500

    # ---- 5) 八张截图 ----
    CropGrab (Join-Path $imgDir '01-main.png')

    Expand-Flyout '自动砍怪选项'
    CropGrab (Join-Path $imgDir '03-attack.png')
    Invoke-UiButton '生物过滤'
    Start-Sleep -Milliseconds 1200
    CropGrab (Join-Path $imgDir '06-mob-filter.png')
    Invoke-UiButton '关闭'
    Start-Sleep -Milliseconds 800
    Close-AllFlyouts

    Expand-Flyout '鼠标控制选项'
    # 距离滑条展开时会抢到键盘焦点并弹出 ShowOnFocus 工具提示压图：
    # 把焦点挪到没有工具提示的「左键模式」下拉（05 里该处聚焦实测无提示），再截图
    $focusEl = $null
    for ($i = 0; $i -lt 10 -and -not $focusEl; $i++) {
        $focusEl = Find-NamedAny '左键模式'
        if (-not $focusEl) { Start-Sleep -Milliseconds 200 }
    }
    if ($focusEl) { $focusEl.SetFocus(); Start-Sleep -Milliseconds 500 }
    CropGrab (Join-Path $imgDir '04-mouse-interval-click.png')
    Set-ComboItem '左键模式' '间隔长按'
    Start-Sleep -Milliseconds 500
    CropGrab (Join-Path $imgDir '05-mouse-interval-hold.png')
    Set-ComboItem '左键模式' '间隔点击'
    Start-Sleep -Milliseconds 400
    Close-AllFlyouts

    Expand-Flyout '自动重连选项'
    CropGrab (Join-Path $imgDir '07-reconnect.png')
    Close-AllFlyouts

    # ---- 6) 连接本地测试服（TestAlpha）→ 08 ----
    Invoke-UiButton '连接'
    Wait-For -TimeoutMs 30000 -What '连接成功' -Probe {
        $s = Get-WindowStateText $script:AppRoot
        if ($s -like '已连接*') { $s }
    } | Out-Null
    $st = Get-WindowStateText $script:AppRoot
    if ($st -ne '已连接 127.0.0.1:25599') { throw "状态异常: $st" }
    Start-Sleep -Seconds 4
    $logTxt = Get-RecentLogText -Max 60
    Assert-NoSensitive '日志' $logTxt
    CropGrab (Join-Path $imgDir '08-connected.png')
    Invoke-UiButton '断开'
    Wait-For -TimeoutMs 20000 -What '断开完成' -Probe {
        $s = Get-WindowStateText $script:AppRoot
        if ($s -eq '未连接') { $s }
    } | Out-Null
} catch {
    Write-Host "EXCEPTION $($_.Exception.Message)"
    $script:ShotFail = $true
} finally {
    try { Close-AllFlyouts } catch { }
    try { Stop-App } catch { }
    try { Restore-Foreground } catch { }

    # ---- 7) 三处库逐个还原并逐字节校验 ----
    if ($libSwapped) {
        foreach ($s in $swaps) {
            try {
                if (Test-Path $s.Dat) { Remove-Item $s.Dat -Force }
                if (Test-Path $s.Key) { Remove-Item $s.Key -Force }
                $tmpLeft = $s.Dat + '.tmp'
                if (Test-Path $tmpLeft) { Remove-Item $tmpLeft -Force }
                Copy-Item $s.StashDat $s.Dat -Force
                if ($s.KeyMd5) { Copy-Item $s.StashKey $s.Key -Force }
                $nowDat = Get-FileMd5 $s.Dat
                $nowKey = $null
                if (Test-Path $s.Key) { $nowKey = Get-FileMd5 $s.Key }
                if ($nowDat -eq $s.DatMd5 -and $nowKey -eq $s.KeyMd5) {
                    $s.Restored = $true
                    Write-Host "LIB-RESTORE OK  $($s.Dat)  md5=$nowDat"
                } else {
                    Write-Host "LIB-RESTORE FAIL  $($s.Dat)  dat=$nowDat(期望 $($s.DatMd5)) key=$nowKey(期望 $($s.KeyMd5))"
                    Write-Host "灾备副本: $($s.TempDat) / $($s.TempKey)"
                }
            } catch {
                Write-Host "LIB-RESTORE EXCEPTION  $($s.Dat)  $($_.Exception.Message)  灾备: $($s.TempDat)"
            }
        }
        # stash 灾备只在全部校验通过后才清掉；校验失败要留着救命
        $pending = @($swaps | Where-Object { -not $_.Restored })
        if ($pending.Count -eq 0) {
            $libRestored = $true
            foreach ($s in $swaps) { try { Remove-Item $s.StashDat,$s.StashKey -Force -ErrorAction SilentlyContinue } catch { } }
        }
    }
}

if ($script:ShotFail) { exit 1 }
if ($libSwapped -and -not $libRestored) { exit 2 }
exit 0
