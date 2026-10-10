<#
    A5：把测试脚本从临时目录迁移进仓库，并把写死的绝对路径改成相对路径。

    背景：迁移前所有脚本都躺在 %TEMP%\opencode\ 下（随时可能被清理），
    且互相引用、输出、exe 路径全是写死的：
      - . "%TEMP%\opencode\autolib.ps1" / uia.ps1
      - $script:AppExe = '...\MCCX\MCCX.App\bin\Debug\...\MCCX.exe'
      - 输出写回 %TEMP%\opencode\<名字>_out.txt
    搬进仓库后这些路径全部失效。

    本脚本只做迁移与改写，不改测试逻辑：
      - lib\    : autolib.ps1 / uia.ps1（公共库）
      - suites\ : run6 / v_ui7 / shot_readme / dump_accounts / ver_check + run_p 运行器
      - cases\  : run6 依赖的用例脚本（p_* / v_ui* / d_* / t*_ 等）
      - 输出统一改到 tests\artifacts\（已 gitignore）

    默认 dry-run：只打印将会发生的替换，不落盘。确认后加 -Apply。
    用法：
      pwsh -File tests\tools\Migrate-TestScripts.ps1                 # 预览
      pwsh -File tests\tools\Migrate-TestScripts.ps1 -Apply          # 执行
      pwsh -File tests\tools\Migrate-TestScripts.ps1 -Apply -SourceDir <旧目录>
#>
[CmdletBinding()]
param(
    [string] $SourceDir = (Join-Path $env:TEMP 'opencode'),
    [string] $RepoRoot  = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SourceDir)) {
    throw "源目录不存在：$SourceDir"
}

$testsDir = Join-Path $RepoRoot 'tests'
Write-Output ("仓库根目录：{0}" -f $RepoRoot)
Write-Output ("源目录    ：{0}" -f $SourceDir)
Write-Output ("模式      ：{0}" -f $(if ($Apply) { 'APPLY（写入）' } else { 'DRY-RUN（只预览）' }))

# 迁移清单：源文件名 -> 目标子目录
$shared = @('autolib.ps1', 'uia.ps1')
$suites = @('run6.ps1', 'run_p.ps1', 'v_ui7.ps1', 'shot_readme.ps1', 'dump_accounts.ps1', 'ver_check.ps1')
# run6.ps1 依赖的用例脚本（按前缀收录，避免混进一次性探针）
$casePrefixes = @('p_', 'v_ui', 'd_', 't1_', 't2_', 't3_', 't4_')

$plan = [System.Collections.Generic.List[object]]::new()

foreach ($n in $shared) {
    $src = Join-Path $SourceDir $n
    if (Test-Path -LiteralPath $src) { $plan.Add([pscustomobject]@{ Name = $n; Group = 'lib' }) }
}

foreach ($n in $suites) {
    $src = Join-Path $SourceDir $n
    if (Test-Path -LiteralPath $src) { $plan.Add([pscustomobject]@{ Name = $n; Group = 'suites' }) }
}

Get-ChildItem -LiteralPath $SourceDir -Filter '*.ps1' -File | ForEach-Object {
    $name = $_.Name
    # 已在清单里的（lib/suites）跳过，避免重复
    if ($plan | Where-Object { $_.Name -eq $name }) { return }

    foreach ($prefix in $casePrefixes) {
        if ($name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $plan.Add([pscustomobject]@{ Name = $name; Group = 'cases' })
            return
        }
    }
}

Write-Output ("`n将迁移 {0} 个脚本：" -f $plan.Count)

# 迁移源目录的运行时构造（源码不写死绝对路径，换机器/换用户也成立）
$oldTmp = Join-Path $env:TEMP 'opencode'

$replacements = @(
    @{ Pattern = [regex]::Escape("$oldTmp\uia.ps1");       Replacement = '<LIB>\uia.ps1';       Note = 'uia 库引用' },
    @{ Pattern = [regex]::Escape("$oldTmp\autolib.ps1");   Replacement = '<LIB>\autolib.ps1';   Note = 'autolib 库引用' },
    # run_p.ps1 与 run6.ps1 同目录（tests\suites），不是 artifacts
    @{ Pattern = [regex]::Escape("$oldTmp\run_p.ps1");     Replacement = '<SAMEDIR>\run_p.ps1'; Note = '同目录运行器' },
    @{ Pattern = [regex]::Escape((Join-Path $RepoRoot 'MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64')); Replacement = '<EXEDIR>'; Note = 'exe 目录' },
    @{ Pattern = [regex]::Escape((Join-Path $RepoRoot 'docs\images'));                              Replacement = '<IMGDIR>'; Note = 'README 图目录' },
    @{ Pattern = [regex]::Escape((Join-Path $RepoRoot 'accounts-stash'));                            Replacement = '<STASHDIR>'; Note = '账号暂存目录' },
    @{ Pattern = [regex]::Escape($oldTmp);               Replacement = '<TESTS>';             Note = '测试目录（输出等）' }
)

$changedFiles = 0
foreach ($item in $plan | Sort-Object Group, Name) {
    $src = Join-Path $SourceDir $item.Name
    $dstDir = Join-Path $testsDir $item.Group
    $bytes = [System.IO.File]::ReadAllBytes($src)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if ($hasBom) { $text = $text.Substring(1) }

    $hits = 0
    foreach ($r in $replacements) {
        $m = [regex]::Matches($text, $r.Pattern)
        $hits += $m.Count
    }

    Write-Output ("  [{0,-6}] {1,-22} {2,6}B  BOM={3,-6} 替换点={4}" -f $item.Group, $item.Name, $bytes.Length, $(if ($hasBom) { 'yes' } else { 'no' }), $hits)
    if ($hits -gt 0) { $changedFiles++ }

    if (-not $Apply) { continue }

    foreach ($r in $replacements) {
        $text = [regex]::Replace($text, $r.Pattern, $r.Replacement)
    }

    if (-not (Test-Path -LiteralPath $dstDir)) { New-Item -ItemType Directory -Path $dstDir -Force | Out-Null }

    # 占位符展开成相对脚本位置的表达式。
    # 关键一：源脚本里的路径大多写在【单引号】字符串里（'...\MCCX.exe'），
    #         所以必须按占位符词法替换，不能依赖引号形式。
    # 关键二：不要生成会嵌套同类引号的表达式（'$(Join-Path $x 'y')' 与 -f 都会引入引号而非法）。
    #         统一用字符串拼接，表达式内部除 '' 外不出现引号；含表达式的字符串外层用双引号。
    # 关键三：仓库根在表达式内部就地算出来（tests\<组>\x.ps1 上两级），
    #         不依赖脚本里预先存在的 $script:RepoRoot，也不需要往脚本里插行——
    #         插行会破坏 dot-source 语句（踩过：p_blank.ps1 的 autolib 引用被整行替换掉）。
    $libDirExpr    = '$(Join-Path $PSScriptRoot ''..\lib'')'
    $artifactsExpr = '$(Join-Path $PSScriptRoot ''..\artifacts'')'
    $rootExpr      = 'Split-Path -Parent (Split-Path -Parent $PSScriptRoot)'
    $exeDirExpr    = '$(({0}) + ''\MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64'')' -f $rootExpr
    $imgDirExpr    = '$(({0}) + ''\docs\images'')' -f $rootExpr
    $stashDirExpr  = '$(({0}) + ''\accounts-stash'')' -f $rootExpr

    $placeholders = [ordered]@{
        '<SAMEDIR>' = '$PSScriptRoot'
        '<LIB>'     = $libDirExpr
        '<TESTS>'   = $artifactsExpr
        '<EXEDIR>'  = $exeDirExpr
        '<IMGDIR>'  = $imgDirExpr
        '<STASHDIR>' = $stashDirExpr
    }

    $lines = $text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        foreach ($key in $placeholders.Keys) {
            if (-not $line.Contains($key)) { continue }

            $expr = $placeholders[$key]
            # 展开式里如果本身带单引号（如 '$($script:RepoRoot + ''\x'')'），
            # 而原文又把它包在单引号里，就会形成 '...'...'...' 的非法嵌套。
            # 这时把外层单引号换成双引号（双引号里 $() 一样会展开，反斜杠也没有转义问题）。
            if ($expr.Contains("'")) {
                $line = [regex]::Replace($line, "'([^'\r\n]*)" + [regex]::Escape($key) + "([^'\r\n]*)'", {
                    param($m)
                    '"' + $m.Groups[1].Value + $expr + $m.Groups[2].Value + '"'
                })
            }

            # 剩下的（或本来就在双引号里的）直接做词法替换
            if ($line.Contains($key)) {
                $line = $line.Replace($key, $expr)
            }
        }
        $lines[$i] = $line
    }
    $text = $lines -join "`n"

    # 刻意不往脚本里插 $script:RepoRoot：仓库根已经在 EXEDIR/IMGDIR/STASHDIR 表达式内部就地算出，
    # 插行只会破坏原有语句（历史 bug：把 p_blank.ps1 的 autolib dot-source 整行替换掉了）。

    # 隐私清洗：测试脚本要入库，绝不能带上用户的真实服务器域名/端口。
    # 这是硬规则（other\注意事项.md：用户真实服务器与账号勿写进仓库文件），
    # 所以做成迁移的一部分，避免以后再迁一次又把真实地址带回来。
    # 注意：shot_readme.ps1 里的敏感词列表是"截图隐私断言"用的守卫名单，属于有意保留，不清洗。
    $privacyScrub = @(
        @{ Pattern = "play\.simpfun\.cn";                Replacement = '127.0.0.1' },
        @{ Pattern = "mccx-port-test\.simpfun\.cn";      Replacement = 'mccx-port-test.invalid' },
        @{ Pattern = "'11724'";                          Replacement = "'25599'" }
    )
    $scrubbed = 0
    foreach ($s in $privacyScrub) {
        $m = [regex]::Matches($text, $s.Pattern)
        if ($m.Count -gt 0) {
            $text = [regex]::Replace($text, $s.Pattern, $s.Replacement)
            $scrubbed += $m.Count
        }
    }
    if ($scrubbed -gt 0) {
        Write-Output ("    隐私清洗：{0} 处真实服务器信息已替换为测试值" -f $scrubbed)
    }

    # 修掉原脚本里就有的语法错误：PowerShell 会把 "$round:" 里的冒号当成作用域限定符，
    # 必须写成 ${round}。原文件在临时目录里就编不过，属既有问题，顺手修掉免得回归网自带一颗哑弹。
    $text = $text -replace '\$round:', '${round}:'

    # run_p.ps1 的用例目录：用例在 tests\cases\，输出在 tests\artifacts\（原脚本两者都指 artifacts）。
    if ($item.Name -eq 'run_p.ps1') {
        $text = $text -replace '\$\(Join-Path \$PSScriptRoot ''\.\.\\artifacts''\)\\\$Name\.ps1', '$(Join-Path $PSScriptRoot ''..\cases'')$Name.ps1'
        if ($text -notmatch "Test-Path -LiteralPath \(Split-Path -Parent \`$dst\)") {
            $guard = "if (-not (Test-Path -LiteralPath (Split-Path -Parent `$dst))) {`n    New-Item -ItemType Directory -Path (Split-Path -Parent `$dst) -Force | Out-Null`n}`n"
            $text = $text -replace '(?m)^(& \$src \*>&1)', ($guard + '$1')
        }
    }

    # 仍残留占位符就报出来，避免静默留下坏路径
    $left = [regex]::Matches($text, '<(LIB|SAMEDIR|TESTS|EXEDIR|IMGDIR|STASHDIR)>')
    if ($left.Count -gt 0) {
        Write-Output ("    ！仍有 {0} 处占位符未展开，已写入但需人工确认" -f $left.Count)
    }

    $enc = [System.Text.UTF8Encoding]::new($hasBom)
    [System.IO.File]::WriteAllText((Join-Path $dstDir $item.Name), $text, $enc)
}

Write-Output ("`n需要改写的脚本数：{0}" -f $changedFiles)
if (-not $Apply) {
    Write-Output "这是 DRY-RUN，没有写任何文件。确认无误后加 -Apply 执行。"
} else {
    Write-Output ("已迁移到：{0}" -f $testsDir)
}
