using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MCCX_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MCCX_App;

/// <summary>
/// 主界面。仅负责控件交互（回车发送、日志滚动、弹“添加账号”窗口），业务逻辑都在 ViewModel 里。
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>用户上翻超过这个距离后不再自动跟随到底部。</summary>
    private const double LogFollowThresholdPx = 80;

    public MainViewModel ViewModel { get; }

    private ScrollViewer? _logScrollViewer;
    private bool _logScrollPending;
    private ObservableCollection<LogEntry>? _hookedLogs;

    public MainPage()
    {
        // x:Bind 在加载前读取 ViewModel，必须先于 InitializeComponent 赋值
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();

        // 日志列表属于选中账号，切换账号后要重新挂载 CollectionChanged
        HookLogs();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RequestAddAccount += ShowAddAccountDialog;
        Loaded += OnPageLoaded;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Logs))
            HookLogs();
    }

    private void HookLogs()
    {
        ObservableCollection<LogEntry> logs = ViewModel.Logs;
        if (ReferenceEquals(_hookedLogs, logs))
            return;

        if (_hookedLogs is not null)
            _hookedLogs.CollectionChanged -= OnLogsCollectionChanged;

        _hookedLogs = logs;
        _hookedLogs.CollectionChanged += OnLogsCollectionChanged;
    }

    /// <summary>“添加账号”弹窗：窗口是视图层的事，填完把结果交给 ViewModel 入库并开新会话。</summary>
    private async void ShowAddAccountDialog()
    {
        if (XamlRoot is null)
            return;

        AddAccountDialog dialog;
        try
        {
            dialog = new AddAccountDialog
            {
                XamlRoot = XamlRoot,
            };
        }
        catch (Exception ex)
        {
            // 窗口建不起来不能把整个界面带崩：原因写进日志让用户看见
            ViewModel.SelectedAccount?.WriteNote($"§c打开“添加账号”窗口失败：{ex}");
            return;
        }

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ViewModel.SelectedAccount?.WriteNote($"§c显示“添加账号”窗口失败：{ex}");
            return;
        }

        if (result != ContentDialogResult.Primary)
            return;

        ViewModel.AddAccount(
            dialog.ServerHost,
            dialog.Port,
            dialog.MinecraftVersion,
            dialog.Username,
            dialog.AutoConnect);
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer ??= FindScrollViewer(LogList);
#if DEBUG
        DumpLayout();
#endif
    }

#if DEBUG
    /// <summary>
    /// 开发期自检：把连接参数区和自动化区的视觉树布局写到临时文件，
    /// 用来排查“控件错位/整行溢出”这类只能看见结果、看不见原因的问题。
    /// Release 构建不参与编译，不产生任何运行时开销。
    /// </summary>
    private void DumpLayout()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"window {ActualWidth}x{ActualHeight} scale={XamlRoot?.RasterizationScale}");
            sb.AppendLine("--- 祖先链 ---");
            DependencyObject? p = AutomationRow;
            while (p is FrameworkElement a)
            {
                sb.AppendLine($"  {a.GetType().Name} name={a.Name} w={a.ActualWidth:0.##} desired={a.DesiredSize.Width:0.##} minW={a.MinWidth}");
                p = a.Parent;
            }
            sb.AppendLine("--- ParamsGrid ---");
            AppendLayout(sb, ParamsGrid, 0);
            sb.AppendLine("--- AutomationRow ---");
            AppendLayout(sb, AutomationRow, 0);

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mccx_layout.txt");
            System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        }
        catch
        {
            // 自检失败不能影响界面
        }
    }

    private static void AppendLayout(System.Text.StringBuilder sb, DependencyObject node, int depth)
    {
        if (node is not FrameworkElement fe)
            return;

        string extra = fe switch
        {
            DropDownButton db => $" content={db.Content?.GetType().Name}" +
                                 $" flyout={(db.Flyout is Flyout fly ? fly.Content?.GetType().Name : db.Flyout?.GetType().Name)}",
            ToggleSwitch ts => $" onContent={ts.OnContent} offContent={ts.OffContent}",
            TextBlock tb => $" text={tb.Text}",
            _ => string.Empty,
        };

        sb.AppendLine($"{new string(' ', depth * 2)}{fe.GetType().Name} name={fe.Name} " +
                      $"w={fe.ActualWidth:0.##} h={fe.ActualHeight:0.##} desired={fe.DesiredSize.Width:0.##} " +
                      $"minW={fe.MinWidth} x={fe.Translation.X:0.##} margin={fe.Margin} halign={fe.HorizontalAlignment} " +
                      $"vis={fe.Visibility}{extra}");

        if (depth >= 7)
            return;

        int count = VisualTreeHelper.GetChildrenCount(fe);
        for (int i = 0; i < count; i++)
            AppendLayout(sb, VisualTreeHelper.GetChild(fe, i), depth + 1);
    }
#endif

    /// <summary>
    /// 一条日志可能拆成多行、MCC 又会成批刷日志，CollectionChanged 会在一帧内连发几十次。
    /// 以前每次都 ScrollIntoView，列表反复重排导致闪烁；这里合并成每帧最多滚一次。
    /// </summary>
    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_logScrollPending)
            return;

        _logScrollPending = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FollowLogToEnd))
            _logScrollPending = false;
    }

    private void FollowLogToEnd()
    {
        _logScrollPending = false;

        _logScrollViewer ??= FindScrollViewer(LogList);
        if (_logScrollViewer is not { } scroll)
            return;

        double end = scroll.ScrollableHeight;
        if (end <= 0)
            return;

        // 用户正在翻历史时不要抢滚动条（否则也会显得乱闪）
        if (end - scroll.VerticalOffset > LogFollowThresholdPx)
            return;

        // 关闭滚动动画：动画帧同样会被新一轮日志打断，观感上就是闪
        scroll.ChangeView(null, end, null, disableAnimation: true);
    }

    /// <summary>从 ListView 的模板里找到承载日志的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
            return scrollViewer;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        if (ViewModel.SendCommand.CanExecute(null))
            ViewModel.SendCommand.Execute(null);

        e.Handled = true;
    }

    /// <summary>
    /// 参数下拉（Flyout）展开时，WinUI 把焦点交给第一个文本框，但光标停在文本最前面，
    /// 用户得再点一下或按 End 才能改值。这里在展开后把光标补到文本末尾。
    /// </summary>
    private void OnFlyoutOpened(object sender, object e)
    {
        if (sender is not Flyout flyout)
            return;

        // Opened 触发的瞬间焦点可能还没落定，延后一拍再处理
        DispatcherQueue.TryEnqueue(() =>
        {
            TextBox? box = FindFocusedTextBox(flyout.Content);
            if (box is null)
                return; // 焦点在下拉框等其它控件上就不打扰它

            string text = box.Text ?? string.Empty;
            box.Select(text.Length, 0);
        });
    }

    /// <summary>在 Flyout 内容树里找当前拿到焦点的文本框；都没有焦点就返回 null（不抢焦点）。</summary>
    private static TextBox? FindFocusedTextBox(DependencyObject? root)
    {
        if (root is null)
            return null;

        if (root is TextBox box && box.FocusState != FocusState.Unfocused)
            return box;

        int count;
        try
        {
            count = VisualTreeHelper.GetChildrenCount(root);
        }
        catch
        {
            return null; // 个别容器不支持遍历，跳过即可
        }

        for (int i = 0; i < count; i++)
        {
            TextBox? found = FindFocusedTextBox(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }
}
