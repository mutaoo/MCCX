using MCCX.Core.Ipc;

namespace MCCX_App;

/// <summary>
/// 自定义进程入口，一身两职：
/// ① 正常启动 = WinUI 界面（逻辑与 XAML 编译器生成的 Main 完全一致）；
/// ② <c>MCCX.exe --runner --cmd &lt;句柄&gt; --evt &lt;句柄&gt;</c> = 多开子进程，
///    只跑一个独立的 MCC 会话（见 <see cref="RunnerHost"/>），完全不碰 WinUI，因此不会弹第二个窗口。
///
/// 用法前提：csproj 里定义了 DISABLE_XAML_GENERATED_MAIN，关掉编译器生成的 Main。
/// 好处是子进程与主程序共用同一个 exe、同一份依赖，不存在“漏拷运行文件”的问题。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--runner", StringComparison.Ordinal))
            return RunnerHost.Run(GetOption(args, "--cmd"), GetOption(args, "--evt"));

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(_ =>
        {
            global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext context = new(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            App.XamlGeneratedCreateApplicationInstance();
        });

        return 0;
    }

    /// <summary>取 <c>--名字 值</c> 形式的参数，取不到返回 null。</summary>
    private static string? GetOption(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
