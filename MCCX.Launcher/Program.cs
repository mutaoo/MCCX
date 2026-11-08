using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MCCX_Launcher;

/// <summary>
/// 便携包根启动器（2026-10-25 用户要求：包根一眼能看到启动文件与用户数据）。
///
/// <para>发布包结构：<c>包根\MCCX.exe</c>（本启动器）+ <c>包根\UserData\</c> + <c>包根\app\</c>（真正的程序）。
/// 本程序只负责把命令行参数原样转给 <c>app\MCCX.exe</c>，把工作目录设成 <c>app\</c>，然后自己退出；
/// 不加载 WinUI，也不参与多开（子进程由主程序用 <c>Environment.ProcessPath</c> 从 app\ 拉起）。</para>
///
/// <para>主程序靠包根的 <c>.mccx-layout</c> 标记文件识别这套布局，把数据根定在包根；
/// 从源码直接跑 <c>bin\Debug\...\MCCX.exe</c> 时没有标记，数据仍放在 exe 同级的 <c>UserData\</c>。</para>
/// </summary>
internal static class Program
{
    /// <summary>主程序相对启动器的位置（发布包布局固定为 包根\app\MCCX.exe）。</summary>
    private const string AppRelativePath = @"app\MCCX.exe";

    /// <summary>用户数据目录名（与 app\ 平级，启动时顺手建出来，方便用户找到）。</summary>
    private const string UserDataFolderName = "UserData";

    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppContext.BaseDirectory;
        string appExe = Path.Combine(root, AppRelativePath);

        if (!File.Exists(appExe))
        {
            ShowError($"找不到主程序：\n{appExe}\n\n请把 MCCX.exe 和 app 文件夹放在同一个目录里。");
            return 1;
        }

        // 数据目录与 app 平级：先建出来，用户打开包就能看到数据放在哪
        try
        {
            Directory.CreateDirectory(Path.Combine(root, UserDataFolderName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 建不出来不拦启动：只读位置由主程序退到 %LOCALAPPDATA%\MCCX
            _ = ex;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = appExe,
            WorkingDirectory = Path.GetDirectoryName(appExe)!,
            UseShellExecute = false,
        };

        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowError($"启动主程序失败：\n{ex.Message}");
            return 1;
        }
    }

    /// <summary>启动器没有控制台，出错用系统消息框提示。</summary>
    private static void ShowError(string text) =>
        MessageBoxW(IntPtr.Zero, text, "MCCX", 0x00000010 /* MB_ICONERROR */);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
