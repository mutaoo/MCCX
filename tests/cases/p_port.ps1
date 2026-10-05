# 端口自动解析的 UI 验收：
#   1) IP + 端口留空      -> 提示不查 SRV，回填默认 25565
#   2) 服务器写 host:port -> 自动拆分，主机/端口框都回填
#   3) 域名 + 端口留空    -> 真的发起 SRV 查询，查不到回退 25565
#   4) 端口写非数字       -> 报错且不发起连接
# 注意：用例会改写“当前选中账号”的服务器/端口（TwoWay 绑定直接进账号），
#       所以开头记基线、finally 里原样放回，别给用户留脏地址。
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

function Get-ServerBox { Find-Edit $script:AppRoot 'ServerBox' }
function Get-PortBox { Find-Edit $script:AppRoot 'PortBox' }

function Reset-Form {
    param([string]$Server, [string]$Port)
    Set-Edit (Get-ServerBox) $Server
    Set-Edit (Get-PortBox) $Port
}

function Wait-Idle {
    # 等这次“连接”命令跑完（成功或失败都算），判据是按钮从禁用回到可用。
    # 不能只看状态条：点完“连接”到子进程回话之间状态仍是“未连接”，会踩空提前放行，
    # 下一个用例再点“连接”就会撞上还在跑的上一次流程（按钮 IsEnabled=false）。
    Wait-For -TimeoutMs 90000 -What '连接命令结束（按钮恢复可用）' -Probe {
        $b = $null
        try { $b = Find-Button $script:AppRoot '连接' } catch { $b = $null }
        if ($b -and $b.Current.IsEnabled) { 'enabled' }
    } | Out-Null
    $s = Get-WindowStateText $script:AppRoot
    if ($s -ne '未连接') {
        throw "回到未连接失败：当前状态=$s"
    }
}

$null = Start-App
Save-Foreground
Clear-Log

# 基线：当前选中账号原来的服务器/端口，测完必须放回去
$origServer = ''
$origPort = ''
try { $origServer = [string](Get-EditValue (Get-ServerBox)) } catch { }
try { $origPort = [string](Get-EditValue (Get-PortBox)) } catch { }
Write-Host "  基线: server='$origServer' port='$origPort'"

try {

# ---------- 用例 1：IP + 端口留空 ----------
# 主机用 TEST-NET-1（192.0.2.1，文档保留地址，必然连不上）：
# 不能用 127.0.0.1 —— 用户可能正开着单人世界“对局域网开放”，会真连进人家的游戏里。
Write-Host '--- case 1: IP, 端口留空 ---'
Reset-Form '192.0.2.1' ''
Invoke-UiButton '连接'
Assert-Log 'IP 地址查 SRV 没有意义' '用例1: 提示 IP 不查 SRV' 20000
Assert-Log '默认端口 25565' '用例1: 使用默认端口 25565' 10000
$p1 = Get-EditValue (Get-PortBox)
Assert-True ($p1 -eq '25565') '用例1: 端口框回填 25565' "实际=$p1"
Wait-Idle

# ---------- 用例 2：地址里直接写 host:port ----------
Write-Host '--- case 2: 127.0.0.1:25566 ---'
Clear-Log
Reset-Form '127.0.0.1:25566' ''
Invoke-UiButton '连接'
Wait-For -TimeoutMs 20000 -What '地址被拆开' -Probe {
    $h = Get-EditValue (Get-ServerBox)
    $p = Get-EditValue (Get-PortBox)
    if ($h -eq '127.0.0.1' -and $p -eq '25566') { "$h / $p" }
} | Out-Null
Assert-True ((Get-EditValue (Get-ServerBox)) -eq '127.0.0.1') '用例2: 服务器框只剩主机' "实际=$(Get-EditValue (Get-ServerBox))"
Assert-True ((Get-EditValue (Get-PortBox)) -eq '25566') '用例2: 端口框回填 25566' "实际=$(Get-EditValue (Get-PortBox))"
Wait-Idle

# ---------- 用例 3：域名 + 端口留空 → 真的去查 SRV ----------
# 用一个必然 NXDOMAIN 的名字：既能验证 SRV 查询链路，又不会真的连上任何服务器。
Write-Host '--- case 3: 域名端口留空, 查 SRV ---'
Clear-Log
Reset-Form 'mccx-port-test.invalid' ''
Invoke-UiButton '连接'
Assert-Log '正在查询 DNS SRV' '用例3: 发起了 SRV 查询' 20000
Assert-Log '没查到 SRV 记录' '用例3: 查不到 SRV' 20000
Assert-Log '默认端口 25565' '用例3: 回退默认 25565' 10000
$p3 = Get-EditValue (Get-PortBox)
Assert-True ($p3 -eq '25565') '用例3: 端口框回填 25565' "实际=$p3"
Wait-Idle

# ---------- 用例 4：端口写非数字 → 报错不连接 ----------
Write-Host '--- case 4: 非法端口 ---'
Clear-Log
Reset-Form '127.0.0.1' 'abc'
Invoke-UiButton '连接'
Assert-Log '端口“abc”无效' '用例4: 非法端口提示' 20000
Wait-Idle

} finally {
    # 收尾：把服务器/端口原样放回（用例失败抛异常也要走这里）
    if ($origServer -ne '') {
        try { Reset-Form $origServer $origPort; Write-Host "  已还原: server='$origServer' port='$origPort'" }
        catch { Write-Host "  ! 还原失败: $($_.Exception.Message)" }
    }
}

$n = Get-FailureCount
Write-Host "FAILURES=$n"
Restore-Foreground
exit ([int]($n -gt 0))
