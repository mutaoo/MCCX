# 三条 UI 改动的视觉验收：①点文字展开+箭头+悬停底色 ②生物过滤做成参数子列表+每类全选 ③服务器框加长
# 注意：第 4) 步“生物过滤子下拉”已过时——生物过滤现已是模态弹窗，几何/视觉验收看 v_ui7.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TopHelper {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fUnknown);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] p, int cbSize);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public static void MoveTo(int x, int y) {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dx = (int)Math.Round(x * 65535.0 / (sw - 1));
        inputs[0].mi.dy = (int)Math.Round(y * 65535.0 / (sh - 1));
        inputs[0].mi.dwFlags = (int)(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
        SendInput(1, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

function Raise-Mccx {
    $h = [IntPtr]$script:Hwnd
    $null = [TopHelper]::ShowWindow($h, 9)
    [void][TopHelper]::SetWindowPos($h, [TopHelper]::HWND_TOPMOST, 0, 0, 0, 0,
        [TopHelper]::SWP_NOMOVE -bor [TopHelper]::SWP_NOSIZE -bor [TopHelper]::SWP_SHOWWINDOW)
    [TopHelper]::SwitchToThisWindow($h, $true)
    Start-Sleep -Milliseconds 400
    [void][TopHelper]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 400
}

function Grab {
    param([string]$Path)
    Raise-Mccx
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Path"
}

function Move-Pointer {
    param([int]$X, [int]$Y, [int]$WaitMs = 1000)
    [TopHelper]::MoveTo($X, $Y)
    Start-Sleep -Milliseconds 250
    [TopHelper]::MoveTo($X + 1, $Y)
    Start-Sleep -Milliseconds $WaitMs
}

function Find-NamedAny {
    param([string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

$null = Start-App
Save-Foreground
Raise-Mccx
try {
    # --- 服务器框实际宽度（物理像素 / DPI 2.0 = DIP） ---
    $srv = Find-ById $script:AppRoot 'ServerBox'
    $sr = $srv.Current.BoundingRectangle
    Write-Host ("ServerBox w={0}px ({1} DIP)  x={2}..{3}" -f [int]$sr.Width, [int]($sr.Width / 2), [int]$sr.X, [int]($sr.X + $sr.Width))
    foreach ($n in @('PortBox', 'UserBox', 'VersionBox')) {
        $e = Find-ById $script:AppRoot $n
        if ($e) { $r = $e.Current.BoundingRectangle; Write-Host ("{0,-10} w={1}px" -f $n, [int]$r.Width) }
    }
    $conn = Get-UiButton '连接'
    $cr = $conn.Current.BoundingRectangle
    Write-Host ("连接按钮 y={0}（应在状态行）" -f [int]$cr.Y)

    # 1) 静止态：标签带不带下拉箭头、服务器框有多长
    Move-Pointer ([int]($sr.X - 60)) ([int]($sr.Y - 80)) 600     # 先把指针挪开
    Grab "$out\v3_main.png"

    # 2) 悬停“自动砍怪”标签：按钮底色应变化（提示可点开）
    $lbl = Get-UiButton '自动砍怪选项'
    $lr = $lbl.Current.BoundingRectangle
    Write-Host ("自动砍怪选项 rect=({0},{1}) {2}x{3}" -f [int]$lr.X, [int]$lr.Y, [int]$lr.Width, [int]$lr.Height)
    Move-Pointer ([int]($lr.X + $lr.Width / 2)) ([int]($lr.Y + $lr.Height / 2))
    Grab "$out\v3_hover_label.png"

    # 3) 点文字展开参数弹层
    try {
        $ec = $lbl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $lbl }
    Start-Sleep -Milliseconds 1200
    Grab "$out\v3_fly_params.png"

    # 4) 展开子列表“生物过滤”，看分类 + 全选框
    $exp = Find-NamedAny '生物过滤'
    if (-not $exp) { Write-Host '!! 找不到“生物过滤”子列表' }
    else {
        Write-Host ("生物过滤 控件类型=" + $exp.Current.ControlType.ProgrammaticName)
        try {
            $ec2 = $exp.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec2.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec2.Expand() }
        } catch {
            try {
                $tp = $exp.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
                if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
            } catch {
                try { $ip = $exp.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $ip.Invoke() }
                catch { Write-Host '!! 子列表展开失败（无可用 Pattern）' }
            }
        }
        Start-Sleep -Milliseconds 1200
        foreach ($c in @('敌对生物全选', '中立生物全选', '友好生物全选', '过滤模式', '僵尸')) {
            $el = Find-UiElement $script:CT::CheckBox $c
            if (-not $el) { $el = Find-NamedAny $c }
            if ($el) {
                $r2 = $el.Current.BoundingRectangle
                $ct2 = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
                if ([double]::IsInfinity($r2.X) -or [double]::IsInfinity($r2.Y) -or
                    [double]::IsInfinity($r2.Width) -or [double]::IsInfinity($r2.Height)) {
                    Write-Host ("  {0,-14} {1,-9} rect=<未测量/在滚动区外>" -f $c, $ct2)
                } else {
                    Write-Host ("  {0,-14} {1,-9} rect=({2},{3}) {4}x{5}" -f $c, $ct2, [int]$r2.X, [int]$r2.Y, [int]$r2.Width, [int]$r2.Height)
                }
            } else { Write-Host "  $c NOT FOUND" }
        }
        Grab "$out\v3_fly_filter.png"
    }

    Write-Host 'done'
} finally {
    try { Close-AllFlyouts } catch { }
    [void][TopHelper]::SetWindowPos([IntPtr]$script:Hwnd, [TopHelper]::HWND_NOTOPMOST, 0, 0, 0, 0,
        [TopHelper]::SWP_NOMOVE -bor [TopHelper]::SWP_NOSIZE -bor [TopHelper]::SWP_SHOWWINDOW)
    Restore-Foreground
    Stop-App
}
exit 0
