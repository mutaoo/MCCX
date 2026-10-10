# Read-only: decrypt accounts.dat to list account names (key via DPAPI in Windows PowerShell).
$ErrorActionPreference = 'Stop'

$exeDir = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64')"
# 2026-10-05：账号库在用户数据目录（程序目录下的 UserData）里
$dataDir = Join-Path $exeDir 'UserData'
$keyFile = Join-Path $dataDir 'accounts.key'
$keyHexPath = Join-Path $env:TEMP 'mccx_key.hex'

$ps5 = @'
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security
$entropy = [System.Text.Encoding]::UTF8.GetBytes("MCCX.AccountStore.v1")
$legEntropy = [System.Text.Encoding]::UTF8.GetBytes("MccX.AccountStore.v1")
$protected = [System.IO.File]::ReadAllBytes($args[0])
try { $key = [System.Security.Cryptography.ProtectedData]::Unprotect($protected, $entropy, "CurrentUser") }
catch { $key = [System.Security.Cryptography.ProtectedData]::Unprotect($protected, $legEntropy, "CurrentUser") }
[System.IO.File]::WriteAllText($args[1], ([System.BitConverter]::ToString($key) -replace "-", ""))
'@
$ps5Path = Join-Path $env:TEMP 'mccx_unwrap_key.ps1'
[System.IO.File]::WriteAllText($ps5Path, $ps5, [System.Text.UTF8Encoding]::new($false))
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ps5Path $keyFile $keyHexPath | Out-Null
$key = [Convert]::FromHexString((Get-Content $keyHexPath -Raw).Trim())

function Show-Accounts([string]$datPath, [string]$label) {
    if (-not (Test-Path $datPath)) { Write-Output "[$label] missing"; return }
    $payload = [System.IO.File]::ReadAllBytes($datPath)
    if ($payload[0] -ne 1) { Write-Output "[$label] bad format $($payload[0])"; return }
    $nonce = $payload[1..12]
    $tag = $payload[13..28]
    $cipher = $payload[29..($payload.Length - 1)]
    $plain = [byte[]]::new($cipher.Length)
    $aes = [System.Security.Cryptography.AesGcm]::new($key, 16)
    $aes.Decrypt($nonce, $cipher, $tag, $plain)
    $json = [System.Text.Encoding]::UTF8.GetString($plain)
    $obj = $json | ConvertFrom-Json
    $list = if ($obj -is [array]) { $obj } else { $obj.accounts }
    Write-Output "--- $label : $($list.Count) account(s) ---"
    foreach ($a in $list) {
        Write-Output ("  - {0} | {1}:{2} | ver={3} | last={4}" -f $a.username, $a.serverHost, $a.port, $a.minecraftVersion, $a.lastUsedAt)
    }
}

Show-Accounts (Join-Path $dataDir 'accounts.dat') 'current'
Show-Accounts "$(Join-Path $PSScriptRoot '..\artifacts')\accounts-backup\accounts.dat" 'backup-0928'
