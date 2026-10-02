using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using MCCX_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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
        {
            HookLogs();

            // 换账号等于换一份日志集合：列表会重新排版并停在顶端，
            // 而且“离底部太远就不跟随”的逻辑会让它再也追不上最新行，这里强制拉到底。
            ForceScrollLogToEnd();
        }
    }

    /// <summary>把日志列表滚到底部最新一行（切账号后用，不考虑用户之前翻到哪儿）。</summary>
    private void ForceScrollLogToEnd()
    {
        // 先等 ItemsSource 换好、再等一帧量完布局，两次入队才能滚到真正的末尾
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _logScrollViewer ??= FindScrollViewer(LogList);

            if (ViewModel.Logs.Count > 0)
                LogList.ScrollIntoView(ViewModel.Logs[^1]);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_logScrollViewer is { } scroll && scroll.ScrollableHeight > 0)
                    scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            });
        });
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

    /// <summary>
    /// 生物过滤：入口在“砍怪参数”里，点了改开和“添加账号”一样的模态弹窗。
    /// 打开前先收起参数弹层（不然弹窗压在弹层上）；关窗后按开窗前的样子展回去——
    /// 弹窗只是过滤设置的详情页，关掉就顺手把“砍怪参数”一起带关，用户还得重开一次才能继续调距离/冷却。
    /// </summary>
    private async void OpenMobFilter_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
            return;

        var flyout = AttackOptionsButton?.Flyout;
        bool reopenFlyout = flyout is { IsOpen: true };
        flyout?.Hide();

        try
        {
            MobFilterDialog dialog;
            try
            {
                dialog = new MobFilterDialog(ViewModel) { XamlRoot = XamlRoot };
            }
            catch (Exception ex)
            {
                // 弹不出来不能把界面带崩：原因写进日志让用户看见
                ViewModel.SelectedAccount?.WriteNote($"§c打开“生物过滤”窗口失败：{ex}");
                return;
            }

            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                ViewModel.SelectedAccount?.WriteNote($"§c显示“生物过滤”窗口失败：{ex}");
            }
        }
        finally
        {
            // 关窗后展回“砍怪参数”弹层（开窗前它是开着的才展）
            if (reopenFlyout && flyout is not null && AttackOptionsButton is not null)
                flyout.ShowAt(AttackOptionsButton);
        }
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _logScrollViewer ??= FindScrollViewer(LogList);

        // 首次打开也要落在最新一行，不能停在开机那几条老日志上
        ForceScrollLogToEnd();
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

    /// <summary>账号项里删除叉号的 x:Name（模板内容没有生成字段，只能按名字作用域找）。</summary>
    private const string AccountDeleteButtonName = "AccountDeleteButton";

    /// <summary>鼠标移到账号项上：淡入右侧删除叉号。</summary>
    private void AccountItem_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetAccountDeleteButtonOpacity(sender, visible: true);

    /// <summary>鼠标移出账号项：淡出删除叉号。</summary>
    private void AccountItem_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetAccountDeleteButtonOpacity(sender, visible: false);

    private void SetAccountDeleteButtonOpacity(object sender, bool visible)
    {
        if (sender is not FrameworkElement element)
            return;

        if (FindAccountDeleteButton(element) is { } button)
            button.Opacity = visible ? 1 : 0;
    }

    /// <summary>标签按钮悬停底色：WinUI 默认 PointerOver 只把模板根画深 8/255，肉眼几乎看不出，这里盖一层明显的。</summary>
    private static readonly Brush LabelHoverLightBrush = CreateLabelHoverBrush(0xE8, 0xE8, 0xE8);

    private static readonly Brush LabelHoverDarkBrush = CreateLabelHoverBrush(0x3C, 0x3C, 0x3C);

    private static Brush CreateLabelHoverBrush(byte r, byte g, byte b)
        => new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));

    private Panel? _hoverRoot;
    private Control? _hoverControl;
    private Brush? _hoverBrush;
    private long _hoverToken;

    /// <summary>鼠标移到“自动砍怪 / 鼠标控制 / 自动重连 / 生物过滤”文字上：整块按钮加一层背景。</summary>
    private void LabelButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Control control)
            return;

        ClearLabelHover();

        if (FindTemplatePanel(control) is not { } root)
            return;

        _hoverRoot = root;
        _hoverControl = control;
        _hoverBrush = control.ActualTheme == ElementTheme.Dark ? LabelHoverDarkBrush : LabelHoverLightBrush;
        root.Background = _hoverBrush;
        _hoverToken = root.RegisterPropertyChangedCallback(Panel.BackgroundProperty, OnLabelRootBackgroundChanged);
    }

    /// <summary>
    /// 状态机的 PointerOver 落值排在 PointerEntered 之后，会把这层底色盖掉；
    /// 盯住模板根的 Background，只要不是我们这层就补回来。
    /// </summary>
    private void OnLabelRootBackgroundChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_hoverRoot is null || _hoverBrush is null || !ReferenceEquals(sender, _hoverRoot))
            return;

        if (!ReferenceEquals(_hoverRoot.Background, _hoverBrush))
            _hoverRoot.Background = _hoverBrush;
    }

    /// <summary>鼠标移出：撤掉这层底色，按钮回到默认底色。</summary>
    private void LabelButton_PointerExited(object sender, PointerRoutedEventArgs e)
        => ClearLabelHover();

    private void ClearLabelHover()
    {
        if (_hoverRoot is null)
            return;

        _hoverRoot.UnregisterPropertyChangedCallback(Panel.BackgroundProperty, _hoverToken);

        // 这层是本地值，直接清会连模板的 TemplateBinding 一起清掉（按钮就再也不是默认底色了），
        // 所以清完重新绑回按钮自身的 Background，主题切换也还跟着走。
        _hoverRoot.ClearValue(Panel.BackgroundProperty);
        if (_hoverControl is not null)
        {
            _hoverRoot.SetBinding(Panel.BackgroundProperty, new Binding
            {
                Path = new PropertyPath(nameof(Control.Background)),
                Source = _hoverControl,
            });
        }

        _hoverRoot = null;
        _hoverControl = null;
        _hoverBrush = null;
    }

    /// <summary>模板里真正画底色的那一层（实测是模板根 Grid，Control.Background 自己画不出来）。</summary>
    private static Panel? FindTemplatePanel(DependencyObject root)
    {
        DependencyObject? node = root;
        for (int depth = 0; depth < 3 && node is not null; depth++)
        {
            if (node is Panel panel)
                return panel;

            node = VisualTreeHelper.GetChildrenCount(node) > 0
                ? VisualTreeHelper.GetChild(node, 0)
                : null;
        }

        return null;
    }

    /// <summary>在账号项模板里定位删除叉号：先按名字作用域查，失败再走可视树兜底。</summary>
    private static Button? FindAccountDeleteButton(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            if (element is Button self && self.Name == AccountDeleteButtonName)
                return self;

            if (element.FindName(AccountDeleteButtonName) is Button named)
                return named;
        }

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
            Button? found = FindAccountDeleteButton(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// 删除账号前先二次确认：删账号 = 关掉它的子进程 + 从加密账号库移除，误点代价太大。
    /// 弹窗里带上账号名与地址，确认后把该账号交给 ViewModel 删除（不依赖它是否被选中）。
    /// </summary>
    private async void AccountDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || button.DataContext is not AccountViewModel account)
            return;

        if (XamlRoot is null)
            return;

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "删除账号",
            Content = $"确定删除账号 {account.DisplayName}（{account.ServerSummary}）吗？\n" +
                      "删除会同时结束它的子进程，此操作不可撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }
        catch (Exception ex)
        {
            // 弹不出来（比如已有窗口打开）也不能静默吞掉，原因写进该账号日志
            account.WriteNote($"§c删除确认窗口失败：{ex.Message}");
            return;
        }

        if (ViewModel.DeleteAccountCommand.CanExecute(account))
            ViewModel.DeleteAccountCommand.Execute(account);
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
