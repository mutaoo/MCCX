<#
    测试套件自检：语法 + 路径解析。
    迁移/改动测试脚本后先跑这个，避免"路径改坏了但只有真正跑用例时才发现"。

    检查项：
      1) 所有 .ps1 能被 PowerShell 解析（语法错误直接列行号）。
      2) lib\autolib.ps1 能被 dot-source，且 $script:AppExe 指向的文件存在。
      3) 各脚本里的 $PSScriptRoot 相对路径（..\lib、..\artifacts、$script:RepoRoot）能解析到预期位置。

    用法：pwsh -File tests\tools\Test-TestScripts.ps1
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$testsRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $testsRoot
$fail = 0

Write-Output ("仓库根目录：{0}" -f $repoRoot)

# ---- 1) 语法检查 ----
Write-Output "`n[1/3] 语法检查"
$scripts = Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter '*.ps1' -File
foreach ($s in $scripts) {
    $errors = $null
    $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($s.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        $fail++
        Write-Output ("  FAIL {0}" -f $s.FullName.Replace($repoRoot, '.'))
        $errors | Select-Object -First 5 | ForEach-Object { "       L{0}: {1}" -f $_.Extent.StartLineNumber, $_.Message }
    }
}
if ($fail -eq 0) { Write-Output ("  OK  {0} 个脚本全部通过" -f $scripts.Count) }

# ---- 2) 公共库可加载 + exe 存在 ----
Write-Output "`n[2/3] 公共库与 exe 路径"
$autolib = Join-Path $testsRoot 'lib\autolib.ps1'
if (-not (Test-Path -LiteralPath $autolib)) {
    $fail++
    Write-Output ("  FAIL 缺少 {0}" -f $autolib)
} else {
    try {
        . $autolib
        Write-Output ("  OK  autolib.ps1 已加载；AppExe = {0}" -f $script:AppExe)
        if (Test-Path -LiteralPath $script:AppExe) {
            Write-Output "  OK  MCCX.exe 存在（Debug 构建产物）"
        } else {
            $fail++
            Write-Output "  FAIL MCCX.exe 不存在：先 dotnet build MCCX.slnx -c Debug"
        }
    } catch {
        $fail++
        Write-Output ("  FAIL autolib 加载失败：{0}" -f $_.Exception.Message)
    }
}

# ---- 3) 相对路径解析 ----
Write-Output "`n[3/3] 相对路径解析"
$expect = @(
    @{ Path = (Join-Path $testsRoot 'lib\uia.ps1');                                   Who = 'lib\uia.ps1' },
    @{ Path = (Join-Path $testsRoot 'artifacts');                                     Who = 'artifacts（输出目录）' },
    @{ Path = (Join-Path $repoRoot 'docs\images');                                    Who = 'docs\images' },
    @{ Path = (Join-Path $repoRoot 'accounts-stash');                                 Who = 'accounts-stash（可不存在）' }
)
foreach ($e in $expect) {
    if (Test-Path -LiteralPath $e.Path) {
        Write-Output ("  OK   {0}" -f $e.Who)
    } else {
        Write-Output ("  WARN {0} 不存在：{1}" -f $e.Who, $e.Path)
    }
}

# cases 与 suites 数量
$caseCount = @(Get-ChildItem -LiteralPath (Join-Path $testsRoot 'cases') -Filter '*.ps1' -File -ErrorAction SilentlyContinue).Count
$suiteCount = @(Get-ChildItem -LiteralPath (Join-Path $testsRoot 'suites') -Filter '*.ps1' -File -ErrorAction SilentlyContinue).Count
Write-Output ("`n用例 {0} 个，套件 {1} 个" -f $caseCount, $suiteCount)
Write-Output $(if ($fail -eq 0) { "`nSELF-CHECK OK" } else { "`nSELF-CHECK FAILURES=$fail" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
