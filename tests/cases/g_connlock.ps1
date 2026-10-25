# 连接状态下连接参数不可编辑（2026-10-18 用户需求②：连接中 / 已连接 / 断开中一律禁用，
# 只有彻底没连的时候才让改）：在本地测试服上走一遍状态机，每个状态都读四个框的 UIA IsEnabled：
#   A 刚启动（未连接）：服务器 / 端口 / 用户名 / 版本都可编辑（起点对照）；
#   B 点「连接」：采样到的"连接中"必须也是禁用；连上之后（已连接）四个框都禁用
#     —— 「连接 / 断开」两个按钮的可用性不跟着变（连接禁用、断开可用）；
#   C 点「断开」：采样到的"断开中"同样禁用；回到"未连接"之后四个框恢复可编辑。
# 只读 UIA / 点按钮，不抢前台焦点；专用账号 MccXConnLock，测完删除、账号列表回到基线。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_connlock.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'MccXConnLock'
$script:TestPlayer = $script:TestUser
$script:ParamBoxIds = @('ServerBox', 'PortBox', 'UserBox', 'VersionBox')

# ---------- 断言用的探测 ----------

# 四个参数框的可用性：返回 'ok' = 全部符合期望；否则返回第一处不符的描述（给 Assert-True 的 Detail）
function Get-ParamBoxState {
    param([bool]$ExpectEnabled)
    foreach ($id in $script:ParamBoxIds) {
        $e = Find-ById $script:AppRoot $id
        if (-not $e) { return "$id 找不到" }
        $cur = $e.Current.IsEnabled
        if ($cur -ne $ExpectEnabled) {
            return ("{0} IsEnabled={1}（期望 {2}）" -f $id, $cur, $ExpectEnabled)
        }
    }
    return 'ok'
}

# 等状态到 $Target（'已连接' 认前缀），沿途把采到的"连接中 / 断开中"记下来：
# 那两种是半路上的状态，必须也是禁用 —— 采到才断言，采不到（本机连得太快）不算失败。
function Wait-StateWithSample {
    param([string]$Target, [int]$TimeoutMs = 90000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $script:TransientSamples = 0
    $script:TransientBad = @()
    $last = ''
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $last = Get-WindowStateText $script:AppRoot
        if ($Target -eq '已连接') {
            if ($last -like '已连接 *') { return $last }
        } elseif ($last -eq $Target) {
            return $last
        }
        if ($last -eq '连接中…' -or $last -eq '断开中…') {
            $script:TransientSamples++
            $bad = Get-ParamBoxState $false
            if ($bad -ne 'ok') { $script:TransientBad += ("{0} 时 {1}" -f $last, $bad) }
        }
        Start-Sleep -Milliseconds 120
    }
    throw "等待状态超时: $Target（最后读到 '$last'）"
}

function Assert-TransientDisabled {
    param([string]$Label)
    if ($script:TransientSamples -gt 0) {
        Assert-True ($script:TransientBad.Count -eq 0) `
            ("{0} 半路状态（连接中 / 断开中）的 {1} 次采样都是禁用" -f $Label, $script:TransientSamples) `
            ($script:TransientBad -join ' / ')
    } else {
        Write-Host ("  [info] {0}：本机连/断太快，没采到半路状态（同一条表达式管着，主状态断言已覆盖）" -f $Label)
    }
}

# ---------- 跑 ----------

$null = Stop-App
$null = Start-App

# A 起点：未连接
$st = Get-ParamBoxState $true
Assert-True ($st -eq 'ok') 'A1 未连接时四个参数框都可编辑（服务器 / 端口 / 用户名 / 版本）' $st
Assert-True ((Get-WindowStateText $script:AppRoot) -eq '未连接') 'A2 起点状态是「未连接」'

Start-LocalTestServer -Port '25599'
$baseline = @(Get-UiAccountRows)
$list = Find-ById $script:AppRoot 'AccountList'
if ($list -and (Find-ListItemByText $list $script:TestUser)) {
    Write-Host "  [clean] 删除残留账号 $script:TestUser"
    $null = Remove-AccountViaUi $script:TestUser
    Start-Sleep -Seconds 2
}
Add-UiTestAccount -User $script:TestUser -Server '127.0.0.1' -Port '25599'
Select-UiAccount -User $script:TestUser
$session = @{ User = $script:TestUser; Baseline = $baseline; StateText = '' }

try {
    # B 连接中 / 已连接
    Write-Host "`n=== B 连接：半路状态与已连接都禁用 ==="
    Invoke-UiButton '连接'
    $null = Wait-StateWithSample '已连接'
    Assert-TransientDisabled 'B0'
    $st = Get-ParamBoxState $false
    Assert-True ($st -eq 'ok') 'B1 已连接时四个参数框都禁用' $st
    Assert-True ((Get-UiButton '连接').Current.IsEnabled -eq $false) 'B2 已连接时「连接」按钮是禁用的（原有口径不变）'
    Assert-True ((Get-UiButton '断开').Current.IsEnabled -eq $true) 'B3 已连接时「断开」按钮可用（原有口径不变）'

    # C 断开中 / 未连接
    Write-Host "`n=== C 断开：半路状态禁用、回到未连接后可编辑 ==="
    Invoke-UiButton '断开'
    $null = Wait-StateWithSample '未连接'
    Assert-TransientDisabled 'C0'
    $st = Get-ParamBoxState $true
    Assert-True ($st -eq 'ok') 'C1 断开完成（未连接）后四个参数框重新可编辑' $st
}
finally {
    try { Exit-TestSession $session } catch { Write-Host ("  [clean] " + $_.Exception.Message) }
}

Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_connlock FAILURES=$fail"
exit ([int]($fail -gt 0))
