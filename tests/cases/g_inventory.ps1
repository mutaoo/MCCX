# 玩家背包面板（2026-10-16 第二轮需求：点功能区「背包」按钮，看当前背包和快捷栏）：
#   A 连本地测试服 → RCON `item replace entity` 精准塞物品（快捷栏 0 / 背包 0 / 头盔 / 副手）
#     → 点「背包」→ 面板弹出：标题中文化、原版布局（顶上一行装备横排 + 副手挂在右边、
#       下面 3×9 背包、再下面 1×9 快捷栏；不摆玩家人像、不做合成台）、
#       格子内容与数量对、合成格（窗口 0-4）不画；
#   B 点格子走一个来回：左键拿起（状态行显示"光标拿着 钻石 x5"）→ 点背包空格放下
#     → RCON 回读玩家 NBT 证明东西真的进了背包第 1 格（走的是协议，不是界面自己编的）；
#   C 面板开着时服务端再塞一件 → 界面自己跟着刷（窗口 0 的增量）；
#   D 面板「关闭」/ Esc 收掉后，背包变动 <b>不会</b>把面板弹回来（负向对照：收面板 = 停止推送），
#     再点「背包」立刻是最新的；
#   E 断开连接 → 面板自动收起。
# 顺带截浅色 / 暗色两张图落 artifacts。全程 UIA + RCON，只截图不抢前台。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_inventory.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXInvShot {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$script:TestUser = 'MccXInvTest'
# autolib 默认测试玩家是 MccXBot；本脚本账号叫 MccXInvTest，RCON 目标要跟着改
$script:TestPlayer = $script:TestUser

$artifactDir = Join-Path (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts') 'g_inventory'
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

function Save-Shot {
    param([string]$Path)
    $h = [IntPtr]$script:Hwnd
    $r = New-Object MccXInvShot+RECT
    [void][MccXInvShot]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][MccXInvShot]::PrintWindow($h, $hdc, 3)   # PW_CLIENTONLY | PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  shot -> $Path"
}

# 面板在不在：面板的「关闭」按钮只在面板可见时才进 UIA 树
# （面板本体是 Border，没有自动化对等体，UIA 里看不见，不能拿它当锚点）
function Get-PanelVisible { return ($null -ne (Find-ByName $script:AppRoot '关闭容器窗口')) }

# 找一个格子：格子是 Button，UIA 名字形如 "格子 36：钻石 x5" / "格子 9：空"
# （物品名来自中文名表：没网又没缓存时回退英文，所以断言里两种都收）
# （玩家背包的格子号就是 MCC 窗口 0 的槽位号：5-8 装备、9-35 背包、36-44 快捷栏、45 副手）
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

# 格子在屏幕上的位置（UIA 的 BoundingRectangle，来自布局树、和窗口有没有被挡住无关）：
# 用来断言"原版布局"的上下左右关系。取不到（格子不存在）返回 $null。
function Get-SlotRect {
    param([int]$Index)
    $e = Get-SlotElement $Index
    if (-not $e) { return $null }
    $r = $e.Current.BoundingRectangle
    if ([double]::IsNaN($r.X) -or [double]::IsNaN($r.Y)) { return $null }
    return $r
}

# 一组格子号里"没画出来"的那些（A9/A10 这种整块断言的详情用）
function Get-MissingSlots {
    param([int[]]$Indexes)
    $missing = @()
    foreach ($i in $Indexes) {
        if ($null -eq (Get-SlotRect $i)) { $missing += $i }
    }
    return , $missing
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

# 等某一条断言成立（增量推送是异步的：界面刷出来要几百毫秒）
function Wait-SlotName {
    param([int]$Index, [string]$Pattern, [int]$TimeoutMs = 8000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $n = Get-SlotName $Index
        if ($n -like $Pattern) { return $n }
        Start-Sleep -Milliseconds 400
    }
    return (Get-SlotName $Index)
}

# 面板里的文字（标题 / 副标题 / 分组小标题 / 状态行都是 TextBlock，有自动化对等体）
function Get-PanelTexts {
    $tc = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)
    $out = @()
    foreach ($t in $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tc)) {
        $n = [string]$t.Current.Name
        if ($n) { $out += $n }
    }
    return $out
}

# 玩家的 NBT 背包（注意 NBT 槽位号和窗口槽位号不是一套：NBT 0-8 快捷栏 = 窗口 36-44，
# NBT 9-35 背包 = 窗口 9-35）
function Get-InventoryDump { return (Invoke-Rcon ("data get entity {0} Inventory" -f $script:TestPlayer)) }

function Give-Slot {
    param([string]$Slot, [string]$Item)
    return (Invoke-Rcon ("item replace entity {0} {1} with {2}" -f $script:TestPlayer, $Slot, $Item))
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

# 断言失败时的详情太长会把控制台刷爆，截一段
function Get-Brief {
    param([string]$Text, [int]$Max = 90)
    $t = ($Text -replace '\s+', ' ').Trim()
    if ($t.Length -le $Max) { return $t }
    return $t.Substring(0, $Max)
}

$null = Stop-App
$null = Start-App
$session = Enter-TestSession -User $script:TestUser -Port '25599'

try {
    # ---------- A 塞物品 + 打开面板 ----------
    Write-Host "`n=== A 塞物品 / 打开背包面板 ==="
    # 高空平台落脚（其它用例同一套路：tp 用绝对坐标，别站到地面上跟地形较劲）
    foreach ($dx in 0..3) {
        foreach ($dz in -1..1) {
            $null = Invoke-Rcon ("setblock {0} 109 {1} stone" -f (-345 + $dx), (-443 + $dz))
        }
    }
    Write-Host ("  tp -> " + (Invoke-Rcon ("execute as {0} run tp @s -345 110 -443" -f $script:TestPlayer)))
    Write-Host ("  gm -> " + (Invoke-Rcon ("execute as {0} run gamemode creative" -f $script:TestPlayer)))
    $null = Invoke-Rcon 'difficulty peaceful'
    # 上一轮跑剩的东西（断开后 item replace 就够不着了）先清干净，免得截图和断言跟着上一轮走
    Write-Host ("  clear -> " + (Invoke-Rcon ("clear {0}" -f $script:TestPlayer)))
    Start-Sleep -Milliseconds 900

    # 装备 / 背包 / 快捷栏 / 副手各来一件，把三组都点亮
    Assert-True ((Give-Slot 'hotbar.0' 'minecraft:diamond 5') -match 'Replaced') 'A1 快捷栏 0 塞进 5 个钻石'
    Assert-True ((Give-Slot 'inventory.0' 'minecraft:nether_star 1') -match 'Replaced') 'A2 背包 0 塞进下界之星'
    Assert-True ((Give-Slot 'armor.head' 'minecraft:diamond_helmet 1') -match 'Replaced') 'A3 头上塞进钻石头盔'
    Assert-True ((Give-Slot 'weapon.offhand' 'minecraft:shield 1') -match 'Replaced') 'A4 副手塞进盾牌'
    Start-Sleep -Milliseconds 1200

    Invoke-UiButton '背包'
    Assert-True (Wait-Panel $true) 'A5 点「背包」后面板弹出'
    Start-Sleep -Milliseconds 1200

    $texts = Get-PanelTexts
    Assert-True ($texts -contains '背包') 'A6 面板标题是"背包"'
    Assert-True (($texts | Where-Object { $_ -like '*背包 · 快捷栏 · 装备*' }).Count -ge 1) 'A7 副标题说明带背包 / 快捷栏 / 装备'

    # 原版背包布局（2026-10-18 用户要求）：顶上一行装备横排（头盔 → 胸甲 → 护腿 → 靴子）+ 副手，
    # 再下面 3×9 背包、1×9 快捷栏；不做右侧合成台、不摆玩家人像那块（MCC 没有皮肤数据，
    # 留白反而像布局漏了）；装备与副手竖排"竖直占比太高"，改成一行（2026-10-18 用户第二次反馈）。
    # 那三块的小标题去掉了（原版没有标题，看位置就知道），所以这里改成断言"每一格都在、
    # 而且摆的位置对"——布局本身就是这块需求要验的东西。
    $r5 = Get-SlotRect 5; $r6 = Get-SlotRect 6; $r7 = Get-SlotRect 7; $r8 = Get-SlotRect 8
    Assert-True ((Get-MissingSlots (5..8)).Count -eq 0) `
        'A8 装备栏四格都在（窗口 5-8）' ("缺: " + ((Get-MissingSlots (5..8)) -join ','))
    Assert-True (([int]$r5.Y -eq [int]$r6.Y) -and ([int]$r6.Y -eq [int]$r7.Y) -and ([int]$r7.Y -eq [int]$r8.Y)) `
        'A8b 装备栏是横排（四格 Y 相同）'
    Assert-True (($r5.X -lt $r6.X) -and ($r6.X -lt $r7.X) -and ($r7.X -lt $r8.X)) `
        'A8c 装备栏自左而右是头盔 → 胸甲 → 护腿 → 靴子'
    $r45 = Get-SlotRect 45
    Assert-True ($null -ne $r45) 'A8d 副手（窗口 45）画出来了'
    Assert-True (($null -ne $r45) -and ([int]$r45.Y -eq [int]$r5.Y) -and ($r45.X -gt $r8.X)) `
        'A8e 副手挂在装备栏同一行的右边（空一小段）'

    Assert-True ((Get-MissingSlots (9..35)).Count -eq 0) `
        'A9 背包 3×9（窗口 9-35）都在' ("缺: " + ((Get-MissingSlots (9..35)) -join ','))
    $r9 = Get-SlotRect 9
    Assert-True (([int](Get-SlotRect 17).Y -eq [int]$r9.Y) -and ((Get-SlotRect 10).X -gt $r9.X)) `
        'A9b 背包是每行 9 格（9 / 17 同一行）'
    Assert-True ((Get-SlotRect 18).Y -gt $r9.Y) 'A9c 背包分了 3 行（18 在 9 的下一行）'

    Assert-True ((Get-MissingSlots (36..44)).Count -eq 0) `
        'A10 快捷栏 1×9（窗口 36-44）都在' ("缺: " + ((Get-MissingSlots (36..44)) -join ','))
    $r36 = Get-SlotRect 36
    Assert-True (([int]$r36.X -eq [int]$r9.X) -and ($r36.Y -gt (Get-SlotRect 35).Y)) `
        'A10b 快捷栏与背包左对齐、在背包下面（原版就是这样一条竖线）'

    Assert-True (($texts -contains '快捷栏（9 格）') -eq $false) `
        'A10c 分组小标题已经去掉了（原版布局里没有标题）'

    # 物品名走中文名表（官方 zh_cn，缓存在 UserData\lang）：有表时是中文，没网又没缓存时回退英文；
    # 所以下面这些断言两种都收（"钻石" / "Diamond"），验的是"这一格显示的是那件东西"。
    Assert-True ((Get-SlotName 36) -match '^格子 36：.*(钻石|Diamond)') 'A11 快捷栏 0（窗口 36）显示钻石' (Get-SlotName 36)
    Assert-True ((Get-SlotName 36) -like '*x5*') 'A12 数量 5 显示出来' (Get-SlotName 36)
    Assert-True ((Get-SlotName 9) -match '^格子 9：.*(下界之星|Nether Star)') 'A13 背包 0（窗口 9）显示下界之星' (Get-SlotName 9)
    Assert-True ((Get-SlotName 5) -match '^格子 5：.*(钻石头盔|Diamond Helmet)') 'A14 头盔（窗口 5）显示钻石头盔' (Get-SlotName 5)
    Assert-True ((Get-SlotName 45) -match '^格子 45：.*(盾牌|Shield)') 'A15 副手（窗口 45）显示盾牌' (Get-SlotName 45)
    Assert-True ((Get-SlotName 44) -like '格子 44：空') 'A16 快捷栏最后一格是空的' (Get-SlotName 44)
    Assert-True ($null -eq (Get-SlotElement 0)) 'A17 合成格（窗口 0-4）不画面板上'

    Save-Shot (Join-Path $artifactDir 'open_light.png')

    # ---------- B 点格子走一个来回 ----------
    Write-Host "`n=== B 点格子（拿起 / 放下）==="
    $before = Get-InventoryDump
    Assert-True ($before -match 'Slot: 0b') 'B1 起始：服务端 NBT 第 0 格（快捷栏 0）有东西' (Get-Brief $before)

    [void](Invoke-Element (Get-SlotElement 36))
    Start-Sleep -Seconds 2
    Assert-True ((Get-PanelTexts | Where-Object { $_ -match '光标拿着.*(钻石|Diamond)' }).Count -ge 1) `
        'B2 左键拿起：状态行显示"光标拿着 钻石 x5"' (Get-Brief ((Get-PanelTexts) -join ' | '))
    Assert-True ((Get-SlotName 36) -like '格子 36：空') 'B3 拿起后格子 36 显示空' (Get-SlotName 36)
    Assert-True ((Get-InventoryDump) -notmatch 'Slot: 0b') 'B4 服务端快捷栏 0 也已取空' (Get-Brief (Get-InventoryDump))

    # 放到背包第 1 格（窗口 10 = NBT Slot 10）：空格子，放下去就是"放下整摞"
    [void](Invoke-Element (Get-SlotElement 10))
    Start-Sleep -Seconds 2
    Assert-True ((Get-SlotName 10) -match '^格子 10：.*(钻石|Diamond)') 'B5 左键放下：格子 10 显示钻石' (Get-SlotName 10)
    $after = Get-InventoryDump
    Assert-True (($after -match 'Slot: 10b') -and ($after -match 'diamond')) `
        'B6 服务端 NBT 第 10 格收到钻石（点击真过了协议）' (Get-Brief $after)
    Assert-True ((Get-PanelTexts | Where-Object { $_ -like '*光标拿着*' }).Count -eq 0) 'B7 光标已空（状态行不再有"光标拿着"）'

    # ---------- C 面板开着时服务端改动 → 界面自己刷 ----------
    Write-Host "`n=== C 实时同步（面板开着）==="
    $null = Give-Slot 'hotbar.1' 'minecraft:golden_apple 3'
    Assert-True ((Wait-SlotName 37 '*x3*') -match '^格子 37：.*(金苹果|Golden Apple) x3') `
        'C1 服务端塞的金苹果自动出现在格子 37' (Get-SlotName 37)

    Set-ThemeSwitch $true
    Save-Shot (Join-Path $artifactDir 'open_dark.png')
    Assert-True (Get-PanelVisible) 'C2 切主题后面板还在'
    Set-ThemeSwitch $false

    # ---------- D 收面板 + 不会再自己弹回来 ----------
    Write-Host "`n=== D 收面板 / 不再弹回来 ==="
    Invoke-UiButton '关闭容器窗口'
    Assert-True (Wait-Panel $false) 'D1 点「关闭」后面板收起'

    # 负向对照：收掉了就别再推，背包接着变也不该把面板弹回来
    $null = Give-Slot 'hotbar.2' 'minecraft:emerald 7'
    Start-Sleep -Seconds 3
    Assert-True ((Get-PanelVisible) -eq $false) 'D2 收掉后背包变动不会把面板弹回来'

    Invoke-UiButton '背包'
    Assert-True (Wait-Panel $true) 'D3 再点「背包」面板又开'
    Assert-True ((Wait-SlotName 38 '*x7*') -match '^格子 38：.*(绿宝石|Emerald) x7') `
        'D4 重开立刻是最新的（看得到收面板期间放进来的绿宝石）' (Get-SlotName 38)

    # ---------- E Esc 收面板 ----------
    # 键盘路径必须真焦点 + 真按键；抢不到前台时 SendKeys 会打进别的窗口（环境问题不是程序问题），
    # 这种情况跳过断言只报一句 SKIP，同 g_container 第 C4 段的做法。
    Write-Host "`n=== E Esc 收面板 ==="
    Set-Foreground
    $fg = ''
    try { $fg = Get-ForegroundProcess } catch { $fg = '' }
    if ($fg -ne 'MCCX') {
        Write-Host "  [skip] 抢不到前台（当前前台=$fg），跳过 Esc 收面板断言"
    } else {
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Seconds 2
        Assert-True (Wait-Panel $false 5000) 'E1 Esc 收掉面板'
    }
    Restore-Foreground

    # ---------- F 断开连接 → 面板自动收起 ----------
    Write-Host "`n=== F 断开后面板自动收起 ==="
    if (-not (Get-PanelVisible)) {
        Invoke-UiButton '背包'
        Assert-True (Wait-Panel $true) 'F1 断开前先把面板打开'
    }
    Invoke-UiButton '断开'
    Assert-True (Wait-Panel $false 20000) 'F2 断开后面板自动收起'
}
finally {
    # 玩家这时候可能已经断开（F 段点了「断开」），item replace 就够不着了：
    # 这里尽力清一次，真正兜底的是下次开跑时 A 段的 clear。
    try { Write-Host ("  [clean] clear -> " + (Invoke-Rcon ("clear {0}" -f $script:TestPlayer))) } catch { Write-Host ("  [clean] 清背包失败: " + $_.Exception.Message) }
    try { Exit-TestSession $session } catch { Write-Host ("  [clean] " + $_.Exception.Message) }
}

Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_inventory FAILURES=$fail"
exit ([int]($fail -gt 0))
