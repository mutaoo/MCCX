# 发布便携包：包根 = MCCX.exe（启动器）+ UserData\ + app\（主程序与全部运行文件）
#
# 用法（在工作区根目录或任意位置）：
#   pwsh -File build\publish.ps1
#   pwsh -File build\publish.ps1 -Output D:\tmp\mccx-package
#
# 注意：
#   - 不要给主程序 publish 加 -r win-x64（会踩 NETSDK1152，见开发文档 §12）；
#   - 目标目录里已有的 UserData\ 会被保留（不删用户数据），其余内容重建；
#   - 发布前先关掉正在运行的 MCCX，避免 Release 产物被占用。

param(
    [string]$Output = 'publish',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([System.IO.Path]::IsPathRooted($Output)) {
    $outputPath = $Output
} else {
    $outputPath = Join-Path $repoRoot $Output
}

$appProject = Join-Path $repoRoot 'MCCX.App\MCCX.App.csproj'
$launcherProject = Join-Path $repoRoot 'MCCX.Launcher\MCCX.Launcher.csproj'
$appOutput = Join-Path $outputPath 'app'

$running = Get-Process MCCX -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning ("检测到 MCCX 正在运行（PID: {0}）；若它占用 Release 产物，发布可能报 MSB3026。" -f (($running | ForEach-Object { $_.Id }) -join ', '))
}

Write-Host "== 目标目录：$outputPath"
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

# 保留 UserData（用户数据），其余旧内容全部重建
Get-ChildItem $outputPath -Force |
    Where-Object { $_.Name -ne 'UserData' } |
    Remove-Item -Recurse -Force

Write-Host '== 发布主程序 -> app\'
& dotnet publish $appProject -c $Configuration --self-contained false `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false `
    -o $appOutput -m:1
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败（exit $LASTEXITCODE）" }

Write-Host '== 发布启动器 -> 包根'
& dotnet publish $launcherProject -c $Configuration --self-contained false -o $outputPath -m:1
if ($LASTEXITCODE -ne 0) { throw "启动器发布失败（exit $LASTEXITCODE）" }

# 布局标记：主程序看到上一级有这个文件，就把数据根定在包根（UserData 与 app\ 平级）
$markerPath = Join-Path $outputPath '.mccx-layout'
[System.IO.File]::WriteAllText($markerPath, "launcher`n", (New-Object System.Text.UTF8Encoding($false)))

New-Item -ItemType Directory -Path (Join-Path $outputPath 'UserData') -Force | Out-Null

$files = Get-ChildItem $outputPath -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
Write-Host ''
Write-Host "== 完成：$($files.Count) 个文件 / $sizeMb MB"
Write-Host "   包根：MCCX.exe（启动器）+ UserData\ + app\"
