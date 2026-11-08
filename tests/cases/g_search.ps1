# 2026-10-14 需求实测：日志搜索（搜索框/计数/清空）+ 输入框占位文案 + 命令建议浮层。
# 2026-10-15 改版：去掉上/下跳转按钮（改为滚轮翻看命中），搜索框内嵌放大镜图标。
# 2026-10-16 改版：命令栏和日志搜索框变成"连上服务器才显示"，所以本用例要先连本地测试服
#   （Enter-TestSession：起本地测试服 + 专用测试账号 MccXSearchTest，结束删账号还原基线，
#    不碰用户已有账号）；顺带把"未连接时命令栏禁用、搜索框展开到命令输入框位置"也钉住。
# 2026-11-06 改版（用户要求）：断开后命令栏不再收起，而是"保留布局但禁用/透明"，日志搜索框
#   从右下角逐渐展开到命令区（宽度覆盖整行，高度与命令输入框一致）；重新连上再动画收回右下角。
#   所以第 0 段不再断言"节点不存在"，改成断言节点仍在、命令栏控件 IsEnabled=False、
#   搜索框可用且左右边缘贴住命令区、顶边/高度与命令输入框一致。
# 2026-10-16 追加（用户反馈）：筛选结束（清空搜索框）后视图必须回到最底的最新日志，而不是停在
#   第一条命中（顶部）。复现靠 RCON 向本地测试服慢慢广播 40 行 say 当"一屏放不下的长日志"，
#   再"筛一个命中很多的词 → 清空"；原样只对本地测试服广播，用户自己的服务器一概不碰。
# 测完把两个输入框清空，不留测试内容；不连用户自己的服务器。
# 用法：pwsh -ExecutionPolicy Bypass -File tests/cases/g_search.ps1
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXSearchShot {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$fail = 0
function Check([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name  $detail"; $script:fail++ }
}

# 2026-10-16 需求：搜索框里没字时显示放大镜图标，一有字就暂时隐藏、清空后再出现。
# 放大镜只是 FontIcon（UIA 里没有节点），只能数像素：图标占框左边缘往右 9~24.5 DIP 那块
# （没字时 TextBox 的左 Padding 是 26，专门给它让位；有字时收到 4，输入的文字直接顶到框左边）。
# 取"框左边缘往右 5~18 DIP、上下 4~27 DIP"这块数墨点：
#   · 没字时：图标（9~24.5 DIP）大部分落在里面 → 有墨点（占位文字在 26 DIP 之后，碰不到这块）；
#   · 有字时：文字已经顶到左边，头一两个字就落在这块里，跟图标分不出来，所以"图标收起"这条
#     改用"只敲一个空格"来量——空白也算有字（图标该收起），而空白本身不落墨，这块应该只剩底色；
#     清空后再量一次，墨点回到图标那个量级。
function Get-IconInk {
    param($Box)
    $r = $Box.Current.BoundingRectangle
    if ($r.Width -lt 10) { return -1 }
    $h = [IntPtr]$script:Hwnd
    $wr = New-Object MccXSearchShot+RECT
    [void][MccXSearchShot]::GetWindowRect($h, [ref]$wr)
    $w = $wr.R - $wr.L; $ht = $wr.B - $wr.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $null = [MccXSearchShot]::PrintWindow($h, $hdc, 3)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    # 框高固定 32 DIP，拿它当比例尺，把 DIP 换算成设备像素（200% DPI 时是 2 倍）
    $scale = $r.Height / 32.0
    $bx = [int]($r.X - $wr.L); $by = [int]($r.Y - $wr.T)
    $x0 = [int]($bx + 5 * $scale); $x1 = [int]($bx + 18 * $scale)
    $y0 = [int]($by + 4 * $scale); $y1 = [int]($by + 27 * $scale)

    # 底色取这块里出现最多的亮度（框内底色），再数"和它明显不一样"的像素
    $lum = @{}
    $vals = New-Object System.Collections.ArrayList
    for ($y = $y0; $y -le $y1; $y++) {
        for ($x = $x0; $x -le $x1; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $l = [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
            [void]$vals.Add($l)
            if ($lum.ContainsKey($l)) { $lum[$l]++ } else { $lum[$l] = 1 }
        }
    }
    $bmp.Dispose()
    $bg = 0; $best = -1
    foreach ($k in $lum.Keys) { if ($lum[$k] -gt $best) { $best = $lum[$k]; $bg = $k } }
    $ink = 0
    foreach ($l in $vals) { if ([Math]::Abs($l - $bg) -gt 40) { $ink++ } }
    return $ink
}

# 2026-11-07：取搜索框内部靠右那块（避开文字/清空按钮）的底色，用来验证"点进去不会变白底"。
# 采样点按框高的比例换算 DIP→设备像素（200% DPI 时是 2 倍），换主题/缩放都不用改。
function Get-BoxBackdrop {
    param($Box)
    $r = $Box.Current.BoundingRectangle
    if ($r.Width -lt 10) { return $null }
    $h = [IntPtr]$script:Hwnd
    $wr = New-Object MccXSearchShot+RECT
    [void][MccXSearchShot]::GetWindowRect($h, [ref]$wr)
    $w = $wr.R - $wr.L; $ht = $wr.B - $wr.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $null = [MccXSearchShot]::PrintWindow($h, $hdc, 3)
    $g.ReleaseHdc($hdc)
    $g.Dispose()

    $scale = $r.Height / 32.0
    $x = [int](($r.X - $wr.L) + $r.Width * 0.8)
    $y = [int](($r.Y - $wr.T) + 16 * $scale)
    $c = $bmp.GetPixel($x, $y)
    $bmp.Dispose()
    return $c
}

# 建议浮层是 Popup，不挂在主窗口子树里：按 AutomationId + 进程跨窗口找（同 g_new 的做法）
function Find-IdAnywhere([string]$Id) {
    $procId = Get-MainProcId
    if (-not $procId) { return $null }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::AutomationIdProperty, $Id)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)))
    return $script:AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 取建议浮层里的所有文本行（关着/没内容时返回 @()）
function Get-SuggestionTexts {
    $hostEl = Find-IdAnywhere 'SuggestionPopupHost'
    if (-not $hostEl) { return @() }
    $c = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)
    $texts = $hostEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
    $out = @()
    foreach ($t in $texts) { $out += [string]$t.Current.Name }
    return $out
}

function Get-SearchCountText {
    $el = Find-IdAnywhere 'LogSearchCount'
    if (-not $el) { return $null }
    return [string]$el.Current.Name
}

Write-Host '=== 启动 MCCX ==='
$null = Start-App
Save-Foreground
Start-Sleep -Seconds 2

$session = $null
try {
    Write-Host "`n=== 0) 未连接时：命令栏保留布局但禁用，日志搜索框展开到命令输入框的位置 ==="
    $stateOff = ''
    try { $stateOff = Get-WindowStateText $script:AppRoot } catch { }
    Check ($stateOff -eq '未连接') '起步就是未连接' "实际='$stateOff'"

    # 2026-11-06 起离线不再 Visibility=Collapsed（否则搜索框量不到命令输入框的目标矩形），
    # 改成整行 Opacity=0 + IsHitTestVisible=false + 四个子控件 IsEnabled=false：
    # UIA 节点仍在，只是点不动、Tab 也进不去。
    $cmdOff = Find-IdAnywhere 'CommandBox'
    $searchOff = Find-IdAnywhere 'LogSearchBox'
    Check ($null -ne $cmdOff) '未连接时命令输入框仍在自动化树里（只禁用，不收起）'
    Check ($null -ne $searchOff) '未连接时日志搜索框仍显示（展开态）'
    if ($cmdOff) {
        Check (-not $cmdOff.Current.IsEnabled) '未连接时命令输入框禁用' "IsEnabled=$($cmdOff.Current.IsEnabled)"
    }
    if ($searchOff) {
        Check ($searchOff.Current.IsEnabled) '未连接时日志搜索框仍可输入' "IsEnabled=$($searchOff.Current.IsEnabled)"
    }
    foreach ($name in @('发送', '清空日志', '常用命令')) {
        $b = $null
        try { $b = Get-UiButton $name } catch { $b = $null }
        Check ($null -ne $b) "未连接时「$name」按钮仍在自动化树里（只禁用，不收起）"
        if ($b) {
            Check (-not $b.Current.IsEnabled) "未连接时「$name」按钮禁用" "IsEnabled=$($b.Current.IsEnabled)"
        }
    }

    # 展开态的几何要求（2026-11-06 第二轮）：宽度覆盖整条命令区（「常用命令」左边缘 →
    # 「清空日志」右边缘），顶边/高度仍与命令输入框一致。BoundingRectangle 是设备像素，允许 ±3 px。
    if ($cmdOff -and $searchOff) {
        $leftBtn = $null; $rightBtn = $null
        try { $leftBtn = Get-UiButton '常用命令' } catch { }
        try { $rightBtn = Get-UiButton '清空日志' } catch { }
        if ($leftBtn -and $rightBtn) {
            $rl = $leftBtn.Current.BoundingRectangle
            $rr = $rightBtn.Current.BoundingRectangle
            $rc = $cmdOff.Current.BoundingRectangle
            $rs = $searchOff.Current.BoundingRectangle
            Write-Host ("  命令区    : x={0} right={1}" -f $rl.X, ($rr.X + $rr.Width))
            Write-Host ("  命令输入框: x={0} y={1} w={2} h={3}" -f $rc.X, $rc.Y, $rc.Width, $rc.Height)
            Write-Host ("  搜索框    : x={0} y={1} w={2} h={3}" -f $rs.X, $rs.Y, $rs.Width, $rs.Height)
            Check ([Math]::Abs($rs.X - $rl.X) -le 3) '展开后搜索框左边缘与命令区左边缘对齐' "Δ=$([Math]::Abs($rs.X - $rl.X))"
            Check ([Math]::Abs(($rs.X + $rs.Width) - ($rr.X + $rr.Width)) -le 3) '展开后搜索框右边缘与命令区右边缘对齐' "Δ=$([Math]::Abs(($rs.X + $rs.Width) - ($rr.X + $rr.Width)))"
            Check ([Math]::Abs($rs.Y - $rc.Y) -le 3) '展开后搜索框顶边与命令输入框对齐' "Δy=$([Math]::Abs($rs.Y - $rc.Y))"
            Check ([Math]::Abs($rs.Height - $rc.Height) -le 3) '展开后搜索框与命令输入框同高' "Δh=$([Math]::Abs($rs.Height - $rc.Height))"
        }
    }

    # 2026-11-07（用户反馈）：离线展开态点进搜索框输入时，整块框会消失；点进去还会变白底。
    # 根因："命中 n/N"徽标原来用布局列把整块撑得比日志区还宽 → TextBox 被挤出可视区；
    # 现在徽标只做渲染偏移（不参与测量）。这里钉住：输入后框仍上屏、焦点态底色不变白、徽标能读到。
    if ($searchOff) {
        $bgBefore = Get-BoxBackdrop $searchOff
        Set-Edit $searchOff 'MCCX'
        Start-Sleep -Milliseconds 700
        $rTyped = $searchOff.Current.BoundingRectangle
        Check ($rTyped.Width -gt 100) '离线展开态输入后搜索框仍在上屏（不会整块消失）' `
            ("实际 w={0} offscreen={1}" -f $rTyped.Width, $searchOff.Current.IsOffscreen)
        $bgFocused = Get-BoxBackdrop $searchOff
        $sameBg = $null -ne $bgBefore -and $null -ne $bgFocused `
            -and [Math]::Abs($bgBefore.R - $bgFocused.R) -le 8 `
            -and [Math]::Abs($bgBefore.G - $bgFocused.G) -le 8 `
            -and [Math]::Abs($bgBefore.B - $bgFocused.B) -le 8
        $bgText = if ($bgBefore -and $bgFocused) {
            '#{0:X2}{1:X2}{2:X2} -> #{3:X2}{4:X2}{5:X2}' -f $bgBefore.R, $bgBefore.G, $bgBefore.B, $bgFocused.R, $bgFocused.G, $bgFocused.B
        } else { 'n/a' }
        Check $sameBg '点进搜索框后底色不变（与日志区一致，不变白底）' $bgText
        $cOff = Get-SearchCountText
        Check (-not [string]::IsNullOrWhiteSpace($cOff)) '离线输入后出现命中计数徽标' "实际=[$cOff]"
        Set-Edit $searchOff ''
        Start-Sleep -Milliseconds 500
    }

    Write-Host "`n=== 连接本地测试服（命令栏恢复可用，搜索框收回右下角） ==="
    $session = Enter-TestSession -User 'MccXSearchTest'
    Write-Host "  已连接: $($session.StateText)"
    foreach ($id in @('CommandBox', 'LogSearchBox', 'FrequentCommandsButton')) {
        Check ($null -ne (Find-IdAnywhere $id)) "连上后 $id 显示出来"
    }
    foreach ($name in @('发送', '清空日志', '常用命令')) {
        $b = $null
        try { $b = Get-UiButton $name } catch { $b = $null }
        Check ($null -ne $b) "连上后「$name」按钮显示出来"
        if ($b) {
            Check ($b.Current.IsEnabled) "连上后「$name」按钮可用" "IsEnabled=$($b.Current.IsEnabled)"
        }
    }

    # 连上后搜索框要回到右下角的小尺寸（宽度 = 发送 + 清空日志，明显比命令输入框窄）。
    # 收回也走 240ms 动画，等它跑完再量。
    Start-Sleep -Milliseconds 600
    $cmdOn = Find-IdAnywhere 'CommandBox'
    $searchOn = Find-IdAnywhere 'LogSearchBox'
    if ($cmdOn) {
        Check ($cmdOn.Current.IsEnabled) '连上后命令输入框可编辑' "IsEnabled=$($cmdOn.Current.IsEnabled)"
    }
    if ($searchOn) {
        $rs = $searchOn.Current.BoundingRectangle
        $rc = if ($cmdOn) { $cmdOn.Current.BoundingRectangle } else { $rs }
        Write-Host ("  连上后搜索框: x={0} y={1} w={2} h={3}" -f $rs.X, $rs.Y, $rs.Width, $rs.Height)
        Check ($rs.Width -lt $rc.Width) '连上后搜索框收回右下角（不再撑到命令输入框宽）' "搜索框=$($rs.Width) 命令框=$($rc.Width)"
        Check ($rs.Y -lt $rc.Y) '连上后搜索框在命令栏上方（右下角）' "搜索框 y=$($rs.Y) 命令框 y=$($rc.Y)"
    }

    Write-Host "`n=== 1) 日志搜索框 ==="
    $search = Find-IdAnywhere 'LogSearchBox'
    Check ($null -ne $search) '日志区右下角有日志搜索框 LogSearchBox'
    if ($search) {
        $name = [string]$search.Current.Name
        Write-Host "  搜索框 UIA Name = $name"
        Check ($name -eq '日志搜索') '搜索框占位/名称是「搜索日志」' "实际=$name"
    }

    # 2026-10-16：匹配数改成了"搜索时才出现的小徽标"（贴搜索框左边），没搜索词时整块收起、UIA 里也没有节点，
    # 所以不再断言它静态存在——改到第 2 段（真搜起来之后）再断言。

    Write-Host "`n=== 1b) 搜索框里的放大镜图标（2026-10-16 需求）==="
    if ($search) {
        # PrintWindow 刚启动后第一张可能是空帧（实测首张数出来是 0），先空抓一张热一下
        $null = Get-IconInk $search
        Start-Sleep -Milliseconds 400
        $inkEmpty = Get-IconInk $search
        Write-Host "  没输入时图标区墨点 = $inkEmpty"
        Check ($inkEmpty -gt 0) '没输入时放大镜图标在（框最左边那块有墨点）' "实际=$inkEmpty"

        Set-Edit $search ' '
        Start-Sleep -Milliseconds 700
        $inkTyped = Get-IconInk $search
        Write-Host "  有输入时（只敲了一个空格）图标区墨点 = $inkTyped"
        Check ($inkTyped -le 2) '框里一有字，放大镜图标暂时隐藏' "实际=$inkTyped（没字时 $inkEmpty）"

        Set-Edit $search ''
        Start-Sleep -Milliseconds 700
        $inkCleared = Get-IconInk $search
        Write-Host "  清空后图标区墨点 = $inkCleared"
        Check ($inkCleared * 2 -gt $inkEmpty) '清空后放大镜图标重新出现' "实际=$inkCleared（原 $inkEmpty）"
    }

    # 找一段日志里真实出现的词当搜索目标（不依赖固定文案）
    $logText = ''
    try { $logText = Get-RecentLogText } catch { $logText = '' }
    $token = ''
    foreach ($line in ($logText -split "`n")) {
        foreach ($w in ($line -split '[^\w]+')) {
            if ($w.Length -ge 4) { $token = $w; break }
        }
        if ($token) { break }
    }
    Write-Host "  从日志里挑的搜索词 = '$token'"
    Check ($token.Length -ge 4) '日志里能挑到长度>=4 的搜索词'

    if ($search -and $token) {
        Write-Host "`n=== 2) 搜一个日志里真实存在的词 ==="
        Set-Edit $search $token
        Start-Sleep -Milliseconds 600
        $c1 = Get-SearchCountText
        Write-Host "  计数 = $c1"
        Check ($null -ne (Find-IdAnywhere 'LogSearchCount')) '搜索时出现匹配计数徽标 LogSearchCount'
        Check ($c1 -match '^命中 1/([0-9]+)$') '显示「命中 1/N」' "实际=$c1"
        $total = 0
        if ($c1 -match '^命中 1/([0-9]+)$') { $total = [int]$Matches[1] }
        Check ($total -ge 1) '至少 1 处命中'

        # 选中状态：TextBox 走 TextPattern，能读到选区就是搜索真的落到了日志上
        try {
            $logBox = Find-IdAnywhere 'LogBox'
            $tp = $logBox.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
            $sel = $tp.GetSelection()
            $selText = ''
            foreach ($r in $sel) { $selText += $r.GetText(10000) }
            Check ($selText.ToLower().Contains($token.ToLower())) '日志里选中了命中处' "选区=$selText"
        } catch {
            Write-Host "  (TextPattern 读选区不可用: $($_.Exception.Message)) 不作硬断言"
        }

        Write-Host "`n=== 3) 已去掉上/下跳转按钮（2026-10-15 改版，改为滚轮翻看命中）==="
        $next = Find-UiElement $script:CT::Button '下一个匹配'
        Check ($null -eq $next) '没有「下一个匹配」按钮' ("实际=" + ($null -ne $next))
        $prev = Find-UiElement $script:CT::Button '上一个匹配'
        Check ($null -eq $prev) '没有「上一个匹配」按钮' ("实际=" + ($null -ne $prev))

        Write-Host "`n=== 4) 搜一个不存在的词 ==="
        Set-Edit $search 'zzmccxnosuchword'
        Start-Sleep -Milliseconds 500
        $c4 = Get-SearchCountText
        Check ($c4 -eq '无匹配') '无命中显示「无匹配」' "实际=$c4"

        Write-Host "`n=== 5) 清空搜索 ==="
        Set-Edit $search ''
        Start-Sleep -Milliseconds 500
        $c5 = Get-SearchCountText
        Check ($null -eq $c5 -or $c5 -eq '') '清空后计数徽标收起（不再显示命中数）' "实际=[$c5]"

        Write-Host "`n=== 5b) 清空搜索后视图要回到最底（2026-10-16 用户反馈）==="
        # 用户反馈：筛选结束（把框清空）后日志总是停在最顶（第一条命中附近），而不是最新一行。
        # 复现链条：视图本来在最底（用户一直在看最新日志）→ 输入一个命中很多的词，日志被筛成
        # "只有命中行"（还是很长）→ 跳去第一条命中的那一下把偏移往回拽，被判成"用户上翻"而暂停 →
        # 清空搜索时那份暂停还在，于是谁都不贴底，视图就留在第一条命中那儿（顶部）。
        # 灌行用"慢慢来"而不是一口气广播：跟随是靠行与行之间的安静期落地的，
        # 一口气刷它来不及贴底（那是另一件事，不混进这条用例）。
        Write-Host '  [srv] 慢慢灌 40 行聊天（让跟随稳定落到底 = 复现前提）'
        for ($i = 1; $i -le 40; $i++) {
            try { $null = Invoke-Rcon "say mccx-scroll $i" } catch { break }
            Start-Sleep -Milliseconds 260
        }
        Start-Sleep -Milliseconds 1500

        $logHost = Get-LogList
        $sp5 = $logHost.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        Check ($sp5.Current.VerticallyScrollable) '日志长到可以滚动（"清空后贴底"的前提）' "scrollable=$($sp5.Current.VerticallyScrollable)"

        $before = -1
        $sw5a = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw5a.ElapsedMilliseconds -lt 6000) {
            $before = [double]$sp5.Current.VerticalScrollPercent
            if ($before -ge 99) { break }
            Start-Sleep -Milliseconds 300
        }
        Check ($before -ge 99) '清空前视图在最底（用户一直在看最新日志）' "实际=$before%"

        Set-Edit $search 'mccx-scroll'
        Start-Sleep -Milliseconds 1200
        $filtered = [double]((Get-LogList).GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)).Current.VerticalScrollPercent
        Write-Host "  筛选后视图位置 = $filtered%（命中很多、第一条在最前面，这里应已回到顶部附近）"

        Set-Edit $search ''
        Start-Sleep -Milliseconds 500
        $c5b = Get-SearchCountText
        Check ($null -eq $c5b -or $c5b -eq '') '清空后计数徽标收起' "实际=[$c5b]"

        $pct5 = -1
        $sw5 = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw5.ElapsedMilliseconds -lt 8000) {
            $s5 = (Get-LogList).GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($s5.Current.VerticallyScrollable) { $pct5 = [double]$s5.Current.VerticalScrollPercent }
            if ($pct5 -ge 99) { break }
            Start-Sleep -Milliseconds 300
        }
        Check ($pct5 -ge 99) '清空搜索后视图回到最底（最新日志），不再停在顶部/第一条命中' "实际=$pct5%"
    }

    Write-Host "`n=== 6) 输入框占位文案 ==="
    $cmdBox = Find-IdAnywhere 'CommandBox'
    Check ($null -ne $cmdBox) '命令输入框存在'
    if ($cmdBox) {
        $ph = [string]$cmdBox.Current.Name
        Write-Host "  CommandBox UIA Name = $ph"
        if ($ph) {
            Check ($ph.Contains('聊天或命令')) '占位文案提到「聊天或命令」' "实际=$ph"
            Check (-not $ph.Contains('//xxx')) '占位文案不再提 //xxx 转义（2026-10-16 已移除该功能）' "实际=$ph"
        } else {
            Write-Host '  (UIA 未暴露占位文案，跳过占位断言)'
        }
    }

    Write-Host "`n=== 7) 命令建议浮层 ==="
    if ($cmdBox) {
        Set-Edit $cmdBox '/hel'
        Start-Sleep -Milliseconds 800
        $texts = Get-SuggestionTexts
        $joined = ($texts -join '')
        Write-Host "  /hel 候选 = [$joined]"
        Check ($joined.Contains('help')) 'MCC 内部命令出现在候选里' "实际=$joined"

        Set-Edit $cmdBox '/zzmccx-no-match-here'
        Start-Sleep -Milliseconds 600
        $texts2 = Get-SuggestionTexts
        Check ($texts2.Count -eq 0) '无候选时浮层关着' ("实际=" + ($texts2 -join '|'))

        Set-Edit $cmdBox 'plain chat'
        Start-Sleep -Milliseconds 600
        $texts3 = Get-SuggestionTexts
        Check ($texts3.Count -eq 0) '不以 / 开头不弹建议' ("实际=" + ($texts3 -join '|'))

        Set-Edit $cmdBox '/hel lo'
        Start-Sleep -Milliseconds 600
        $texts4 = Get-SuggestionTexts
        Check ($texts4.Count -eq 0) '出现空格（要发参数）后不弹建议' ('实际=' + ($texts4 -join '|'))
    }

    Write-Host "`n=== 8) 键盘交互（↑/Tab/Esc）==="
    # 键盘路径必须真焦点 + 真按键（ValuePattern.SetValue 不触发 KeyDown）。
    # 抢不到前台时（例如系统前台被 GameInputSvc 这类进程占住）SendKeys 会打进别的窗口——
    # 那是环境问题不是程序问题，这时跳过键盘断言只报一句 SKIP，免得把回归网染红。
    if ($cmdBox) { Set-Foreground }
    $fg8 = ''
    try { $fg8 = Get-ForegroundProcess } catch { $fg8 = '' }
    if ($cmdBox -and $fg8 -ne 'MCCX') {
        Write-Host "  [skip] 抢不到前台（当前前台=$fg8），跳过键盘交互断言"
        $cmdBox = $null
    }
    if ($cmdBox) {
        Set-Edit $cmdBox ''
        Start-Sleep -Milliseconds 300
        $cmdBox.SetFocus()
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('/he')
        Start-Sleep -Milliseconds 800
        $t8a = Get-SuggestionTexts
        Write-Host "  敲 /he 后候选 = [$(($t8a -join '|'))]"
        Check (($t8a -join '').Contains('help')) '敲键盘时候选出现' "实际=$($t8a -join '|')"

        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        Start-Sleep -Milliseconds 500
        $v8 = ''
        try {
            $vp = $cmdBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $v8 = [string]$vp.Current.Value
        } catch { }
        Write-Host "  Tab 填入后 = '$v8'"
        Check ($v8 -eq '/help') 'Tab 把选中候选填进输入框' "实际=$v8"
        $t8b = Get-SuggestionTexts
        Check ($t8b.Count -eq 0) 'Tab 填入后浮层收起' ("实际=" + ($t8b -join '|'))

        # 重新输入前先清空，否则会拼成 /help/he（带斜杠匹配不到候选）
        Set-Edit $cmdBox ''
        Start-Sleep -Milliseconds 300
        $cmdBox.SetFocus()
        Start-Sleep -Milliseconds 200
        [System.Windows.Forms.SendKeys]::SendWait('/he')
        Start-Sleep -Milliseconds 800
        $t8c = Get-SuggestionTexts
        Check (($t8c -join '').Contains('help')) '再次输入候选重新出现'
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 500
        $t8d = Get-SuggestionTexts
        Check ($t8d.Count -eq 0) 'Esc 关掉浮层' ("实际=" + ($t8d -join '|'))

        # ↑ 在浮层关着时应翻命令历史（吞掉按键），开着时选候选——两种都不把焦点弄丢
        $v8e = ''
        try {
            $vp2 = $cmdBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $v8e = [string]$vp2.Current.Value
        } catch { }
        Write-Host "  Esc 后输入框 = '$v8e'"
    }

    Write-Host "FAILURES=$fail"
}
finally {
    # 收尾：把两个输入框清空（连接状态下这两个控件才在），再断开 + 删掉测试账号还原账号列表
    try {
        $b1 = Find-IdAnywhere 'LogSearchBox'
        if ($b1) { Set-Edit $b1 '' }
    } catch { }
    try {
        $b2 = Find-IdAnywhere 'CommandBox'
        if ($b2) { Set-Edit $b2 '' }
    } catch { }
    try { Exit-TestSession -Session $session } catch { Write-Host ("  [clean] 收尾失败: " + $_.Exception.Message) }
    Restore-Foreground
    Stop-App
}

exit $fail
