# 功能日志开关端到端（2026-10-20 用户需求：开关要控制日志的"发出"，不是发出来再由界面过滤）
#
# 背景：旧实现是 Bot 照常输出、MCCX 界面按行首 [标签] 查 FeatureLogKeys 表吞行。
# MCC 换成官方简体中文后标签从 [AutoFishing] 变成 [自动钓鱼]，表里只有英文键 →
# 匹配落空，开关看起来"失效"（用户截图反馈）。现在开关经管道下发到子进程，
# 直接改 Bot 的 ChatBot.ProcessLogEnabled：关掉后过程日志在 Bot 里就不产生
# （不进控制台 / 日志文件 / 管道），与语言、标签文本、颜色码都无关。
#
# 断言：
#   B1 日志开：状态提示「已开启：距离…」照常
#   C1 日志开：过程日志「攻击 僵尸」真的发出来
#   D1 日志关：过程日志不再产生（界面里也没有）
#   D2 日志关：Bot 仍在攻击（RCON 回读僵尸血量下降，证明只是"不说话"）
#   E1 日志关：开关状态提示「自动砍怪已关闭」照常显示
#   F1 重新打开日志：过程日志立刻恢复（不用重连）
#   G1-G4 关着日志重连：重连后依旧静默、且仍在攻击（开关值走 IPC 配置记账）
#   H1-H4 整个程序重启（不是重连）：没人动开关，重启后依旧静默、且仍在攻击
#         （2026-10-22 用户报"重启 MCCX 后自动钓鱼日志开关失效、要手动拨一次"：
#          RunnerProcess 的配置记账按消息类型去重，所有功能的日志开关共用 logswitch 一个类型，
#          补发时只剩最后发的那一个，其余全退回默认"开"）
#
# 两个坑：
#   * 创造模式下僵尸不追玩家，挨打退开就不再回来 → 判"掉血"而不是"被消灭"；
#   * 夜间会自然刷怪，@e[type=zombie] 会误判 → 召唤的僵尸带 tag=logsw，只认自己那只。
#
# 专用账号 TestLogSw，测完自删（Exit-TestSession 核对账号列表回到基线）。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_logswitch.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestLogSw'
$script:TestPlayer = $script:TestUser

function Summon-TaggedZombie {
    $pos = Get-PlayerPos
    if (-not $pos) { return $false }
    $out = Invoke-Rcon ("summon minecraft:zombie {0} {1} {2} {{Tags:[""logsw""]}}" -f ($pos[0] + 2.0), [math]::Floor($pos[1]), $pos[2])
    Write-Host "  rcon summon -> $out"
    return ($out -match 'Summoned')
}

function Get-TaggedZombieHealth {
    $out = Invoke-Rcon 'data get entity @e[type=minecraft:zombie,tag=logsw,limit=1] Health'
    if ($out -match 'entity data:\s*([0-9]+(?:\.[0-9]+)?)f') { return [double]$Matches[1] }
    return $null
}

function Test-ZombieDamaged {
    param([int]$TimeoutMs = 20000)
    try {
        $null = Wait-For -TimeoutMs $TimeoutMs -What '僵尸掉血（Bot 仍在攻击）' -Probe {
            $x = Get-TaggedZombieHealth
            if ($null -ne $x -and $x -lt 20.0) { $x } else { $null }
        }
        return $true
    } catch { return $false }
}

function Get-RecentText {
    try { return [string](Get-RecentLogText) } catch { return '' }
}

# 日志开关长在参数弹层里，必须先展开对应下拉才能拿到 ToggleSwitch
function Set-LogToggle {
    param([string]$Entry, [string]$Name, [bool]$On)
    Close-AllFlyouts
    $btn = Get-UiButton $Entry
    $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    Start-Sleep -Milliseconds 700
    Set-ToggleOn $Name $On
    $ec.Collapse()
    Start-Sleep -Milliseconds 400
}

$session = $null
try {
    $null = Restart-App
    Start-Sleep -Seconds 2
    $session = Enter-TestSession -User $script:TestUser
    Write-Host "--- connected ---"

    # ---------- A. 高空平台 + 免伤 + 夜晚 + 剑 ----------
    $null = Invoke-Rcon 'fill -346 109 -445 -340 109 -439 stone'
    $null = Invoke-Rcon ("execute as {0} run tp @s -345 110 -443" -f $script:TestUser)
    $null = Invoke-Rcon ("gamemode creative {0}" -f $script:TestUser)
    $null = Set-ServerDifficulty 'easy'
    $null = Invoke-Rcon 'time set night'
    $null = Invoke-Rcon 'kill @e[type=minecraft:zombie,tag=logsw]'
    Start-Sleep -Seconds 2
    $null = Invoke-Rcon ("item replace entity {0} weapon.mainhand with minecraft:diamond_sword" -f $script:TestUser)
    Start-Sleep -Seconds 1

    # ---------- B. 打开砍怪（日志开） ----------
    Clear-Log
    Set-ToggleOn '自动砍怪' $true
    Assert-Log '已开启：距离' 'B1 状态提示（功能已开启）' 15000

    # ---------- C. 日志开：过程日志出现 ----------
    $null = Summon-TaggedZombie
    $ok = $true
    try { $null = Wait-LogContains '攻击 僵尸' 20000 } catch { $ok = $false }
    Assert-True $ok 'C1 日志开：过程日志「攻击 僵尸」已产生' ("recent=" + (Get-RecentText))

    # ---------- D. 日志关：不产生过程日志，但 Bot 照打 ----------
    Set-LogToggle -Entry '自动砍怪选项' -Name '自动砍怪日志' -On $false
    Start-Sleep -Seconds 2
    $null = Invoke-Rcon 'kill @e[type=minecraft:zombie,tag=logsw]'
    Start-Sleep -Seconds 2
    Clear-Log
    $null = Summon-TaggedZombie
    Start-Sleep -Seconds 10
    $txt = Get-RecentText
    Assert-True (-not ($txt -match '攻击 ')) 'D1 日志关：过程日志不再产生' ("recent=" + $txt)
    Assert-True (Test-ZombieDamaged 20000) 'D2 日志关：Bot 仍在攻击（僵尸掉血）' ("health=" + (Get-TaggedZombieHealth))

    # ---------- E. 日志关：状态提示照常 ----------
    Clear-Log
    Set-ToggleOn '自动砍怪' $false
    Assert-Log '自动砍怪已关闭' 'E1 日志关：开关状态提示照常显示' 10000

    # ---------- F. 重新打开日志：过程日志恢复 ----------
    Set-LogToggle -Entry '自动砍怪选项' -Name '自动砍怪日志' -On $true
    Set-ToggleOn '自动砍怪' $true
    Start-Sleep -Seconds 1
    Clear-Log
    $null = Summon-TaggedZombie
    $ok2 = $true
    try { $null = Wait-LogContains '攻击 僵尸' 20000 } catch { $ok2 = $false }
    Assert-True $ok2 'F1 重新打开日志：过程日志恢复' ("recent=" + (Get-RecentText))

    # ---------- G. 关着日志重连：依旧静默 ----------
    Set-LogToggle -Entry '自动砍怪选项' -Name '自动砍怪日志' -On $false
    Start-Sleep -Seconds 2
    Disconnect-App
    Invoke-UiButton '连接'
    Assert-Connected 'G1 关着日志重连成功' 90000
    Assert-Log '已进入游戏' 'G2 重新进服' 60000
    Start-Sleep -Seconds 3
    $null = Invoke-Rcon 'kill @e[type=minecraft:zombie,tag=logsw]'
    $null = Invoke-Rcon ("execute as {0} run tp @s -345 110 -443" -f $script:TestUser)
    $null = Invoke-Rcon ("item replace entity {0} weapon.mainhand with minecraft:diamond_sword" -f $script:TestUser)
    Start-Sleep -Seconds 2
    Clear-Log
    $null = Summon-TaggedZombie
    Start-Sleep -Seconds 10
    $txt2 = Get-RecentText
    Assert-True (-not ($txt2 -match '攻击 ')) 'G3 重连后日志仍关：过程日志依旧不产生' ("recent=" + $txt2)
    Assert-True (Test-ZombieDamaged 20000) 'G4 重连后日志仍关：Bot 仍在攻击（僵尸掉血）' ("health=" + (Get-TaggedZombieHealth))

    # ---------- H. 整个程序重启：开关值仍生效（2026-10-22 用户报的"重启后要手动拨一次"） ----------
    # 根因：RunnerProcess 的配置记账按 RunnerMessage.Type 去重，而所有功能的日志开关共用
    # logswitch 这一个类型 → 只有最后发的那一个能补发，其余在子进程里退回默认"开"。
    # 这里直接重启 MCCX（不是重连），新进程必须按账号库里"关着"的状态补发一遍。
    Write-Host "`n=== H. 重启程序后日志开关仍然生效 ==="
    Start-Sleep -Seconds 2
    $null = Restart-App
    Start-Sleep -Seconds 2

    Select-UiAccount -User $script:TestUser
    Invoke-UiButton '连接'
    Assert-Connected 'H1 重启后再连接成功' 90000
    $okH = $true
    try { $null = Wait-LogContains '已进入游戏' 60000 } catch { $okH = $false }
    Assert-True $okH 'H2 重启后重新进服' ("recent=" + (Get-RecentText))

    # 砍怪的"启用"状态本来就不随账号存（每次启动都从关开始），这里手动打开；
    # 要验的是"日志开关"的值（账号库里是关的）—— 新进程启动时必须自己补发，不能等用户再拨一次。
    Set-ToggleOn '自动砍怪' $true
    Assert-Log '已开启：距离' 'H2b 重启后打开砍怪（状态提示照常）' 15000

    $null = Invoke-Rcon ("gamemode creative {0}" -f $script:TestUser)
    $null = Invoke-Rcon ("execute as {0} run tp @s -345 110 -443" -f $script:TestUser)
    $null = Invoke-Rcon 'kill @e[type=minecraft:zombie,tag=logsw]'
    $null = Invoke-Rcon ("item replace entity {0} weapon.mainhand with minecraft:diamond_sword" -f $script:TestUser)
    Start-Sleep -Seconds 3
    Clear-Log
    $null = Summon-TaggedZombie
    Start-Sleep -Seconds 10
    $txtH = Get-RecentText
    Assert-True (-not ($txtH -match '攻击 ')) 'H3 重启后没人动日志开关：过程日志依旧不产生' ("recent=" + $txtH)
    Assert-True (Test-ZombieDamaged 20000) 'H4 重启后仍在攻击（僵尸掉血）' ("health=" + (Get-TaggedZombieHealth))
}
finally {
    try { Exit-TestSession $session } catch { Write-Host ("  [clean] " + $_.Exception.Message) }
}

$fails = Get-FailureCount
Write-Host ("FAILURES={0}" -f $fails)
if ($fails -eq 0) { Write-Host 'SUMMARY g_logswitch OK' } else { Write-Host 'SUMMARY g_logswitch FAIL' }
exit $fails
