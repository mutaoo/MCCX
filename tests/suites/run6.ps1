$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
foreach ($s in @('p_ui', 'p_layout', 'p_blank', 'p_bind', 'p_multi', 'p_port', 'p_look', 'p_filter', 'v_ui2')) {
    Write-Output "=== RUN $s ==="
    try { & '$PSScriptRoot\run_p.ps1' -Name $s }
    catch { Write-Output "EXCEPTION ${s}: $($_.Exception.Message)" }
    $log = "$(Join-Path $PSScriptRoot '..\artifacts')\${s}_out.txt"
    if (Test-Path $log) {
        $f = Select-String -Path $log -Pattern 'FAILURES ?= ?\d+' | Select-Object -Last 1
        if ($f) { Write-Output "SUMMARY $s $($f.Line.Trim())" } else { Write-Output "SUMMARY $s NO-FAILURES-LINE" }
        $bad = Select-String -Path $log -Pattern '^\s*FAIL\s'
        if ($bad) { $bad | ForEach-Object { Write-Output "  FAILLINE $($_.Line.Trim())" } }
    } else { Write-Output "SUMMARY $s NO-LOG" }
    Get-Process MCCX -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}
Write-Output '=== ALL DONE ==='
exit 0
