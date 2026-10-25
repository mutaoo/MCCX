# 日志里的物品名由 MCC 自己用**官方语言表**翻成中文（2026-10-19 用户要求：不要 MCCX 自己那层
# 英文→中文映射，改用 MCC 的官方翻译）：
#   MCCX 在 MCCRuntime.Initialize 里把游戏语言钉成 zh_cn，MCC 连接时按它载入官方语言表
#   （ChatParser.InitTranslations，第一次联网下、缓存在 exe 目录的 lang\zh_cn.json），
#   服务器发来的"可翻译组件"由 MCC 直接按中文渲染，MCCX 不再事后改日志文本。
#   A 官方表里有的物品：item.minecraft.diamond_sword → [钻石剑]，英文名不再出现；
#   B 官方表里没有的（MCC 自带的语言表是 1.19.3 的，1.20+ 新物品不在里面，如 26.1 的金矛）：
#     MCC 原样打出翻译键 [item.minecraft.golden_spear]（MCC 官方口径，不额外补翻）；
#   C 负向对照：服务器自定义名 / 认不出来的英文词 / 中文标签一律原样保留；
#   D 只翻"可翻译组件"，普通文本里的英文名原样。
# 消息用 RCON tellraw 造（服务器发来的消息，形状和用户截图里那种一模一样），
# 断言走 UIA 读日志（Get-RecentLogText）；专用账号 MccXLogCnTest，测完删除、账号数回到基线。
# 语言表要联网下（缓存在 exe\lang\zh_cn.json）：连上后等它落地，等不到就整节 [skip]（环境问题不是功能问题）。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_logcn.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'MccXLogCnTest'
# autolib 默认测试玩家是 MccXBot；本脚本账号叫 MccXLogCnTest，RCON 的 tellraw 目标要跟着改
$script:TestPlayer = $script:TestUser

# ---------- 造消息 ----------

function Send-TellrawJson {
    # 直接发一段拼好的组件 JSON（tellraw 的第二个参数吃到行尾，空格没问题）
    param([string]$Json)
    Invoke-Rcon ("tellraw {0} {1}" -f $script:TestPlayer, $Json) 4000 | Out-Null
    Start-Sleep -Milliseconds 700
}

function Send-TellrawItemName {
    # 服务器报物品名的原样形状：一段文本 + 一个可翻译组件。
    # MCC 按当前语言表（MCCX 钉成 zh_cn）翻成中文再打进日志，表里没有的键原样打出来 —— 正好覆盖 A / B 两种形态。
    param([string]$Key, [string]$Token)
    Send-TellrawJson ('[{"text":"["},{"translate":"' + $Key + '"},{"text":"] ' + $Token + '"}]')
}

function Send-TellrawText {
    param([string]$Text)
    Send-TellrawJson ('[{"text":"' + $Text + '"}]')
}

# 正向断言：等日志出现这个 pattern；等不到就把"最近日志全文"打出来（Assert-Log 的失败详情
# 只给最早一行，排查时要看的是全文）。
function Assert-LogShows {
    param([string]$Pattern, [string]$Name, [int]$TimeoutMs = 30000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lines = @()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        try { $lines = @(Get-RecentLogText) } catch { }
        if (($lines -join "`n") -match $Pattern) {
            Assert-True $true $Name
            return
        }
        Start-Sleep -Milliseconds 350
    }
    Write-Host '  [dbg] 最近日志全文:'
    foreach ($l in $lines) { Write-Host ('    | ' + $l) }
    Assert-True $false $Name "未匹配: $Pattern"
}

# 负向断言：给消息一点落地时间，最近日志里不能出现这个串
function Assert-LogGone {
    param([string]$Token, [string]$Name, [int]$WaitMs = 2500)
    Start-Sleep -Milliseconds $WaitMs
    $txt = Get-RecentLogText
    if ($txt -match [regex]::Escape($Token)) {
        $lines = @($txt -split "`r?`n")
        Write-Host ("  [dbg] 最近日志: " + (($lines | Select-Object -Last 12) -join ' | '))
        Assert-True $false $Name "不该出现在日志里: $Token"
        return
    }
    Assert-True $true $Name "确实没有: $Token"
}

# ---------- 官方语言表 ----------

function Test-ChineseTableCached {
    # MCC 把官方语言表存在 exe 目录的 lang\zh_cn.json（不写 UserData；容器面板那份是另一回事）
    return (Test-Path (Join-Path (Split-Path $script:AppExe) 'lang\zh_cn.json'))
}

function Wait-ChineseTableCached {
    # MCC 在连接过程中下这份表（协议处理器初始化时 InitTranslations），连接成功即已就绪；留点余量
    param([int]$TimeoutMs = 20000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (Test-ChineseTableCached) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$null = Stop-App
$null = Start-App
$session = Enter-TestSession -User $script:TestUser -Port '25599'

try {
    $hasTable = Wait-ChineseTableCached
    if ($hasTable) {
        Write-Host '  [ui] MCC 官方语言表已就绪：A / B / D 断言'
    } else {
        Write-Host '  [skip] MCC 官方语言表没下下来（没网），跳过 A / B / D 的中文断言（C 段负向对照照跑）'
    }
    Clear-Log

    # ---------- A 老物品：MCC 用官方语言表翻成中文 ----------
    Write-Host "`n=== A 老物品（官方表里有，直接出中文）==="
    Send-TellrawItemName 'item.minecraft.diamond_sword' 'logcn_a1'
    if ($hasTable) {
        Assert-LogShows '\[钻石剑\] logcn_a1' 'A1 item.minecraft.diamond_sword 显示成 [钻石剑]'
        Assert-LogGone 'Diamond Sword' 'A2 英文名不再出现在日志里'
    }

    # ---------- B 官方表里没有的新版物品：MCC 原样打出翻译键 ----------
    Write-Host "`n=== B 新版物品（MCC 自带语言表是 1.19.3 的，表里没有）==="
    Send-TellrawItemName 'item.minecraft.golden_spear' 'logcn_b1'
    if ($hasTable) {
        Assert-LogShows '\[item\.minecraft\.golden_spear\] logcn_b1' 'B1 表里没有的新物品，MCC 原样打出翻译键'
        Assert-LogGone 'Golden Spear' 'B2 不会退回英文名（MCC 只认表，表里没有就给键）'
    }

    # ---------- C 负向对照：认不出来的一律原样 ----------
    Write-Host "`n=== C 负向对照（原样保留）==="
    Send-TellrawText '[极品灵石] logcn_c1'
    Assert-LogShows '\[极品灵石\] logcn_c1' 'C1 服务器自定义名（中文）原样保留'

    Send-TellrawText '[not_an_item] logcn_c2'
    Assert-LogShows '\[not_an_item\] logcn_c2' 'C2 认不出来的英文词原样保留'

    Send-TellrawText '[系统] logcn_c3'
    Assert-LogShows '\[系统\] logcn_c3' 'C3 中文标签（[系统]）原样保留'

    # ---------- D 只翻"可翻译组件"，普通文本不动 ----------
    Write-Host "`n=== D 只翻可翻译组件 ==="
    Send-TellrawJson '[{"text":"前面 ["},{"translate":"item.minecraft.bow"},{"text":"] 后面 logcn_d1"}]'
    if ($hasTable) {
        Assert-LogShows '前面 \[弓\] 后面 logcn_d1' 'D1 中括号与前后文保持原样，只有可翻译组件译成中文'
        # 普通文本里的英文名不翻（MCC 只认 translate 组件，玩家名 / 普通英文词不动）
        Send-TellrawText '裸着写的 Bow 后面 logcn_d2'
        Assert-LogShows '裸着写的 Bow 后面 logcn_d2' 'D2 不在组件里的英文名原样保留'
    }
}
finally {
    try { Exit-TestSession $session } catch { Write-Host ("  [clean] " + $_.Exception.Message) }
}

Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_logcn FAILURES=$fail"
exit ([int]($fail -gt 0))
