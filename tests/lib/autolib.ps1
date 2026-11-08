# MccX 自动化测试公共库：UIA 操作 + RCON 控制测试服
# 约定：所有提示用 Write-Host（不进管道），函数只 return 真正的返回值，
#       否则 $x = Fn 会拿到 Object[] 导致 Assert-True 报类型错误。
# 注意：本文件含中文，必须以 UTF-8 with BOM 保存。
# 控制台输出统一 UTF-8：否则中文经管道回传会变成乱码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\uia.ps1"

$script:AppRoot = $null
$script:LogListEl = $null
$script:AppExe = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64')\MCCX.exe"
$script:RconHost = '127.0.0.1'
$script:RconPort = 25575
$script:RconPass = 'mccxtest'
$script:TestPlayer = 'MccXBot'
$script:Failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param($Condition, [string]$Name, [string]$Detail = '')
    $ok = [bool]$Condition
    if ($ok) {
        Write-Host "PASS  $Name"
    } else {
        $script:Failures.Add($Name)
        Write-Host "FAIL  $Name  $Detail"
    }
}

function Get-FailureCount { return $script:Failures.Count }

function Get-JavaMajorVersion {
    param([Parameter(Mandatory = $true)][string]$JavaExe)
    # java -version 把版本写 stderr；直接 2>&1 在 $ErrorActionPreference='Stop' 下会被当终止错误吞掉，
    # 因此走进程重定向读取，不依赖 PowerShell 的流语义。
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $JavaExe
        $psi.Arguments = '-version'
        $psi.RedirectStandardError = $true
        $psi.RedirectStandardOutput = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $p = [System.Diagnostics.Process]::Start($psi)
        $text = $p.StandardError.ReadToEnd() + $p.StandardOutput.ReadToEnd()
        $p.WaitForExit()
    } catch { return 0 }
    $m = [regex]::Match($text, 'version\s+"?(\d+)')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 0
}

function Get-TestServerJava {
    # 本地测试服 26.1 需要 Java 25（class 文件版本 69）；Java 21 会 UnsupportedClassVersionError。
    # 解析顺序：MCCX_TEST_JAVA 环境变量 → JAVA_HOME → 常见 JDK 安装目录（取 >= 25 的）→ PATH 中的 java。
    $cands = New-Object System.Collections.Generic.List[string]
    if ($env:MCCX_TEST_JAVA) { $cands.Add($env:MCCX_TEST_JAVA) }
    if ($env:JAVA_HOME) { $cands.Add((Join-Path $env:JAVA_HOME 'bin\java.exe')) }

    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA)) {
        if (-not $base) { continue }
        $roots.Add((Join-Path $base 'Java'))
        $roots.Add((Join-Path $base 'Eclipse Adoptium'))
        $roots.Add((Join-Path $base 'Microsoft'))
        $roots.Add((Join-Path $base 'Zulu'))
    }
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($d in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            $cands.Add((Join-Path $d.FullName 'bin\java.exe'))
        }
    }

    foreach ($c in $cands) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        if ((Get-JavaMajorVersion -JavaExe $c) -ge 25) { return $c }
    }
    $pathJava = (Get-Command java -ErrorAction SilentlyContinue).Source
    if ($pathJava) { return $pathJava }
    throw '找不到 Java，无法启动本地测试服（26.1 需要 Java 25）'
}

function Stop-App {
    $ps = @(Get-Process -Name MCCX -ErrorAction SilentlyContinue)
    foreach ($p in $ps) { try { $p.Kill() } catch { } }

    # 必须等进程真的没了再返回：固定睡 2 秒不够时，Start-App 会看到“还有进程在跑”
    # 于是不启新实例，随后的窗口等待必然超时（等待 MCCX 窗口超时）。
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-Process -Name MCCX -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 500

    $script:AppRoot = $null
    $script:LogListEl = $null
}

function Start-App {
    param([int]$TimeoutMs = 30000)
    if (-not (Get-Process -Name MCCX -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $script:AppExe -WorkingDirectory (Split-Path $script:AppExe)
    }
    $script:AppRoot = Wait-AppWindow -TimeoutMs $TimeoutMs
    Set-EnglishInputLayout
    $script:LogListEl = $null
    Start-Sleep -Seconds 2
    return $script:AppRoot
}

function Restart-App {
    Stop-App
    return Start-App
}

# ---------- UI 定位 ----------

function Find-UiElement {
    # 主窗口子树找不到时回退到桌面树：Flyout/下拉弹层挂在 PopupWindowSiteBridge 下，
    # 不属于主窗口子树，必须按 进程 + 控件类型 + 名称 跨窗口精确定位。
    param($ControlType, [string]$Name)
    $procId = Get-MainProcId
    if (-not $procId) { return $null }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)))
    return $script:AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Get-MainProcId {
    # 多开时同一 exe 有多个进程（每个账号一个 --runner 子进程），
    # 只能用“带主窗口的那个”当过滤条件，取 First 1 会拿到子进程 pid，导致什么都查不到。
    try {
        if ($script:AppRoot) { return [int]$script:AppRoot.Current.ProcessId }
    } catch { }
    $p = Get-Process -Name MCCX -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { return [int]$p.Id }
    return 0
}

function Get-UiButton {
    param([string]$Name)
    $b = Find-Button $script:AppRoot $Name
    if (-not $b) { $b = Find-UiElement $script:CT::Button $Name }
    if (-not $b) { throw "找不到按钮: $Name" }
    return $b
}

function Get-UiToggle {
    param([string]$Name)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Button)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { $el = Find-UiElement $script:CT::Button $Name }
    if (-not $el) { throw "找不到开关: $Name" }
    $null = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $el
}

function Get-UiEdit {
    param([string]$Name)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Edit)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { $el = Find-UiElement $script:CT::Edit $Name }
    if (-not $el) { throw "找不到输入框: $Name" }
    return $el
}

function Get-UiCombo {
    param([string]$Name)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::ComboBox)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { $el = Find-UiElement $script:CT::ComboBox $Name }
    if (-not $el) { throw "找不到下拉框: $Name" }
    return $el
}

function Set-UiValue {
    param([string]$Name, [string]$Value)
    $el = Get-UiEdit $Name
    Set-Edit $el $Value
}

function Get-UiText {
    param([string]$Name)
    $el = Get-UiEdit $Name
    return Get-EditValue $el
}

# ---------- 按钮 ----------

function Invoke-UiButton {
    # 带“按钮可用性等待 + 重定位 + 报错定位”的安全点击
    param([string]$Name, [int]$WaitEnabledMs = 15000, [bool]$Optional = $false)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastErr = ''
    while ($sw.ElapsedMilliseconds -lt $WaitEnabledMs) {
        $b = Get-UiButton $Name
        if ($b.Current.IsEnabled) {
            try {
                Write-Host "  [ui] click '$Name'"
                Invoke-Element $b
                return
            } catch {
                $lastErr = $_.Exception.Message
                Write-Host "  [ui] click '$Name' raised: $lastErr"
            }
        } else {
            $lastErr = 'IsEnabled=false'
        }
        Start-Sleep -Milliseconds 300
    }
    if ($Optional) {
        Write-Host "  [ui] optional click '$Name' skipped ($lastErr)"
        return
    }
    throw "按钮 [$Name] 无法点击: $lastErr"
}

# ---------- 开关 ----------

function Get-ToggleOn {
    param([string]$Name)
    $el = Get-UiToggle $Name
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}

function Set-ToggleOn {
    param([string]$Name, [bool]$On)
    $el = Get-UiToggle $Name
    # 不抢焦点：UIA TogglePattern 不需要前台，抢了会把用户正在输入的按键灌进本程序
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    for ($i = 0; $i -lt 6; $i++) {
        $now = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        if ($now -eq $On) { return }
        $tp.Toggle()
        Start-Sleep -Milliseconds 250
    }
    $now = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    if ($now -ne $On) { throw "开关 [$Name] 未能切换到 $On" }
}

# 用 UIA 关掉所有已展开的参数下拉。
# 不用 SendKeys ESC：那要求本程序在前台，否则 ESC 会发给用户正在玩的游戏窗口。
function Close-AllFlyouts {
    if (-not $script:AppRoot) { return }
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)
    $btns = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $btns) {
        try {
            $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) {
                $ec.Collapse()
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 400
}

# ---------- 下拉框 ----------
# 实测：WinUI ComboBox 展开后，选项在独立的 PopupWindowSiteBridge Pane 里，
# 主窗口子树找不到，必须从桌面树按 ClassName + Name 定位弹层里的 ListItem。

function Find-ComboPopupItem {
    param([string]$ComboName, [string]$ItemName)

    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $itemCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $ItemName)))

    $paneCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $ComboName)))
    $pane = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $paneCond)
    if ($pane) {
        $hit = $pane.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
        if ($hit) { return $hit }
    }

    $paneCond2 = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')
    $panes = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $paneCond2)
    foreach ($p in $panes) {
        $hit = $p.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
        if ($hit) { return $hit }
    }

    $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, (Get-MainProcId))
    $whole = New-Object System.Windows.Automation.AndCondition($itemCond, $pidCond)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $whole)
}

function Set-ComboItem {
    param([string]$ComboName, [string]$ItemName)
    $combo = Get-UiCombo $ComboName
    # 不抢焦点（同 Set-ToggleOn）

    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
    }
    Start-Sleep -Milliseconds 700

    try {
        $picked = Find-ComboPopupItem -ComboName $ComboName -ItemName $ItemName
        if (-not $picked) {
            throw "下拉项 [$ItemName] 在弹层中未找到"
        }
        Select-Item $picked
        Start-Sleep -Milliseconds 300

        $sip = $picked.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if (-not $sip.Current.IsSelected) {
            throw "下拉框 [$ComboName] 选择 [$ItemName] 未生效 (IsSelected=false)"
        }
        Write-Host "  [ui] combo '$ComboName' -> '$ItemName'"
    } finally {
        try {
            $ec2 = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec2.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
                $ec2.Collapse()
            }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
}

# ---------- 账号删除（新版流程） ----------

function Remove-AccountViaUi {
    # 删除流程已改版：底部“删除”按钮移除，改成账号项右侧的叉号（AutomationId 同模板，
    # 名称=“删除账号”）+ ContentDialog 二次确认。返回 $false 表示该账号已不在列表里。
    param([string]$Name, [int]$TimeoutMs = 20000)

    $list = Find-ById $script:AppRoot 'AccountList'
    if (-not $list) { throw '找不到账号列表 AccountList' }

    $item = Find-ListItemByText $list $Name
    if (-not $item) { return $false }

    Select-Item $item
    Start-Sleep -Milliseconds 500

    # 叉号就在这个账号项的模板里，绝不能按名字全局找（否则会点到别的账号）
    $btn = $null
    for ($i = 0; $i -lt 10 -and -not $btn; $i++) {
        $btn = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '删除账号')))))
        if (-not $btn) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $btn) { throw "账号项 [$Name] 里找不到删除叉号（删除账号）" }

    Invoke-Element $btn

    # 二次确认弹窗：主按钮文案“删除”
    $ok = $null
    for ($i = 0; $i -lt [Math]::Max(8, $TimeoutMs / 250) -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 250
        $ok = Find-UiElement $script:CT::Button '删除'
    }
    if (-not $ok) { throw "点叉号后没有弹出删除确认窗口（账号 $Name）" }

    # 弹窗必须是这个账号的：账号名出现在正文里。对不上就绝不能按“删除”，
    # 否则会误删别的账号（这条同时能揪出“叉号点错行”这类问题）。
    $dlg = $null
    foreach ($t in $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)))) {
        $n = [string]$t.Current.Name
        if ($n.Contains('确定删除账号')) { $dlg = $n; break }
    }
    if ($null -eq $dlg) { throw "删除确认弹窗读不到正文（账号 $Name）" }
    if (-not $dlg.Contains($Name)) {
        throw "删除确认弹窗与目标账号不符，拒绝确认：目标=$Name 弹窗=$dlg"
    }

    Invoke-Element $ok
    return $true
}

# ---------- 日志 ----------

function Get-LogList {
    # 2026-10-05：日志容器从 ListView 换成了 ScrollViewer(LogScrollHost) + ItemsRepeater
    # （ListView 会在行内抢指针，导致行内 TextBox 拖不出选区）。
    # 注意 **ItemsRepeater 自己没有 UIA 节点**（WinUI 不给它做 automation peer），
    # 按 AutomationId=LogList 找会扑空；它的父 ScrollViewer 有节点，而且日志行（Edit）
    # 都在它下面，所以这里返回 LogScrollHost，找不到再退回 LogList。
    if (-not $script:LogListEl) {
        # 重试几次再放弃：切"多开"/页面重建的瞬间，LogScrollHost 可能短暂不在 UIA 树里。
        # 一次找不到就抛会**打断整个用例**（2026-10-18 出现过：p_multi 的 D1 读日志时抛
        # '找不到日志区域'，用例直接中断，后面的收尾步骤全没跑）。
        for ($i = 0; $i -lt 8 -and -not $script:LogListEl; $i++) {
            try { $script:LogListEl = Find-ById $script:AppRoot 'LogScrollHost' } catch { }
            if (-not $script:LogListEl) {
                try { $script:LogListEl = Find-ById $script:AppRoot 'LogList' } catch { }
            }
            if (-not $script:LogListEl) { Start-Sleep -Milliseconds 250 }
        }
        if (-not $script:LogListEl) { throw '找不到日志区域（LogScrollHost / LogList）' }
    }
    return $script:LogListEl
}

function Get-RecentLogText {
    param([int]$Max = 60)
    $log = Get-LogList

    # 2026-10-05：日志容器从 ListView 换成 ItemsRepeater（为了让行内 TextBox 能正常框选文字，
    # ListView 会在行内抢指针做自己的选中）。于是条目不再是 ListItem，改为收集所有 Edit
    # （每行一个只读 TextBox）并按 ValuePattern 取文本。
    #
    # 2026-10-18：这里以前还有一段"先把滚动宿主（AutomationId=LogScrollHost）滚到底再读"的代码，
    # 但它是**死代码**：变量名写成了 `$host`，而 PowerShell 的 `$Host` 是只读自动变量，
    # 赋值必抛异常、又被紧跟的 catch 吞掉，所以那一步从来没生效过。查明后**决定不补**：
    #   · 视图贴底跟随（默认）时 UIA 读到的本来就是最新那一段，"再滚一次"没有额外收益；
    #   · 强行 SetScrollPercent(-1, 100) 只会把可读窗口收窄成"最新一屏"，反而更容易漏掉
    #     "刚写入、正好被挤到窗口上方"的行；
    #   · 虚拟化列表的滚动范围是估算值，滚一次不一定真到底，还会把每次轮询拖慢约 0.4 秒。
    # 需要"往回看老几行"时走 Get-LogTextBackward（Assert-Log 只在没找到时才翻）。
    # 单次读的上限就是"当前实体化的一屏"（实测约 58 行）：确实要放宽可以调
    # ItemsRepeater.VerticalCacheLength（App 侧，代价是常驻容器变多）。
    Start-Sleep -Milliseconds 250

    $cEdit = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Edit)
    $edits = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cEdit)
    $lines = @()
    foreach ($e in $edits) {
        $v = ''
        try {
            $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $v = [string]$vp.Current.Value
        } catch { $v = [string]$e.Current.Name }
        if ($v) { $lines += $v }
    }

    # 万一一个 Edit 都没找到（容器结构再变），退回按条目读，保底不把用例整体带崩
    if ($lines.Count -eq 0) { $lines = @(Get-ItemTexts $log) }

    if ($lines.Count -gt $Max) {
        $lines = $lines[($lines.Count - $Max)..($lines.Count - 1)]
    }
    return ($lines -join "`n")
}

function Clear-Log {
    # 2026-10-16：命令栏（含"清空日志"）改成只在连上服务器后才显示；没连上时按钮在 UIA 里根本不存在。
    # 2026-11-06：离线不再收起（命令栏只保留布局、子控件禁用），所以按钮节点仍在，必须按 IsEnabled 判断。
    # 未连接时清不了日志，这里直接跳过并留一行说明——调用方别再拿"清完日志"当唯一依据。
    $btn = $null
    try { $btn = Get-UiButton '清空日志' } catch { $btn = $null }
    if (-not $btn -or -not $btn.Current.IsEnabled) {
        Write-Host '  [ui] 未连接：清空日志按钮不可用，跳过清日志'
        return
    }
    Invoke-Element $btn
    Start-Sleep -Milliseconds 400
}

function Wait-LogContains {
    param([string]$Pattern, [int]$TimeoutMs = 30000, [string]$What = '')
    if (-not $What) { $What = "日志出现 [$Pattern]" }
    return Wait-For -TimeoutMs $TimeoutMs -What $What -Probe {
        $txt = Get-RecentLogText
        if ($txt -match $Pattern) { $txt } else { $null }
    }
}

function Get-LogTextBackward {
    # 把"当前一屏"和"往回翻时经过的几屏"拼起来 —— 用于断言"某行确实进过日志"。
    # 为什么需要：日志区是虚拟化列表，UIA 一次只读得到"已实体化"的行 —— 实测宽 731 px 的宿主上
    # 约 53 行（可见一屏约 26 行 + VerticalCacheLength 多留的那部分）。日志一旦长过这个宽度，
    # 被顶上去的老行单次读就永远读不到（2026-10-18 批量跑 g_* / p_multi 的假红就是这个机制）。
    # 2026-10-18 实测确认这条兜底成立：日志长过视口时 ScrollPattern 是可滚动的，
    # `Scroll(NoAmount, LargeDecrement)` 每叫一次真的往回挪约一屏、让更多老行实体化
    # （实测 pct 100 → 84 → 65 时，能读到的行数 53 → 79 → 85）；反过来 SetScrollPercent(-1,100)
    # 会把可读行数压回 54 —— 这也是"平时不要硬滚到底"的实测依据。
    # 日志没长过视口时本来就全读得到（此时 VerticallyScrollable=False、Scroll 会抛异常，已兜住）。
    # 翻完滚回底部，不留半路状态；只在"正常读没找到"时才调用 —— happy path 一次都不用翻。
    param([string]$StopPattern = '', [int]$Steps = 8, [int]$SleepMs = 200)
    $chunks = @()
    $chunks += (Get-RecentLogText -Max 100000)
    if ($StopPattern -and (($chunks -join "`n") -match $StopPattern)) { return ($chunks -join "`n") }

    $logHost = $null
    try { $logHost = Find-ById $script:AppRoot 'LogScrollHost' } catch { }
    if (-not $logHost) { return ($chunks -join "`n") }
    $sp = $null
    try { $sp = $logHost.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern) } catch { }
    if (-not $sp) { return ($chunks -join "`n") }

    try {
        $scrollable = $false
        try { $scrollable = [bool]$sp.Current.VerticallyScrollable } catch { }
        if (-not $scrollable) { return ($chunks -join "`n") }

        $noAmount = [System.Windows.Automation.ScrollAmount]::NoAmount
        $onePageUp = [System.Windows.Automation.ScrollAmount]::LargeDecrement
        for ($i = 0; $i -lt $Steps; $i++) {
            $atTop = $false
            try { $atTop = ($sp.Current.VerticalScrollPercent -le 0.5) } catch { }
            if ($atTop) { break }
            try { $sp.Scroll($noAmount, $onePageUp) } catch { break }
            Start-Sleep -Milliseconds $SleepMs
            $chunks += (Get-RecentLogText -Max 100000)
            if ($StopPattern -and (($chunks -join "`n") -match $StopPattern)) { break }
        }
    } finally {
        try { $sp.SetScrollPercent(-1, 100) } catch { }
        Start-Sleep -Milliseconds 100
    }
    return ($chunks -join "`n")
}

function Assert-Log {
    # 失败信息里带上"最后一次读到多少行、最早一行是什么"：
    # 日志区是虚拟化列表（UIA 只实体化可视窗口，实测约 58 行），失败的典型原因就是
    # "那一行已经被挤出窗口"，有这两个数字一眼就能区分是"没写日志"还是"窗口太小"。
    param([string]$Pattern, [string]$Name, [int]$TimeoutMs = 30000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $last = ''
    $found = $false
    $deepTried = $false
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        # 读日志本身也可能短暂失败（页面重建瞬间）—— 不能让它把用例打断
        try { $last = Get-RecentLogText } catch { }
        if ($last -match $Pattern) { $found = $true; break }
        # 先正常等（刚写进来的行就在最新一屏，最快的路径是直接读到）；
        # 等不到再往回翻一遍（兜住"被后续几十行挤出窗口"的情况），翻完继续等。
        if (-not $deepTried -and $sw.ElapsedMilliseconds -gt 2500) {
            $deepTried = $true
            try {
                $back = Get-LogTextBackward -StopPattern $Pattern
                if ($back -match $Pattern) { $found = $true; break }
            } catch { }
        }
        Start-Sleep -Milliseconds 350
    }
    if ($found) {
        Assert-True $true $Name
        return
    }
    $lines = @($last -split "`n")
    $head = ''
    if ($lines.Count -gt 0) {
        $head = $lines[0]
        if ($head.Length -gt 60) { $head = $head.Substring(0, 60) }
    }
    Assert-True $false $Name ("未匹配: {0}；可读行数={1}，最早一行=[{2}]" -f $Pattern, $lines.Count, $head)
}

function Assert-Connected {
    # 断言"已经连上服务器"：**看状态条**，不再断言日志里那一行「连接成功！」。
    # 为什么改：日志区是虚拟化列表，UIA 只实体化约 58 行；进服瞬间会灌进几十行
    # （本地测试服跑久了更明显 —— 加入时服务端会把聊天记录一起回放），
    # 「连接成功！」经常在第一次读取之前就被挤出可读窗口。2026-10-18 实测同一批 run_g：
    # 07:30 六个用例全绿、11:44 红 3 个、12:30 红 5 个，失败信息全是这类连接断言；
    # 而同一用例单独跑都是绿的 —— 断言本身有固有竞态，不是功能坏了。
    # 日志通道仍有覆盖：紧随其后的 Assert-Log '已进入游戏'，以及 Ensure-Connected 里的
    # Wait-LogContains '已进入游戏'。
    # 参数顺序刻意对齐原来的 `Assert-Log '<pattern>' '<label>' <timeout>`，方便就地替换。
    param([string]$Name = '本地测试服连接成功', [int]$TimeoutMs = 90000)
    $st = ''
    try {
        $st = Wait-For -TimeoutMs $TimeoutMs -What '状态条显示「已连接 …」' -Probe {
            $x = Get-WindowStateText $script:AppRoot
            if ($x -like '已连接 *') { $x } else { $null }
        }
    } catch {
        $st = Get-WindowStateText $script:AppRoot
    }
    Assert-True ($st -like '已连接 *') $Name "state=[$st]"
}

# ---------- 连接状态 ----------

function Ensure-Connected {
    param([int]$TimeoutMs = 90000, [bool]$ClearLogFirst = $true)
    $root = $script:AppRoot
    if (-not $root) { throw 'AppRoot 未初始化，请先 Start-App' }

    if ($ClearLogFirst) { Clear-Log }

    $s = Get-WindowStateText $root
    if ($s -like '已连接 *') { return $s }

    Set-Edit (Find-Edit $root 'ServerBox') '127.0.0.1'
    Set-Edit (Find-Edit $root 'PortBox') '25566'
    Set-Edit (Find-Edit $root 'UserBox') $script:TestPlayer
    Set-Edit (Find-Edit $root 'VersionBox') 'auto'
    Invoke-UiButton '连接'

    $st = Wait-For -TimeoutMs $TimeoutMs -What '连接成功' -Probe {
        $s2 = Get-WindowStateText $root
        if ($s2 -like '已连接 *') { $s2 }
    }
    # 真正进入世界之后才允许发 RCON 布置命令，
    # 否则 execute as <玩家> 匹配不到实体，命令会被服务端静默忽略。
    Wait-LogContains '已进入游戏' 30000 | Out-Null
    return $st
}

function Disconnect-App {
    param([int]$TimeoutMs = 60000)
    $st = Get-WindowStateText $script:AppRoot
    if ($st -like '已连接 *') {
        Invoke-UiButton '断开'
    } else {
        Write-Host "  [ui] skip disconnect (state=$st)"
    }
    Wait-For -TimeoutMs $TimeoutMs -What '断开完成' -Probe {
        $s = Get-WindowStateText $script:AppRoot
        if ($s -eq '未连接') { $s }
    } | Out-Null
}

function Send-MccCommand {
    param([string]$Text)
    $box = Find-Edit $script:AppRoot 'CommandBox'
    Set-Edit $box $Text
    Invoke-UiButton '发送'
    Start-Sleep -Milliseconds 600
}

# ---------- 测试会话（命令栏要连上服务器才可用，要操作这些控件的用例先开一个） ----------
# 2026-10-16 需求：没连上服务器时命令栏（常用命令/输入框/发送/清空日志）和日志搜索框整块收起。
# 2026-11-06 改版：命令栏离线不再收起，而是保留布局 + 子控件禁用（日志搜索框会展开到命令输入框的位置）。
# 所以"要操作命令栏"的用例（g_search / g_new…）仍必须先真的连上一个服务器——只用 UIA 点界面，
# 连的是本机测试服，账号是专用测试账号，结束删掉，不碰用户已有账号（流程与 p_multi / g_walk 同款，
# 那些用例各自留了一份副本，这里收拢一份给新用例用）。只想找控件节点、不点它的用例可以离线跑。

function Test-LocalServerListening {
    param([string]$Port = '25599')
    return [bool](netstat -ano | Select-String ":$Port\s.*LISTENING")
}

# 起本地测试服（26.1 / Java 25）；已经在跑就直接用。端口按 server.properties 里的实际值对齐。
function Start-LocalTestServer {
    param([string]$Port = '25599', [int]$TimeoutSec = 120)
    $dir = Join-Path $PSScriptRoot '..\artifacts\mcserver\26.1'
    if (-not (Test-Path (Join-Path $dir 'server.jar'))) { throw "本地测试服缺失: $dir" }
    $props = Join-Path $dir 'server.properties'
    if (Test-Path $props) {
        Set-Content -Path $props -Value ((Get-Content $props) -replace '^server-port=.*$', "server-port=$Port") -Encoding ASCII
    }
    if (Test-LocalServerListening -Port $Port) {
        Write-Host "  [srv] 本地测试服已在运行 127.0.0.1:$Port"
        return
    }
    $java = Get-TestServerJava
    Start-Process -FilePath $java -ArgumentList '-Xmx1G', '-jar', 'server.jar', 'nogui' `
        -WorkingDirectory $dir -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dir 'test.out.log') `
        -RedirectStandardError (Join-Path $dir 'test.err.log')
    for ($i = 0; $i -lt [int]($TimeoutSec / 2); $i++) {
        Start-Sleep -Seconds 2
        if (Test-LocalServerListening -Port $Port) {
            Write-Host "  [srv] 本地测试服已启动 127.0.0.1:$Port"
            return
        }
    }
    throw "本地测试服启动超时（$Port 未监听）"
}

# 账号列表里每一项的文字（账号名 / 状态 / 地址都在模板的文字里；item 自己的 Name 只是 ViewModel 类型名）
function Get-UiAccountRows {
    $list = Find-ById $script:AppRoot 'AccountList'
    if (-not $list) { return @() }
    $c = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)
    $rows = @()
    foreach ($it in (Get-ListItems $list)) {
        $parts = @()
        foreach ($t in $it.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) { $parts += [string]$t.Current.Name }
        $rows += ($parts -join ' | ')
    }
    return $rows
}

function Add-UiTestAccount {
    param([string]$User, [string]$Server = '127.0.0.1', [string]$Port = '25599')
    Invoke-UiButton '添加账号'
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗出现' -Probe {
        $e = $null; try { $e = Find-ById $script:AppRoot 'NewServerBox' } catch { }
        if ($e) { 'open' } else { $null }
    }
    # 默认勾着"立即连接"：先取消，等会儿自己点连接（也留出"未连接时界面长什么样"的观察窗口）
    $cb = $null
    for ($i = 0; $i -lt 10 -and -not $cb; $i++) {
        $c = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::CheckBox)),
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '立即连接')))
        $cb = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if (-not $cb) { Start-Sleep -Milliseconds 300 }
    }
    if ($cb) {
        $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $tp.Toggle(); Start-Sleep -Milliseconds 300 }
    }
    Set-Edit (Find-Edit $script:AppRoot 'NewServerBox') $Server
    Set-Edit (Find-Edit $script:AppRoot 'NewPortBox') $Port
    Set-Edit (Find-Edit $script:AppRoot 'NewUserBox') $User
    Set-Edit (Find-Edit $script:AppRoot 'NewVersionBox') 'auto'
    $btn = Find-Button $script:AppRoot '添加'
    if (-not $btn) { throw '添加账号弹窗里找不到 [添加] 按钮' }
    Invoke-Element $btn
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗关闭' -Probe {
        $e = $null; try { $e = Find-ById $script:AppRoot 'NewServerBox' } catch { }
        if ($e) { $null } else { 'closed' }
    }
}

# ---------- 账号列表的通用读取 ----------
# 2026-10-18：`Get-AccountListEl` / `Get-AccountCount` 原来是 11 个用例各写一份
# （内容逐字相同），`Wait-AccountCount` 只能用调用方那份 —— 调用方漏定义就直接报
# "无法识别 Get-AccountCount"。收进公共库，用例只管调。

function Get-AccountListEl {
    $l = Find-ById $script:AppRoot 'AccountList'
    if (-not $l) { throw '找不到账号列表 AccountList' }
    return $l
}

function Get-AccountCount { return @(Get-ListItems (Get-AccountListEl)).Count }

function Wait-AccountCount {
    # 账号列表项数回到期望值（轮询等 UI 结算）。
    # 为什么需要：删掉一个账号后 ListView 会重建，UIA 可能**短暂**少读到一个 ListItem，
    # 单点读一次会偶发假红 —— 2026-10-18 `g_refill` 收尾 X1 出现过 after=10 before=11，
    # 而当时账号库文件里 11 个账号一个没少。只容忍"暂时对不上"：真被污染（多/少一个账号）
    # 会一直对不上，超时后返回当前值，X1 断言照样失败。
    param([int]$Expect, [int]$TimeoutMs = 10000)
    try {
        $null = Wait-For -TimeoutMs $TimeoutMs -What "账号数回到 $Expect" -Probe {
            if ((Get-AccountCount) -eq $Expect) { 'ok' } else { $null }
        }
    } catch { }
    return (Get-AccountCount)
}

function Select-UiAccount {
    param([string]$User, [int]$TimeoutMs = 10000)
    $item = $null
    for ($i = 0; $i -lt 10 -and -not $item; $i++) {
        $item = Find-ListItemByText (Find-ById $script:AppRoot 'AccountList') $User
        if (-not $item) { Start-Sleep -Milliseconds 400 }
    }
    if (-not $item) { throw "账号列表里找不到 $User" }
    Select-Item $item
    $null = Wait-For -TimeoutMs $TimeoutMs -What "面板切到 $User" -Probe {
        $v = $null; try { $v = Get-EditValue (Find-Edit $script:AppRoot 'UserBox') } catch { }
        if ($v -eq $User) { $v } else { $null }
    }
}

# 开一个"已连接"的测试会话：起本地测试服 → 清残留测试账号 → 记账号列表基线 → 加账号 → 选中 → 连接。
# 返回的 hashtable 交给 Exit-TestSession 收尾（断开 + 删账号 + 核对列表回到基线）。
function Enter-TestSession {
    param([string]$User = 'MccXUiTest', [string]$Server = '127.0.0.1', [string]$Port = '25599', [int]$TimeoutMs = 120000)
    Start-LocalTestServer -Port $Port
    $list = Find-ById $script:AppRoot 'AccountList'
    if (-not $list) { throw '找不到账号列表 AccountList' }
    if (Find-ListItemByText $list $User) {
        Write-Host "  [clean] 删除残留账号 $User"
        $null = Remove-AccountViaUi $User
        Start-Sleep -Seconds 2
    }
    $baseline = @(Get-UiAccountRows)
    Add-UiTestAccount -User $User -Server $Server -Port $Port
    Select-UiAccount -User $User
    Invoke-UiButton '连接'
    $st = Wait-For -TimeoutMs $TimeoutMs -What "连接 $Server`:$Port" -Probe {
        $s = Get-WindowStateText $script:AppRoot
        if ($s -like '已连接 *') { $s }
    }
    # 真正进世界之后命令栏才算"活"的（和 Ensure-Connected 同一个理由）
    Wait-LogContains '已进入游戏' 60000 | Out-Null
    return @{ User = $User; Baseline = $baseline; StateText = $st }
}

function Exit-TestSession {
    param($Session)
    if (-not $Session) { return }
    try { Disconnect-App } catch { Write-Host ("  [clean] 断开失败: " + $_.Exception.Message) }
    try {
        if (-not (Remove-AccountViaUi $Session.User)) { Write-Host "  [clean] 测试账号 $($Session.User) 已不在列表里" }
    } catch { Write-Host ("  [clean] 删账号失败: " + $_.Exception.Message) }
    Start-Sleep -Seconds 2
    $after = @(Get-UiAccountRows)
    if (($after -join '|') -ne ($Session.Baseline -join '|')) {
        Write-Host '  [clean] 警告：账号列表和基线不一致'
        Write-Host "    before: $($Session.Baseline -join ' / ')"
        Write-Host "    after : $($after -join ' / ')"
    } else {
        Write-Host '  [clean] 账号列表已回到基线'
    }
}

# ---------- 世界/玩家状态（全部走 RCON） ----------

function Get-PlayerHealth {
    # 死亡玩家 Health=0；不在线则拿不到数据
    $r = Invoke-RconPlayer 'data get entity @s Health'
    if ($r -match '([0-9]+(?:\.[0-9]+)?)') { return [double]$Matches[1] }
    return -1.0
}

function Ensure-PlayerAlive {
    $st = Get-WindowStateText $script:AppRoot
    if ($st -notlike '已连接 *') {
        Ensure-Connected | Out-Null
    }

    $h = Get-PlayerHealth
    if ($h -gt 0) { return $h }

    # 玩家带着死亡数据（Health=0）进服：用 MCC 内部命令 /respawn 复活
    Write-Host "  player dead (health=$h) -> sending /respawn"
    Send-MccCommand '/respawn'
    Start-Sleep -Seconds 2
    $h2 = Get-PlayerHealth
    if ($h2 -gt 0) {
        Assert-True $true 'player revived via /respawn' "(health=$h2)"
        return $h2
    }

    Write-Host "  /respawn did not work (health=$h2) -> full reconnect"
    Clear-Log
    Disconnect-App
    Ensure-Connected | Out-Null
    $h3 = Get-PlayerHealth
    Assert-True ($h3 -gt 0) 'player alive after reconnect' "(health=$h3)"
    return $h3
}

function Set-PlayerGamemode {
    param([string]$Mode = 'creative')
    $out = Invoke-Rcon ("gamemode {0} {1}" -f $Mode, $script:TestPlayer)
    Write-Host "  rcon 'gamemode $Mode' -> $out"
    # gamemode 的 RCON 回包偶尔为空，用 playerGameType 回读做权威校验
    # 0=生存 1=创造 2=冒险 3=旁观
    $code = @{ survival = 0; creative = 1; adventure = 2; spectator = 3 }[$Mode]
    $gt = Invoke-RconPlayer 'data get entity @s playerGameType'
    Write-Host "  playerGameType -> $gt"
    return ($gt -match (": ?{0}\s*$" -f $code))
}

function Get-PlayerPos {
    # 返回 @($x,$y,$z)（double），失败返回 $null
    $raw = Invoke-RconPlayer 'data get entity @s Pos'
    if ($raw -match '\[(-?[\d.]+)d,\s*(-?[\d.]+)d,\s*(-?[\d.]+)d\]') {
        return @([double]$Matches[1], [double]$Matches[2], [double]$Matches[3])
    }
    Write-Host "  ! 解析 Pos 失败: $raw"
    return $null
}

function Summon-ZombieNear {
    # 注意：execute as <玩家> run summon ~ ~ ~ 的 ~ 是 RCON 控制台位置（世界出生点），
    # 不是玩家位置，客户端收不到生成包。必须算出绝对坐标。
    param([double]$Dist = 2.0)
    $pos = Get-PlayerPos
    if (-not $pos) { return $false }
    $out = Invoke-Rcon (
        "summon minecraft:zombie {0} {1} {2}" -f ($pos[0] + $Dist), [math]::Floor($pos[1]), $pos[2])
    Write-Host "  rcon summon -> $out"
    return ($out -match 'Summoned')
}

function Set-ServerDifficulty {
    param([string]$Level = 'peaceful')
    $out = Invoke-Rcon ("difficulty {0}" -f $Level)
    Write-Host "  rcon 'difficulty $Level' -> $out"
    return $true
}

function Setup-World {
    # 让玩家进入可安全测试的状态：活着 + 创造 + 和平（清掉残留怪物）
    param([bool]$NeedMobs = $false, [bool]$Night = $false)

    $null = Ensure-PlayerAlive
    $gm = Set-PlayerGamemode 'creative'
    Assert-True $gm 'set creative mode (rcon output checked)'

    $null = Set-ServerDifficulty 'peaceful'
    if ($Night) { $null = Invoke-Rcon 'time set night' }
    if ($NeedMobs) {
        $null = Set-ServerDifficulty 'easy'
        $null = Invoke-Rcon 'time set night'
    }
    Start-Sleep -Milliseconds 800
}

function Set-TestArena {
    # 高空开阔平台：四周是空气，避免“召唤的怪物卡进方块窒息”“脚下没立足点”等干扰。
    # 用绝对坐标（execute as 的 ~ 是 RCON 控制台位置，绝不能用）。
    $x = -345; $y = 110; $z = -443

    foreach ($dx in 0..3) {
        foreach ($dz in -1..1) {
            $null = Invoke-Rcon ("setblock {0} {1} {2} stone" -f ($x + $dx), ($y - 1), ($z + $dz))
        }
    }

    $out = Invoke-Rcon ("execute as MccXBot run tp @s {0} {1} {2} facing {3} {4} {5}" -f `
        $x, $y, $z, ($x + 1.0), ($y + 1.62), $z)
    Write-Host "  arena tp -> [$out]"
    Start-Sleep -Milliseconds 900

    $pos = Get-PlayerPos
    if ($pos) {
        Write-Host ("  arena pos -> ({0}, {1}, {2})" -f $pos[0], $pos[1], $pos[2])
        if ([math]::Abs($pos[1] - $y) -gt 1) {
            Write-Host "  ! 场地传送到位失败，重试一次"
            $out = Invoke-Rcon ("execute as MccXBot run tp @s {0} {1} {2}" -f $x, $y, $z)
            Write-Host "  arena tp retry -> [$out]"
            Start-Sleep -Milliseconds 900
        }
    }
    return $true
}

# ---------- RCON ----------

function Invoke-Rcon {
    param([string]$Command, [int]$TimeoutMs = 5000)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($script:RconHost, $script:RconPort)
    } catch {
        $client.Close()
        throw "RCON 连接失败: $_"
    }

    $stream = $client.GetStream()
    $stream.ReadTimeout = $TimeoutMs
    $enc = [System.Text.Encoding]::UTF8

    function Write-RconPacket([int]$id, [int]$type, [string]$body) {
        $b = $enc.GetBytes($body)
        $len = 4 + 4 + $b.Length + 2
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([int]$len)
        $bw.Write([int]$id)
        $bw.Write([int]$type)
        $bw.Write($b)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Flush()
        $buf = $ms.ToArray()
        $stream.Write($buf, 0, $buf.Length)
        $stream.Flush()
        $bw.Dispose()
        $ms.Dispose()
    }

    function Read-RconPacket {
        $head = New-Object byte[] 4
        $read = 0
        while ($read -lt 4) {
            $n = $stream.Read($head, $read, 4 - $read)
            if ($n -le 0) { throw 'RCON 连接被关闭' }
            $read += $n
        }
        $len = [BitConverter]::ToInt32($head, 0)
        if ($len -lt 10 -or $len -gt 4110) { throw "RCON 包长度异常: $len" }
        $payload = New-Object byte[] $len
        $read = 0
        while ($read -lt $len) {
            $n = $stream.Read($payload, $read, $len - $read)
            if ($n -le 0) { throw 'RCON 数据包被截断' }
            $read += $n
        }
        $rid = [BitConverter]::ToInt32($payload, 0)
        $rtype = [BitConverter]::ToInt32($payload, 4)
        $bodyLen = $len - 10
        $body = ''
        if ($bodyLen -gt 0) { $body = $enc.GetString($payload, 8, $bodyLen) }
        return @{ Id = $rid; Type = $rtype; Body = $body }
    }

    try {
        Write-RconPacket 1 3 $script:RconPass
        $auth = Read-RconPacket
        if ($auth.Id -eq -1) { throw 'RCON 认证失败' }

        Write-RconPacket 2 2 $Command
        $text = ''
        $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $r = Read-RconPacket
            } catch {
                break
            }
            if ($r.Id -eq 2) {
                $text += $r.Body
            }
            if (-not $stream.DataAvailable) {
                # 服务端可能先回一个空包再回正文，静默窗口要够长
                Start-Sleep -Milliseconds 350
                if (-not $stream.DataAvailable) { break }
            }
        }
        return $text
    } finally {
        try { $stream.Dispose() } catch { }
        $client.Close()
    }
}

function Invoke-RconPlayer {
    param([string]$Command, [int]$TimeoutMs = 5000)
    return Invoke-Rcon -Command ("execute as {0} run {1}" -f $script:TestPlayer, $Command) -TimeoutMs $TimeoutMs
}
