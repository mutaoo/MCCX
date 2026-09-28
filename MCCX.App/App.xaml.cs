using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using MCCX.Core;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MCCX_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    /// <summary>应用专属日志目录（temp），用于把未处理异常落盘，便于排查 XAML/运行时崩溃。</summary>
    internal static readonly string CrashLogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "mccx-unhandled.log");

    public App()
    {
        InitializeComponent();

        // WinUI 的未处理异常默认只会静默终止进程，这里先落盘再放行，
        // 否则“点了按钮就没了”这类问题只能靠猜。
        UnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\r\n\r\n");
            }
            catch
            {
                // 落盘失败也不能二次抛异常
            }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 必须在创建任何页面/会话之前安装 MCC 的 GUI 控制台后端
        MCCRuntime.Initialize();

        _window = new MainWindow();
        _window.Activate();
    }
}
