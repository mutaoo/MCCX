# 要求①验收：标签按钮的悬停底色（移开→悬停→再移开，三次采样对比亮度）+ 3 倍放大对比图
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
$zoom = 3

function Raise-Mccx {
    $h = [IntPtr]$script:Hwnd
    $null = [TopHelper]::ShowWindow($h, 9)
    [void][TopHelper]::SetWindowPos($h, [TopHelper]::HWND_TOPMOST, 0, 0, 0, 0,
        [TopHelper]::SWP_NOMOVE -bor [TopHelper]::SWP_NOSIZE -bor [TopHelper]::SWP_SHOWWINDOW)
    [TopHelper]::SwitchToThisWindow($h, $true)
    Start-Sleep -Milliseconds 600
    [void][TopHelper]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 800
    $fg = [TopHelper]::GetForegroundWindow()
    Write-Host ("  [fg] match={0}" -f ($fg -eq $h))
}

function Snap {
    param([string]$Path, [System.Drawing.Rectangle]$R)
    $bmp = New-Object System.Drawing.Bitmap $R.Width, $R.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($R.X, $R.Y, 0, 0, (New-Object System.Drawing.Size($R.Width, $R.Height)))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Mean {
    param([string]$Path)
    $b = [System.Drawing.Bitmap]::FromFile($Path)
    $sum = 0L; $n = 0L
    for ($y = 0; $y -lt $b.Height; $y += 2) {
        for ($x = 0; $x -lt $b.Width; $x += 2) {
            $c = $b.GetPixel($x, $y)
            $sum += [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
            $n++
        }
    }
    $b.Dispose()
    return [math]::Round($sum / $n, 2)
}

function Zoom {
    param([string]$Src, [string]$Dst)
    $b = [System.Drawing.Bitmap]::FromFile($Src)
    $z = New-Object System.Drawing.Bitmap ($b.Width * $zoom), ($b.Height * $zoom)
    $g = [System.Drawing.Graphics]::FromImage($z)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($b, 0, 0, $z.Width, $z.Height)
    $z.Save($Dst, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $z.Dispose(); $b.Dispose()
}

$null = Start-App
Save-Foreground
try {
    Raise-Mccx
    $lbl = Get-UiButton '自动砍怪选项'
    $lr = $lbl.Current.BoundingRectangle
    $rect = New-Object System.Drawing.Rectangle ([int]($lr.X - 8)), ([int]($lr.Y - 8)), ([int]($lr.Width + 16)), ([int]($lr.Height + 16))
    Write-Host ("label rect=({0},{1}) {2}x{3}" -f $rect.X, $rect.Y, $rect.Width, $rect.Height)

    # 1) 指针在别处（常态）
    [TopHelper]::MoveTo(40, 40)
    Start-Sleep -Milliseconds 900
    $p1 = "$out\v6_normal.png"; Snap $p1 $rect

    # 2) 指针移到标签中心，等 300ms（底色已变、tooltip 还没出来）
    [TopHelper]::MoveTo([int]($lr.X + $lr.Width / 2), [int]($lr.Y + $lr.Height / 2))
    Start-Sleep -Milliseconds 300
    $p2 = "$out\v6_hover_early.png"; Snap $p2 $rect

    # 3) 再等一会，截整屏（看 tooltip + 悬停态）
    Start-Sleep -Milliseconds 1500
    $sb = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $b2 = New-Object System.Drawing.Bitmap $sb.Width, $sb.Height
    $g2 = [System.Drawing.Graphics]::FromImage($b2)
    $g2.CopyFromScreen($sb.Location, [System.Drawing.Point]::Empty, $sb.Size)
    $b2.Save("$out\v6_hover_full.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g2.Dispose(); $b2.Dispose()

    # 4) 移开，确认回弹
    [TopHelper]::MoveTo(40, 40)
    Start-Sleep -Milliseconds 900
    $p3 = "$out\v6_normal2.png"; Snap $p3 $rect

    $m1 = Mean $p1; $m2 = Mean $p2; $m3 = Mean $p3
    Write-Host ("mean  normal={0}  hover={1}  normal2={2}   delta={3}" -f $m1, $m2, $m3, [math]::Round($m2 - $m1, 2))
    if ([math]::Abs($m2 - $m1) -ge 2.0) { Write-Host 'HOVER_BG = 有（亮度变化 >= 2）' } else { Write-Host 'HOVER_BG = 不明显（亮度变化 < 2）' }
    if ([math]::Abs($m3 - $m1) -le 1.5) { Write-Host 'HOVER_RESTORE = 移开后回到常态' } else { Write-Host 'HOVER_RESTORE = 未回弹' }

    Zoom $p1 "$out\v6_label_normal_z.png"
    Zoom $p2 "$out\v6_label_hover_z.png"
    Write-Host 'saved v6_label_normal_z.png / v6_label_hover_z.png / v6_hover_full.png'
    Write-Host 'done'
} finally {
    [void][TopHelper]::SetWindowPos([IntPtr]$script:Hwnd, [TopHelper]::HWND_NOTOPMOST, 0, 0, 0, 0,
        [TopHelper]::SWP_NOMOVE -bor [TopHelper]::SWP_NOSIZE -bor [TopHelper]::SWP_SHOWWINDOW)
    Restore-Foreground
    Stop-App
}
exit 0
