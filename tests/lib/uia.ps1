# 控制台输出统一 UTF-8：否则中文经管道回传会变成乱码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not ('MccXUi.Win32' -as [type])) {
    Add-Type -Namespace MccXUi -Name Win32 -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")]
public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
[DllImport("user32.dll")]
public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[DllImport("kernel32.dll")]
public static extern uint GetCurrentThreadId();
'@
}

$script:CT = [System.Windows.Automation.ControlType]
$script:AE = [System.Windows.Automation.AutomationElement]
$script:Hwnd = [IntPtr]::Zero
$script:PrevFg = [IntPtr]::Zero

function Set-Foreground {
    # 抢焦点前记住原来的前台窗口，交给 Restore-Foreground 还回去：
    # 用户可能正玩着 MC，焦点被测试抢走后，他按住的方向键/空格会灌进 MccX 的输入框。
    if ($script:PrevFg -eq [IntPtr]::Zero) {
        $script:PrevFg = [MccXUi.Win32]::GetForegroundWindow()
    }
    if ($script:Hwnd -ne [IntPtr]::Zero) {
        [void][MccXUi.Win32]::ShowWindow($script:Hwnd, 9)   # SW_RESTORE
        [void][MccXUi.Win32]::SetForegroundWindow($script:Hwnd)
        Start-Sleep -Milliseconds 200
    }
}

function Save-Foreground {
    # 记录测试开始时的前台窗口，结束时若焦点被本程序抢走就还回去
    $script:PrevFg = [MccXUi.Win32]::GetForegroundWindow()
}

function Restore-Foreground {
    if ($script:PrevFg -eq [IntPtr]::Zero) { return }
    $now = [MccXUi.Win32]::GetForegroundWindow()
    if ($now -eq $script:Hwnd -or $now -eq [IntPtr]::Zero) {
        [void][MccXUi.Win32]::SetForegroundWindow($script:PrevFg)
    }
    $script:PrevFg = [IntPtr]::Zero
}

function Get-ForegroundProcess {
    $h = [MccXUi.Win32]::GetForegroundWindow()
    $pid2 = 0
    [void][MccXUi.Win32]::GetWindowThreadProcessId($h, [ref]$pid2)
    try { return (Get-Process -Id $pid2).ProcessName } catch { return '?' }
}

function Get-AppWindow {
    param([string]$ProcName = 'MCCX')
    $p = Get-Process -Name $ProcName -ErrorAction Stop |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "找不到 $ProcName 的主窗口" }
    $script:Hwnd = $p.MainWindowHandle
    return $AE::FromHandle($p.MainWindowHandle)
}

function Wait-AppWindow {
    param([string]$ProcName = 'MCCX', [int]$TimeoutMs = 20000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try { return (Get-AppWindow $ProcName) } catch { Start-Sleep -Milliseconds 300 }
    }
    throw "等待 $ProcName 窗口超时"
}

function Find-ById {
    param($Root, [string]$Id)
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-ByName {
    param($Root, [string]$Name)
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-Button {
    param($Root, [string]$Content)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Button)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Content)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Find-Edit {
    param($Root, [string]$AutomationId)
    $el = Find-ById $Root $AutomationId
    if (-not $el) { throw "找不到输入框 $AutomationId" }
    return $el
}

function Set-Edit {
    # 实测：ValuePattern.SetValue 在 WinUI TextBox 上稳定；SendKeys 会丢键，不可用。
    # 这里加上回读强校验，写不进去就直接报错，避免“静默写空值”导致测试误判。
    # 不抢前台焦点：用户可能正在玩游戏，抢了焦点他按的键就会灌进这些输入框。
    # ValuePattern.SetValue 不依赖焦点，这里连 SetFocus 都不做（SetFocus 在部分窗口会顺带激活窗口）。
    param($El, [string]$Text)
    Start-Sleep -Milliseconds 150

    $vp = $El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($Text)
    Start-Sleep -Milliseconds 250

    $got = Get-EditValue $El
    if ($got -ne $Text) {
        Start-Sleep -Milliseconds 300
        $vp.SetValue($Text)
        Start-Sleep -Milliseconds 300
        $got = Get-EditValue $El
    }
    if ($got -ne $Text) { throw "写入输入框失败: 期望 '$Text' 实际 '$got'" }
}

function Get-EditValue {
    # 优先用 TextPattern 读真实文本，ValuePattern 在 WinUI 上偶尔返回脏值
    param($El)
    try {
        $tp = $El.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
        $t = ($tp.DocumentRange.GetText(-1)).TrimEnd([char]13, [char]10)
        if ($t) { return $t }
    } catch { }
    try {
        $vp = $El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return [string]$vp.Current.Value
    } catch { }
    return ''
}

function Invoke-Element {
    param($El)
    $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $ip.Invoke()
    Start-Sleep -Milliseconds 200
}

function Get-ListItems {
    param($ListEl)
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::ListItem)
    return $ListEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Get-ItemTexts {
    param($ListEl)
    $items = Get-ListItems $ListEl
    $out = @()
    foreach ($i in $items) {
        # 2026-10-05：控制台日志行改成了"只读 TextBox"（为了能在行内自由框选文字、
        # 并把选中的字读出来给右键菜单复制）。UIA 里它是 Edit 控件，而且文字在
        # **ValuePattern** 上、Name 是空的——所以这里 Text/Edit 两种都收，且 Edit 必须
        # 走 ValuePattern 取值，否则会读出一堆空串（曾导致所有日志断言全灭）。
        $parts = @()

        $cText = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Text)
        $texts = $i.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cText)
        foreach ($t in $texts) {
            $n = [string]$t.Current.Name
            if ($n) { $parts += $n }
        }

        $cEdit = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Edit)
        $edits = $i.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cEdit)
        foreach ($t in $edits) {
            $v = ''
            try {
                $vp = $t.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $v = [string]$vp.Current.Value
            } catch {
                $v = [string]$t.Current.Name
            }
            if ($v) { $parts += $v }
        }

        if ($parts.Count -eq 0) { $out += [string]$i.Current.Name }
        else { $out += ($parts -join ' / ') }
    }
    return $out
}

function Select-Item {
    param($Item)
    $sp = $Item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sp.Select()
    Start-Sleep -Milliseconds 250
}

function Get-WindowStateText {
    param($Root)
    # 优先按 AutomationId 精确定位状态条：左侧账号列表每项也带状态文字（多个“未连接”），
    # 只按文案匹配会先撞到列表项，拿到的是别的账号的状态。
    $el = $null
    try { $el = Find-ById $Root 'StateTextBlock' } catch { $el = $null }
    if ($el) { return [string]$el.Current.Name }

    # 兜底：状态 TextBlock 位于连接栏内，唯一匹配四种已知状态文案
    $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Text)))
    foreach ($t in $all) {
        $n = $t.Current.Name
        if ($n -eq '未连接' -or $n -like '已连接 *' -or $n -eq '连接中…' -or $n -eq '断开中…') { return $n }
    }
    return ''
}

function Find-ListItemByText {
    param($ListEl, [string]$Pattern)
    foreach ($i in (Get-ListItems $ListEl)) {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Text)
        $texts = $i.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
        $parts = @()
        foreach ($t in $texts) { $parts += $t.Current.Name }
        if ((($parts -join ' / ')) -match $Pattern) { return $i }
    }
    return $null
}

function Wait-For {
    param([scriptblock]$Probe, [int]$TimeoutMs = 30000, [string]$What = 'condition')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        $v = & $Probe
        if ($v) { return $v }
        Start-Sleep -Milliseconds 400
    }
    throw "等待超时: $What"
}
