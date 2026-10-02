using System.Reflection;
using System.Runtime.InteropServices;
using MCCX.Core;
using Microsoft.UI.Xaml;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MCCX_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    // 默认宽度对齐用户给的参照：PowerShell（Windows Terminal）窗。
    // 实测参考窗 2372px 边框 / 2350px 可见（=1175 DIP 可见宽）；本窗边框差同为 22px，
    // 取 1186 DIP 后可见宽正好也是 1175 DIP——两窗并排左右沿完全对齐。
    // 参数行（服务器/端口/用户名/版本 + 连接、断开）在 1186 下服务器框仍有 ~270 DIP；
    // 手动缩窗时守住 1110 DIP 的下限——再窄这一行就会从右边被挤掉（输入框/按钮被裁）。
    private const int DefaultWidthDip = 1186;
    private const int MinWidthDip = 1110;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>DIP → 物理像素（AppWindow 的尺寸单位是物理像素）。</summary>
    private int DipToPx(int dip)
    {
        uint dpi = GetDpiForWindow(Hwnd);
        if (dpi == 0) dpi = 96;
        return (int)Math.Round(dip * dpi / 96.0);
    }

    /// <summary>屏幕比目标还窄时按屏宽收，窗口不会开出屏幕外。</summary>
    private int FitToScreen(int wantedPx)
    {
        int screenWidth = GetSystemMetrics(0); // SM_CXSCREEN：主屏物理像素宽
        return screenWidth > 0 ? Math.Min(wantedPx, screenWidth - 8) : wantedPx;
    }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 标题栏副标题显示版本号，取 csproj 的 InformationalVersion（单一来源）；
        // SourceLink 可能给它追加 +提交哈希，展示时去掉
        string version = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        int plus = version.IndexOf('+');
        AppTitleBar.Subtitle = plus >= 0 ? version[..plus] : version;

        // unpackaged 应用必须给绝对路径，相对路径按进程工作目录解析，启动方式不同就会失效
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        // 首次激活时按设计宽度拉一次（构造期窗口尺寸还没定下来）
        bool widthApplied = false;
        Activated += (_, _) =>
        {
            if (widthApplied)
                return;
            widthApplied = true;

            int target = FitToScreen(DipToPx(DefaultWidthDip));
            if (AppWindow.Size.Width < target)
                AppWindow.Resize(new SizeInt32(target, AppWindow.Size.Height));
        };

        // 手动缩窗时守住下限：参数行（服务器/端口/用户名/版本/连接/断开）不能被从右边挤掉。
        // 尺寸变化回调里直接 Resize 会重入，丢给 DispatcherQueue 下一帧再改。
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidSizeChange)
                return;

            int min = FitToScreen(DipToPx(MinWidthDip));
            if (AppWindow.Size.Width >= min - 1) // 留 1px 余量，避免像素取整来回抖
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                int limit = FitToScreen(DipToPx(MinWidthDip));
                if (AppWindow.Size.Width < limit - 1)
                    AppWindow.Resize(new SizeInt32(limit, AppWindow.Size.Height));
            });
        };

        // 关窗时断开 MCC 连接并释放会话，避免 MCC 的前台线程阻止进程退出
        Closed += (_, _) =>
        {
            if (RootFrame.Content is MainPage page)
                page.ViewModel.Dispose();

            // 连接过会话的场景下，WinUI 消息循环不自行退出、Application.Exit() 也无效
            //（实测退出信号被吞），约 3.4s 后在 XAML 层 CoreMessagingXP.dll 以
            // 0xC000027B 崩掉、进程变僵尸。此时会话/子进程/账号库均已释放，直接结束进程。
            Environment.Exit(0);
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }
}
