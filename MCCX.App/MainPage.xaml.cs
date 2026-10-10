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
    /// 上一次 ViewChanged 时视图的垂直偏移。用来判断这次是"往上翻"还是"被自动跟随往下拽"。
    ///
    /// <para>2026-10-06：原来靠比对 <c>_logScrollTarget</c> 与实际偏移、或落在"自己滚动的时间窗"里
    /// 来区分自己和用户，两者都会误判（详见 <see cref="LogScrollViewer_ViewChanged"/>）。
    /// 方向判据没这个歧义，所以这里只记上一次偏移值。</para>
    /// </summary>
    private double _logLastOffset;

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

    /// <summary>贴底重试的剩余次数（刚插入的行还没参与布局时，下一帧再补滚一次）。</summary>
    private int _logSettlePasses;

    /// <summary>贴底重试上限：每批新日志最多补滚这么多次。</summary>
    private const int MaxLogSettlePasses = 4;

    /// <summary>LayoutUpdated 兜底的限流：距上次兜底至少间隔这么多毫秒。</summary>
    private const long LogLayoutFollowIntervalMs = 60;

    private long _logLayoutFollowTicks;

    /// <summary>重建文本后布局回弹期（毫秒），期间不追底，见 <see cref="_logFollowBlockedUntilTicks"/>。</summary>
    private const int LogRebuildFollowQuietMs = 80;

    /// <summary>
    /// 追底阻断截止时刻（TickCount64）。2026-10-09 ⑨：Text= 之后布局会来回弹
    /// （实测 ActualHeight 1180→1265→1180→…），回弹期贴底会被瞬时回退的 extent
    /// 钳掉几十像素、再由后续跟随追回——肉眼可见的"抖一下"。挡住
    /// <see cref="LogRebuildFollowQuietMs"/> 再追，偏移就只剩单向上行
    /// （与正常日志增长跟随同一个观感）。窗口关闭时的 Settle 会补贴底。
    /// </summary>
    private long _logFollowBlockedUntilTicks;

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
            _logScrollViewer ??= ResolveLogScrollHost();

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

    /// <summary>
    /// 把日志集合挂到本页（换账号 = 换一份集合）并整份灌进 <see cref="LogBox"/>。
    ///
    /// 两层通知：集合本身（增删 / 清空）走 <see cref="OnLogsCollectionChanged"/>；
    /// 单条日志**就地改文本**（连续重复合并成"原文 xN"）走 LogEntry.PropertyChanged——
    /// 合并不增删条目，只听集合会漏掉计数刷新。
    ///
    /// 2026-10-08 起控制台是一个大只读 TextBox，不再有 ItemsRepeater 逐行渲染，
    /// 所以"集合 → 界面"必须在这里手动重建文本。
    /// </summary>
    private void HookLogs()
    {
        ObservableCollection<LogEntry> logs = ViewModel.Logs;
        if (!ReferenceEquals(_hookedLogs, logs))
        {
            if (_hookedLogs is not null)
            {
                _hookedLogs.CollectionChanged -= OnLogsCollectionChanged;
                foreach (LogEntry entry in _hookedLogs)
                    entry.PropertyChanged -= OnLogEntryTextChanged;
            }

            _hookedLogs = logs;
            _hookedLogs.CollectionChanged += OnLogsCollectionChanged;
            foreach (LogEntry entry in _hookedLogs)
                entry.PropertyChanged += OnLogEntryTextChanged;
        }

        // 刚挂上、换账号都要画一次；换账号顺带上一个账号留下的选区清掉
        RebuildLogText(clearSelection: true);
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
        _logScrollViewer ??= ResolveLogScrollHost();

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
    /// 以前每次都 ScrollIntoView，列表反复重排导致闪烁；这里合并成每帧最多滚一次——
    /// <b>文本重建同样按帧合并</b>（见 <see cref="MarkLogTextDirty"/>）。
    /// </summary>
    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新条目接上"合并计数就地刷新"的通知；被裁掉的旧条目摘掉。
        // Reset（清空日志）时 OldItems 为空——那批条目已无人引用，随条目一起被 GC 收走，不会漏。
        if (e.NewItems is not null)
        {
            foreach (LogEntry entry in e.NewItems.OfType<LogEntry>())
                entry.PropertyChanged += OnLogEntryTextChanged;
        }

        if (e.OldItems is not null)
        {
            foreach (LogEntry entry in e.OldItems.OfType<LogEntry>())
                entry.PropertyChanged -= OnLogEntryTextChanged;
        }

        MarkLogTextDirty();

        if (_logScrollPending)
            return;

        _logScrollPending = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FollowLogToEnd))
            _logScrollPending = false;
    }

    /// <summary>日志文本已经变了、还没写进 <see cref="LogBox"/>。</summary>
    private bool _logTextDirty;

    /// <summary>文本重绘已入队（CollectionChanged 一帧几十次，不能一次次入队）。</summary>
    private bool _logTextQueued;

    /// <summary>某条日志的展示文本被就地改了（重复合并成"原文 xN"）→ 需要重画。</summary>
    private void OnLogEntryTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogEntry.Text))
            MarkLogTextDirty();
    }

    /// <summary>
    /// 标记"文本要重画"，按帧合并成一次 <see cref="FlushLogText"/>。
    /// </summary>
    private void MarkLogTextDirty()
    {
        _logTextDirty = true;
        if (_logTextQueued)
            return;

        _logTextQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FlushLogText))
            _logTextQueued = false;
    }

    /// <summary>
    /// 把积累的文本变更写进 <see cref="LogBox"/>。
    ///
    /// <b>指针正按在日志区时一律延后</b>：设置 Text 会把当前选区清掉，正在拖的那次框选会当场作废
    /// ——这与"自动跟随挪视图"是同一类事故，用户 2026-10-06 / 2026-10-08 反馈的正是它。
    /// 松手（LogBox_PointerReleased）和下一批日志会接手，不会漏画。
    /// </summary>
    private void FlushLogText()
    {
        _logTextQueued = false;
        if (!_logTextDirty)
            return;

        if (LogPointerPressed)
            return; // 保持脏标记，松手或下一批日志再画

        RebuildLogText(clearSelection: false);
    }

    /// <summary>
    /// 从 LogEntry 全量重建 <see cref="LogBox"/> 的文本
    /// （有界：AccountViewModel 只留 1000+100 行，全量重建的代价是一次几十 KB 的 join）。
    ///
    /// 写入前后处理选区：日志只会在<b>末尾增行</b>或<b>开头裁行</b>，
    /// 末尾增行时旧下标完全有效——选中状态得以保留，用户拖完选区再按 Ctrl+C 不会扑空。
    /// </summary>
    private void RebuildLogText(bool clearSelection)
    {
        if (_hookedLogs is not { } logs)
            return;

        var sb = new System.Text.StringBuilder();
        foreach (LogEntry entry in logs)
        {
            if (sb.Length > 0)
                sb.Append('\n');

            sb.Append(entry.Text);
        }

        int selStart = clearSelection ? 0 : LogBox.SelectionStart;
        int selLength = clearSelection ? 0 : LogBox.SelectionLength;

        // 2026-10-09 ⑨：Text= / Select 会触发原生 caret 追逐（异步、会把视口拽离底部）——开抑制窗口
        BeginSuppressBringIntoView();
        LogBox.Text = sb.ToString();
        _logTextDirty = false;

        if (selStart <= LogBox.Text.Length)
            LogBox.Select(selStart, Math.Min(selLength, LogBox.Text.Length - selStart));

        // 需求④：文本变了，链接位置也变 → 重画链接下划线（不改 Text，ValuePattern 不受影响）。
        MarkLinkUnderlinesDirty();

        // 2026-10-09 ⑨：Text= 期间 extent 可能瞬时回退把视口钳离底部；
        // 跟随未暂停（用户本就在底）就补一轮贴底重试把它按回去（Settle 回调自带暂停判断）。
        if (!_logFollowPaused)
            ScheduleScrollSettle();
    }

    private void FollowLogToEnd()
    {
        _logScrollPending = false;
        _logSettlePasses = 0; // 每批新日志都有自己的一轮贴底重试

        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is not { } scroll)
            return;

        double end = scroll.ScrollableHeight;
        if (end <= 0)
            return;

        // 用户正在框选：绝不挪视图（挪一下就把这次选区作废），等松手或取消选区后，下一批日志再跟
        if (LogSelectionInProgress)
            return;

        // 用户正在翻历史时不要抢滚动条；滚回底部附近就自动恢复跟随。
        if (_logFollowPaused && end - scroll.VerticalOffset > LogFollowResumeThresholdPx)
            return;

        _logFollowPaused = false;

        if (Environment.TickCount64 < _logFollowBlockedUntilTicks)
            return; // 2026-10-09 ⑨：重建后布局回弹期（80ms）不追底，窗口关闭时的 Settle 补贴底

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
                _logScrollViewer ??= ResolveLogScrollHost();
                if (_logScrollViewer is not { } scroll || _logFollowPaused)
                    return;

                // 框选中不贴底：贴底会把用户刚拉出来的选区清掉
                if (LogSelectionInProgress)
                    return;

                if (Environment.TickCount64 < _logFollowBlockedUntilTicks)
                    return; // 2026-10-09 ⑨：重建后布局回弹期（80ms）不贴底，同 LayoutUpdated

                double end = scroll.ScrollableHeight;
                if (end - scroll.VerticalOffset > 1)
                {
                    ScrollLogTo(scroll, end);
                }
            }))
        {
            _logSettlePasses = 0;
        }
    }

    /// <summary>
    /// 版面变化的兜底：窗口宽度变了要重新折行、文本重建也会改行数，这些都不走 CollectionChanged
    /// ——没被用户上翻时，视图离底部就补一次贴底。限流避免布局抖动放大成忙等。
    /// （重复合并成"原文 xN"已改由 LogEntry.PropertyChanged → 文本重建接管。）
    /// </summary>
    private void LogBox_LayoutUpdated(object? sender, object e)
    {
        if (_logFollowPaused)
            return;

        // 框选中不贴底：日志每来一行就贴一次底的话，正在拖的选区会被反复作废
        if (LogSelectionInProgress)
            return;

        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is not { } scroll)
            return;

        double end = scroll.ScrollableHeight;
        if (end - scroll.VerticalOffset <= 1)
            return;

        long now = Environment.TickCount64;
        if (now < _logFollowBlockedUntilTicks)
            return; // 2026-10-09 ⑨：重建后布局回弹期（80ms）不追底，见 _logFollowBlockedUntilTicks

        if (now - _logLayoutFollowTicks < LogLayoutFollowIntervalMs)
            return;

        _logLayoutFollowTicks = now;
        ScrollLogTo(scroll, end);
    }

    /// <summary>滚到底部，并记下"这次滚动是我们发起的"（免得被当成用户上翻）。</summary>
    private void ScrollLogTo(ScrollViewer scroll, double offset)
    {
        scroll.ChangeView(null, offset, null, disableAnimation: true);
    }

    /// <summary>
    /// 视图变化：回到底部附近 → 恢复跟随；确实往回滚 → 暂停跟随（便于翻历史）。
    ///
    /// <para>2026-10-06 修复：判据从"偏移与代码滚动目标值的差"和"是否落在自己滚动的时间窗内"
    /// 改成<b>偏移有没有变小</b>。前两个都会误判：ChangeView 的目标是在读 extent 之后才落地的，
    /// 自己滚的那一帧实际落点能比目标小几十像素（于是自己滚被判成用户上翻，跟随永久停摆）；
    /// 而时间窗又会把"刚贴完底、用户马上往回翻"那一次吞掉（跟着立刻被拽回底部，翻历史翻不动）。
    /// 方向判据没这个歧义：跟随只会往下拽，用户翻历史一定是往上滚。</para>
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
            _logLastOffset = offset;
            return;
        }

        // 只有"往回滚"才算用户翻历史（自动跟随只会把视图往下拽）
        if (offset < _logLastOffset - LogFollowResumeThresholdPx)
            _logFollowPaused = true;

        _logLastOffset = offset;
    }

    /// <summary>
    /// 取日志区的滚动宿主：<b>只认 XAML 里显式写出来的 <c>LogScrollHost</c></b>。
    ///
    /// <para>2026-10-06 修复：原来这里会退到 <c>FindScrollViewer(LogList)</c>，而它是沿可视树
    /// 往下找第一个 ScrollViewer —— 从 ItemsRepeater 出发第一个命中的是<b>行内 TextBox 模板
    /// 自带的 ContentElement ScrollViewer</b>（viewport 只有十几像素、ScrollableHeight 恒为 0）。
    /// 一旦绑到它，"跟随最新一行"的三条路径就全在 <c>end &lt;= 0</c> 处早退，日志区永远停在开头；
    /// 而且谁先执行（Page 的 Loaded 还是 ItemsRepeater 的 Loaded）决定绑到哪个，行为时灵时不灵。</para>
    ///
    /// <para>宁可返回 null 也不乱认：没有显式宿主时下面三条跟随路径直接不动，
    /// 最多是"不自动跟随"，不会去操纵一个不相干的内部 ScrollViewer。</para>
    /// </summary>
    private ScrollViewer? ResolveLogScrollHost() => LogScrollHost;

    /// <summary>日志区的 ScrollViewer 只有模板套用后才有，这里挂上 ViewChanged 以识别“用户上翻”。</summary>
    private void LogBox_Loaded(object sender, RoutedEventArgs e)
    {
        // 2026-10-08：日志区改成单个大 TextBox，文本不再由 x:Bind ItemsSource 驱动，
        // 首次加载必须在这里挂上集合并整份画一次（换账号走 OnViewModelPropertyChanged 同一条路）。
        HookLogs();

        // 滚动宿主就是 XAML 里那个 LogScrollHost，不用再往控件模板里找了。
        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is { } scroll)
        {
            scroll.ViewChanged -= LogScrollViewer_ViewChanged;
            scroll.ViewChanged += LogScrollViewer_ViewChanged;
        }

        // 版面兜底：重新折行、文本重建改了行数，这些不走 CollectionChanged
        LogBox.LayoutUpdated -= LogBox_LayoutUpdated;
        LogBox.LayoutUpdated += LogBox_LayoutUpdated;

        // 2026-10-06 用户反馈"控制台还是不能自由框选文字"：框选手势期间必须知道用户正在操作，
        // 否则日志一刷就自动跟到底、把选区清掉。
        // PointerPressed 必须 handledEventsToo：日志文字（模板内层）会把按下标成已处理，
        // 普通挂法一次回调都收不到——2026-10-09 探针实测坐实（三次点击零 press 回调），
        // 也就是说 10-06 那版"按住期间不刷新"的保护其实一直没生效，用户 10-08 再次投诉与此吻合。
        // 挂 LogBox 本体即限定在日志区内，无需再判坐标。
        // PointerReleased/CaptureLost 挂在页面上并收 handled，因为"松手发生在日志区之外"
        // （右键菜单、拖出边界）时也必须把标志清掉，否则标志卡在 true、日志会一直不跟随。
        // 先 Remove 再 Add，避免 Loaded 多次触发时叠加同一个处理器。
        LogBox.RemoveHandler(PointerPressedEvent, new PointerEventHandler(LogBox_PointerPressed));
        LogBox.AddHandler(PointerPressedEvent, new PointerEventHandler(LogBox_PointerPressed), handledEventsToo: true);
        RemoveHandler(PointerReleasedEvent, new PointerEventHandler(LogBox_PointerReleased));
        AddHandler(PointerReleasedEvent, new PointerEventHandler(LogBox_PointerReleased), handledEventsToo: true);
        RemoveHandler(PointerCaptureLostEvent, new PointerEventHandler(LogBox_PointerCaptureLost));
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(LogBox_PointerCaptureLost), handledEventsToo: true);

        // 2026-10-09 ⑨：重建文本期间吞掉 LogBox 的 BringIntoViewRequested，
        // 防止 Text=/Select 的原生 caret 追逐把外层视口从底部拽走（详见处理函数注释）。
        LogBox.RemoveHandler(BringIntoViewRequestedEvent, new Windows.Foundation.TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>(LogBox_BringIntoViewRequested));
        LogBox.AddHandler(BringIntoViewRequestedEvent, new Windows.Foundation.TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>(LogBox_BringIntoViewRequested), handledEventsToo: true);

        ForceScrollLogToEnd();
    }

    // ---- 框选期间不许自动跟随（2026-10-06 用户反馈"控制台不能自由框选"）----

    /// <summary>日志区那个大 TextBox（SelectionLength &gt; 0 表示用户确实选中了字）。</summary>
    private TextBox? _logSelectionBox;

    /// <summary>
    /// 用户此刻正在日志区做选择操作。
    ///
    /// <para>为什么必须有这个状态：自动跟随靠 <see cref="ScrollViewer.ChangeView"/> 挪视图，
    /// 而 XAML 里"拖动选中文本"和"平移滚动"走的是同一条 Direct Manipulation 管线——
    /// 视图只要在拖动过程中被程序挪一下，这次框选就作废。日志在刷时三条跟随路径每几十毫秒挪一次，
    /// 于是选区刚拉出来就被清掉，表现就是"怎么拖都选不中"。</para>
    ///
    /// <para>注意不能拿 <c>_logFollowPaused</c> 顶替：那个只在"用户自己滚动离开底部"时才置位，
    /// 而框选完全不产生滚动事件，它会一直是 false。</para>
    /// </summary>
    private bool LogSelectionInProgress =>
        LogPointerPressed
        || _logSelectionBox is { SelectionLength: > 0 };

    /// <summary>指针按下的时刻（0 表示当前没有按下）。</summary>
    private long _logPointerDownTicks;

    /// <summary>
    /// 按下标志的兜底有效期：超过这么久就当它已经失效。
    /// PointerReleased / PointerCaptureLost 正常都会清掉它，但一旦漏了（窗口切换、弹出层吃掉松手等），
    /// 卡住的标志会让日志永远不再自动跟随——那比一次框选失败糟糕得多。
    /// </summary>
    private const long LogPointerDownStaleMs = 60_000;

    /// <summary>
    /// 指针正按在日志区（框选手势进行中）：这段时间<b>既不挪视图也不重绘文本</b>——
    /// 两者任何一个动一下都会把正在拖的选区作废。松手后由 <see cref="FlushLogText"/> 补画。
    /// </summary>
    private bool LogPointerPressed =>
        _logPointerDownTicks is long ticks && Environment.TickCount64 - ticks < LogPointerDownStaleMs;

    private void LogBox_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _logPointerDownTicks = Environment.TickCount64;
    }

    /// <summary>
    /// 松手：清框选标志 + 补画文本；顺带处理 2026-10-09 需求④的 <b>Ctrl+左键点链接</b>。
    ///
    /// 链接点击原本挂在 Tapped 上，实测探针（RightTapped/菜单正常、Tapped 零回调）
    /// 证明 WinUI 的 TextBox 不抛 Tapped，改在这里做：按下已被文本框消化、光标已落在点击处，
    /// 松手时找链接。本处理器挂在页面上（handledEventsToo），
    /// 所以要自己圈定"这次按下起于日志区、松手仍在日志区内、没有拖出选区"才算一次点击。
    ///
    /// 2026-10-09 问题⑪：链接定位从"光标行"改成"<b>指针行 + 行内列</b>"
    /// （<see cref="GetCharIndexAtPointer"/>）——光标会被 Text= 重建/焦点时序带偏，
    /// 行内多链接时还恒取第一个（点哪个都开连接行的 play.simpfun.cn）。
    /// </summary>
    private void LogBox_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool pressStartedOnLogBox = _logPointerDownTicks != 0;
        _logPointerDownTicks = 0;

        // 框选期间攒下的文本更新在这里补画
        if (_logTextDirty)
            FlushLogText();

        if (!pressStartedOnLogBox || !IsCtrlDown())
            return;

        Windows.Foundation.Point p = e.GetCurrentPoint(LogBox).Position;
        if (p.X < 0 || p.Y < 0 || p.X > LogBox.ActualWidth || p.Y > LogBox.ActualHeight)
            return; // 松手在日志区外（拖出边界）：不算点击

        if (LogBox.SelectionLength > 0)
            return; // 拖出了选区：那是框选，不是点击

        int idx = GetCharIndexAtPointer(p);
        if (idx >= 0)
        {
            TryOpenLinkInText(GetLineAt(LogBox.Text, idx, out int lineStart), idx - lineStart);
        }
        else
        {
            TryOpenLinkInText(GetLineAtCaret()); // 指针反查不到：退回旧行为（光标行的第一个链接）
        }
    }

    private void LogBox_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // 实测顺序是"捕获拆除先于松手事件"（2026-10-09 探针：captureLost 比 rel 早 1~10ms），
        // 无条件清标志会让紧随其后的松手读到 0——框选保护、Ctrl+点击的"按下起于日志区"都白判。
        // 按钮还按着 = 真·中途丢捕获（该清，防止标志卡住）；按钮已松开 = 正常松手的捕获拆除，
        // 交由随后的 PointerReleased 去清（它挂在页面上收 handled，必达）。
        var props = e.GetCurrentPoint(LogBox).Properties;
        bool anyButtonDown = props.IsLeftButtonPressed || props.IsRightButtonPressed || props.IsMiddleButtonPressed;
        if (anyButtonDown)
            _logPointerDownTicks = 0;

        if (_logTextDirty)
            FlushLogText();
    }

    /// <summary>日志 TextBox 的选区变化（2026-10-08 起日志区只有一个大 TextBox）：供"有选区就别自动跟随"判断。</summary>
    private void LogLine_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
            return;

        _logSelectionBox = box;
    }

    // ---- 日志复制与链接跳转（2026-10-04 用户需求：控制台无法复制、链接点不开）----

    /// <summary>
    /// 日志行里的链接（截到空白与中英文标点前）。
    /// 2026-10-09 ②(a) 扩围：除 http(s):// 外也认 www. 前缀与常见 TLD 结尾的裸域名
    /// （用户测试的 www.baidu.com 不带协议头，旧正则一条都匹配不上）。TLD 用白名单，
    /// 避开 server.jar / test.out.log 这类文件名假阳性（另加"TLD 后紧跟 .扩展名 或 \ 路径"的
    /// 否定前瞻，专杀 MCCX.App.dll、MCCX.App\Main 这类程序集/路径误匹配）；打开时无协议头统一补 https://。
    /// </summary>
    private static readonly Regex UrlRegex = new(
        @"(?:https?://|www\.)[^\s""'<>\[\]()（），。；、]+|(?<![\w.-])(?:[a-z0-9-]+\.)+(?:com|cn|net|org|io|co|me|cc|xyz|top|vip|club|app|dev|site|online|shop|fun|live|tv|info|biz|store|tech|space|pro|life|work|day|wiki|news|game|win|today|gov|edu|mil)\b(?!\.[a-z0-9])(?!\\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>最近一次右键命中的那一行文本（右键菜单的"复制 / 打开链接"用它）。</summary>
    private string? _logContextText;

    /// <summary>Ctrl 是否按住：WinUI 没有现成的修饰键参数，问当前线程的键盘状态。</summary>
    private static bool IsCtrlDown() =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// 打开某段日志文本里的链接（Ctrl+点击行，或右键菜单里点"打开链接"）。
    /// 合并成一个大 TextBox 后按"行"取文本（<see cref="GetLineAt"/>）；
    /// 重复合并的计数后缀是" xN"（前面有空格），不会被链接正则吞进去。
    ///
    /// 2026-10-09 问题⑪：<paramref name="preferCol"/> ≥ 0 时取<b>离该列最近</b>的匹配——
    /// 旧行为恒取行内第一个，一行有多个链接时点哪个都开第一个（连接行排最前的
    /// 正是服务器地址，用户实测"点哪个链接都跳 play.simpfun.cn"）。
    /// 列落在某链接内部时距离为 0 直接命中，与字底画的下划线一一对应。
    /// </summary>
    private static bool TryOpenLinkInText(string? lineText, int preferCol = -1)
    {
        if (string.IsNullOrEmpty(lineText))
            return false;

        MatchCollection matches = UrlRegex.Matches(lineText);
        if (matches.Count == 0)
            return false;

        Match chosen = matches[0];
        if (preferCol >= 0)
        {
            int bestGap = int.MaxValue;
            foreach (Match cand in matches)
            {
                int gap = preferCol < cand.Index ? cand.Index - preferCol
                        : preferCol >= cand.Index + cand.Length ? preferCol - (cand.Index + cand.Length - 1)
                        : 0;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    chosen = cand;
                    if (gap == 0)
                        break; // 点击就落在链接内部
                }
            }
        }

        string url = chosen.Value.TrimEnd('.', ':', ';', ',', '!', '?', '，', '。');
        if (url.Length == 0)
            return false;

        // 2026-10-09 ②(a)：裸域名（www.baidu.com / baidu.com）没协议头，new Uri 会抛
        // ——无协议头统一补 https:// 再开。
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        try
        {
            _ = Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (UriFormatException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// 右键日志区：记下<b>指针下面那一行</b>的文本，供 <see cref="LogContextCopy_Click"/>
    /// （没选中时退回这一行）用。右键不一定挪光标，所以按指针位置反查
    /// （<see cref="GetLineTextAtPointer"/>），查不到才退回光标行。
    /// </summary>
    private void LogBox_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _logContextText = GetLineTextAtPointer(e.GetPosition(LogBox)) ?? GetLineAtCaret();

        // 不设 e.Handled：ContextFlyout 的弹出与它挂钩，交回系统，菜单照常弹
    }

    /// <summary>光标（或选区起点）所在那一行的文本；空日志返回 null。</summary>
    private string? GetLineAtCaret()
    {
        string text = LogBox.Text;
        if (text.Length == 0)
            return null;

        return GetLineAt(text, Math.Clamp(LogBox.SelectionStart, 0, text.Length - 1));
    }

    /// <summary>
    /// 指针下面的字符下标（2026-10-09 问题⑪：Ctrl+点击按<b>指针</b>定位行与列，不依赖光标）。
    /// WinUI 的 TextBox 没有 <c>GetCharacterIndexFromPoint</c>，改用
    /// <see cref="TextBox.GetRectFromCharacterIndex(int, bool)"/> 两段二分/扫描：
    /// <list type="number">
    /// <item>阶段一：字符矩形 Top 随下标单调不减（⑩b 实测），Y 二分出点击的<b>视觉行</b>末下标；</item>
    /// <item>阶段二：TextWrapping=Wrap 下一个逻辑行可跨多个视觉行，先把范围收窄到该视觉行，
    /// 行内按 rect.Left 线性扫出点击列（等宽字体步长一致，行长有界，一次点击扫几十次无所谓）。</item>
    /// </list>
    /// 找不到（空文本 / 矩形全 NaN）返回 -1。坐标与 <c>e.GetPosition(LogBox)</c> 同一 DIP 坐标系。
    /// </summary>
    private int GetCharIndexAtPointer(Windows.Foundation.Point p)
    {
        string text = LogBox.Text;
        if (text.Length == 0)
            return -1;

        // ---- 阶段一：Y 二分 → best = 最后一个 Top ≤ p.Y 的下标（点击落在这条视觉行） ----
        int lo = 0;
        int hi = text.Length - 1;
        int best = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            Windows.Foundation.Rect rect = LogBox.GetRectFromCharacterIndex(mid, trailingEdge: false);

            // 末尾等位置可能拿不到矩形（NaN / 空矩形）：当作"还没到"，往左半边找
            if (double.IsNaN(rect.Top) || double.IsNaN(rect.Height))
            {
                hi = mid - 1;
                continue;
            }

            if (rect.Top <= p.Y)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0)
            return -1;

        while (best > 0 && IsLineBreak(text[best]))
            best--; // 下标落在行分隔符上归上一行（'\r\n' 两个字符整体算上一行的尾）

        // ---- 收窄到 best 所在的视觉行：同一 Top 的连续下标段 ----
        double rowTop = LogBox.GetRectFromCharacterIndex(best, trailingEdge: false).Top;

        int rowStart = best;
        while (rowStart > 0 && !IsLineBreak(text[rowStart - 1]))
        {
            Windows.Foundation.Rect r = LogBox.GetRectFromCharacterIndex(rowStart - 1, trailingEdge: false);
            if (double.IsNaN(r.Top) || Math.Abs(r.Top - rowTop) > 0.5)
                break;
            rowStart--;
        }

        int rowEnd = best + 1; // 开区间
        while (rowEnd < text.Length && !IsLineBreak(text[rowEnd]))
        {
            Windows.Foundation.Rect r = LogBox.GetRectFromCharacterIndex(rowEnd, trailingEdge: false);
            if (double.IsNaN(r.Top) || Math.Abs(r.Top - rowTop) > 0.5)
                break;
            rowEnd++;
        }

        // ---- 阶段二：行内按 X 扫出点击列（r.Left 随下标单调不减；线性扫不依赖单调性） ----
        int colIdx = rowStart;
        for (int i = rowStart; i < rowEnd; i++)
        {
            Windows.Foundation.Rect r = LogBox.GetRectFromCharacterIndex(i, trailingEdge: false);
            if (double.IsNaN(r.Left))
                break;
            if (r.Left > p.X)
                break; // 已越过点击列
            colIdx = i;
            if (p.X < r.Right)
                break; // 点击落在该字符内部（字符边矩形宽恒为 0 时此条件不触发，靠下一轮 Left 判断）
        }

        return colIdx;
    }

    /// <summary>指针下面那一行的文本；查不到返回 null（调用方退回光标行）。</summary>
    private string? GetLineTextAtPointer(Windows.Foundation.Point p)
    {
        int idx = GetCharIndexAtPointer(p);
        return idx < 0 ? null : GetLineAt(LogBox.Text, idx);
    }

    /// <summary>
    /// 行分隔符判定（2026-10-09 问题⑪的根因件）：<b>WinUI 的 TextBox 存储的换行不是 <c>'\n'</c></b>
    /// ——chk_url 探针实测日志区 ValuePattern 文本里控制字符只有 CR=13、没有 LF=10，
    /// 而 LogBox 实打实渲染出二十多行。旧实现按 <c>'\n'</c> 找行，一个换行都切不出来，
    /// <see cref="GetLineAtCaret"/> 恒返回<b>整篇日志</b> → Ctrl+点击恒开"全日志第一个链接"
    /// （连接行的 play.simpfun.cn）——正是用户报的"点哪个链接都跳 play.simpfun.cn"。
    /// 读取侧必须同时认 <c>'\r'</c> 与 <c>'\n'</c>（<c>'\r\n'</c> 整体算一个换行）。
    /// </summary>
    private static bool IsLineBreak(char c) => c == '\n' || c == '\r';

    /// <summary>取 text 中第 index 个字符所在的那一行（不含换行符；落在换行符上归上一行）。</summary>
    private static string GetLineAt(string text, int index) => GetLineAt(text, index, out _);

    /// <summary>同上，另返回行首下标（Ctrl+点击按"行内列"挑最近链接要用）。</summary>
    private static string GetLineAt(string text, int index, out int lineStart)
    {
        lineStart = 0;
        if (text.Length == 0)
            return string.Empty;

        int i = Math.Clamp(index, 0, text.Length - 1);
        while (i > 0 && IsLineBreak(text[i]))
            i--; // 落在行分隔符上归上一行；'\r\n' 两个字符整体都算上一行的尾

        int start = i;
        while (start > 0 && !IsLineBreak(text[start - 1]))
            start--;

        int end = i;
        while (end < text.Length && !IsLineBreak(text[end]))
            end++;

        lineStart = start;
        return end >= start ? text[start..end] : string.Empty;
    }

    /// <summary>
    /// 右键菜单「复制」（2026-10-05 用户要求：去掉底部"复制日志"按钮，改成选中文字后右键复制）：
    /// 有选中就只复制选中的那几个字——合并成一个 TextBox 后选区可以横跨多行，
    /// 正是用户要的"像记事本一样复制好几行"；没选中才退回右键命中的那一行。
    /// </summary>
    private void LogContextCopy_Click(object sender, RoutedEventArgs e)
    {
        if (LogBox.SelectionLength > 0)
        {
            CopyLinesToClipboard([LogBox.SelectedText]);
            return;
        }

        if (!string.IsNullOrEmpty(_logContextText))
            CopyLinesToClipboard([_logContextText]);
    }

    // ---- 2026-10-09 ⑨：日志重建期间屏蔽 BringIntoView（点击选中时视口上下跳动的修复）----

    /// <summary>
    /// 是否处于"重建日志文本"的原生追逐抑制窗口（True 时吞掉 LogBox 的 BringIntoViewRequested）。
    ///
    /// <para>根因（2026-10-09 插桩实测）：按住期间攒下新行 → 松手 RebuildLogText 设 Text= 会把
    /// caret 重置到 0、随后 Select 又把它放回原位，两次都异步请求外层滚动——实测松手后
    /// +15ms 视口冲到 offset=0（顶）、+120ms 又落在 caret 处（半空），offset 变小还把
    /// _logFollowPaused 置真、卡死不再跟随。静态环境（无新行、无 Text=）点击完全不跳，
    /// 与用户"连着服点就跳、探针里点不跳"的差异完全吻合。</para>
    ///
    /// <para>窗口必须跨帧：追逐是异步的（最晚 +120ms、数个布局帧之后），只包同步段拦不住——
    /// 用一次性定时器 400ms 后关闭。窗口内用户自己的滚动/点击不受影响（滚动不走 BringIntoView；
    /// 窗口内点击的 caret 本来就在视野内，请求为无操作）。</para>
    /// </summary>
    private bool _suppressBringIntoView;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bringIntoViewSuppressTimer;

    /// <summary>打开 400ms 抑制窗口（覆盖实测 +15~120ms 的原生追逐，留足余量）。</summary>
    private void BeginSuppressBringIntoView()
    {
        if (_bringIntoViewSuppressTimer is null)
        {
            _bringIntoViewSuppressTimer = DispatcherQueue.CreateTimer();
            _bringIntoViewSuppressTimer.Tick += (s, _) =>
            {
                s.Stop();
                _suppressBringIntoView = false;
                // 窗口结束时 extent 早已稳定：没被用户上翻就再补一次贴底，
                // 兜住"瞬时钳位停在半空"（Settle 回调自带暂停/框选判断）。
                if (!_logFollowPaused)
                    ScheduleScrollSettle();
            };
        }

        _suppressBringIntoView = true;
        _logFollowBlockedUntilTicks = Environment.TickCount64 + LogRebuildFollowQuietMs;
        _bringIntoViewSuppressTimer.Stop();
        _bringIntoViewSuppressTimer.Interval = TimeSpan.FromMilliseconds(400);
        _bringIntoViewSuppressTimer.Start();
    }

    private void LogBox_BringIntoViewRequested(object sender, BringIntoViewRequestedEventArgs e)
    {
        if (!_suppressBringIntoView)
            return;

        e.Handled = true; // 重建窗口内：不许原生把视口从底部拽走
    }

    // ---- 链接下划线提示（2026-10-09 需求④：Ctrl+左键跳转 + 链接下方加下划线，去掉右键"打开链接"）----
    //
    // 纯文本 TextBox 没法只给某几个字加下划线（那要富文本、会丢 ValuePattern，测试读日志的
    // Get-RecentLogText / Wait-LogContains 一套都得重做）。所以在 LogBox 之上叠一层
    // IsHitTestVisible=False 的 Canvas（XAML 里的 LinkUnderlineLayer），用
    // GetRectFromCharacterIndex 算出每个链接字符的矩形、在字底画一条 1px 线。
    // Canvas 与 LogBox 同处一个 Grid、同样撑到内容全高，随外层 LogScrollHost 一起滚动。

    /// <summary>下划线是否要重画（文本/尺寸变了置位，按帧合并成一次 <see cref="RedrawLinkUnderlines"/>）。</summary>
    private bool _linkUnderlineDirty;

    /// <summary>是否已经排了一次下划线重画（避免重复入队）。</summary>
    private bool _linkUnderlineQueued;

    /// <summary>标记下划线要重画，按帧合并。</summary>
    private void MarkLinkUnderlinesDirty()
    {
        _linkUnderlineDirty = true;
        if (_linkUnderlineQueued)
            return;

        _linkUnderlineQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _linkUnderlineQueued = false;
                if (_linkUnderlineDirty)
                    RedrawLinkUnderlines();
            }))
        {
            _linkUnderlineQueued = false;
        }
    }

    /// <summary>日志区尺寸变了（换行宽度变 → 链接矩形跟着变）：重画下划线。</summary>
    private void LogBox_SizeChanged(object sender, SizeChangedEventArgs e) => MarkLinkUnderlinesDirty();

    /// <summary>
    /// 按 <see cref="LogBox"/> 当前文本里的每个 http(s) 链接，在 <see cref="LinkUnderlineLayer"/>
    /// 上画 1px 下划线。链接可能因 TextWrapping 被折行，所以按<b>字符</b>取矩形、
    /// 同一行内把相邻字符的线段并成一条（Top 相同即同行），跨行则各画各的。
    /// </summary>
    private void RedrawLinkUnderlines()
    {
        _linkUnderlineDirty = false;
        LinkUnderlineLayer.Children.Clear();

        string text = LogBox.Text;
        if (text.Length == 0)
            return;

        foreach (Match m in UrlRegex.Matches(text))
        {
            string url = m.Value.TrimEnd('.', ':', ';', ',', '!', '?', '，', '。');
            if (url.Length == 0)
                continue;

            int start = m.Index;
            int len = url.Length;
            double lastTop = double.NaN;
            double segStartX = 0;
            double segEndX = 0;

            for (int i = 0; i < len; i++)
            {
                int ci = start + i;
                if (ci >= text.Length)
                    break;

                Windows.Foundation.Rect r = LogBox.GetRectFromCharacterIndex(ci, trailingEdge: false);
                if (double.IsNaN(r.Top) || double.IsNaN(r.Left))
                    continue;

                // 字符矩形宽恒为 0（"字符边"矩形，见 LogBox_RightTapped 的实测注释），
                // 右边缘要取下一个字符的左边缘——按 Width 画线会一条都画不出来（2026-10-09 探针实测）。
                double charRight = GetCharRightEdge(text, ci, r);

                if (double.IsNaN(lastTop) || Math.Abs(r.Top - lastTop) > 0.5)
                {
                    // 换了行：把上一段收尾（Bottom 取上一个字符矩形下沿）
                    if (!double.IsNaN(lastTop))
                    {
                        Windows.Foundation.Rect prevR = LogBox.GetRectFromCharacterIndex(start + i - 1, trailingEdge: false);
                        // 2026-10-09 ②(b) 实测：字符矩形高 = 2×行高（行高14 → 矩形高28，最后一行也一样），
                        // 直接拿 Height 当行底会把下划线整掉到下一行（用户看到的"划到下一条日志中"）。
                        double prevBottom = double.IsNaN(prevR.Height) ? lastTop + 14 : prevR.Top + prevR.Height / 2;
                        AddUnderlineSegment(segStartX, segEndX, lastTop, prevBottom);
                    }

                    lastTop = r.Top;
                    segStartX = r.Left;
                    segEndX = charRight;
                }
                else
                {
                    segEndX = charRight;
                }
            }

            if (!double.IsNaN(lastTop))
            {
                // 最后一段：行底 = 行顶 + 行高（矩形高=2×行高，除以2才是本行行底；见上一条注释）
                Windows.Foundation.Rect lastR = LogBox.GetRectFromCharacterIndex(start + len - 1, trailingEdge: false);
                double bottom = double.IsNaN(lastR.Height) ? lastTop + 14 : lastR.Top + lastR.Height / 2;
                AddUnderlineSegment(segStartX, segEndX, lastTop, bottom);
            }
        }
    }

    /// <summary>
    /// 某个字符的右边缘 X（<see cref="RedrawLinkUnderlines"/> 用）。
    /// WinUI 的 <c>GetRectFromCharacterIndex(trailingEdge: false)</c> 返回<b>字符左边</b>的零宽"字符边"矩形
    /// （实测 Width 恒为 0，与 <see cref="GetLineTextAtPointer"/> 的注释一致），
    /// 右边只能取下一个字符的左边；下标越界或下一个字符已换行（链接被折行）时，
    /// 退回用同一行上一个字符的宽度（等宽字体步长）推算。
    /// </summary>
    private double GetCharRightEdge(string text, int ci, Windows.Foundation.Rect r)
    {
        if (ci + 1 < text.Length)
        {
            Windows.Foundation.Rect next = LogBox.GetRectFromCharacterIndex(ci + 1, trailingEdge: false);
            if (!double.IsNaN(next.Left) && next.Left > r.Left && Math.Abs(next.Top - r.Top) < 0.5)
                return next.Left;
        }

        if (ci > 0)
        {
            Windows.Foundation.Rect prev = LogBox.GetRectFromCharacterIndex(ci - 1, trailingEdge: false);
            if (!double.IsNaN(prev.Left) && prev.Left < r.Left && Math.Abs(prev.Top - r.Top) < 0.5)
                return r.Left + (r.Left - prev.Left);
        }

        return r.Left + 7; // 兜底：Consolas 12 号一个字宽约 6.5~7 DIP
    }

    /// <summary>在 LinkUnderlineLayer 上放一条 1px 高的线段（x 从 x1 到 x2，压在字底 yBottom 处）。</summary>
    private void AddUnderlineSegment(double x1, double x2, double yTop, double yBottom)
    {
        if (x2 <= x1)
            return;

        var line = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = x2 - x1,
            Height = 1,
            Fill = UnderlineBrush,
        };

        // 贴到字底：取该字符矩形下沿再往上 1px，线压在字脚下而不是盖住下一行
        double y = yBottom - 1;
        if (y < yTop)
            y = yTop;

        Canvas.SetLeft(line, x1);
        Canvas.SetTop(line, y);
        LinkUnderlineLayer.Children.Add(line);
    }

    /// <summary>下划线颜色：次级文字色，浅/暗色主题都跟着走（只作"可点"提示，不抢正文）。</summary>
    private static Brush UnderlineBrush =>
        (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

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
