# g_logkeys - log area shortcut e2e (Ctrl+C copy, Ctrl+left-click link).
#
# Why this file exists: the log area was rewritten as a virtualized ItemsRepeater
# (MCCX.App/MainPage.Log.cs, "D scheme"). The per-row TextBoxes are IsHitTestVisible /
# IsTabStop = false, so every shortcut is implemented by hand - and had zero coverage:
#   * Ctrl+C with a pointer-drag selection  -> Page accelerators + LogScrollHost_KeyDown
#   * Ctrl+left-click on a link             -> LogRow_PointerReleased
# Both were broken in a way only real input exposes: PointerCaptureLost arrives BEFORE
# PointerReleased, so a "was this press a click?" test based on the selecting flag was
# already false by the time the release ran (Ctrl+click never fired), and the modifier
# key state has to be read from the pointer event itself, not only from the key queue.
#
# Deliberate side effects:
#   * MCCX is brought to the foreground (Save-Foreground / Restore-Foreground, like g_search);
#   * the real mouse cursor is moved for the drag/click (original position restored at the end);
#   * the clipboard is written (original text restored at the end).
# Set MCCX_TEST_NO_MOUSE=1 to run only the keyboard halves (C/D), which never touch the mouse.
# The link half (B) opens a browser tab, so it is opt-in: set MCCX_TEST_LINK=1 to run it.
# If MCCX cannot take the foreground the case prints [skip] lines and asserts nothing (house rule
# for real-input cases, same as g_search step 8) - losing the foreground is an environment problem.
#
# Usage: pwsh -ExecutionPolicy Bypass -File tests\cases\g_logkeys.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'MccXKeyLog'
$script:LinkPort = 18789

$queryText = 'probe filler line number'
$linkUrl = "http://127.0.0.1:$($script:LinkPort)/mccx-link-probe"
$sentinelA = '__mccx_logkeys_sentinel_A__'
$sentinelC = '__mccx_logkeys_sentinel_C__'
$sentinelD = '__mccx_logkeys_sentinel_D__'

if (-not ('MccXKeyLog.Input' -as [type])) {
    Add-Type -Namespace MccXKeyLog -Name Input -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, System.UIntPtr e);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, System.UIntPtr e);
'@
}

# ---------- input / clipboard helpers ----------

function Get-VirtualScreen { return [System.Windows.Forms.SystemInformation]::VirtualScreen }

function Move-MouseTo {
    # Absolute mouse_event (what SendInput does): plain SetCursorPos does not reliably
    # produce WM_MOUSEMOVE for the target window, and then drags select nothing.
    param([int]$X, [int]$Y)
    $vs = Get-VirtualScreen
    $nx = [int](($X - $vs.Left) * 65535 / $vs.Width)
    $ny = [int](($Y - $vs.Top) * 65535 / $vs.Height)
    [MccXKeyLog.Input]::mouse_event(0x8001, [uint32]$nx, [uint32]$ny, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 50
}

function Set-MouseButtonState {
    param([string]$State)
    $flag = 0x0002
    if ($State -eq 'Up') { $flag = 0x0004 }
    [MccXKeyLog.Input]::mouse_event([uint32]$flag, 0, 0, 0, [UIntPtr]::Zero)
}

function Set-CtrlState {
    param([string]$State)
    $flag = 0
    if ($State -eq 'Up') { $flag = 2 }
    [MccXKeyLog.Input]::keybd_event(0x11, 0, [uint32]$flag, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
}

function Get-ClipboardText {
    try { return [string][System.Windows.Forms.Clipboard]::GetText() } catch { return '' }
}

function Set-ClipboardText {
    param([string]$Text)
    # NOTE: [Clipboard]::SetText('') throws (null argument) - an empty restore must be Clear().
    try {
        if ([string]::IsNullOrEmpty($Text)) { [System.Windows.Forms.Clipboard]::Clear() }
        else { [System.Windows.Forms.Clipboard]::SetText($Text) }
    } catch { }
}

function Get-LogRows {
    # ItemsRepeater has no UIA peer; the realized row TextBoxes are Edit elements under
    # LogScrollHost. Rows that are not on screen report Rect.Empty (Y = +Infinity) - drop them.
    $log = Get-LogList
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)
    $found = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($e in $found) {
        $v = ''
        try { $v = [string]$e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { $v = '' }
        if (-not $v) { continue }
        $r = $e.Current.BoundingRectangle
        if ([double]::IsInfinity($r.Y) -or $r.Width -le 0 -or $r.Height -le 0) { continue }
        $rows.Add([pscustomobject]@{ Text = $v; Rect = $r })
    }
    return $rows
}

function Select-LogRowRange {
    # Drag from the left part of a row to the right: a character-level (not whole-line) selection.
    param($Row)
    $y = [int]($Row.Rect.Y + $Row.Rect.Height / 2)
    $x0 = [int]($Row.Rect.X + 20)
    Move-MouseTo $x0 $y
    Set-MouseButtonState 'Down'
    for ($k = 1; $k -le 12; $k++) { Move-MouseTo ([int]($x0 + $k * 12)) $y }
    Set-MouseButtonState 'Up'
    Start-Sleep -Milliseconds 400
}

# ---------- run ----------

$savedClip = Get-ClipboardText
# Never propagate junk: if the clipboard already holds this case's own token (left by an earlier
# run that died before restoring), treat the original as "nothing worth keeping".
if ($savedClip -eq 'LOGKEY_CMDTEXT') { $savedClip = '' }
$savedCursor = [System.Windows.Forms.Cursor]::Position
$session = $null

try {
    Save-Foreground
    $null = Start-App
    $session = Enter-TestSession -User $script:TestUser

    Write-Host "`n=== 0) fill the log until the rows overflow the viewport ==="
    # The drag/click helpers locate a row by bounding box, and rows below the last one are not
    # realized at all, so there must be more lines than fit (~52 at Consolas 12 / 200% DPI).
    for ($i = 1; $i -le 60; $i++) {
        $null = Invoke-Rcon "say $queryText $i tail $linkUrl"
        Start-Sleep -Milliseconds 80
    }
    Start-Sleep -Milliseconds 1200

    # Grabbing the foreground can lose to a transient (e.g. GameInputSvc), so retry a few times
    # before giving up.
    $onTop = $false
    $fg = ''
    for ($k = 1; $k -le 6; $k++) {
        Set-Foreground
        try { $fg = Get-ForegroundProcess } catch { $fg = '' }
        if ($fg -eq 'MCCX') { $onTop = $true; break }
        Start-Sleep -Milliseconds 800
    }
    if (-not $onTop) {
        # House rule (same as g_search step 8): not getting the foreground is an environment
        # problem, not a product problem - say so loudly and skip instead of turning the net red.
        Write-Host "  [skip] MCCX could not take the foreground (foreground=$fg)."
        Write-Host '  [skip] This case drives real keyboard/mouse input, so NOTHING was asserted.'
        Write-Host '  [skip] Re-run when no other process is holding the foreground.'
    } else {
        $rows = Get-LogRows
        $vs = Get-VirtualScreen
        Write-Host "  realized rows on screen = $($rows.Count)"
        $row = $rows | Where-Object { $_.Text.Contains($queryText) -and $_.Rect.Bottom -lt ($vs.Bottom - 20) } |
            Sort-Object { $_.Rect.Y } | Select-Object -Last 1
        $linkRow = $rows | Where-Object { $_.Text.Contains('mccx-link-probe') -and $_.Rect.Bottom -lt ($vs.Bottom - 20) } |
            Sort-Object { $_.Rect.Y } | Select-Object -Last 1

        if ($env:MCCX_TEST_NO_MOUSE) {
            Write-Host '  [skip] A/B mouse halves disabled by MCCX_TEST_NO_MOUSE=1'
        } else {
            Write-Host "`n=== A) drag-select a log row, then Ctrl+C ==="
            Assert-True ($null -ne $row) 'A0 a realized probe row is on screen'
            if ($row) {
                Set-ClipboardText $sentinelA
                Select-LogRowRange $row
                [System.Windows.Forms.SendKeys]::SendWait('^c')
                Start-Sleep -Milliseconds 900
                $clipA = Get-ClipboardText
                Write-Host "  A row text  = [$($row.Text)]"
                Write-Host "  A clipboard = [$clipA]"
                Assert-True ($clipA -ne $sentinelA -and $clipA.Length -gt 0) 'A1 Ctrl+C after a drag-select puts text on the clipboard'
                Assert-True ($row.Text.Contains($clipA)) 'A2 the clipboard holds a character range of that log line'
                Assert-True ($clipA.Length -lt $row.Text.Length) 'A3 the copy is a sub-range, not the whole line'
            }

            Write-Host "`n=== B) Ctrl+left-click on a link ==="
            if (-not $env:MCCX_TEST_LINK) {
                Write-Host '  [skip] opt-in: set MCCX_TEST_LINK=1 (opens one browser tab)'
            } elseif (-not $linkRow) {
                Assert-True $false 'B0 a realized row holding the probe link is on screen'
            } else {
                $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $script:LinkPort)
                $listener.Start()
                try {
                    Set-Foreground
                    Start-Sleep -Milliseconds 300
                    $y = [int]($linkRow.Rect.Y + $linkRow.Rect.Height / 2)
                    $x = [int]($linkRow.Rect.X + 40)
                    Set-CtrlState 'Down'
                    Move-MouseTo $x $y
                    Set-MouseButtonState 'Down'
                    Start-Sleep -Milliseconds 120
                    Set-MouseButtonState 'Up'
                    Set-CtrlState 'Up'

                    $got = $false
                    $req = ''
                    $deadline = (Get-Date).AddSeconds(15)
                    while ((Get-Date) -lt $deadline) {
                        if ($listener.Pending()) {
                            $client = $listener.AcceptTcpClient()
                            $stream = $client.GetStream()
                            $stream.ReadTimeout = 3000
                            $buf = New-Object byte[] 256
                            $n = 0
                            try { $n = $stream.Read($buf, 0, $buf.Length) } catch { $n = 0 }
                            $req = ([System.Text.Encoding]::ASCII.GetString($buf, 0, $n) -split "`r`n")[0]
                            $client.Close()
                            $got = $true
                            break
                        }
                        Start-Sleep -Milliseconds 200
                    }
                    Write-Host "  B first request line = [$req]"
                    Assert-True $got 'B1 Ctrl+left-click on a link opened it (the browser fetched the URL)'
                } finally {
                    try { $listener.Stop() } catch { }
                }
            }
        }

        Write-Host "`n=== C) Ctrl+A / Ctrl+C with the focus in the log search box (not in the log) ==="
        # The accelerators hang off the Page, so they work even though LogScrollHost does not hold
        # the focus. The search box is left EMPTY so the assertion cannot be confused with the
        # filter's own selection (in filter mode the log selection is exactly the query text).
        $searchBox = Find-ById $script:AppRoot 'LogSearchBox'
        $logHost = Get-LogList
        Assert-True ($null -ne $searchBox) 'C0 the log search box exists'
        if ($searchBox -and $logHost) {
            Set-Edit $searchBox ''
            Start-Sleep -Milliseconds 800
            $logHost.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait('^a')
            Start-Sleep -Milliseconds 500
            Set-ClipboardText $sentinelC
            $searchBox.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait('^c')
            Start-Sleep -Milliseconds 900
            $clipC = Get-ClipboardText
            Write-Host "  C copied $($clipC.Length) chars, lines = $(@($clipC -split "`n").Count)"
            Assert-True ($clipC -ne $sentinelC -and $clipC.Length -gt 0) 'C1 Ctrl+A selected the log and Ctrl+C copied it while the focus sat in the search box'
            Assert-True ($clipC.Contains("`n")) 'C2 the whole log was selected, not a single line'
            Assert-True ($clipC.Contains($queryText)) 'C3 the copied text really is log content'
        }

        Write-Host "`n=== D) Ctrl+C inside the command box must NOT copy the log ==="
        $cmdBox = Find-ById $script:AppRoot 'CommandBox'
        Assert-True ($null -ne $cmdBox) 'D0 the command box exists (only while connected)'
        if ($cmdBox) {
            Set-ClipboardText $sentinelD
            Set-Edit $cmdBox 'LOGKEY_CMDTEXT'
            Start-Sleep -Milliseconds 400
            $cmdBox.SetFocus()
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait('^a')
            Start-Sleep -Milliseconds 300
            [System.Windows.Forms.SendKeys]::SendWait('^c')
            Start-Sleep -Milliseconds 700
            $clipD = Get-ClipboardText
            Write-Host "  D clipboard = [$clipD]"
            Assert-True ($clipD -eq 'LOGKEY_CMDTEXT') 'D1 Ctrl+C in the command box copies the command text (log accelerators do not hijack it)'
            Set-Edit $cmdBox ''
            Start-Sleep -Milliseconds 300
        }
    }
} finally {
    Write-Host "`n=== cleanup ==="
    try { Set-ClipboardText $savedClip } catch { }
    try { [System.Windows.Forms.Cursor]::Position = $savedCursor } catch { }
    try { if ($session) { Exit-TestSession -Session $session } } catch { Write-Host ("  [clean] " + $_.Exception.Message) }
    try { Disconnect-App } catch { }
    try { Stop-App } catch { }
    try { Restore-Foreground } catch { }
}

$fail = Get-FailureCount
Write-Host "SUMMARY g_logkeys FAILURES=$fail"
exit ([int]($fail -gt 0))
