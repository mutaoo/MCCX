using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using MCCX.Core;
using MCCX.Core.Dialogs;
using MCCX_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MCCX_App;

/// <summary>
/// 主界面。仅负责控件交互（回车发送、日志滚动、弹“添加账号”窗口），业务逻辑都在 ViewModel 里。
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// 用户手动滚上去看历史后，暂时不再自动跟随到底部；
    /// 一旦自己滚回底部附近（≤ 这个距离），恢复自动跟随。0 = 一直跟随。
    /// </summary>
    private const double LogFollowResumeThresholdPx = 24;

    public MainViewModel ViewModel { get; }

    private ScrollViewer? _logScrollViewer;
    private bool _logScrollPending;

    /// <summary>用户正在翻历史：期间新日志不再把视图拽到底部。</summary>
    private bool _logFollowPaused;

    /// <summary>
    /// 最近一次由代码发起的滚动目标。用来把"我们自己滚的"和"用户上翻"分开：
    /// 只要视图还停在目标附近（或还在底部），就绝不当成用户上翻 —— 否则
    /// 布局没算完导致 ChangeView 被钳住的那一次，会被误判成用户操作，跟随从此再也不开。
    /// </summary>
    private double _logScrollTarget = double.NaN;

    /// <summary>贴底重试的剩余次数（刚插入的行还没参与布局时，下一帧再补滚一次）。</summary>
    private int _logSettlePasses;

    /// <summary>贴底重试上限：每批新日志最多补滚这么多次。</summary>
    private const int MaxLogSettlePasses = 4;

    /// <summary>LayoutUpdated 兜底的限流：距上次兜底至少间隔这么多毫秒。</summary>
    private const long LogLayoutFollowIntervalMs = 60;

    private long _logLayoutFollowTicks;

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
        ViewModel.RequestServerDialog += OnServerDialogRequested;
        ViewModel.CloseServerDialog += OnServerDialogClosed;
        Loaded += OnPageLoaded;

        // 启动时把开关拨到上次记住的位置（MainWindow 已按同一份设置把主题应用好了）。
        // 语义（2026-10-05 用户要求）：勾选 = 开 = 暗色（滑块在右），不勾 = 关 = 浅色（滑块在左）。
        ThemeSwitch.IsChecked = UiSettingsStore.ReadDarkMode();
        ApplyThemeSwitchVisual();
        SyncGroupAccountsButton();

        // 账号列表两种模板的选择器：列表项是账号就渲染账号行，是组头就渲染组头
        // （WinUI 3 没有 GroupStyle/IGroupable，分组靠把组头也塞进列表 + 这里分流）
        AccountList.ItemTemplateSelector = new AccountTemplateSelector(
            Resources["AccountItemTemplate"] as DataTemplate,
            Resources["AccountGroupHeaderTemplate"] as DataTemplate);

        // 过滤浮层里两个前缀框的互斥显隐（初始按当前账号的方式摆一次）
        SyncServerFilterRows();
    }

    /// <summary>
    /// 暗色模式开关（2026-10-05 改成月亮/太阳小开关，去掉了"暗色模式"文字）：
    /// 勾选 = 开 = 暗色 #121212（滑块在右），不勾 = 关 = 浅色（滑块在左）。
    /// 提示文本就显示当前状态（"暗色模式 开" / "暗色模式 关"），见 ApplyThemeSwitchVisual。
    /// </summary>
    private void ThemeSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool darkMode = ThemeSwitch.IsChecked == true;
        ApplyThemeSwitchVisual();

        if (MainWindow.Instance is { } window)
            window.ApplyTheme(darkMode);
    }

    /// <summary>
    /// 按开关状态改轨道底色、滑块位置、图标与提示文本。
    /// 直接改模板外的具名元素，不走 VisualStateManager——状态只有两态，代码里改更直观。
    /// </summary>
    private void ApplyThemeSwitchVisual()
    {
        bool dark = ThemeSwitch.IsChecked == true;

        if (ThemeSwitchTrack is { } track)
            track.Background = new SolidColorBrush(dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x33, 0x33, 0x33) // 深灰底（夜里）
                : Windows.UI.Color.FromArgb(0xFF, 0x6E, 0xC7, 0xE8)); // 蓝底（白天）

        // 滑块与图标一起走：图标落在滑块正中央；开（暗色）在右，关（浅色）在左
        HorizontalAlignment knob = dark ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Thickness knobMargin = dark ? new Thickness(0, 0, 2, 0) : new Thickness(2, 0, 0, 0);

        if (ThemeSwitchThumb is { } thumb)
        {
            thumb.HorizontalAlignment = knob;
            thumb.Margin = knobMargin;
        }

        if (ThemeSwitchGlyph is { } glyph)
        {
            glyph.HorizontalAlignment = knob;
            glyph.Margin = knobMargin;
            glyph.Text = dark ? "☾" : "☀";   // 暗色=月亮 / 浅色=太阳
        }

        // 提示就显示当前状态，别再写长说明（用户 2026-10-05 要求）
        ToolTipService.SetToolTip(ThemeSwitch, dark ? "暗色模式 开" : "暗色模式 关");
    }

    /// <summary>
    /// 服务器信息过滤的两个前缀框按当前方式互斥显示（2026-10-05 用户要求）：
    /// 选"只显示指定前缀"只出上面那个，选"只屏蔽指定前缀"只出下面那个，
    /// 其余三种方式（不过滤 / 全屏蔽 / 屏蔽玩家消息）两个都不需要，全藏起来。
    /// 两份前缀值本身各自保存，藏起来不影响已填的内容。
    ///
    /// <b>容错是必须的</b>：这两行在 Flyout 内容里，弹层没打开过时可能还没实例化（字段为 null）。
    /// 本方法会被 <see cref="OnViewModelPropertyChanged"/> 调用，一旦抛异常就会把
    /// "切账号"那条通知链打断，表现成点了左侧账号右侧面板不跟着换（2026-10-05 实测踩过）。
    /// </summary>
    private void SyncServerFilterRows()
    {
        int mode = ViewModel.ServerFilterModeIndex;

        if (ServerFilterShowRow is { } showRow)
            showRow.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (ServerFilterBlockRow is { } blockRow)
            blockRow.Visibility = mode == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// "按服务器分组"按钮：打开后账号按服务器（域名/IP 相同，端口不参与）归类，组头显示域名；
    /// 再点一次恢复原来的单列（最近使用倒序）。状态写 ui-settings.json，下次启动保持。
    /// </summary>
    private void GroupAccountsButton_Click(object sender, RoutedEventArgs e)
    {
        bool grouped = ViewModel.SetGroupAccountsByServer(!ViewModel.GroupAccountsByServer);
        UiSettingsStore.WriteGroupAccountsByServer(grouped);
        SyncGroupAccountsButton();
    }

    /// <summary>分组按钮的文案跟着状态走（按下态由模板的选中视觉表示）。</summary>
    private void SyncGroupAccountsButton()
    {
        GroupAccountsButton.Content = ViewModel.GroupAccountsByServer ? "取消分组" : "按服务器分组";
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

        // 切到某个账号时它手上还压着一条没处理的服务器对话框 → 立刻补弹
        if (e.PropertyName == nameof(MainViewModel.SelectedAccount)
            && ViewModel.SelectedAccount?.PendingDialog is { } pending)
        {
            _ = ShowServerDialogAsync(ViewModel.SelectedAccount, pending);
        }

        // 过滤方式变了 → 两个前缀框该显哪一个也跟着变（换账号时同样要重算，
        // 因为不同账号的方式可能不一样）
        if (e.PropertyName == nameof(MainViewModel.ServerFilterModeIndex)
            || e.PropertyName == nameof(MainViewModel.SelectedAccount))
        {
            SyncServerFilterRows();
        }
    }

    /// <summary>把日志列表滚到底部最新一行（切账号后用，不考虑用户之前翻到哪儿）。</summary>
    private void ForceScrollLogToEnd()
    {
        // 先等 ItemsSource 换好、再等一帧量完布局，两次入队才能滚到真正的末尾。
        // 2026-10-05：日志容器换成 ItemsRepeater，它没有 ScrollIntoView；直接滚 ScrollViewer 即可
        // （它就是显式的 LogScrollHost，滚到底 = 跟着最新一行）。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _logScrollViewer ??= LogScrollHost ?? FindScrollViewer(LogList);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_logScrollViewer is { } scroll && scroll.ScrollableHeight > 0)
                {
                    // 换账号等于换一份日志：无视用户之前翻到哪儿，强制恢复跟随
                    _logFollowPaused = false;
                    ScrollLogTo(scroll, scroll.ScrollableHeight);
                }
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
            SyncDialogTheme(dialog);
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
                SyncDialogTheme(dialog);
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
        _logSettlePasses = 0; // 每批新日志都有自己的一轮贴底重试

        _logScrollViewer ??= FindScrollViewer(LogList);
        if (_logScrollViewer is not { } scroll)
            return;

        double end = scroll.ScrollableHeight;
        if (end <= 0)
            return;

        // 用户正在翻历史时不要抢滚动条；滚回底部附近就自动恢复跟随。
        if (_logFollowPaused && end - scroll.VerticalOffset > LogFollowResumeThresholdPx)
            return;

        _logFollowPaused = false;

        // 关闭滚动动画：动画帧同样会被新一轮日志打断，观感上就是闪
        ScrollLogTo(scroll, end);
        ScheduleScrollSettle();
    }

    /// <summary>
    /// 刚插入的行往往还没参与布局，ChangeView 会被钳到"旧的底部"：
    /// 下一帧再量一次，没贴住就再滚一次（最多 <see cref="MaxLogSettlePasses"/> 轮）。
    /// 这是"新日志来了视图不动"最常见的一类根因。
    /// </summary>
    private void ScheduleScrollSettle()
    {
        if (_logSettlePasses >= MaxLogSettlePasses)
            return;

        _logSettlePasses++;

        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _logScrollViewer ??= FindScrollViewer(LogList);
                if (_logScrollViewer is not { } scroll || _logFollowPaused)
                    return;

                double end = scroll.ScrollableHeight;
                if (end - scroll.VerticalOffset > 1)
                    ScrollLogTo(scroll, end);
            }))
        {
            _logSettlePasses = 0;
        }
    }

    /// <summary>
    /// 日志项内容变化（比如重复合并成 “原文 xN” 后变高）不会触发 CollectionChanged，
    /// 这里做兜底：没被用户上翻时，视图离底部就补一次贴底。限流避免布局抖动放大成忙等。
    /// </summary>
    private void LogList_LayoutUpdated(object? sender, object e)
    {
        if (_logFollowPaused)
            return;

        _logScrollViewer ??= FindScrollViewer(LogList);
        if (_logScrollViewer is not { } scroll)
            return;

        double end = scroll.ScrollableHeight;
        if (end - scroll.VerticalOffset <= 1)
            return;

        long now = Environment.TickCount64;
        if (now - _logLayoutFollowTicks < LogLayoutFollowIntervalMs)
            return;

        _logLayoutFollowTicks = now;
        ScrollLogTo(scroll, end);
    }

    /// <summary>滚到底部，并记下"这次滚动是我们发起的"（免得被当成用户上翻）。</summary>
    private void ScrollLogTo(ScrollViewer scroll, double offset)
    {
        _logScrollTarget = offset;
        scroll.ChangeView(null, offset, null, disableAnimation: true);
    }

    /// <summary>
    /// 视图变化：回到底部附近 → 恢复跟随；确实离开底部、且不是我们自己滚的 → 暂停跟随（便于翻历史）。
    /// </summary>
    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;

        double end = scroll.ScrollableHeight;
        double offset = scroll.VerticalOffset;

        // 还在底部附近：谈不上"用户上翻"，跟随保持开启
        if (end - offset <= LogFollowResumeThresholdPx)
        {
            _logFollowPaused = false;
            return;
        }

        // 视图正停在我们刚设的目标上（布局慢半拍、或滚动还没走到头）：不是用户操作
        if (!double.IsNaN(_logScrollTarget) && Math.Abs(offset - _logScrollTarget) <= LogFollowResumeThresholdPx)
            return;

        _logFollowPaused = true;
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

    /// <summary>日志区的 ScrollViewer 只有模板套用后才有，这里挂上 ViewChanged 以识别“用户上翻”。</summary>
    private void LogList_Loaded(object sender, RoutedEventArgs e)
    {
        // 2026-10-05：日志区改成"显式 ScrollViewer + ItemsRepeater"，滚动宿主就是 XAML 里那个
        // LogScrollHost，不用再往控件模板里找了。
        _logScrollViewer ??= LogScrollHost ?? FindScrollViewer(LogList);
        if (_logScrollViewer is { } scroll)
        {
            scroll.ViewChanged -= LogScrollViewer_ViewChanged;
            scroll.ViewChanged += LogScrollViewer_ViewChanged;
        }

        // 重复合并（xN）这类"只改内容不增删条目"的刷新不走 CollectionChanged，靠布局兜底
        LogList.LayoutUpdated -= LogList_LayoutUpdated;
        LogList.LayoutUpdated += LogList_LayoutUpdated;

        ForceScrollLogToEnd();
    }

    // ---- 日志复制与链接跳转（2026-10-04 用户需求：控制台无法复制、链接点不开）----

    /// <summary>日志行里的 http/https 链接（截到空白与中英文标点前）。</summary>
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>\[\]()（），。；、]+",
        RegexOptions.Compiled);

    /// <summary>最近一次右键命中的日志行（右键菜单的"复制 / 打开链接"用它）。</summary>
    private LogEntry? _logContextEntry;

    /// <summary>最近一次右键命中的那一行里"可选中文本"的 TextBox（用来取 SelectedText）。</summary>
    private TextBox? _logContextTextBox;

    /// <summary>Ctrl 是否按住：WinUI 没有现成的修饰键参数，问当前线程的键盘状态。</summary>
    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// 打开某条日志里的链接（Ctrl+点击行，或右键菜单里点"打开链接"）。
    /// 行文本用 BaseText：不带重复合并的 "xN" 后缀，也没有 § 颜色码。
    /// </summary>
    private static bool TryOpenLinkInEntry(LogEntry? entry)
    {
        if (entry is null)
            return false;

        Match m = UrlRegex.Match(entry.BaseText);
        if (!m.Success)
            return false;

        string url = m.Value.TrimEnd('.', ':', ';', ',', '!', '?', '，', '。');
        if (url.Length == 0)
            return false;

        _ = Launcher.LaunchUriAsync(new Uri(url));
        return true;
    }

    /// <summary>Ctrl+点击日志行：行内有链接就交给系统浏览器打开（普通点击是选中文字）。</summary>
    private void LogList_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsCtrlDown())
            return;

        if (e.OriginalSource is not DependencyObject hit)
            return;

        if (FindLogEntry(hit) is { } entry && TryOpenLinkInEntry(entry))
            e.Handled = true;
    }

    /// <summary>
    /// 右键日志区：记下被右键的那一行（以及行内那个可选中文本的 TextBox），
    /// 供 <see cref="LogContextCopy_Click"/> / <see cref="LogContextOpenLink_Click"/> 用。
    /// </summary>
    private void LogList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _logContextEntry = null;
        _logContextTextBox = null;

        if (e.OriginalSource is not DependencyObject hit)
            return;

        if (FindLogEntry(hit) is not { } entry)
            return;

        _logContextEntry = entry;
        _logContextTextBox = FindDescendant<TextBox>(hit);
        e.Handled = true;
    }

    /// <summary>
    /// 从被点中的元素往上找它所属的那条日志。
    /// 2026-10-05：日志区从 ListView 换成 ItemsRepeater，已经没有 <c>ListViewItem</c> 这个祖先了，
    /// 所以改成"往上找第一个 DataContext 是 <see cref="LogEntry"/> 的元素"——模板根 Grid 的
    /// DataContext 就是这一行的 LogEntry，与容器无关，换回 ListView 也照样能用。
    /// </summary>
    private static LogEntry? FindLogEntry(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.DataContext is LogEntry entry)
                return entry;

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// 右键菜单「复制」（2026-10-05 用户要求：去掉底部"复制日志"按钮，改成选中文字后右键复制）：
    /// 有选中就只复制选中的那几个字，没选中才退回整行。
    /// </summary>
    private void LogContextCopy_Click(object sender, RoutedEventArgs e)
    {
        string? selected = _logContextTextBox?.SelectedText;
        if (!string.IsNullOrEmpty(selected))
        {
            CopyLinesToClipboard([selected]);
            return;
        }

        if (_logContextEntry is { } entry)
            CopyLinesToClipboard([entry.BaseText]);
    }

    /// <summary>右键菜单「打开链接」：这一行有 http(s) 链接才打开。</summary>
    private void LogContextOpenLink_Click(object sender, RoutedEventArgs e) =>
        TryOpenLinkInEntry(_logContextEntry);

    /// <summary>
    /// 鼠标移到日志行上：这一行里有链接就把整行加下划线，提示"这行能点开链接"。移开就还原。
    ///
    /// 为什么是"下边框"而不是 <c>TextDecorations.Underline</c>：行内容是只读 TextBox
    /// （为了能在行内自由框选并读出 SelectedText），而 WinUI 3 的 TextBox 没有 TextDecorations。
    /// 画一条贴着文字下沿的边框，视觉上就是下划线。
    /// </summary>
    private void LogItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DependencyObject node)
            return;

        if (FindLogEntry(node) is not { } entry)
            return;

        if (!UrlRegex.IsMatch(entry.BaseText))
            return;

        if (FindDescendant<TextBox>(node) is { } box)
            SetLogUnderline(box, true);
    }

    /// <summary>鼠标移出日志行：去掉下划线。</summary>
    private void LogItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DependencyObject node)
            return;

        if (FindDescendant<TextBox>(node) is { } box)
            SetLogUnderline(box, false);
    }

    /// <summary>下划线 = 只留 1 物理像素的下边框；线色取文字色压暗一档，明暗两套主题都能看清。</summary>
    private static void SetLogUnderline(TextBox box, bool on)
    {
        if (on)
        {
            if (box.BorderThickness.Bottom > 0)
                return;

            box.BorderBrush = box.Foreground is SolidColorBrush solid
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, solid.Color.R, solid.Color.G, solid.Color.B))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x80, 0x80, 0x80));
            box.BorderThickness = new Thickness(0, 0, 0, 1);
            return;
        }

        if (box.BorderThickness.Bottom <= 0)
            return;

        box.BorderThickness = new Thickness(0);
        box.BorderBrush = null;
    }

    private static void CopyLinesToClipboard(IReadOnlyList<string> lines)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(string.Join(Environment.NewLine, lines));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    /// <summary>沿可视树往上找指定类型祖先（要拿到承载日志的 ListViewItem）。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>沿可视树往下找指定类型后代（行内那个可选中文本的 TextBox）。</summary>
    private static T? FindDescendant<T>(DependencyObject? node) where T : DependencyObject
    {
        if (node is null)
            return null;

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        switch (e.Key)
        {
            case VirtualKey.Enter:
                if (ViewModel.SendCommand.CanExecute(null))
                    ViewModel.SendCommand.Execute(null);

                e.Handled = true;
                return;

            // 上/下方向键翻命令历史（读到一条时吞掉按键，免得光标跳到行首/行尾）
            case VirtualKey.Up:
                e.Handled = ViewModel.HistoryPrev();
                if (e.Handled)
                    CaretToEnd(box);
                return;

            case VirtualKey.Down:
                e.Handled = ViewModel.HistoryNext();
                if (e.Handled)
                    CaretToEnd(box);
                return;
        }
    }

    /// <summary>回填历史后把光标停在末尾，用户可以直接接着改。</summary>
    private static void CaretToEnd(TextBox box)
    {
        string text = box.Text ?? string.Empty;
        box.Select(text.Length, 0);
    }

    #region 常用命令下拉

    private void OnFrequentFlyoutOpening(object sender, object e) => UpdateFrequentEmptyHint();

    private void UpdateFrequentEmptyHint()
    {
        if (FrequentCommandsEmptyHint is not { } hint)
            return;

        hint.Visibility = ViewModel.HasFrequentCommands ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>点一条常用命令：填进输入框（不直接发），光标停在末尾。</summary>
    private void FrequentCommand_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FrequentCommand command)
            return;

        ViewModel.CommandInput = command.Text;
        FrequentCommandsFlyout?.Hide();

        CommandBox.Focus(FocusState.Programmatic);
        CaretToEnd(CommandBox);
    }

    /// <summary>删除一条常用命令（不需要二次确认：随时能再加回来）。</summary>
    private void RemoveFrequentCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not FrequentCommand command)
            return;

        ViewModel.RemoveFrequentCommand(command);
        UpdateFrequentEmptyHint();
    }

    /// <summary>
    /// 「视角调整」下拉里的方向按钮：把 Tag（east/south/west/north/up/down）交给 ViewModel
    /// → 当前账号 → 子进程 Bot，下一个 tick 真正转向（弹层保持打开，方便连点几个方向）。
    /// </summary>
    private void LookDirection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            ViewModel.LookDirection(tag);
    }

    private void AddFrequentCommand_Click(object sender, RoutedEventArgs e) => AddFrequentCommandFromBox();

    private void NewFrequentCommand_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        AddFrequentCommandFromBox();
        e.Handled = true;
    }

    private void AddFrequentCommandFromBox()
    {
        if (NewFrequentCommandBox is not { } box)
            return;

        if (!ViewModel.AddFrequentCommand(box.Text))
        {
            box.Focus(FocusState.Programmatic);
            return;
        }

        box.Text = string.Empty;
        box.Focus(FocusState.Programmatic);
        UpdateFrequentEmptyHint();
    }

    #endregion

    #region 服务器对话框（密码用界面输入框填）

    /// <summary>暗色模式下弹窗背后的烟雾层：60% 黑（浅色那层白纱实测也是 ~60% 强度）。</summary>
    private static readonly Brush DarkDialogSmokeBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0));

    /// <summary>
    /// 弹窗主题同步。两件事，都因为 ContentDialog 是"脱离父视觉树"的弹层：
    /// ① 弹窗本体不跟着窗口根的 <c>RequestedTheme</c> 走（WinUI 已知行为；
    ///    <c>Application.RequestedTheme</c> 又只能启动时设，运行中改会抛异常），
    ///    所以弹之前把当前主题手动抄给弹窗；
    /// ② 弹窗背后压暗全窗的"烟雾层"（弹窗父节点下的 Rectangle）主题跟系统走，
    ///    而且它的 Fill 是按系统主题算死的 —— 实测连改父节点主题它都不会重算，
    ///    暗色下仍是一层白纱，只能在 Loaded 里直接把 Fill 换成 60% 黑。
    ///
    /// Flyout / ComboBox 下拉在 WinAppSDK 2.3 上会自己跟根元素主题走，不需要处理。
    /// </summary>
    private static void SyncDialogTheme(ContentDialog dialog)
    {
        if (MainWindow.Instance?.Content is Panel root)
            dialog.RequestedTheme = root.RequestedTheme;

        dialog.Loaded += (_, _) =>
        {
            DependencyObject? popupRoot = VisualTreeHelper.GetParent(dialog);
            if (popupRoot is FrameworkElement host)
                host.RequestedTheme = dialog.RequestedTheme;

            if (popupRoot is null || dialog.RequestedTheme != ElementTheme.Dark)
                return;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(popupRoot); i++)
            {
                if (VisualTreeHelper.GetChild(popupRoot, i) is Rectangle smoke)
                    smoke.Fill = DarkDialogSmokeBrush;
            }
        };
    }

    /// <summary>正在显示的服务器对话框输入窗（WinUI 同时只允许一个 ContentDialog）。</summary>
    private ContentDialog? _serverDialog;

    /// <summary>上面那个窗口属于哪个账号 / 哪条对话框。</summary>
    private AccountViewModel? _serverDialogAccount;
    private int _serverDialogRevision = -1;

    private void OnServerDialogRequested(AccountViewModel account, MccDialogInfo info)
    {
        if (!ReferenceEquals(account, ViewModel.SelectedAccount))
        {
            // 没选中它：先存在账号的 PendingDialog 里，切过去时补弹
            account.WriteNote($"§8收到对话框但当前未选中该账号，已暂存（切换账号后补弹）：「{info.Title}」（编号 {info.Revision}）");
            return;
        }

        _ = ShowServerDialogAsync(account, info);
    }

    private void OnServerDialogClosed(AccountViewModel account, int revision)
    {
        account.ClearPendingDialog(revision);

        if (_serverDialog is { } dialog
            && ReferenceEquals(_serverDialogAccount, account)
            && _serverDialogRevision == revision)
        {
            try
            {
                dialog.Hide();
            }
            catch
            {
                // 已经关掉了就算了
            }
        }
    }

    /// <summary>
    /// 弹出服务器对话框的输入窗。取值由 MCCX 直接回写给 MCC（/dialog input + click 的等价通路），
    /// **不经过聊天框**：密码不会进日志、也不会被当成公屏消息发到服务器。
    /// </summary>
    private async Task ShowServerDialogAsync(AccountViewModel account, MccDialogInfo info)
    {
        if (XamlRoot is null)
        {
            account.WriteNote($"§c无法弹出对话框输入框（窗口未就绪），已错过：「{info.Title}」（编号 {info.Revision}）");
            return;
        }

        // 上一个还没关（服务器紧接着又弹了一条）：先收掉再弹新的
        if (_serverDialog is not null)
        {
            try
            {
                _serverDialog.Hide();
            }
            catch
            {
                // 关不掉也继续，下面的 ShowAsync 会把失败原因写进日志
            }

            _serverDialog = null;
            _serverDialogAccount = null;
            _serverDialogRevision = -1;
            await Task.Delay(200);
        }

        if (!ReferenceEquals(account, ViewModel.SelectedAccount))
            return; // 等弹窗的功夫用户切了账号：这条已经存在 PendingDialog 里

        Dictionary<string, Control> fields = new(StringComparer.Ordinal);
        bool submitted = false;
        ContentDialog dialog = null!;

        dialog = BuildServerDialog(account, info, fields, () =>
        {
            submitted = true;
            account.ClearPendingDialog(info.Revision);
            try
            {
                dialog.Hide();
            }
            catch
            {
                // 关不掉就算了，ShowAsync 那边会正常返回
            }
        });

        _serverDialog = dialog;
        _serverDialogAccount = account;
        _serverDialogRevision = info.Revision;

        // 发起：与“接收”行配对——有接收没发起=界面链路断，两行都没有=包没到
        account.WriteNote($"§7弹出服务器对话框输入框：「{info.Title}」（编号 {info.Revision}）");

        bool owner = true;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            account.WriteNote($"§c弹出服务器对话框失败：{ex.Message}");
        }
        finally
        {
            owner = ReferenceEquals(_serverDialog, dialog);
            if (owner)
            {
                _serverDialog = null;
                _serverDialogAccount = null;
                _serverDialogRevision = -1;
            }

            account.ClearPendingDialog(info.Revision);
        }

        // 用户直接关掉（ESC / 点“取消”）→ 顺手把服务器那边也取消；
        // 期间又来了更新的一条对话框就别动它（PendingDialog 还压着新的一条）。
        if (!submitted && owner && account.PendingDialog is null)
            account.CancelDialog();
    }

    /// <summary>组装输入窗：正文 + 每个输入项一个控件 + 一行动作按钮。</summary>
    private ContentDialog BuildServerDialog(
        AccountViewModel account,
        MccDialogInfo info,
        Dictionary<string, Control> fields,
        Action onSubmitted)
    {
        StackPanel panel = new() { Spacing = 6 };

        if (!string.IsNullOrWhiteSpace(info.Body))
        {
            panel.Children.Add(new TextBlock
            {
                Text = info.Body,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.85,
            });
        }

        foreach (MccDialogField field in info.Inputs)
        {
            Control control = CreateDialogFieldControl(field);
            fields[field.Key] = control;

            // 复选框自己带着标签，不再单起一行
            if (control is CheckBox)
            {
                panel.Children.Add(control);
                continue;
            }

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
            });
            panel.Children.Add(control);
        }

        if (info.Actions.Count > 0)
        {
            StackPanel buttons = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
            };

            foreach (MccDialogAction action in info.Actions)
            {
                Button button = new()
                {
                    Content = action.Label,
                    MinWidth = 88,
                };

                int index = action.Index;
                button.Click += (_, _) =>
                {
                    if (SubmitServerDialog(account, fields, index))
                        onSubmitted();
                };

                buttons.Children.Add(button);
            }

            panel.Children.Add(buttons);
        }

        // 固定宽度：竖排 StackPanel 用无限宽去量子元素，不给宽度的话输入框会跟着文字缩成一小条
        panel.Width = 440;

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = string.IsNullOrWhiteSpace(info.Title) ? "服务器对话框" : info.Title,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        // 关闭键/ESC = 这条不要了：告诉服务器取消（本地没有进行中的对话框时是安静的空操作）
        dialog.CloseButtonClick += (_, _) => account.CancelDialog();

        // 暗色模式下弹窗要跟着黑（ContentDialog 不继承窗口主题，见 SyncDialogTheme）
        SyncDialogTheme(dialog);
        return dialog;
    }

    /// <summary>把界面上的取值写回会话并点动作；失败原因由会话写进该账号日志，弹窗留着让用户改。</summary>
    private static bool SubmitServerDialog(
        AccountViewModel account,
        Dictionary<string, Control> fields,
        int actionIndex)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Control> pair in fields)
            values[pair.Key] = ReadDialogFieldValue(pair.Value);

        if (!account.SubmitDialog(values, actionIndex))
            return false;

        account.WriteNote($"§7已提交服务器对话框（动作 {actionIndex}）。");
        return true;
    }

    private static string ReadDialogFieldValue(Control control) => control switch
    {
        PasswordBox password => password.Password,
        TextBox box => box.Text ?? string.Empty,
        ComboBox combo => combo.SelectedItem?.ToString() ?? string.Empty,
        CheckBox check => check.IsChecked == true ? "true" : "false",
        _ => string.Empty,
    };

    /// <summary>按输入项类型建控件；密码类输入用 PasswordBox 遮蔽。</summary>
    private static Control CreateDialogFieldControl(MccDialogField field)
    {
        switch (field.Kind)
        {
            case MccDialogKind.Boolean:
                return new CheckBox
                {
                    IsChecked = field.InitialValue.Equals("true", StringComparison.OrdinalIgnoreCase),
                    Content = string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label,
                };

            case MccDialogKind.Option when field.Options is { Count: > 0 } options:
            {
                ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (string option in options)
                    combo.Items.Add(option);

                int index = options.FindIndex(option => string.Equals(option, field.InitialValue, StringComparison.Ordinal));
                combo.SelectedIndex = index >= 0 ? index : 0;
                return combo;
            }

            case MccDialogKind.Text when field.IsSecret:
                return new PasswordBox
                {
                    Password = field.InitialValue,
                    PlaceholderText = field.MaxLength > 0 ? $"最多 {field.MaxLength} 字符" : string.Empty,
                };

            default:
                return new TextBox
                {
                    Text = field.InitialValue,
                    IsSpellCheckEnabled = false,
                    IsTextPredictionEnabled = false,
                    AcceptsReturn = field.Multiline,
                    TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    PlaceholderText = field.MaxLength > 0 ? $"最多 {field.MaxLength} 字符" : string.Empty,
                };
        }
    }

    #endregion

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
            SyncDialogTheme(dialog);
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
        // 过滤浮层第一次打开时里面两行才刚实例化，此时按当前方式补一次显隐
        //（构造期那次调用可能因为字段还没生成而空转，见 SyncServerFilterRows 的说明）
        SyncServerFilterRows();

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
