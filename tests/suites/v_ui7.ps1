# 本轮 UI 改动的几何 + 视觉验收：
#   1) 参数一行（带冒号标签）+ 连接/断开；连接状态+延迟浮在日志区右上角（2026-10-09 问题③定稿）
#   2) “生物过滤”改成和“添加账号”一样的弹窗：分类可展开/收回，分类条与生物列表底色分层
#   3) “添加账号”按钮在“账号列表”标题行最右侧；三个参数弹层宽度统一
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TopHelper {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fUnknown);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cbSize);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public static void MoveTo(int x, int y) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        inputs[0].mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
        inputs[0].mi.dwFlags = (int)(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
        SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

$dir = "$(Join-Path $PSScriptRoot '..\artifacts')"

# 屏幕是物理像素，UIA 的 BoundingRectangle 也是物理像素：换算成 DIP 才好按设计值断言
$script:Scale = 2.0
$layoutFile = Join-Path $env:TEMP 'mccx_layout.txt'
if (Test-Path $layoutFile) {
    $first = Get-Content $layoutFile -TotalCount 1
    if ($first -match 'scale=([0-9.]+)') { $script:Scale = [double]$Matches[1] }
}
Write-Host "scale=$($script:Scale)"

function Dip { param([double]$Px) return [math]::Round($Px / $script:Scale, 1) }

function Get-R {
    param($El)
    return $El.Current.BoundingRectangle
}

function Find-NamedAny {
    # 按名字找控件、不限类型：参数弹层/弹窗内容不一定挂在主窗口子树下
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

function Grab {
    param([string]$Path)
    Raise-Mccx
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Path"
}

function Get-Pixel {
    param([string]$Path, [int]$X, [int]$Y)
    $b = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        $c = $b.GetPixel($X, $Y)
        return @([int]$c.R, [int]$c.G, [int]$c.B)
    } finally { $b.Dispose() }
}

function Set-CategoryExpanded {
    param([string]$Name)
    $el = Find-NamedAny $Name
    if (-not $el) { throw "找不到分类展开按钮: $Name" }
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $tp.Toggle()
        Start-Sleep -Milliseconds 400
    }
}

function Open-FilterDialog {
    $b = Find-NamedAny '自动砍怪选项'
    if (-not $b) { return $false }
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch {
        try { Invoke-Element $b } catch { return $false }
    }
    Start-Sleep -Milliseconds 800

    $fb = Find-NamedAny '生物过滤'
    if (-not $fb) { return $false }
    Write-Host ("生物过滤 控件类型=" + $fb.Current.ControlType.ProgrammaticName)
    Invoke-Element $fb

    for ($i = 0; $i -lt 8; $i++) {
        if ($null -ne (Find-UiElement $script:CT::ComboBox '过滤模式')) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Close-FilterDialog {
    $btn = Find-Button $script:AppRoot '关闭'
    if (-not $btn) { $btn = Find-UiElement $script:CT::Button '关闭' }
    if ($btn) { try { Invoke-Element $btn } catch { } }
    for ($i = 0; $i -lt 8; $i++) {
        if ($null -eq (Find-UiElement $script:CT::ComboBox '过滤模式')) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

$null = Start-App
Save-Foreground
Raise-Mccx

try {
    # ---- 右侧面板要先有选中账号 ----
    if (-not (Find-ById $script:AppRoot 'ServerBox')) {
        $list = Find-ById $script:AppRoot 'AccountList'
        $items = Get-ListItems $list
        if ($items.Count -eq 0) { throw '账号列表为空，无法验收右侧面板' }
        Select-Item $items[0]
        Start-Sleep -Milliseconds 1200
    }

    # ================= 1) 参数一行放齐（带冒号标签）、连接状态+延迟浮在日志区右上角 =================
    $srv   = Find-ById $script:AppRoot 'ServerBox'
    $port  = Find-ById $script:AppRoot 'PortBox'
    $user  = Find-ById $script:AppRoot 'UserBox'
    $ver   = Find-ById $script:AppRoot 'VersionBox'
    $conn  = Get-UiButton '连接'
    $disc  = Get-UiButton '断开'
    $state = Find-ById $script:AppRoot 'StateTextBlock'
    $log   = Find-ById $script:AppRoot 'LogScrollHost'
    # 功能区（自动化卡片）是个 Border，UIA 里不露 AutomationId，拿卡片里的首/末两个按钮当锚点，
    # 卡片顶/底边 = 按钮顶/底边 ∓ 卡片内边距(8)。
    $autoTop = Find-NamedAny '自动砍怪选项'
    $autoBot = Find-NamedAny '自动重连选项'

    $sr = Get-R $srv; $pr = Get-R $port; $ur = Get-R $user; $vr = Get-R $ver
    $cr = Get-R $conn; $dr = Get-R $disc; $st = Get-R $state
    $lg = Get-R $log; $at = Get-R $autoTop; $ab = Get-R $autoBot
    $cardTop = $at.Y - 8 * $script:Scale
    $cardBottom = $ab.Y + $ab.Height + 8 * $script:Scale

    Write-Host ("ServerBox   x={0} y={1} w={2} DIP" -f (Dip $sr.X), (Dip $sr.Y), (Dip $sr.Width))
    Write-Host ("端口        x={0} y={1}" -f (Dip $pr.X), (Dip $pr.Y))
    Write-Host ("用户名      x={0} y={1}" -f (Dip $ur.X), (Dip $ur.Y))
    Write-Host ("版本        x={0} y={1}" -f (Dip $vr.X), (Dip $vr.Y))
    Write-Host ("连接        x={0} y={1}" -f (Dip $cr.X), (Dip $cr.Y))
    Write-Host ("断开        x={0} y={1} right={2}" -f (Dip $dr.X), (Dip $dr.Y), (Dip ($dr.X + $dr.Width)))
    Write-Host ("状态        x={0} y={1} text={2}" -f (Dip $st.X), (Dip $st.Y), $state.Current.Name)
    Write-Host ("功能区      y={0}..{1}  日志 top={2}" -f (Dip $cardTop), (Dip $cardBottom), (Dip $lg.Y))

    # 服务器框是弹性列：窗口越宽越宽，但下限还在（窗口最小宽度也锁着整行）
    Assert-True ((Dip $sr.Width) -ge 160) 'U1 服务器框 ≥160 DIP' "w=$(Dip $sr.Width) DIP"
    # 端口列放宽到 92：5 位端口 + 右侧清除按钮（×）才不会把最后一位数字盖掉
    Assert-True ((Dip $pr.Width) -ge 90) 'U1b 端口框 ≥90 DIP（65535 整个看得见）' "w=$(Dip $pr.Width) DIP"
    # 输入框前面的文字都带冒号
    foreach ($lblName in @('服务器：', '端口：', '用户名：', '版本：')) {
        Assert-True ($null -ne (Find-ByName $script:AppRoot $lblName)) "U1c 标签带冒号：$lblName"
    }
    # 一整行要落在面板内：断开按钮右沿不能超过面板右沿（标签加冒号后不能把按钮挤出去）
    $rowRight = Dip ($dr.X + $dr.Width); $panelRight = Dip ($lg.X + $lg.Width)
    Assert-True (($rowRight - $panelRight) -le 4) 'U1d 参数行没被挤出面板（连接/断开完整可见）' "rowRight=$rowRight panelRight=$panelRight"

    # 整组参数一行：比行中心点（TextBlock/ComboBox/TextBox/按钮高度各不同，不能比顶边）
    $srvCenter = $sr.Y + $sr.Height / 2
    foreach ($pair in @(
            @('端口', $pr), @('用户名', $ur), @('版本', $vr), @('连接', $cr), @('断开', $dr))) {
        $r = $pair[1]
        $dy = [math]::Abs(($r.Y + $r.Height / 2) - $srvCenter) / $script:Scale
        Assert-True ($dy -le 14) ("U1r {0} 与服务器框同一行" -f $pair[0]) "dCenter=$dy DIP"
    }

    # 连接状态+延迟浮在日志区右上角（2026-10-09 问题③定稿）：状态不再自己占行，
    # 整块盖在日志顶部（覆盖层 Margin(0,-4,6,0)+Padding(10,5)+边框 1 → 顶隙 2 DIP；
    # 第四轮反馈间隙再砍一半，顶边用负边距压进行距带，状态文字仍在日志顶之下不破 U7c）。
    $stBottom = $st.Y + $st.Height
    $topGap = Dip ($st.Y - $lg.Y)
    Write-Host ("状态浮日志右上  顶隙={0} DIP（日志顶={1} 状态顶={2}）" -f $topGap, (Dip $lg.Y), (Dip $st.Y))
    Assert-True (($topGap -ge -4) -and ($topGap -le 40)) 'U4 状态浮在日志区顶部（不再自己占一行）' "topGap=$topGap DIP"

    # 整行落在开关行（卡片）下面，不和开关抢地方
    $gapSide = Dip ($st.Y - ($ab.Y + $ab.Height))
    Assert-True ($gapSide -ge 4) 'U5 状态在开关行下方（功能卡片外）' "gap=$gapSide DIP"

    # 卡片与日志之间不夹状态/延迟行（两行已撤，空 row 已整行删除，只剩一次行距 10）。
    # 锚点“按钮底+8”比卡片真底矮一截（卡内留白+边框），实测值比行距大 10+ 属正常；
    # 若状态行被加回来，gap 会再涨 30+ DIP，被上界挡下。
    $gapLog = Dip ($lg.Y - $cardBottom)
    Assert-True (($gapLog -ge 10) -and ($gapLog -le 60)) 'U6 卡片与日志之间不夹状态行（只剩行距）' "gap=$gapLog DIP"

    # 覆盖层贴日志区右上角：Margin 右 6 + Padding 8 = 文字右沿内缩面板右缘 14 DIP
    $stRight = Dip ($st.X + $st.Width)
    $rightGap = $panelRight - $stRight
    Assert-True (($rightGap -ge 4) -and ($rightGap -le 24)) 'U7 状态文字右沿内缩面板右缘（浮在日志右上）' "rightGap=$rightGap DIP panelRight=$panelRight"
    Assert-True (($rowRight - $stRight) -ge 0 -and ($rowRight - $stRight) -le 24) 'U7a 状态文字右沿不超出参数行右沿（同右对齐）' "gap=$(($rowRight - $stRight)) DIP"
    # 状态整块落在日志区内（顶边不早于日志顶、底边不晚于日志底），即"盖在日志上"而非挤在日志外
    $logBottom = $lg.Y + $lg.Height
    Assert-True (($st.Y -ge ($lg.Y - 4 * $script:Scale)) -and ($stBottom -le ($logBottom + 4 * $script:Scale))) 'U7c 状态浮在日志区内部（上沿之下、底沿之上）' "stateTop=$(Dip $st.Y) stateBottom=$(Dip $stBottom) logTop=$(Dip $lg.Y) logBottom=$(Dip $logBottom)"

    # 参数行与功能区之间不该夹状态行（状态已挪到卡片下面）
    Assert-True (($cardTop - ($sr.Y + $sr.Height)) -le 60 * $script:Scale) 'U7b 参数行与功能区之间没有夹状态行' "gap=$(Dip ($cardTop - ($sr.Y + $sr.Height))) DIP"

    # ================= 3) 添加账号与标题留空 =================
    $hdr = Find-ByName $script:AppRoot '账号列表'
    $add = Get-UiButton '添加账号'
    $hr = Get-R $hdr; $ar = Get-R $add
    $gap = Dip ($ar.X - ($hr.X + $hr.Width))
    Write-Host ("标题 right={0} 添加账号 left={1} 间距={2} DIP" -f (Dip ($hr.X + $hr.Width)), (Dip $ar.X), $gap)
    Assert-True ($gap -ge 12) 'U8 “添加账号”与“账号列表”之间留了空隙' "gap=$gap DIP"

    Grab "$dir\v7_main.png"

    # ================= 2) 生物过滤弹窗 =================
    Assert-True (Open-FilterDialog) 'U9 “生物过滤”可打开弹窗'

    # 参数弹层应已收起：弹窗压在弹层上会连带着一起关，这里直接看它
    $lbl = Find-NamedAny '自动砍怪选项'
    $lblState = ''
    try {
        $ec = $lbl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $lblState = [string]$ec.Current.ExpandCollapseState
    } catch { $lblState = '<无Pattern>' }
    Assert-True ($lblState -ne 'Expanded') 'U10 打开弹窗时参数弹层已收起' "state=$lblState"

    foreach ($n in @('敌对生物（41）', '中立生物（13）', '友好生物（37）')) {
        Assert-True ($null -ne (Find-UiElement $script:CT::Text $n)) "U11 分类标题存在：$n"
    }
    foreach ($n in @('敌对展开', '中立展开', '友好展开')) {
        Assert-True ($null -ne (Find-NamedAny $n)) "U12 分类展开按钮存在：$n"
    }
    foreach ($n in @('敌对生物全选', '中立生物全选', '友好生物全选')) {
        Assert-True ($null -ne (Find-UiElement $script:CT::CheckBox $n)) "U13 分类全选框存在：$n"
    }

    # 每类列表自己限高：下面两个分类标题必须还露在可视区里（不能被敌对的 41 项顶下去）
    $closeBtn = Find-Button $script:AppRoot '关闭'
    $closeR = Get-R $closeBtn
    foreach ($n in @('中立生物（13）', '友好生物（37）')) {
        $h = Find-UiElement $script:CT::Text $n
        $hr = Get-R $h
        $visible = (($hr.Y + $hr.Height) -le ($closeR.Y + 4 * $script:Scale)) -and ($hr.Height -gt 0)
        Assert-True $visible "U13b 分类标题在可视区内：$n" "y=$(Dip $hr.Y) close=$(Dip $closeR.Y)"
    }

    # 默认：敌对展开、中立收起
    Assert-True ($null -ne (Find-UiElement $script:CT::CheckBox '僵尸')) 'U14 默认展开敌对（僵尸可见）'
    Assert-True ($null -eq (Find-UiElement $script:CT::CheckBox '狼')) 'U15 默认收起中立（狼不可见）'
    Set-CategoryExpanded '中立展开'
    Assert-True ($null -ne (Find-UiElement $script:CT::CheckBox '狼')) 'U16 展开中立后狼可见'

    Grab "$dir\v7_dialog.png"

    # 分类条底色 vs 生物列表底色：同一列上取两点，颜色必须分得开。
    # 列表那一点取标题正下方（列表限高后靠后的生物会滚出可视区，取它的坐标会落到屏幕外）。
    $title = Find-UiElement $script:CT::Text '敌对生物（41）'
    $tr = Get-R $title
    $sx = [int]($tr.X + $tr.Width * 0.6)
    $syHeader = [int]($tr.Y + $tr.Height / 2)
    $syList = [int]($tr.Y + $tr.Height + 12 * $script:Scale)
    Write-Host "sample header=($sx,$syHeader) list=($sx,$syList)"
    $c1 = Get-Pixel "$dir\v7_dialog.png" $sx $syHeader
    $c2 = Get-Pixel "$dir\v7_dialog.png" $sx $syList
    $diff = [math]::Max([math]::Max([math]::Abs($c1[0] - $c2[0]), [math]::Abs($c1[1] - $c2[1])), [math]::Abs($c1[2] - $c2[2]))
    Write-Host ("底色对比 分类条 rgb({0},{1},{2})  列表 rgb({3},{4},{5})  max差={6}" -f $c1[0], $c1[1], $c1[2], $c2[0], $c2[1], $c2[2], $diff)
    Assert-True ($diff -ge 6) 'U17 分类条与生物列表底色有区分' "maxDiff=$diff"

    # 三类全展开看整体高度（弹窗不越界）
    Set-CategoryExpanded '友好展开'
    Start-Sleep -Milliseconds 400
    Grab "$dir\v7_dialog_all.png"
    $combo = Get-UiCombo '过滤模式'
    $cr2 = Get-R $combo
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Assert-True (($cr2.X -ge 0) -and ($cr2.Y -ge 0)) 'U18 弹窗在屏幕内' "combo=($(Dip $cr2.X),$(Dip $cr2.Y))"

    Assert-True (Close-FilterDialog) 'U19 弹窗可关闭'
    Assert-True ($null -eq (Find-UiElement $script:CT::ComboBox '过滤模式')) 'U20 关闭后弹窗内容已移除'

    # 关掉生物过滤窗，入口所在的“砍怪参数”弹层要自己展回来（不能被一起带走）
    $lbl2 = Find-NamedAny '自动砍怪选项'
    $lbl2State = ''
    try {
        $ec2 = $lbl2.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $lbl2State = [string]$ec2.Current.ExpandCollapseState
    } catch { $lbl2State = '<无Pattern>' }
    Assert-True ($lbl2State -eq 'Expanded') 'U21 关闭弹窗后“砍怪参数”弹层自动展回' "state=$lbl2State"

    # 三个参数弹层都要向下展开：弹层内第一个控件的顶边必须 ≥ 入口按钮底边（自动重连曾翻到上方）
    # 顺带量每个弹层的宽度：三个必须统一（312 + 弹层内边距），不能宽窄不一
    try { Close-AllFlyouts } catch { }
    Start-Sleep -Milliseconds 400
    $flyWidths = @()
    foreach ($t in @(
            @('自动砍怪选项', '攻击距离'),
            @('鼠标控制选项', '左键模式'),
            @('自动重连选项', '重连次数'))) {
        $btn = Find-NamedAny $t[0]
        if (-not $btn) { Assert-True $false ("U22 找到入口：{0}" -f $t[0]) '入口不存在'; $flyWidths += -1; continue }
        $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
        Start-Sleep -Milliseconds 700
        $inner = Find-NamedAny $t[1]
        if (-not $inner) { Assert-True $false ("U22 弹层已展开：{0}" -f $t[0]) '找不到弹层内控件'; $flyWidths += -1; continue }
        $br = Get-R $btn; $ir = Get-R $inner
        $dy = Dip ($ir.Y - ($br.Y + $br.Height))
        Assert-True ($ir.Y -ge ($br.Y + $br.Height)) ("U22 {0} 向下展开" -f $t[0]) "弹层顶边比按钮底边低 $dy DIP"

        $paneW = -1
        try {
            # 从弹层内控件沿父链向上，取“仍属于弹层”（≤600 DIP）的最宽祖先：
            # 到桌面/主窗口一级（>600）就停手，剩下的就是弹层窗口（内容 312 + 内边距）
            $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $n = $inner
            $best = $inner
            while ($true) {
                try { $p = $w.GetParent($n) } catch { break }
                if ($null -eq $p) { break }
                $pw = -1
                try { $pw = [int][math]::Round((Dip $p.Current.BoundingRectangle.Width)) } catch { break }
                if ($pw -gt 600) { break }
                $bw = [int][math]::Round((Dip $best.Current.BoundingRectangle.Width))
                if ($pw -ge $bw) { $best = $p }
                $n = $p
            }
            $paneW = [int][math]::Round((Dip $best.Current.BoundingRectangle.Width))
        } catch { }
        $flyWidths += $paneW

        $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) { $ec.Collapse() }
        Start-Sleep -Milliseconds 400
    }
    try { Close-AllFlyouts } catch { }

    Write-Host ("弹层宽度     " + ($flyWidths -join ' / ') + " DIP")
    $paneWs = @($flyWidths | Where-Object { $_ -gt 0 })
    if ($paneWs.Count -ne 3) {
        Assert-True $false 'U23 三个参数弹层宽度都能量到' "count=$($paneWs.Count) widths=$($flyWidths -join ',')"
    } else {
        $wmin = ($paneWs | Measure-Object -Minimum).Minimum
        $wmax = ($paneWs | Measure-Object -Maximum).Maximum
        Assert-True (($wmax - $wmin) -le 4) 'U23 三个参数弹层宽度统一' "min=$wmin max=$wmax DIP"
        # 上限兜底：万一爬父链爬到了主窗口（1186），三边相等也是假通过，用绝对范围卡死
        Assert-True (($wmin -ge 300) -and ($wmax -le 520)) 'U23b 量到的确实是弹层窗口' "min=$wmin max=$wmax DIP"
    }

    Write-Host 'done'
}
catch {
    Assert-True $false '用例执行异常' $_.Exception.Message
    Write-Host ("EXCEPTION: " + $_.Exception.Message)
}
finally {
    try { Close-FilterDialog } catch { }
    try { Close-AllFlyouts } catch { }
    [void][TopHelper]::SetWindowPos([IntPtr]$script:Hwnd, [TopHelper]::HWND_NOTOPMOST, 0, 0, 0, 0,
        [TopHelper]::SWP_NOMOVE -bor [TopHelper]::SWP_NOSIZE -bor [TopHelper]::SWP_SHOWWINDOW)
    Restore-Foreground
    Stop-App
}

$fc = Get-FailureCount
Write-Host "V_UI7 FAILURES = $fc"
exit ([int]($fc -gt 0))
