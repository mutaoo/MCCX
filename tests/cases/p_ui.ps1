# UI 冒烟：日志账号路径 / 横排开关 / 参数下拉按钮（Flyout）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

function Open-Flyout {
    param([string]$BtnName)
    $b = $null
    try { $b = Get-UiButton $BtnName } catch { return $false }
    if (-not $b) { return $false }
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $ec.Expand()
        }
    } catch {
        try { Invoke-Element $b } catch { return $false }
    }
    Start-Sleep -Milliseconds 700
    return $true
}

$null = Start-App
Save-Foreground
Write-Host "--- app up ---"

# 1) 日志里显示用户数据目录（账号库与配置类文件都在 UserData 文件夹里，2026-10-05 起）
$hit = Wait-LogContains -Pattern '用户数据目录：' -TimeoutMs 15000
$line = ''
if ($hit) { $line = (@($hit -split "`n") | Where-Object { $_ -match '用户数据目录：' } | Select-Object -First 1) }
Assert-True ($hit -and ($line -match '用户数据目录：')) '日志显示用户数据目录' $line
Write-Host "  -> $line"

# 1b) 2026-10-16 需求（第三轮）：没连上服务器时，命令栏（常用命令 / 输入框 / 发送 / 清空日志）
#     不可操作，要连上服务器才能用（连上之后的样子在 g_search 里验）。
#     2026-11-06 改版：命令栏不再收起，而是保留布局 + 子控件 IsEnabled=False；日志搜索框离线也显示，
#     并展开到整条命令区（宽度覆盖整行，高度同命令输入框）。这几条正好跑在"刚起来、未连接"的状态下。
$stateOff = ''
try { $stateOff = Get-WindowStateText $script:AppRoot } catch { }
Assert-True ($stateOff -eq '未连接') '起步就是未连接' "实际='$stateOff'"
$cmdOff = $null
try { $cmdOff = Find-ById $script:AppRoot 'CommandBox' } catch { $cmdOff = $null }
$searchOff = $null
try { $searchOff = Find-ById $script:AppRoot 'LogSearchBox' } catch { $searchOff = $null }
Assert-True ($null -ne $cmdOff) '未连接时命令行输入框仍在自动化树里（只禁用，不收起）'
Assert-True ($null -ne $searchOff) '未连接时日志搜索框仍显示（展开态）'
if ($cmdOff) {
    Assert-True (-not $cmdOff.Current.IsEnabled) '未连接时命令行输入框禁用' "IsEnabled=$($cmdOff.Current.IsEnabled)"
}
if ($searchOff) {
    Assert-True ($searchOff.Current.IsEnabled) '未连接时日志搜索框仍可输入' "IsEnabled=$($searchOff.Current.IsEnabled)"
}
foreach ($name in @('发送', '清空日志', '常用命令')) {
    $btn = $null
    try { $btn = Get-UiButton $name } catch { $btn = $null }
    Assert-True ($null -ne $btn) "未连接时「$name」按钮仍在自动化树里（只禁用，不收起）"
    if ($btn) {
        Assert-True (-not $btn.Current.IsEnabled) "未连接时「$name」按钮禁用" "IsEnabled=$($btn.Current.IsEnabled)"
    }
}
# 展开态几何（2026-11-06 第二轮）：宽度 == 整条命令区（「常用命令」左边缘 →「清空日志」右边缘），
# 顶边/高度仍与命令输入框一致；±3 设备像素容忍边框/取整。
if ($cmdOff -and $searchOff) {
    $leftBtn = $null; $rightBtn = $null
    try { $leftBtn = Get-UiButton '常用命令' } catch { $leftBtn = $null }
    try { $rightBtn = Get-UiButton '清空日志' } catch { $rightBtn = $null }
    if ($leftBtn -and $rightBtn) {
        $rl = $leftBtn.Current.BoundingRectangle
        $rr = $rightBtn.Current.BoundingRectangle
        $rc = $cmdOff.Current.BoundingRectangle
        $rs = $searchOff.Current.BoundingRectangle
        Assert-True ([Math]::Abs($rl.X - $rs.X) -le 3 `
            -and [Math]::Abs(($rr.X + $rr.Width) - ($rs.X + $rs.Width)) -le 3 `
            -and [Math]::Abs($rc.Y - $rs.Y) -le 3 `
            -and [Math]::Abs($rc.Height - $rs.Height) -le 3) `
            '未连接时搜索框展开到整条命令区（左右贴边、高度同命令输入框）' `
            ("area=({0}..{1}) cmdY={2} h={3} search=({4},{5},{6},{7})" -f $rl.X, ($rr.X + $rr.Width), $rc.Y, $rc.Height, $rs.X, $rs.Y, $rs.Width, $rs.Height)
    }
}
# 对照：和"连不连都得在"的控件分开验，免得整块界面塌了也被当成"收起来了"
foreach ($name in @('连接', '断开', '添加账号')) {
    $btn = $null
    try { $btn = Get-UiButton $name } catch { $btn = $null }
    Assert-True ($null -ne $btn) "未连接时「$name」按钮仍在"
}
Assert-True ($null -ne (Find-ById $script:AppRoot 'LogScrollHost')) '未连接时日志区仍在'

# 2) 功能开关仍在同一行且可读状态
# （自动补充 2026-10-03、自动行走 2026-10-04 追加在行尾，同为"文字 + 开关"）
foreach ($n in @('自动砍怪', '鼠标控制', '自动钓鱼', '自动重连', '自动补充', '自动行走')) {
    $ok = $true
    try { $null = Get-ToggleOn $n } catch { $ok = $false }
    Assert-True $ok "开关存在: $n"
}
Assert-True ((Get-ToggleOn '自动砍怪') -eq $false) '砍怪开关默认关'

# 3) 下拉按钮：砍怪参数
$opened = Open-Flyout '自动砍怪选项'
Assert-True $opened '砍怪参数下拉按钮可展开'
# 注意：测试会把 MCCX 抢到前台，用户此刻在别的窗口打字/点击时，
# 按键会灌进弹层里已获焦点的输入框（实测 '3.0d'、'3.0 '、'3.0l '），
# 切窗还会把弹层轻触关掉（读不到控件）。所以读不到就重新展开再读，最多 4 次。
$val = ''
for ($i = 0; $i -lt 4; $i++) {
    if ($i -gt 0) { $null = Open-Flyout '自动砍怪选项'; Start-Sleep -Milliseconds 300 }
    try { $val = Get-EditValue (Get-UiEdit '攻击距离') } catch { $val = '' }
    if ($val -match '^\d+(\.\d+)?$') { break }
}
# 2026-10-04：参数跟随账号存取后，距离显示的是该账号上次保存的值（Num(3.0)='3'），
# 不再一定是字面 '3.0'；这里只要求面板可见、值是数字（真正的功能断言在 g_* 套件里）。
$defaultOk = ($val -match '^\d+(\.\d+)?$')
Assert-True $defaultOk '砍怪参数面板可见且距离可读' "实际='$val'"
if ($val -like '3.0*' -and $val -ne '3.0') {
    Write-Host "  ! 检测到外部按键灌入，已自愈回 3.0"
    try { Set-Edit (Get-UiEdit '攻击距离') '3.0' } catch { }
}

# 4) 下拉按钮：鼠标参数（含左/右键两组参数）
$opened2 = Open-Flyout '鼠标控制选项'
Assert-True $opened2 '鼠标参数下拉按钮可展开'
$hasCombo = $true
try { $null = Get-UiCombo '左键模式' } catch { $hasCombo = $false }
Assert-True $hasCombo '鼠标参数面板可见（含“左键模式”）'

# 5) 下拉按钮：重连参数
$opened3 = Open-Flyout '自动重连选项'
Assert-True $opened3 '重连参数下拉按钮可展开'
$recon = ''
for ($i = 0; $i -lt 4; $i++) {
    if ($i -gt 0) { $null = Open-Flyout '自动重连选项'; Start-Sleep -Milliseconds 300 }
    try { $recon = Get-EditValue (Get-UiEdit '重连次数') } catch { $recon = '' }
    if ($recon -match '^\d+$') { break }
}
# 2026-10-04：默认次数自 2026-10-03 起改为 0（=无限），且次数随账号保存，
# 面板里显示的是该账号的存量值；这里只要求面板可见、值是数字。
Assert-True ($recon -match '^\d+$') '重连参数面板可见且次数可读（默认 0=无限）' "实际='$recon'"

# 6) 钓鱼参数下拉（2026-10-04 起有参数：收杆检测 / 抛竿超时 / 重抛间隔）
$opened4 = Open-Flyout '自动钓鱼选项'
Assert-True $opened4 '钓鱼参数下拉按钮可展开'
$ftime = ''
for ($i = 0; $i -lt 4; $i++) {
    if ($i -gt 0) { $null = Open-Flyout '自动钓鱼选项'; Start-Sleep -Milliseconds 300 }
    try { $ftime = Get-EditValue (Get-UiEdit '钓鱼抛竿超时') } catch { $ftime = '' }
    if ($ftime -like '300*') { break }
}
$faultOk = ($ftime -like '300*')
Assert-True $faultOk '钓鱼参数面板可见且默认抛竿超时=300' "实际='$ftime'"
if ($faultOk -and $ftime -ne '300') {
    Write-Host "  ! 检测到外部按键灌入，已自愈回 300"
    try { Set-Edit (Get-UiEdit '钓鱼抛竿超时') '300' } catch { }
}
$fdelay = ''
try { $fdelay = Get-EditValue (Get-UiEdit '钓鱼重抛间隔') } catch { $fdelay = '' }
Assert-True ($fdelay -like '0.4*') '钓鱼重抛间隔默认=0.4' "实际='$fdelay'"

Close-AllFlyouts
Start-Sleep -Milliseconds 300

# 7) 日志复制（2026-10-05 改版）：底部"复制日志"按钮已按用户要求移除，
#    改成"行内自由框选文字 + 右键菜单复制"。这里断言：
#    · 那个按钮确实没了（点不到、名字也搜不到）；
#    · 日志行是只读 TextBox（能在行内框选，UIA 里是 Edit 控件）；
#    · 至少有一行文本能被读出来（证明框选/复制的载体还在）。
try { Set-Clipboard -Value '' } catch { }
$copyBtn = $null
try { $copyBtn = Get-UiButton '复制日志' } catch { $copyBtn = $null }
Assert-True ($null -eq $copyBtn) '底部"复制日志"按钮已移除（改用选中文字后右键复制）'

$logList = Get-LogList
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    $script:AE::ControlTypeProperty, $script:CT::Edit)
# 2026-10-06：日志区是 ItemsRepeater，没有 ListView 那样的"列表项"UIA 节点，
# Get-ListItems 取不到东西；直接找 LogScrollHost 下的 Edit（每行一个只读 TextBox）。
$logEdits = $logList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
Assert-True ($logEdits.Count -gt 0) '日志行是可框选的只读输入框（行内自由选择文字）' ("LogScrollHost 下 Edit 数量=" + $logEdits.Count)

$firstLogText = ''
if ($logEdits.Count -gt 0) { $firstLogText = Get-EditValue $logEdits.Item(0) }
Assert-True (-not [string]::IsNullOrWhiteSpace($firstLogText)) '第一行日志文本能从只读输入框里读出来' $firstLogText
Assert-True ((Get-RecentLogText -Max 5).Length -gt 0) '日志文本仍能读到（右键复制的内容来源）'

$alive = [bool](Get-Process -Name MCCX -ErrorAction SilentlyContinue)
Assert-True $alive '展开参数面板后程序未崩溃'

Restore-Foreground

$n = Get-FailureCount
Write-Host "FAILURES=$n"
exit ([int]($n -gt 0))
