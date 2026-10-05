# GUI 实测：验证 2026-10-03 的四项修复是否真的生效。
# 只看不改（不修改账号库内容），结束前把进程收干净。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_fixes.ps1
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

# 账号列表元素（同 p_multi / p_blank 等用例的写法：autolib 里没有这个 helper）
function Get-AccountListEl {
    $l = Find-ById $script:AppRoot 'AccountList'
    if (-not $l) { throw '找不到账号列表 AccountList' }
    return $l
}

$script:GuiFail = 0
function Check([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name  $detail"; $script:GuiFail++ }
}

Write-Host '=== 启动 MCCX ==='
# 注意：不要写 `Restart-App | Out-Null` —— 它会把 AutomationElement 推进管道，
# 随后调用 Get-UiToggle/Get-UiButton 时该对象会被当成 $Name 绑进参数（报错信息里显示
# "找不到开关: System.Windows.Automation.AutomationElement"）。直接赋值最稳。
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }

# ---------- 1) 「视角调整」下拉：6 个视角移动按钮（恢复开关已移除） ----------
Write-Host "`n=== 1) 「视角调整」下拉 ==="
# 2026-10-04 新需求：「进服后恢复上次视角」开关已整体移除——进服不干预视角、本地不存视角
# （沿用服务器记忆，与 MCC 一致），弹层里只剩 6 个「视角移动」按钮。
# 下面验证：开关确实不见了 + 6 个按钮都在。
$vaBtn = $null
try { $vaBtn = Get-UiButton '视角调整选项' } catch {
    Write-Host ("  Get-UiButton 视角调整选项 异常：" + $_.Exception.Message)
}
if ($vaBtn) {
    $ec = $null
    try {
        $ec = $vaBtn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
        Start-Sleep -Milliseconds 900

        $viewToggle = $null
        try { $viewToggle = Find-UiElement $script:CT::Button '视角恢复' } catch { }
        if (-not $viewToggle) {
            try { $viewToggle = Find-UiElement $script:CT::CheckBox '视角恢复' } catch { }
        }
        if ($viewToggle) {
            Check $false '「视角恢复」开关已移除' 'UIA 仍找到名为「视角恢复」的开关（功能已拆）'
        } else {
            Check $true '「视角恢复」开关已移除（进服不再有恢复干预）'
        }

        foreach ($b in @('视角向东', '视角向南', '视角向西', '视角向北', '视角抬头看天', '视角低头看地')) {
            $el = $null
            try { $el = Find-UiElement $script:CT::Button $b } catch { }
            Check ($null -ne $el) "视角移动按钮 [$b] 在弹层里"
        }

        # 没连接时点一下要有一行提示（不许静默）。只有选中了账号才会写日志。
        $state = Find-ById $script:AppRoot 'StateTextBlock'
        if ($state) {
            try {
                Clear-Log
                Invoke-UiButton '视角向东' 5000 $true | Out-Null
                $hit = $false
                try { Wait-LogContains '视角移动需要先' 6000 | Out-Null; $hit = $true } catch { $hit = $false }
                Check $hit '未连接时点「向东」有提示日志'
            } catch {
                Check $false '未连接时点「向东」有提示日志' $_.Exception.Message
            }
        } else {
            Write-Host '  （未选中账号：跳过点击反馈检查）'
        }
    } catch {
        Check $false '打开「视角调整」弹层' $_.Exception.Message
    } finally {
        try { $ec.Collapse() } catch { }
        Start-Sleep -Milliseconds 400
    }
} else {
    Check $false '找到「视角调整选项」按钮' '未找到（行上应只剩这个下拉按钮）'
}

# ---------- 2) 自动重连弹层：次数默认 0 + 新提示文案 ----------
Write-Host "`n=== 2) 自动重连弹层 ==="
$reconnectBtn = $null
try { $reconnectBtn = Get-UiButton '自动重连选项' } catch {
    Write-Host ("  Get-UiButton 自动重连选项 异常：" + $_.Exception.Message)
}
if ($reconnectBtn) {
    try {
        $ec = $reconnectBtn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
        Start-Sleep -Milliseconds 900

        try {
            $attemptsEl = Get-UiEdit '重连次数'
            $v = Get-EditValue $attemptsEl
            Write-Host "  重连次数 = [$v]"
            Check ($v -eq '0') '重连次数默认 0（无限）' "实际=[$v]"
        } catch {
            Check $false '找到「重连次数」输入框' $_.Exception.Message
        }

        $hintFound = $false
        try {
            $hint = Find-UiElement $script:CT::Text '断线后按间隔自动重连；次数填 0 表示无限重连，关掉开关会立即停止正在进行的重连。'
            $hintFound = $null -ne $hint
        } catch { }
        Check $hintFound '重连弹层已显示「0 = 无限」提示'

        # 间隔框也应能读到（证明弹层结构没坏）
        try {
            $delayEl = Get-UiEdit '重连间隔'
            Write-Host ("  重连间隔 = [" + (Get-EditValue $delayEl) + "]")
            Check $true '重连间隔输入框可读'
        } catch {
            Check $false '重连间隔输入框可读' $_.Exception.Message
        }
    } catch {
        Check $false '打开自动重连弹层' $_.Exception.Message
    } finally {
        try { $ec.Collapse() } catch { }
        Start-Sleep -Milliseconds 400
    }
} else {
    Check $false '找到「自动重连选项」按钮' '未找到'
}

# ---------- 3) 账号列表（账号库可解密 + 界面未崩） ----------
Write-Host "`n=== 3) 账号列表 ==="
$count = 0
try {
    $listEl = Get-AccountListEl
    $items = @(Get-ListItems $listEl)
    $count = $items.Count
    Write-Host ("  列表项数 = $count")
    $items | Select-Object -First 4 | ForEach-Object { Write-Host ("    - " + $_) }
} catch {
    Write-Host ("  读取列表异常：" + $_.Exception.Message)
}
Check ($count -gt 0) '账号列表已渲染（账号库可解密）' "项数=$count"

# ---------- 4) 日志区可用 + 状态行 ----------
Write-Host "`n=== 4) 日志区与状态行 ==="
$logOk = $false
try {
    $logEl = Get-LogList
    $logOk = $null -ne $logEl
} catch { }
Check $logOk '日志列表存在'

try {
    $state = Find-ById $script:AppRoot 'StateTextBlock'
    if ($state) { Write-Host ("  状态行 = [" + $state.Current.Name + "]") }
    else { Write-Host '  状态行不可见（未选中账号时本来如此）' }
} catch { }

Write-Host "`n=== 收尾 ==="
Stop-App
Write-Host ("SUMMARY g_fixes FAILURES=$script:GuiFail")
exit $(if ($script:GuiFail -eq 0) { 0 } else { 1 })
