using System.Runtime.InteropServices;
using MCCX.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

    #region 主题（暗色模式：黑底白字，2026-10-03 用户要求）

    /// <summary>当前窗口实例：界面层切主题要拿到窗口（本程序只有这一个窗）。</summary>
    internal static MainWindow? Instance { get; private set; }

    /// <summary>
    /// 暗色模式的底色：#121212（2026-10-05 用户指定，不再用纯黑）。
    /// 整窗、标题栏、卡片底一律用它，暗色下不会出现"黑底 + 纯黑卡片"分不出层次的情况。
    /// </summary>
    private static readonly Windows.UI.Color DarkBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x12, 0x12, 0x12);

    private static readonly Brush DarkWindowBrush = new SolidColorBrush(DarkBackgroundColor);

    /// <summary>启动时读上次的选择：默认浅色（保持既有外观）。</summary>
    private static bool ReadStoredTheme() => UiSettingsStore.ReadDarkMode();

    /// <summary>
    /// 「设置」子菜单里 Debug 模式开关的初始状态（2026-10-09 需求⑤）。
    /// 只反映持久化值；真正把开关打进 MCC 是 MCCSession 在构造 McClient 前读同一个标志。
    /// </summary>
    private bool ReadStoredDebugMode() => UiSettingsStore.ReadDebugMode();

    /// <summary>
    /// Debug 模式开关（2026-10-09 需求⑤）：存进 ui-settings.json，对**下一次（重）连**生效
    /// —— MCC 的 DebugEnabled 在 McClient 构造时读一次，已连上的会话不受影响。
    /// 2026-10-09 第五轮反馈：状态不再用打勾显示，标签后空一格写「开 / 关」（与开关行一致），
    /// 存储值是唯一事实源：每次点击都从文件读旧值翻转，不存在界面态与文件态打架。
    /// </summary>
    private void DebugModeItem_Click(object sender, RoutedEventArgs e)
    {
        bool on = !ReadStoredDebugMode();
        UiSettingsStore.WriteDebugMode(on);
        DebugModeItem.Text = DebugModeText(on);
    }

    /// <summary>「设置」菜单里 Debug 模式的状态文字：标签 + 空格 + 开/关。</summary>
    private static string DebugModeText(bool on) => on ? "Debug 模式 开" : "Debug 模式 关";

    /// <summary>应用主题（不动设置文件，启动时用）。</summary>
    private void ApplyThemeCore(bool darkMode)
    {
        ElementTheme theme = darkMode ? ElementTheme.Dark : ElementTheme.Light;

        // 主题打在窗口根元素上：整棵可见内容树（含标题栏）跟着变。
        // 背景：浅色留空 = 让 Mica 底透出来，和以前一模一样；暗色盖一层纯黑，
        // 标题栏与内容区一起黑，不依赖系统是不是深色
        // （Background 只有 Panel/Control/Border 上有，本窗内容是 XAML 里那层 Grid）。
        //
        // 弹层不会自动跟着走：Flyout / ComboBox 下拉会跟（WinAppSDK 2.3 实测），
        // ContentDialog **不会**，要弹之前手动抄主题 + 改它背后的烟雾层
        // —— 见 MainPage.SyncDialogTheme 与文档 §9"WinUI 主题三坑"。
        //
        // 注意：**不要改 Application.Current.RequestedTheme** —— WinUI 3 在启动后设它
        // 会直接抛 COMException 0x80131515（实测，见 %TEMP%\mccx-unhandled.log），
        // 异常走 UnhandledException 会把进程打死、连窗口都不出，只能用元素主题。
        if (Content is Panel root)
        {
            root.RequestedTheme = theme;
            root.Background = darkMode ? DarkWindowBrush : null;
        }

        ApplyCaptionButtonColors(darkMode);
    }

    /// <summary>
    /// 系统标题栏按钮（最小化/最大化/关闭）的配色。
    /// <c>ExtendsContentIntoTitleBar=true</c> 时这三个按钮仍由系统绘制，默认跟着**系统**主题走，
    /// 于是暗色模式下窗口右上角会留三块浅色方块（2026-10-05 用户反馈）。
    /// 这里在暗色时把它们染成与内容区一致的 #121212 + 白字；
    /// 浅色时**不能**交回系统（null）：那样按钮被画成不透明白块，压在 Mica 标题栏上
    /// 就是一块块方砖、底色与背景对不上（2026-10-08 用户反馈），改成透明让底色
    /// 直接透出标题栏背景，观感与内容区连成一片，悬停/按下反馈仍由系统画。
    /// </summary>
    private void ApplyCaptionButtonColors(bool darkMode)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            if (!darkMode)
            {
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonForegroundColor = null;
                titleBar.ButtonInactiveForegroundColor = null;
                titleBar.ButtonHoverBackgroundColor = null;
                titleBar.ButtonPressedBackgroundColor = null;
                return;
            }

            titleBar.ButtonBackgroundColor = DarkBackgroundColor;
            titleBar.ButtonInactiveBackgroundColor = DarkBackgroundColor;
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.White;
            // 悬停/按下给两级浅一档的深灰：纯 #121212 上看不出按钮可点
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x23, 0x23, 0x23);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x2E, 0x2E);
        }
        catch (Exception)
        {
            // 取不到标题栏（极早期构造或非桌面会话）不影响主流程：顶多按钮仍是系统色
        }
    }

    /// <summary>界面切主题：应用 + 记住（下次启动保持）。</summary>
    public void ApplyTheme(bool darkMode)
    {
        ApplyThemeCore(darkMode);
        UiSettingsStore.WriteDarkMode(darkMode);
    }

    #endregion

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        // Debug 模式状态文字回填上次的值（必须在 InitializeComponent 之后：XAML 已建好该 MenuFlyoutItem）
        DebugModeItem.Text = DebugModeText(ReadStoredDebugMode());

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 先落主题再导航：否则页面先按浅色画出来再翻黑，会闪一下。
        // 放在 SetTitleBar 之后，是因为暗色还要顺带把系统标题栏按钮染色（ApplyCaptionButtonColors）。
        ApplyThemeCore(ReadStoredTheme());

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
