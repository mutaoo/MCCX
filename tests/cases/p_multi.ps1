# 多开账号回归：启动恢复列表 / 添加弹窗 / 面板切换 / 子进程连接 / 删除 / 持久化
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

function Get-AccountListEl {
    $l = Find-ById $script:AppRoot 'AccountList'
    if (-not $l) { throw '找不到账号列表 AccountList' }
    return $l
}

function Get-AccountCount { return @(Get-ListItems (Get-AccountListEl)).Count }

function Find-EditByIdSafe {
    param([string]$Id)
    $e = $null
    try { $e = Find-ById $script:AppRoot $Id } catch { $e = $null }
    return $e
}

function Get-EditValueSafe {
    param([string]$Id)
    $e = Find-EditByIdSafe $Id
    if (-not $e) { return $null }
    return Get-EditValue $e
}

function Find-DialogCheckBox {
    param([string]$Name)
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::CheckBox)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)))
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Invoke-AddAccountButton {
    $b = Find-Button $script:AppRoot '添加账号'
    if (-not $b) { throw '找不到 [添加账号] 按钮' }
    Invoke-Element $b
}

function Test-LocalServerListening {
    return [bool](netstat -ano | Select-String ':25599\s.*LISTENING')
}

function Ensure-TestServer {
    # D 段要验证“子进程真实连接”，用外网服务器会受其在线状态影响（曾因对方关服导致用例失败）。
    # 改为本地测试服：不在就自己拉起，端口固定 25599，可重复执行。
    $dir = "$(Join-Path $PSScriptRoot '..\artifacts')\mcserver\1.21.11"
    if (-not (Test-Path (Join-Path $dir 'server.jar'))) { throw "本地测试服缺失: $dir" }
    $props = Join-Path $dir 'server.properties'
    if (Test-Path $props) {
        Set-Content -Path $props -Value ((Get-Content $props) -replace '^server-port=.*$', 'server-port=25599') -Encoding ASCII
    }
    if (Test-LocalServerListening) {
        Write-Host '  [srv] 本地测试服已在运行 127.0.0.1:25599'
        return
    }
    $java = (Get-Command java -ErrorAction SilentlyContinue).Source
    if (-not $java) { throw 'java 不在 PATH，无法启动本地测试服' }
    Start-Process -FilePath $java -ArgumentList '-Xmx1G', '-jar', 'server.jar', 'nogui' `
        -WorkingDirectory $dir -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dir 'test.out.log') `
        -RedirectStandardError (Join-Path $dir 'test.err.log')
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        if (Test-LocalServerListening) {
            Write-Host '  [srv] 本地测试服已启动 127.0.0.1:25599'
            return
        }
    }
    throw '本地测试服启动超时（25599 未监听）'
}

function Clear-StrayRunners {
    # runner 子进程只在账号删除 / 程序退出时才会被杀（断开不会），
    # 上一个套件（例如 p_port 连了 4 次但从不断开）留下的残留会干扰
    # “子进程已回收、仅剩主程序”这类断言。起手连主程序一起重启干净。
    $strays = @(Get-CimInstance Win32_Process -Filter "Name='MCCX.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '--runner' })
    if ($strays.Count -eq 0) { return }
    Write-Host ("  [clean] 清理残留 runner: " + (($strays | ForEach-Object { $_.ProcessId }) -join ','))
    Get-Process -Name MCCX -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 2
}

$null = Start-App
Save-Foreground
Write-Host "--- app up ---"

# ============ A. 启动即恢复账号列表（需求 3.1 / 5. 多账号导航） ============
$before = Get-AccountCount
Assert-True ($before -ge 1) 'A1 启动后账号列表已从账号库恢复' "count=$before"
Write-Host "  -> accounts=$before"

$server0 = Get-EditValueSafe 'ServerBox'
Assert-True ($null -ne $server0) 'A2 有账号时右侧面板可见'
Assert-True ((Get-EditValueSafe 'UserBox') -ne 'TestMulti') 'A3 初始选中的不是测试账号'

$null = Get-UiButton '添加账号'
Write-Host 'A4 [添加账号] 按钮存在 OK'

# ============ B. 添加账号弹窗（默认立即连接） ============
Invoke-AddAccountButton
$null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗出现' -Probe {
    if (Find-EditByIdSafe 'NewServerBox') { 'open' } else { $null }
}
Write-Host 'B1 弹窗已打开'

$cb = $null
for ($i = 0; $i -lt 10 -and -not $cb; $i++) {
    $cb = Find-DialogCheckBox '立即连接'
    if (-not $cb) { Start-Sleep -Milliseconds 300 }
}
Assert-True ($null -ne $cb) 'B2 弹窗含“立即连接”选项'
$tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$state = [string]$tp.Current.ToggleState
Assert-True ($state -eq 'On') 'B3 “立即连接”默认勾选' "实际=$state"
# 取消勾选：这一步只验证“能添加”，不让测试账号真的去连网
$tp.Toggle()
Start-Sleep -Milliseconds 300

Set-Edit (Find-Edit $script:AppRoot 'NewServerBox') '127.0.0.1'
Set-Edit (Find-Edit $script:AppRoot 'NewPortBox') '25599'
Set-Edit (Find-Edit $script:AppRoot 'NewUserBox') 'TestMulti'
Set-Edit (Find-Edit $script:AppRoot 'NewVersionBox') 'auto'

$addBtn = Find-Button $script:AppRoot '添加'
if (-not $addBtn) { throw '找不到弹窗的 [添加] 按钮' }
Invoke-Element $addBtn
$null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗关闭' -Probe {
    if (Find-EditByIdSafe 'NewServerBox') { $null } else { 'closed' }
}
Write-Host 'B4 提交成功，弹窗已关闭'

$after = Get-AccountCount
Assert-True ($after -eq ($before + 1)) 'B5 列表增加一个账号' "before=$before after=$after"
$item = Find-ListItemByText (Get-AccountListEl) 'TestMulti'
Assert-True ($null -ne $item) 'B6 列表里出现 TestMulti'

Assert-True ((Wait-For -TimeoutMs 6000 -What '面板显示新账号服务器' -Probe {
        $v = Get-EditValueSafe 'ServerBox'; if ($v -eq '127.0.0.1') { $v } else { $null } }) -eq '127.0.0.1') 'B7 面板服务器=127.0.0.1'
Assert-True ((Wait-For -TimeoutMs 6000 -What '面板显示新账号端口' -Probe {
        $v = Get-EditValueSafe 'PortBox'; if ($v -eq '25599') { $v } else { $null } }) -eq '25599') 'B8 面板端口=25599'
Assert-True ((Wait-For -TimeoutMs 6000 -What '面板显示新账号游戏名' -Probe {
        $v = Get-EditValueSafe 'UserBox'; if ($v -eq 'TestMulti') { $v } else { $null } }) -eq 'TestMulti') 'B9 面板游戏名=TestMulti'

# ============ C. 点左侧账号切换右侧窗口（每账号独立面板） ============
$items = @(Get-ListItems (Get-AccountListEl))
Select-Item $items[0]
$u1 = Wait-For -TimeoutMs 8000 -What '面板切到第一个账号' -Probe {
    $v = Get-EditValueSafe 'UserBox'
    if ($v -and $v -ne 'TestMulti') { $v } else { $null }
}
Write-Host "  -> 第一个账号游戏名=$u1"

$item = Find-ListItemByText (Get-AccountListEl) 'TestMulti'
if (-not $item) { $item = Find-ListItemByText (Get-AccountListEl) 'TestMulti' }
Select-Item $item
$u2 = Wait-For -TimeoutMs 8000 -What '面板切回 TestMulti' -Probe {
    $v = Get-EditValueSafe 'UserBox'
    if ($v -eq 'TestMulti') { $v } else { $null }
}
Assert-True ($u2 -eq 'TestMulti') 'C1 切换左侧账号后右侧面板跟随'
Assert-True ($u1 -ne 'TestMulti') 'C2 两个账号的面板数据彼此独立' "u1=$u1"

# ============ D. TestMulti 走子进程真实连接（多开） ============
Ensure-TestServer
Set-Edit (Find-Edit $script:AppRoot 'ServerBox') '127.0.0.1'
Set-Edit (Find-Edit $script:AppRoot 'PortBox') '25599'
Clear-Log
Invoke-UiButton '连接'

Assert-Log '账号子进程已就绪' 'D1 多开子进程启动并回传日志' 20000
$procs = @(Get-Process -Name MCCX -ErrorAction SilentlyContinue).Count
Assert-True ($procs -ge 2) 'D2 主程序 + 至少一个子进程同时存在' "procs=$procs"
Write-Host "  -> MCCX 进程数=$procs"

Assert-Log '连接成功' 'D3 子进程连接成功' 60000
$stateTxt = ''
try { $stateTxt = Wait-For -TimeoutMs 30000 -What '状态=已连接' -Probe {
        $x = Get-WindowStateText $script:AppRoot; if ($x -like '已连接*') { $x } else { $null } } } catch { }
Assert-True ($stateTxt -like '已连接*') 'D4 状态栏显示已连接' "state=$stateTxt"

# 输入走的是选中账号的子进程
Set-Edit (Find-Edit $script:AppRoot 'CommandBox') 'hello from p_multi'
Invoke-UiButton '发送'
Assert-Log 'hello from p_multi' 'D5 输入发送到该账号的子进程' 20000

# 日志隔离：切到别的账号，看不到 TestMulti 的子进程日志
$items2 = @(Get-ListItems (Get-AccountListEl))
Select-Item $items2[0]
$null = Wait-For -TimeoutMs 8000 -What '切到第一个账号' -Probe {
    $v = Get-EditValueSafe 'UserBox'; if ($v -and $v -ne 'TestMulti') { $v } else { $null }
}
Start-Sleep -Milliseconds 500
$otherLog = Get-RecentLogText
Assert-True (($otherLog -notmatch '账号子进程已就绪')) 'D6 各账号日志互不串台' ($otherLog -replace "`n", ' | ')

# 切回 TestMulti 断开
$item = Find-ListItemByText (Get-AccountListEl) 'TestMulti'
Select-Item $item
$null = Wait-For -TimeoutMs 8000 -What '切回 TestMulti' -Probe {
    $v = Get-EditValueSafe 'UserBox'; if ($v -eq 'TestMulti') { $v } else { $null }
}
Invoke-UiButton '断开'
$null = Wait-For -TimeoutMs 30000 -What '断开完成' -Probe {
    $x = Get-WindowStateText $script:AppRoot; if ($x -eq '未连接') { $x } else { $null }
}
Write-Host 'D7 已断开 OK'

# ============ F. 删除账号：列表、账号库、子进程一起收干净 ============
# 新版删除走“账号项右侧叉号 + 二次确认弹窗”（底部“删除”按钮已移除）
Assert-True (Remove-AccountViaUi 'TestMulti') 'F0 删除走叉号+确认弹窗'
$null = Wait-For -TimeoutMs 10000 -What 'TestMulti 被删除' -Probe {
    if (-not (Find-ListItemByText (Get-AccountListEl) 'TestMulti')) { 'gone' } else { $null }
}
Assert-True ((Get-AccountCount) -eq $before) 'F1 删除后账号数恢复' "count=$(Get-AccountCount) before=$before"

$null = Wait-For -TimeoutMs 20000 -What '子进程被回收' -Probe {
    $n = @(Get-Process -Name MCCX -ErrorAction SilentlyContinue).Count
    if ($n -eq 1) { $n } else { $null }
}
Write-Host 'F2 子进程已回收（仅剩主程序）OK'

# ============ G. 重启后列表与删除结果都持久化 ============
$null = Restart-App
Save-Foreground
Assert-True ((Get-AccountCount) -eq $before) 'G1 重启后账号数不变' "count=$(Get-AccountCount) before=$before"
Assert-True ($null -eq (Find-ListItemByText (Get-AccountListEl) 'TestMulti')) 'G2 测试账号删除已持久化'
Assert-True ((Get-EditValueSafe 'ServerBox') -ne 'TestMulti') 'G3 重启后右侧面板正常显示'

$alive = [bool](Get-Process -Name MCCX -ErrorAction SilentlyContinue)
Assert-True $alive '程序仍在运行'

Restore-Foreground

$n = Get-FailureCount
Write-Host "FAILURES=$n"
exit ([int]($n -gt 0))
