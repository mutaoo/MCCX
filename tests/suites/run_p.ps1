# 用法: run_p.ps1 <脚本名>  例如 run_p.ps1 p_bind
param([Parameter(Mandatory=$true)][string]$Name)
# 控制台输出统一 UTF-8：否则中文经管道回传会变成乱码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'
$src = "$(Join-Path $PSScriptRoot '..\cases')$Name.ps1"
$dst = "$(Join-Path $PSScriptRoot '..\artifacts')\${Name}_out.txt"
if (-not (Test-Path -LiteralPath (Split-Path -Parent $dst))) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
}
& $src *>&1 | Out-File -FilePath $dst -Encoding utf8
exit $LASTEXITCODE
