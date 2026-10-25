# 服务器容器窗口（箱子 / 商店 GUI）端到端（2026-10-16 需求：服务器开 GUI 时在 MCCX 里显示出来，
# 点格子把动作发回服务端；同日第三轮：窗口要和玩家背包联动，所以窗口下面会跟着画一块"物品栏"）：
#   A 摆一个箱子塞几件物品 → 用 MCC 内部命令 /useitem x y z 打开 → 面板弹出、标题中文化、
#     格子内容/数量（64 叠）都对、空格显示"空"、联动的物品栏也在（窗口槽位 27-62）；
#   B 点格子（UIA InvokePattern，走的是真点击的 Click 处理）→ RCON 回读箱子证明"拿起"到了服务端，
#     界面同步变空；再点一次放回 → 箱子恢复（一来一回都过了协议）；
#     B4 起再点联动物品栏（塞快捷栏 → 拿起到光标 → 放进箱子）：证明联动那块用的就是这个窗口的
#     真实槽位号（箱子 0-26、玩家物品栏 27-62），服务端认得；
#   C 面板「关闭」→ 面板收起，重新 /useitem 还能再打开（关窗包没把状态弄坏）；
#   D 传送到远处 → 服务端自己关窗 → 面板自动收起（服务端关窗 / 断开都要收干净）。
# 顺带截几张图（浅色 / 暗色 / 收起后）落 artifacts，人眼过一眼图标与配色；面板现在改成同
# 「添加账号」的蒙板弹窗（2026-10-16 第四轮用户要求）：蒙板盖住功能区 / 日志区 / 命令栏，
# 账号列表与连接参数那一行留在外面，所以蒙板范围也靠这几张图人眼确认。蒙板挡鼠标，但 UIA
# 的 InvokePattern / ValuePattern 不走命中测试，本用例的点击与写值一条都不用改。
# A12-A14 钉图标画风与兜底图去向（2026-10-16 用户要求"画风统一"）：一律原版贴图，方块也是平铺贴图 →
# 缓存里的 oak_planks 必须是 64x64（16px 最近邻放大 4 倍），不是第三方 128x127 渲染图；
# 原版没有平铺贴图的物品（旗帜）才走兜底渲染图，而且它只落临时目录、不进 item-icons-v3。
# 全程只用 UIA + RCON，不抢前台焦点；专用账号 MccXContTest，测完自删还原。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_container.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXContainerShot {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$script:TestUser = 'MccXContTest'
# autolib 默认测试玩家是 MccXBot；本脚本账号叫 MccXContTest，RCON 目标要跟着改
$script:TestPlayer = $script:TestUser

$artifactDir = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts') 'g_container'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

# 箱子摆在高空测试平台上（Set-TestArena 一带），别占它留给 bot 站位的那一格，
# 免得和其它用例抢同一块地方。bot 站箱子西边 2 格，伸手够得着（reach 4.5）。
$chestX = -343; $chestY = 110; $chestZ = -443
$chestPos = "$chestX $chestY $chestZ"
$botX = -345; $botY = 110; $botZ = -443

function Save-Shot {
    param([string]$Path)
    $h = [IntPtr]$script:Hwnd
    $r = New-Object MccXContainerShot+RECT
    [void][MccXContainerShot]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][MccXContainerShot]::PrintWindow($h, $hdc, 3)   # PW_CLIENTONLY | PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  shot -> $Path"
}

# 图标缓存目录（程序目录 UserData 下的 item-icons-v3）与 PNG 宽高（读 IHDR，够用且不依赖图片解码器）
function Get-IconCacheDir {
    Join-Path (Join-Path (Split-Path $script:AppExe) 'UserData') 'item-icons-v3'
}

function Get-PngSize {
    param([string]$Path)
    try {
        $bytes = New-Object byte[] 24
        $fs = [System.IO.File]::OpenRead($Path)
        try { [void]$fs.Read($bytes, 0, 24) } finally { $fs.Dispose() }
        $w = [int]$bytes[16] * 16777216 + [int]$bytes[17] * 65536 + [int]$bytes[18] * 256 + [int]$bytes[19]
        $h = [int]$bytes[20] * 16777216 + [int]$bytes[21] * 65536 + [int]$bytes[22] * 256 + [int]$bytes[23]
        return "${w}x${h}"
    } catch {
        return ''
    }
}

# 面板在不在：面板的「关闭」按钮只在面板可见时才进 UIA 树
# （面板本体是 Border，没有自动化对等体，UIA 里看不见，不能拿它当锚点）
function Get-PanelVisible { return ($null -ne (Find-ByName $script:AppRoot '关闭容器窗口')) }

# 找一个格子：格子是 Button，UIA 名字形如 "格子 0：Diamond x5" / "格子 5：空"
function Get-SlotElement {
    param([int]$Index)
    $bc = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)
    foreach ($b in $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $bc)) {
        if (([string]$b.Current.Name).StartsWith("格子 ${Index}：")) { return $b }
    }
    return $null
}

function Get-SlotName {
    param([int]$Index)
    $e = Get-SlotElement $Index
    if ($e) { return [string]$e.Current.Name }
    return '(格子不存在)'
}

# 等某一条断言成立（增量推送是异步的：界面刷出来要几百毫秒）
function Wait-SlotName {
    param([int]$Index, [string]$Pattern, [int]$TimeoutMs = 8000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $n = Get-SlotName $Index
        if ($n -match $Pattern) { return $n }
        Start-Sleep -Milliseconds 400
    }
    return (Get-SlotName $Index)
}

function Wait-Panel {
    param([bool]$Visible = $true, [int]$TimeoutMs = 15000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ((Get-PanelVisible) -eq $Visible) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# 面板里的文字（标题 / 副标题 / 状态行都是 TextBlock，有自动化对等体）
function Get-PanelTexts {
    $tc = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)
    $out = @()
    foreach ($t in $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) {
        $n = [string]$t.Current.Name
        if ($n) { $out += $n }
    }
    return $out
}

function Get-ChestDump { return (Invoke-Rcon "data get block $chestPos") }

# 断言失败时的详情太长会把控制台刷爆，截一段
function Get-Brief {
    param([string]$Text, [int]$Max = 90)
    $t = ($Text -replace '\s+', ' ').Trim()
    if ($t.Length -le $Max) { return $t }
    return $t.Substring(0, $Max)
}

# RCON 布置命令要等区块加载：刚传送过去时 setblock 会回 "That position is not loaded"，重试到能操作为止。
# 已经摆过同样的方块时服务端回 "Could not set the block"（新旧状态一样，不算失败），照样放过。
function Invoke-RconWhenLoaded {
    param([string]$Command, [int]$Tries = 15)
    $out = ''
    for ($i = 0; $i -lt $Tries; $i++) {
        $out = Invoke-Rcon $Command
        if ($out -notmatch 'not loaded') { return $out }
        Start-Sleep -Milliseconds 1000
    }
    return $out
}

function Set-ThemeSwitch {
    param([bool]$Dark)
    $sw = $null
    $all = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ([string]$e.Current.AutomationId -eq 'ThemeSwitch') { $sw = $e; break }
    }
    if (-not $sw) { throw '找不到暗色模式开关 ThemeSwitch' }
    $tp = $sw.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $on = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    if ($on -ne $Dark) { $tp.Toggle(); Start-Sleep -Milliseconds 900 }
}

$null = Stop-App
$null = Start-App
$session = Enter-TestSession -User $script:TestUser -Port '25599'

try {
    # ---------- A 摆箱子 + 打开 ----------
    Write-Host "`n=== A 摆箱子 / 物品 / 打开 ==="
    # 区块跟着玩家加载：先传送把这片区块叫醒 → 铺站台 → 再站定（面朝箱子）→ 摆箱子
    Write-Host ("  tp#1 -> " + (Invoke-Rcon ("execute as {0} run tp @s {1} {2} {3}" -f $script:TestPlayer, $botX, $botY, $botZ)))
    Write-Host ("  platform -> " + (Invoke-RconWhenLoaded "setblock $botX $($botY - 1) $botZ stone"))
    Write-Host ("  tp#2 -> " + (Invoke-Rcon ("execute as {0} run tp @s {1} {2} {3} facing {4} {5} {6}" -f `
        $script:TestPlayer, $botX, $botY, $botZ, $chestX, ($chestY + 0.5), $chestZ)))
    Write-Host ("  chest -> " + (Invoke-RconWhenLoaded "setblock $chestPos chest"))

    # 玩家的物品栏在服务器上是持久的：先清干净，免得上一轮跑剩的东西跑进联动物品栏里
    # （截图和断言跟着上一轮走）。清箱子靠 finally 里的 setblock air。
    Write-Host ("  clear -> " + (Invoke-Rcon ("clear {0}" -f $script:TestPlayer)))

    # item replace 只对"确实是容器"的方块成功（回 Replaced），顺手当箱子就位校验
    $filled = Invoke-Rcon "item replace block $chestPos container.0 with minecraft:diamond 5"
    Write-Host "  item.0 -> $filled"
    Assert-True ($filled -match 'Replaced') 'A0 箱子就位（item replace 成功，说明方块是容器）' $filled

    foreach ($pair in @(@(1, 'minecraft:nether_star 1'),
                        @(4, 'minecraft:oak_planks 64'), @(13, 'minecraft:diamond_sword 1'),
                        @(26, 'minecraft:written_book 1'),
                        # 第 6 格：原版没有平铺贴图的物品（旗帜），用来验"兜底渲染图不落磁盘缓存"（A13/A14）
                        @(6, 'minecraft:white_banner 1'))) {
        $cmd = "item replace block $chestPos container.$($pair[0]) with $($pair[1])"
        Write-Host ("  item.$($pair[0]) -> " + (Invoke-Rcon $cmd))
    }
    # 图标画风断言（A12）验的是"现下现取"：先把上一轮留下的方块图标删掉，免得读的是旧结果。
    Remove-Item (Join-Path (Get-IconCacheDir) 'oak_planks.png') -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600

    # MCC 内部命令 /useitem <x> <y> <z>：对指定方块发一次右键（服务端开窗）
    Send-MccCommand "/useitem $chestX $chestY $chestZ"
    Assert-True (Wait-Panel $true) 'A1 打开箱子后面板弹出'

    Start-Sleep -Milliseconds 1200
    $texts = Get-PanelTexts
    Assert-True ($texts -contains '箱子') 'A2 原版英文窗名 Chest 已显示成"箱子"'
    Assert-True (($texts | Where-Object { $_ -like '*箱子*第 1 号窗口*' }).Count -ge 1) 'A3 副标题带容器类型与窗号'
    # 物品名走中文名表（官方 zh_cn，缓存在 UserData\lang）：有表时是中文，没网又没缓存时回退英文；
    # 所以下面这些断言两种都收（"钻石" / "Diamond"），验的是"这一格显示的是那件东西"。
    Assert-True ((Get-SlotName 0) -match '^格子 0：.*(钻石|Diamond)') 'A4 格子 0 显示钻石' (Get-SlotName 0)
    Assert-True ((Get-SlotName 4) -like '格子 4：*x64*') 'A5 64 叠数量显示' (Get-SlotName 4)
    Assert-True ((Get-SlotName 13) -match '^格子 13：.*(钻石剑|Diamond Sword)') 'A6 格子 13 显示钻石剑' (Get-SlotName 13)
    Assert-True ((Get-SlotName 5) -like '格子 5：空') 'A7 空格子标"空"' (Get-SlotName 5)

    # 联动（2026-10-16 第三轮需求）：箱子打开时，玩家自己的物品栏一起画在下面。
    # 槽位号是"整个菜单"的号：箱子 0-26，玩家物品栏（背包 27 + 快捷栏 9）27-62，共 63 格。
    # 注意这跟单独开「背包」面板不是一套号（那边是 MCC 窗口 0 的号：5-8 装备 / 9-35 背包 / 36-44 快捷栏）。
    Assert-True ($texts -contains '物品栏') 'A8 联动物品栏的小标题在'
    Assert-True ((Get-SlotName 27) -like '格子 27：*') 'A9 物品栏第一格（窗口槽位 27）画出来了' (Get-SlotName 27)
    Assert-True ($null -ne (Get-SlotElement 62)) 'A10 物品栏最后一格（快捷栏末格 = 窗口槽位 62）也在'
    Assert-True ($null -eq (Get-SlotElement 63)) 'A11 窗口只有 63 格（27 箱子 + 36 玩家），没有第 64 格'

    # 图标画风（2026-10-16 用户要求"画风统一"）：一律原版贴图，方块也是平铺贴图。
    # oak_planks 的贴图是 16px，最近邻放大 4 倍后是 64x64；第三方渲染图是 128x127（以前方块走的就是它）。
    $iconPath = Join-Path (Get-IconCacheDir) 'oak_planks.png'
    $iconDim = ''
    $iconDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $iconDeadline) {
        $iconDim = Get-PngSize $iconPath
        if ($iconDim) { break }
        Start-Sleep -Milliseconds 400
    }
    if (-not $iconDim) {
        Write-Host '  [skip] 方块图标没下到（可能没网），跳过画风断言'
    } else {
        Assert-True ($iconDim -eq '64x64') 'A12 方块图标用原版平铺贴图（oak_planks 16px 放大 4 倍）' "实际=$iconDim"
    }

    # 兜底渲染图的去向：原版没有平铺贴图的物品（旗帜 item/block 两张贴图都是 404）只能走渲染图，
    # 它应该落在本次运行的临时目录里，而 item-icons-v3 里不该有它（下次启动会重新走原版贴图那条路，
    # 这样一次网络抖动不会把某个物品的画风永久改掉）。
    $sessionDir = Join-Path $env:TEMP 'MCCX-item-icons-v3-session'
    $bannerFlat = Join-Path (Get-IconCacheDir) 'white_banner.png'
    $bannerSession = Join-Path $sessionDir 'white_banner.png'
    $bannerDeadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $bannerSession) -and (Get-Date) -lt $bannerDeadline) {
        Start-Sleep -Milliseconds 400
    }
    if (-not (Test-Path $bannerSession) -and -not (Test-Path $bannerFlat)) {
        Write-Host '  [skip] 旗帜图标没下到（可能没网），跳过兜底图缓存断言'
    } else {
        Assert-True (Test-Path $bannerSession) 'A13 原版没有平铺贴图的物品（旗帜）落到兜底渲染图（本次运行的临时目录）'
        Assert-True (-not (Test-Path $bannerFlat)) 'A14 兜底渲染图不写进 item-icons-v3（下次启动重新走原版贴图）'
    }

    Save-Shot (Join-Path $artifactDir 'open_light.png')

    # ---------- B 点格子走一个来回 ----------
    Write-Host "`n=== B 点格子（拿起 / 放下）==="
    $before = Get-ChestDump
    [void](Invoke-Element (Get-SlotElement 0))
    Start-Sleep -Seconds 2
    $picked = Get-ChestDump
    Assert-True (($before -match 'minecraft:diamond"') -and ($picked -notmatch 'minecraft:diamond"')) `
        'B1 左键拿起：服务端箱子里钻石没了' (Get-Brief $picked)
    Assert-True ((Get-SlotName 0) -like '格子 0：空') 'B2 界面同步：格子 0 变空'

    [void](Invoke-Element (Get-SlotElement 0))
    Start-Sleep -Seconds 2
    $back = Get-ChestDump
    Assert-True ($back -match 'minecraft:diamond"') 'B3 左键放下：钻石回到箱子'

    # ---------- B4 联动物品栏（窗口槽位 27-62 是玩家自己的 36 格）----------
    Write-Host "`n=== B4 点联动物品栏 ==="
    # 快捷栏 0 在这个箱子窗口里是槽位 54（27 起 27 格背包 + 9 格快捷栏，最后 9 格是快捷栏）
    $null = Invoke-Rcon ("item replace entity {0} hotbar.0 with minecraft:emerald 5" -f $script:TestPlayer)
    Assert-True ((Wait-SlotName 54 '(绿宝石|Emerald)') -match '^格子 54：.*(绿宝石|Emerald) x5') `
        'B4 服务端塞的绿宝石出现在联动物品栏里（窗口槽位 54 = 快捷栏 0）' (Get-SlotName 54)

    [void](Invoke-Element (Get-SlotElement 54))
    Start-Sleep -Seconds 2
    Assert-True ((Get-SlotName 54) -like '格子 54：空') 'B5 点联动物品栏格子能拿起（该格变空）' (Get-SlotName 54)
    Assert-True ((Get-PanelTexts | Where-Object { $_ -match '光标拿着.*(绿宝石|Emerald)' }).Count -ge 1) `
        'B6 光标上拿着绿宝石（状态行）' (Get-Brief ((Get-PanelTexts) -join ' | '))

    # 放进箱子第 2 格（A 段没塞过的空格）：服务端箱子里出现绿宝石 = 联动的点击真过了协议
    [void](Invoke-Element (Get-SlotElement 2))
    Start-Sleep -Seconds 2
    Assert-True ((Get-ChestDump) -match 'minecraft:emerald') `
        'B7 放进箱子：服务端箱子里出现绿宝石（联动那块用的是这个窗口的真实槽位号）' (Get-Brief (Get-ChestDump))

    # ---------- C 面板关闭按钮 ----------
    Write-Host "`n=== C 关窗按钮 ==="
    Invoke-UiButton '关闭容器窗口'
    Assert-True (Wait-Panel $false) 'C1 点「关闭」后面板收起'

    Send-MccCommand "/useitem $chestX $chestY $chestZ"
    Assert-True (Wait-Panel $true) 'C2 关窗后还能再打开（关窗包没把状态弄坏）'
    Start-Sleep -Milliseconds 1200
    Assert-True (((Get-PanelTexts) | Where-Object { $_ -like '*第 2 号窗口*' }).Count -ge 1) 'C3 第二次开的是第 2 号窗口'

    # ---------- C4 Esc 关窗（KeyboardAccelerator，不用抢输入焦点）----------
    # 键盘路径必须真焦点 + 真按键；抢不到前台时 SendKeys 会打进别的窗口（环境问题不是程序问题），
    # 这种情况跳过断言只报一句 SKIP，同 g_search 第 8 段的做法。
    Set-Foreground
    $fg = ''
    try { $fg = Get-ForegroundProcess } catch { $fg = '' }
    if ($fg -ne 'MCCX') {
        Write-Host "  [skip] 抢不到前台（当前前台=$fg），跳过 Esc 关窗断言"
    } else {
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Seconds 2
        Assert-True (Wait-Panel $false 5000) 'C4 Esc 关掉面板'
        Send-MccCommand "/useitem $chestX $chestY $chestZ"
        Assert-True (Wait-Panel $true) 'C5 Esc 关掉后还能再打开'
        Start-Sleep -Milliseconds 1200
    }
    Restore-Foreground

    # ---------- D 暗色主题下截图 ----------
    Write-Host "`n=== D 暗色主题 ==="
    Set-ThemeSwitch $true
    Save-Shot (Join-Path $artifactDir 'open_dark.png')
    Assert-True (Get-PanelVisible) 'D1 切主题后面板还在'
    Set-ThemeSwitch $false

    # ---------- E 服务端自己关窗 ----------
    Write-Host "`n=== E 服务端关窗（跑远）==="
    Write-Host ("  tp far -> " + (Invoke-Rcon ("execute as {0} run tp @s {1} {2} {3}" -f `
        $script:TestPlayer, ($chestX + 60), $chestY, ($chestZ + 60))))
    Assert-True (Wait-Panel $false 20000) 'E1 跑远后服务端关窗，面板自动收起'

    # 收起后蒙板也得跟着收（蒙板可见性绑在卡片上，见 MainPage.xaml 的 ContainerMask）：
    # 这一张是"没开面板"的样子，和 A 阶段那张 open_light.png 对着看，就能人眼确认
    # 功能区 / 日志区 / 命令栏不再压暗、账号列表与连接参数行本来就没压暗。
    Save-Shot (Join-Path $artifactDir 'closed_light.png')
}
finally {
    try { Invoke-Rcon "setblock $chestPos air" | Out-Null } catch { Write-Host ("  [clean] 清箱子失败: " + $_.Exception.Message) }
    try { Exit-TestSession $session } catch { Write-Host ("  [clean] " + $_.Exception.Message) }
}

Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_container FAILURES=$fail"
exit ([int]($fail -gt 0))
