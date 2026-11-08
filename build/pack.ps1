# 把 publish\ 打成可分发的 zip：外层套一层版本文件夹，解压不散落。
#
# 用法：
#   pwsh -File build\pack.ps1
#   pwsh -File build\pack.ps1 -Source D:\tmp\mccx-package
#
# 结构（版本号取自 build\Version.props）：
#   MCCX-<版本>\MCCX.exe        启动器
#   MCCX-<版本>\.mccx-layout    布局标记
#   MCCX-<版本>\UserData\       空目录，首次运行由启动器/主程序写入
#   MCCX-<版本>\app\            主程序与全部运行文件
#
# 注意：zip 条目分隔符必须是 '/'（ZipFile.CreateFromDirectory 在 Windows 上会写成 '\'，
# 部分解压工具会当成文件名），所以这里用 ZipArchive 逐条写。

param(
    [string]$Source = 'publish',
    [string]$Output
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([System.IO.Path]::IsPathRooted($Source)) {
    $sourcePath = $Source
} else {
    $sourcePath = Join-Path $repoRoot $Source
}

if (-not (Test-Path (Join-Path $sourcePath 'MCCX.exe'))) {
    throw "找不到 $sourcePath\MCCX.exe：先跑 build\publish.ps1"
}
if (-not (Test-Path (Join-Path $sourcePath 'app\MCCX.exe'))) {
    throw "找不到 $sourcePath\app\MCCX.exe：发布包结构不对，先跑 build\publish.ps1"
}

$version = ([xml](Get-Content (Join-Path $repoRoot 'build\Version.props') -Raw)).Project.PropertyGroup.MccxInformationalVersion
$top = "MCCX-$version"
if (-not $Output) {
    $Output = Join-Path $repoRoot "other\$top-win-x64-portable.zip"
}
if (-not [System.IO.Path]::IsPathRooted($Output)) {
    $Output = Join-Path $repoRoot $Output
}

# 暂存目录：把 publish\ 的内容拷进 版本文件夹，再整棵压进去
$stageRoot = Join-Path $env:TEMP 'mccx-pack-stage'
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
$stage = Join-Path $stageRoot $top
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item (Join-Path $sourcePath '*') -Destination $stage -Recurse -Force
# * 不含点开头的隐藏项，单独补 .mccx-layout
Copy-Item (Join-Path $sourcePath '.mccx-layout') -Destination $stage -Force
New-Item -ItemType Directory -Path (Join-Path $stage 'UserData') -Force | Out-Null

if (Test-Path $Output) { Remove-Item $Output -Force }
$outputDir = Split-Path -Parent $Output
if ($outputDir -and -not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$stream = [System.IO.File]::Open($Output, [System.IO.FileMode]::CreateNew)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
$count = 0
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File -Force) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry("$top/$relative", [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $file.LastWriteTime
        $out = $entry.Open()
        $in = [System.IO.File]::OpenRead($file.FullName)
        try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
        $count++
    }

    # 空目录（UserData）没有文件，单独补一条目录条目
    foreach ($dir in Get-ChildItem $stage -Recurse -Directory -Force) {
        if (-not (Get-ChildItem $dir.FullName -Force)) {
            $relative = $dir.FullName.Substring($stage.Length + 1).Replace('\', '/')
            [void]$archive.CreateEntry("$top/$relative/")
        }
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
    Remove-Item $stageRoot -Recurse -Force
}

$info = Get-Item $Output
$sizeMb = [math]::Round($info.Length / 1MB, 1)
Write-Host "== 完成：$count 个文件 / $sizeMb MB"
Write-Host "   $($info.FullName)"
Write-Host "   解压后：$top\MCCX.exe + $top\UserData\ + $top\app\"
