$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
# g_* cases that touch the log area (Get-RecentLogText / LogScrollHost / LogBox / LogSearchBox)
# or the connected flow (they connect to the local test server and drive the real UI).
foreach ($s in @('g_fixes', 'g_msgfilter', 'g_view', 'g_refill', 'g_dialog', 'g_walk', 'g_logkeys', 'g_logcn', 'g_connlock', 'g_logswitch', 'g_autocmd', 'g_loginopt', 'g_useitem')) {
    Write-Output "=== RUN $s ==="
    try { & "$PSScriptRoot\run_p.ps1" -Name $s }
    catch { Write-Output "EXCEPTION ${s}: $($_.Exception.Message)" }
    $log = "$(Join-Path $PSScriptRoot '..\artifacts')\${s}_out.txt"
    if (Test-Path $log) {
        $f = Select-String -Path $log -Pattern 'FAILURES ?= ?\d+' | Select-Object -Last 1
        if ($f) { Write-Output "SUMMARY $s $($f.Line.Trim())" } else { Write-Output "SUMMARY $s NO-FAILURES-LINE" }
        $bad = Select-String -Path $log -Pattern '^\s*FAIL\s'
        if ($bad) { $bad | ForEach-Object { Write-Output "  FAILLINE $($_.Line.Trim())" } }
        # 抢不到前台时用例会整条 [skip]（什么断言都没做）—— 别让它看起来跟"全绿"一样
        if (Select-String -Path $log -Pattern '\[skip\].*NOTHING was asserted' -Quiet) {
            Write-Output "  SKIPCASE $s 抢不到前台，整条跳过（什么都没断言）"
        }
    } else { Write-Output "SUMMARY $s NO-LOG" }
    Get-Process MCCX -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}
Write-Output '=== ALL DONE ==='
exit 0
