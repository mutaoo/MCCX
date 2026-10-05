# 抓取“取消密码框后无限重连”复现现场：状态行时间线 + 日志尾部 + 进程快照 + 前台窗口变化。
# 只读附加到已打开的 MCCX 窗口，不重启应用、不抢焦点（绝不调 Set-Foreground）、绝不读取密码框内容。
# FG 行记录前台窗口归属的变化：若用户反馈“点不进去/焦点被抢”，时间轴可直接对质。
# 用法：.\Capture-CancelRepro.ps1 [-TimeoutSec 90] [-IntervalMs 1000] [-OutFile <路径>]
param(
    [int]$TimeoutSec = 90,
    [int]$IntervalMs = 1000,
    [string]$OutFile = ''
)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\lib\uia.ps1"

if (-not $OutFile) {
    $art = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'
    New-Item -ItemType Directory -Force -Path $art | Out-Null
    $OutFile = Join-Path $art ("cancel-repro-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$enc = New-Object System.Text.UTF8Encoding($true)
$script:OutFile = $OutFile
$script:Seq = 0

function Emit {
    param([string]$Tag, [string]$Text)
    $script:Seq++
    $line = '[{0}] #{1} {2} {3}' -f (Get-Date -Format 'HH:mm:ss.fff'), $script:Seq, $Tag, $Text
    [System.IO.File]::AppendAllText($script:OutFile, ($line + "`r`n"), $enc)
}

[System.IO.File]::WriteAllText($OutFile, ("# cancel-repro capture start {0} timeout={1}s interval={2}ms`r`n" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $TimeoutSec, $IntervalMs), $enc)

$root = Wait-AppWindow -TimeoutMs 10000
$script:AppRoot = $root
Emit 'INFO' ("attached window: " + $root.Current.Name)

function Read-State {
    try {
        $el = Find-ById $script:AppRoot 'StateTextBlock'
        if ($el) { return [string]$el.Current.Name }
    } catch { }
    return '(状态行未找到)'
}

function Read-LogTail {
    param([int]$Max = 120)
    try {
        $log = Find-ById $script:AppRoot 'LogList'
        if (-not $log) { return @('(日志列表未找到)') }
        try {
            $sp = $log.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 100) }
        } catch { }
        Start-Sleep -Milliseconds 120
        $t = @(Get-ItemTexts $log)
        if ($t.Count -eq 0) { return @('(日志为空)') }
        if ($t.Count -gt $Max) { $t = $t[($t.Count - $Max)..($t.Count - 1)] }
        return $t
    } catch { return @("(读取日志失败: $($_.Exception.Message))") }
}

function Read-Proc {
    try {
        $ps = @(Get-Process -Name MCCX -ErrorAction SilentlyContinue | Sort-Object Id |
            ForEach-Object { '{0}@{1}' -f $_.Id, $_.StartTime.ToString('HH:mm:ss') })
        if ($ps.Count -eq 0) { return '(无 MCCX 进程)' }
        return ($ps -join ', ')
    } catch { return '(进程读取失败)' }
}

# 初始快照
$seenState = ''
$seenProc = ''
$seenFg = ''
$state = Read-State
$proc = Read-Proc
$fg = Get-ForegroundProcess
Emit 'STATE' $state
Emit 'PROC' $proc
Emit 'FG' $fg
$seenState = $state
$seenProc = $proc
$seenFg = $fg

function Read-TailWindow {
    # 读当前日志窗口的真实行：空窗口返回 @()；元素不可用（列表缺失/读失败）返回 $null，调用方跳过本采样。
    $t = @(Read-LogTail 200)
    if ($t.Count -eq 1) {
        if ($t[0] -eq '(日志为空)') { return ,@() }
        if ($t[0] -like '(日志列表未找到)*') { return $null }
        if ($t[0] -like '(读取日志失败*') { return $null }
    }
    return ,$t
}

function Get-AppendedTail {
    # 与上一采样窗口做“上一窗口后缀 == 本窗口前缀”的最长重叠比对，取出本窗口新追加的行。
    # 关键：**不按内容去重**——重复出现的同一行也是新实例，必须发。
    # 旧实现按“同一内容第 N 次出现”记基线，在“日志被清空 + ListView 虚拟化只读得到可视 ~50 行”
    # 的组合下会把重连后再次出现的同名行永久吞掉：清空后首次出现把基线降到 1 却不发，
    # 上一实例滑出窗口后下一次出现 occ=1 == 基线 1，从此再也发不出来——即使不清空日志，
    # 只要新实例到达时上一实例已滑出窗口，同样触发（2026-10-04 教训二：
    # “收到服务器对话框”“连接已断开（原因）”等关键行凭空消失，
    # 把复现日志误判成“服务端没发/客户端没收到”）。
    # 重叠至少取 2 行：清空后的新窗口最多与旧窗口末行偶遇 1 行相同，要求 2 行可避免误吞新内容。
    param([string[]]$Prev, [string[]]$Cur)
    if ($null -eq $Cur) { return $null }
    if ($null -eq $Prev -or $Prev.Count -eq 0 -or $Cur.Count -eq 0) { return ,$Cur }
    $maxL = [Math]::Min($Prev.Count, $Cur.Count)
    for ($l = $maxL; $l -ge 2; $l--) {
        $ok = $true
        for ($j = 0; $j -lt $l; $j++) {
            if ($Prev[$Prev.Count - $l + $j] -cne $Cur[$j]) { $ok = $false; break }
        }
        if ($ok) {
            if ($l -ge $Cur.Count) { return ,@() }
            return ,($Cur[$l..($Cur.Count - 1)])
        }
    }
    return ,$Cur   # 找不到 ≥2 行的重叠：当作整个窗口都是新内容（如日志被清空后）
}

$initWin = Read-TailWindow
if ($null -eq $initWin) { $initWin = @('(日志列表未找到)') }
foreach ($l in $initWin) { Emit 'LOG0' $l }
$prevWin = @($initWin)

# 采样循环：状态行只记变化；日志行按窗口重叠比对补发新增（见 Get-AppendedTail 注释）。
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$n = 0
$addedTotal = 0
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds $IntervalMs
    $n++
    $s = Read-State
    if ($s -ne $seenState) {
        Emit 'STATE' $s
        $seenState = $s
    }
    $p = Read-Proc
    if ($p -ne $seenProc) {
        Emit 'PROC' $p
        $seenProc = $p
    }
    $f = Get-ForegroundProcess
    if ($f -ne $seenFg) {
        Emit 'FG' $f
        $seenFg = $f
    }
    $win = Read-TailWindow
    if ($null -eq $win) { continue }
    $added = Get-AppendedTail $prevWin $win
    $prevWin = @($win)
    if ($null -ne $added) {
        foreach ($l in $added) {
            Emit 'LOG+' $l
            $addedTotal++
        }
    }
}
Emit 'INFO' ("samples=" + $n + " newLogLines=" + $addedTotal + " finalState=" + $seenState)
Emit 'INFO' 'capture end'
Write-Host "CAPTURED $OutFile"
Write-Host "samples=$n newLines=$addedTotal"
