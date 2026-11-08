using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using MCCX.Core;
using MCCX.Core.Containers;
using MCCX.Core.Dialogs;
using MCCX_App.Containers;
using MCCX_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MCCX_App;

/// <summary>
/// 主界面。仅负责控件交互（回车发送、日志滚动、弹“添加账号”窗口），业务逻辑都在 ViewModel 里。
/// </summary>
public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    /// <summary>日志搜索框当前是否处于“离线展开到命令输入框”状态（2026-11-06）。</summary>
    private bool _searchExpanded = true;

    /// <summary>展开/收起动画是否正在跑（跑的时候别再叠一条新动画）。</summary>
    private bool _searchAnimating;

    /// <summary>首帧布局完成后才允许按命令输入框的实际位置摆放搜索框。</summary>
    private bool _searchLayoutReady;

    /// <summary>连上时搜索框宽度 =「发送 + 清空日志」+ 中间那个列间距。</summary>
    private double _searchConnectedWidth;

    private Storyboard? _searchLayoutStoryboard;

    /// <summary>搜索框在 XAML 里的默认边框/圆角，收起时按这份值恢复（展开态会改边框/圆角）。</summary>
    private Thickness _searchBoxDefaultBorderThickness;
    private CornerRadius _searchBoxDefaultCornerRadius;

    /// <summary>展开/收起动画时长（用户要求“逐渐展开”，太快会像直接跳过去）。</summary>
    private const double SearchTransitionMilliseconds = 240;

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
        ViewModel.WindowChanged += OnContainerWindowChanged;
        ViewModel.WindowClosed += OnContainerWindowClosed;

        // 图标换新（后台把兜底渲染图补成原版贴图）：面板上同一种物品的格子要一起重画，
        // 不然画风统一只做了一半，见 OnItemIconAvailable。
        ItemIconProvider.IconAvailable += OnItemIconAvailable;

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

        // 离线时命令栏只保留布局、不上屏：右下角日志搜索框要展开到整条命令区（2026-11-06）。
        _searchExpanded = !ViewModel.IsConnected;
        SyncCommandBarForConnection();
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
    /// 前缀输入框右端的展开图标（2026-10-21 需求）：弹一个多行编辑窗改长列表，
    /// 保存后照常走 <see cref="AccountViewModel.ServerFilterShowPrefix"/> 的 setter（已连接时即时下发）。
    /// 和「生物过滤」一样：开窗前先收起参数弹层，关窗后它原来是开着的再展回来。
    /// </summary>
    private async void ExpandServerFilterShow_Click(object sender, RoutedEventArgs e) =>
        await EditServerFilterPrefixAsync(isShow: true);

    /// <inheritdoc cref="ExpandServerFilterShow_Click"/>
    private async void ExpandServerFilterBlock_Click(object sender, RoutedEventArgs e) =>
        await EditServerFilterPrefixAsync(isShow: false);

    private async Task EditServerFilterPrefixAsync(bool isShow)
    {
        if (XamlRoot is null)
            return;

        var flyout = ServerFilterOptionsButton?.Flyout;
        bool reopenFlyout = flyout is { IsOpen: true };
        flyout?.Hide();

        try
        {
            var dialog = new PrefixEditorDialog
            {
                XamlRoot = XamlRoot,
                Title = isShow ? "展开编辑：只显示前缀" : "展开编辑：只屏蔽前缀",
                Text = isShow ? ViewModel.ServerFilterShowPrefix : ViewModel.ServerFilterBlockPrefix,
            };

            SyncDialogTheme(dialog);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (isShow)
                    ViewModel.ServerFilterShowPrefix = dialog.Text;
                else
                    ViewModel.ServerFilterBlockPrefix = dialog.Text;
            }
        }
        catch (Exception ex)
        {
            // 弹不出来不能把界面带崩：原因写进当前账号日志
            ViewModel.SelectedAccount?.WriteNote($"§c打开“前缀编辑”窗口失败：{ex}");
        }
        finally
        {
            if (reopenFlyout && flyout is not null && ServerFilterOptionsButton is not null)
                flyout.ShowAt(ServerFilterOptionsButton);
        }
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

        // 换账号：容器面板属于上一个账号，先收掉（不替它发关窗包，免得把别人的窗口关了）；
        // 切过去的账号如果正开着服务器窗口，用它的快照把面板重画回来（子进程不会为切换重发）。
        // 玩家背包面板不还原：那份快照会过期（子进程停止推送后就没再更新），点一下「背包」重开即可。
        if (e.PropertyName == nameof(MainViewModel.SelectedAccount))
        {
            if (ContainerPanel.Visibility == Visibility.Visible)
                CloseContainerPanel(sendClose: false);

            if (ViewModel.SelectedAccount?.WindowSnapshot is { Player: false } snapshot)
                OnContainerWindowChanged(ViewModel.SelectedAccount, snapshot);
        }

        // 过滤方式变了 → 两个前缀框该显哪一个也跟着变（换账号时同样要重算，
        // 因为不同账号的方式可能不一样）
        if (e.PropertyName == nameof(MainViewModel.ServerFilterModeIndex)
            || e.PropertyName == nameof(MainViewModel.SelectedAccount))
        {
            SyncServerFilterRows();
        }

        // 命令栏与日志搜索框跟着 IsConnected 换位置（2026-11-06 用户要求）：
        // 连上 = 命令栏出现、搜索框收回日志区右下角；断开 = 命令栏只留布局，
        // 搜索框逐渐展开到整条命令区（宽度覆盖整行，高度与命令输入框一致）。
        if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            _searchExpanded = !ViewModel.IsConnected;
            ApplySearchBoxPresentation(_searchExpanded);

            if (ViewModel.IsConnected)
            {
                SyncCommandBarForConnection();
                UpdateSearchFieldWidth();
                // 收回右下角也走动画（2026-11-06 用户要求）；宽/位移与展开共用同一条 Storyboard。
                SetSearchLayout(expanded: false, animate: true);
            }
            else
            {
                // 断开后搜索框仍然可用；清掉上一次的搜索词，否则日志会一直只显示命中的那几行，
                // 而命令栏已经收起，用户找不到清空它的入口。
                if (!string.IsNullOrEmpty(LogSearchBox.Text))
                    LogSearchBox.Text = string.Empty;

                // 命令建议浮层是独立的 Popup，不主动关就会孤零零飘在屏幕上（只能点别处轻触关掉）。
                CloseSuggestions();

                // 断开后容器窗口已经不存在了（服务器不会再发关窗包），面板必须自己收掉，
                // 否则会留着一份点不动的旧格子。
                CloseContainerPanel(sendClose: false);

                // 先切到“命令栏透明但仍在布局里”的状态，再按它的实际位置展开搜索框。
                SyncCommandBarForConnection();
                SetSearchLayout(expanded: true, animate: true);
            }

            // 搜索框显隐/位置会改它自己的高度：等这一轮布局完成后再把独立滚动条的上下边距重算一遍。
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                UpdateLogScrollBarInsets();
                if (_logScrollViewer is { } scroll)
                    SyncLogScrollBar(scroll);
            });
        }
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

        // 命令栏 / 日志搜索框的初始位置（离线时搜索框展开覆盖整条命令区，见 2026-11-06 需求）
        InitializeSearchLayout();

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
                // 发送行为保持不变（2026-10-14 需求：Enter 不用来确认建议），只是先把建议框收掉
                CloseSuggestions();

                if (ViewModel.SendCommand.CanExecute(null))
                    ViewModel.SendCommand.Execute(null);

                e.Handled = true;
                return;

            // 建议框开着时：↑/↓ 在候选间移动；Tab 填入；Esc 关闭（都吞掉按键）
            case VirtualKey.Up:
                if (CommandSuggestionPopup.IsOpen)
                {
                    MoveSuggestion(-1);
                    e.Handled = true;
                    return;
                }

                e.Handled = ViewModel.HistoryPrev();
                if (e.Handled)
                    CaretToEnd(box);
                return;

            case VirtualKey.Down:
                if (CommandSuggestionPopup.IsOpen)
                {
                    MoveSuggestion(1);
                    e.Handled = true;
                    return;
                }

                e.Handled = ViewModel.HistoryNext();
                if (e.Handled)
                    CaretToEnd(box);
                return;

            case VirtualKey.Tab:
                if (CommandSuggestionPopup.IsOpen)
                {
                    AcceptSuggestion(_suggestionIndex);
                    e.Handled = true;
                }
                return;

            case VirtualKey.Escape:
                if (CommandSuggestionPopup.IsOpen)
                {
                    CloseSuggestions();
                    e.Handled = true;
                }
                return;
        }
    }

    /// <summary>回填历史后把光标停在末尾，用户可以直接接着改。</summary>
    private static void CaretToEnd(TextBox box)
    {
        string text = box.Text ?? string.Empty;
        box.Select(text.Length, 0);
    }

    /// <summary>
    /// 命令栏 / 日志搜索框的联动（2026-11-06 用户要求）：
    /// 连上时命令栏正常显示、搜索框贴日志区右下角；断开后命令栏**只保留布局**（透明度 0、不可点），
    /// 搜索框从右下角**逐渐展开到整条命令区**（宽度覆盖整行，高度与命令输入框一致）。
    ///
    /// 为什么命令栏不直接 Visibility=Collapsed：收起后子控件量不到宽度，搜索框就没法对齐到
    /// “整条命令区”这个目标，而且那一行高度会塌掉、展开位置也跟着变。留布局不显示是最稳的做法
    /// （UIA 里这些控件仍在，只是 disabled；2026-11-06 起测试按 IsEnabled 判断）。
    /// </summary>
    private void SyncCommandBarForConnection()
    {
        bool connected = ViewModel.IsConnected;
        CommandBarGrid.Opacity = connected ? 1 : 0;
        CommandBarGrid.IsHitTestVisible = connected;

        // Panel 没有 IsEnabled，逐个控件禁掉：禁用后 Tab 不会再落进这些看不见的控件。
        FrequentCommandsButton.IsEnabled = connected;
        CommandBox.IsEnabled = connected;
        SendButton.IsEnabled = connected;
        ClearLogButton.IsEnabled = connected;
    }

    /// <summary>记下 XAML 里的默认边框/圆角，收起时恢复（只调用一次）。背景不缓存，见 ApplySearchBoxPresentation。</summary>
    private void CaptureSearchBoxDefaults()
    {
        _searchBoxDefaultBorderThickness = LogSearchBox.BorderThickness;
        _searchBoxDefaultCornerRadius = LogSearchBox.CornerRadius;
    }

    /// <summary>
    /// 展开态外观（2026-11-06 用户要求）：只留底边框、去掉上/左/右边框与圆角，背景透明——
    /// 展开时搜索框落在命令栏那一行，底下没有日志文字，透明才能真的只看见一条底线。
    /// 输入为空时用居中的 <see cref="LogSearchHintPanel"/> 代替 TextBox 自带的左对齐占位。
    ///
    /// 2026-11-07（用户要求"点击框后不要白底、和日志区的颜色一致"）：TextBox 模板在指针悬停 /
    /// 获得焦点时会用主题资源重新设置背景和边框（本地 Background 会被状态 Storyboard 盖掉，
    /// 一点就变白底、四边边框也回来了）。所以展开时把 <c>TextControlBackground*</c> 也覆盖成透明、
    /// 焦点边框厚度仍只留底边；收起时再把这些资源键删掉，恢复原生外观。
    /// </summary>
    private void ApplySearchBoxPresentation(bool expanded)
    {
        if (expanded)
        {
            LogSearchBox.BorderThickness = new Thickness(0, 0, 0, 1);
            LogSearchBox.CornerRadius = new CornerRadius(0);
            LogSearchBox.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            // 原生 PlaceholderText 只能左对齐，展开态改用居中提示层，避免两个占位同时出现。
            LogSearchBox.PlaceholderText = string.Empty;

            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            LogSearchBox.Resources["TextControlBackground"] = transparent;
            LogSearchBox.Resources["TextControlBackgroundPointerOver"] = transparent;
            LogSearchBox.Resources["TextControlBackgroundFocused"] = transparent;
            LogSearchBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0, 0, 0, 1);
        }
        else
        {
            LogSearchBox.BorderThickness = _searchBoxDefaultBorderThickness;
            LogSearchBox.CornerRadius = _searchBoxDefaultCornerRadius;
            // 2026-11-07（用户反馈"暗色模式搜索框配色异常"）：不能把启动时抓到的 Background
            // 再赋回去——那是 ThemeResource 已经解析成的**具体画刷**（浅色主题下就是浅灰）。
            // 运行中切暗色时主题资源会重新求值，可这个本地值把它钉死了，所以框一直是浅色。
            // 清掉本地值，让 TextBox 模板里的 TextControlBackground 重新按当前主题取色。
            LogSearchBox.ClearValue(Control.BackgroundProperty);
            LogSearchBox.PlaceholderText = "搜索日志";

            LogSearchBox.Resources.Remove("TextControlBackground");
            LogSearchBox.Resources.Remove("TextControlBackgroundPointerOver");
            LogSearchBox.Resources.Remove("TextControlBackgroundFocused");
            LogSearchBox.Resources.Remove("TextControlBorderThemeThicknessFocused");
        }

        UpdateSearchChrome();
    }

    /// <summary>
    /// 搜索框里的图标 / 占位 / 左内边距跟着「展开与否 + 有没有输入」切换：
    ///   展开 + 空 = 居中提示层（放大镜 + 搜索日志），左侧原生图标隐藏、Padding 收平；
    ///   展开 + 有字 = 只留文字，从左边开始；
    ///   收起 + 空 = 原来的左图标 + 占位、Padding 左 26；
    ///   收起 + 有字 = 图标收起、Padding 左 4。
    /// </summary>
    private void UpdateSearchChrome()
    {
        bool empty = string.IsNullOrEmpty(LogSearchBox.Text);

        if (_searchExpanded)
        {
            LogSearchHintPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LogSearchGlyph.Visibility = Visibility.Collapsed;
            LogSearchBox.Padding = new Thickness(4, 4, 4, 0);
        }
        else
        {
            LogSearchHintPanel.Visibility = Visibility.Collapsed;
            LogSearchGlyph.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LogSearchBox.Padding = empty ? new Thickness(26, 4, 4, 0) : new Thickness(4, 4, 4, 0);
        }

        // 展开/收回时徽标的贴法不一样（展开态上方、收起态左侧），跟着状态一起重算。
        UpdateSearchCountBadgeOffset();
    }

    /// <summary>首帧布局完成后调用一次：离线直接把搜索框摆到整条命令区的位置，不播动画。</summary>
    private void InitializeSearchLayout()
    {
        _searchExpanded = !ViewModel.IsConnected;
        CaptureSearchBoxDefaults();
        ApplySearchBoxPresentation(_searchExpanded);
        SyncCommandBarForConnection();

        if (_searchExpanded)
        {
            SetSearchLayout(expanded: true, animate: false);
        }
        else
        {
            UpdateSearchFieldWidth();
            SetSearchLayout(expanded: false, animate: false);
        }

        _searchLayoutReady = true;
        UpdateLogScrollBarInsets();

        // Loaded 那一刻个别情况下还没量到命令栏宽度（例如启动时没有选中账号），
        // 排到下一轮布局后再摆一次，避免搜索框停在右下角的小尺寸上。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_searchExpanded && _searchLayoutReady)
                SetSearchLayout(expanded: true, animate: false);
        });
    }

    /// <summary>
    /// 2026-10-16 用户要求"日志搜索框和 发送 + 清空日志 两个按钮加起来同宽"（整列对齐更整齐）：
    /// 按钮宽度由内容/字体/缩放决定，所以量它们的实际宽度现算，命令栏一重排（改字号、换 DPI）就跟着重算。
    /// 搜索框在日志区右下角、命令栏在它正下方，两者都靠右且同宽，右列就对齐了。
    /// 只改搜索框本体（LogSearchField）：左边的匹配数徽标是"外挂"的，不算进这个宽度，
    /// 否则有搜索词时搜索框会被徽标挤窄、看不出"和按钮同宽"。
    /// </summary>
    private void CommandBarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel.IsConnected)
        {
            UpdateSearchFieldWidth();
            // 收回动画正在跑时别用“立即摆放”把它打断（连接瞬间命令栏显隐可能触发一次 SizeChanged）。
            if (!_searchExpanded && !_searchAnimating)
                SetSearchLayout(expanded: false, animate: false);
        }
        else if (_searchExpanded && _searchLayoutReady)
        {
            // 离线时窗口缩放：展开态也要继续贴着命令区的新位置。
            SetSearchLayout(expanded: true, animate: false);
        }
    }

    /// <summary>日志区尺寸变化（切账号显隐、窗口缩放）时，离线展开态重新贴合命令区。</summary>
    private void LogAreaGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_searchExpanded && _searchLayoutReady)
            SetSearchLayout(expanded: true, animate: false);
    }

    /// <summary>
    /// 把右下角搜索框的宽度算成"发送 + 清空日志"两个按钮的合计宽度。
    /// 离线展开时宽度由整个命令区决定，这里只记下连上/收起时要用的宽度，由 <see cref="SetSearchLayout"/> 统一写入
    /// （避免先直接改 Width、再播动画时起点被提前覆盖）。
    /// </summary>
    private void UpdateSearchFieldWidth()
    {
        double width = SendButton.ActualWidth + CommandBarGrid.ColumnSpacing + ClearLogButton.ActualWidth;
        if (width <= 0)
            return;

        _searchConnectedWidth = width;
    }

    /// <summary>
    /// 摆放搜索框：<paramref name="expanded"/> = 断开后的展开态。
    /// 展开目标 = 整个命令区（宽度覆盖「常用命令 → 清空日志」整行，纵向对齐命令输入框；
    /// 用 <see cref="UIElement.TransformToVisual"/> 量，DPI/主题/窗口宽度都不写死）；
    /// 收起目标 = 日志区右下角、宽度与「发送 + 清空日志」同宽。
    /// 只动 <see cref="LogSearchField"/> 的 Width 和整块搜索区的 TranslateTransform，不改布局槽位。
    /// </summary>
    private void SetSearchLayout(bool expanded, bool animate)
    {
        double targetWidth;
        double targetX = 0;
        double targetY = 0;

        if (expanded)
        {
            if (!TryGetCommandAreaRect(out Rect target) || !TryGetCommandInputRect(out Rect input))
                return;

            targetWidth = target.Width;
            // 搜索框整块右对齐在日志区右边：右边缘先对齐命令区右边缘，宽度相同后左边缘自然对齐。
            targetX = target.Right - LogAreaGrid.ActualWidth;
            // 纵向仍以命令输入框顶边为准，底边框落在输入框那条线上。
            targetY = input.Top - (LogAreaGrid.ActualHeight - LogSearchHost.ActualHeight - 2);
        }
        else
        {
            targetWidth = _searchConnectedWidth;
        }

        if (targetWidth <= 0)
            return;

        if (!animate || _searchAnimating)
        {
            StopSearchLayoutAnimation();
            LogSearchField.Width = targetWidth;
            LogSearchTranslate.X = targetX;
            LogSearchTranslate.Y = targetY;
            UpdateLogScrollBarInsets();
            return;
        }

        double fromWidth = LogSearchField.ActualWidth > 0 ? LogSearchField.ActualWidth : targetWidth;
        double fromX = LogSearchTranslate.X;
        double fromY = LogSearchTranslate.Y;

        // 动画从“当前视觉值”起跑；先把基准值写成起点，结束时再写回终点。
        LogSearchField.Width = fromWidth;
        LogSearchTranslate.X = fromX;
        LogSearchTranslate.Y = fromY;

        var duration = new Duration(TimeSpan.FromMilliseconds(SearchTransitionMilliseconds));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var widthAnimation = new DoubleAnimation
        {
            From = fromWidth,
            To = targetWidth,
            Duration = duration,
            EasingFunction = easing,
            // Width 会影响布局，WinUI 要求显式允许依赖动画，否则直接跳到终点。
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(widthAnimation, LogSearchField);
        Storyboard.SetTargetProperty(widthAnimation, "Width");
        storyboard.Children.Add(widthAnimation);

        AddTranslateAnimation(storyboard, "X", fromX, targetX, duration, easing);
        AddTranslateAnimation(storyboard, "Y", fromY, targetY, duration, easing);

        storyboard.Completed += (_, _) =>
        {
            // 结束时把终点写回基准属性并停掉动画，后续缩放/重连才能继续直接改。
            LogSearchField.Width = targetWidth;
            LogSearchTranslate.X = targetX;
            LogSearchTranslate.Y = targetY;
            storyboard.Stop();
            _searchLayoutStoryboard = null;
            _searchAnimating = false;
            UpdateLogScrollBarInsets();
        };

        _searchLayoutStoryboard = storyboard;
        _searchAnimating = true;
        storyboard.Begin();
    }

    private void AddTranslateAnimation(Storyboard storyboard, string property, double from, double to,
        Duration duration, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(animation, LogSearchTranslate);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void StopSearchLayoutAnimation()
    {
        _searchLayoutStoryboard?.Stop();
        _searchLayoutStoryboard = null;
        _searchAnimating = false;
    }

    /// <summary>取整条命令区相对日志区的位置（搜索框展开时的宽度目标）。命令栏没布局过时返回 false。</summary>
    private bool TryGetCommandAreaRect(out Rect rect)
    {
        rect = default;
        if (CommandBarGrid.ActualWidth <= 0 || CommandBarGrid.ActualHeight <= 0)
            return false;

        Point origin = CommandBarGrid.TransformToVisual(LogAreaGrid).TransformPoint(new Point(0, 0));
        rect = new Rect(origin.X, origin.Y, CommandBarGrid.ActualWidth, CommandBarGrid.ActualHeight);
        return true;
    }

    /// <summary>取命令输入框相对日志区的位置（搜索框展开时的目标矩形）。命令栏没布局过时返回 false。</summary>
    private bool TryGetCommandInputRect(out Rect rect)
    {
        rect = default;
        if (CommandBox.ActualWidth <= 0 || CommandBox.ActualHeight <= 0)
            return false;

        Point origin = CommandBox.TransformToVisual(LogAreaGrid).TransformPoint(new Point(0, 0));
        rect = new Rect(origin.X, origin.Y, CommandBox.ActualWidth, CommandBox.ActualHeight);
        return true;
    }


    #region 命令建议（2026-10-14 需求：原版 MC 样式的命令建议框）

    /// <summary>建议列表最多显示条数（原版也是十几条，太多会盖住日志）。</summary>
    private const int MaxSuggestions = 12;

    /// <summary>候选（带前导 '/' 的完整命令名），下标与 <see cref="CommandSuggestionList"/> 一一对应。</summary>
    private readonly List<string> _suggestionNames = [];

    /// <summary>当前高亮的候选下标（-1 = 没有）。</summary>
    private int _suggestionIndex = -1;

    /// <summary>程序自己回填输入框（Tab 填入）：不要把这次回填当成"用户改了字"再重算一遍。</summary>
    private bool _suggestionRefilling;

    /// <summary>
    /// 刚回填进去的整条命令。WinUI 的 TextBox 有时在回填结束后才派发 TextChanged（实测
    /// refill 标志已经复位），单靠 <see cref="_suggestionRefilling"/> 拦不住——文本仍等于它
    /// 就说明还是那次回填，不重开浮层；用户再改一个字就清掉它、恢复正常。
    /// </summary>
    private string? _suggestionRefillText;

    /// <summary>输入框文字变化 → 重算候选。以 / 开头且还没出现空格时才弹（聊天/带参数输入不打扰）。</summary>
    private void CommandBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suggestionRefilling)
            return;

        string text = CommandBox.Text ?? string.Empty;
        if (_suggestionRefillText is not null)
        {
            if (string.Equals(text, _suggestionRefillText, StringComparison.Ordinal))
                return;

            _suggestionRefillText = null;
        }

        UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        string text = CommandBox.Text ?? string.Empty;

        if (text.Length == 0 || text[0] != '/' || text.StartsWith("//", StringComparison.Ordinal) ||
            text.Contains(' ') || text.Contains('\t'))
        {
            CloseSuggestions();
            return;
        }

        string prefix = text[1..]; // 已敲的部分（不含 '/'），候选按它前缀匹配

        _suggestionNames.Clear();

        // MCC 内部命令在前，服务器命令补后；同名（大小写不敏感）只留第一条，最多 MaxSuggestions 条
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in MccCommandCatalog.Commands)
        {
            if (_suggestionNames.Count >= MaxSuggestions)
                break;

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                _suggestionNames.Add("/" + name);
        }

        if (ViewModel.SelectedAccount is { } account)
        {
            foreach (string name in account.ServerCommands)
            {
                if (_suggestionNames.Count >= MaxSuggestions)
                    break;

                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(name))
                    _suggestionNames.Add("/" + name);
            }
        }

        if (_suggestionNames.Count == 0)
        {
            CloseSuggestions();
            return;
        }

        _suggestionIndex = 0;
        RenderSuggestions(text);
        CommandSuggestionPopup.IsOpen = true;

        // WinUI 的 Popup 是独立窗口，打开瞬间会把键盘焦点抢走（实测 FocusedElement 变成
        // 浮层本身）→ 后续按键全打到浮层上，输入框一个字都收不到。开完立刻把焦点还给输入框；
        // 再排队补一次，防止浮层的焦点切换发生在 IsOpen 赋值之后的那一帧。
        FocusCommandBoxForSuggestions();
    }

    private void FocusCommandBoxForSuggestions()
    {
        CommandBox.Focus(FocusState.Programmatic);

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (CommandSuggestionPopup.IsOpen)
                CommandBox.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>把候选重新灌进列表。每项显示为"命中前缀（黄）+ 剩余（白）"，高亮行加白纱底。</summary>
    private void RenderSuggestions(string typed)
    {
        var items = new List<SuggestionItem>(_suggestionNames.Count);
        for (int i = 0; i < _suggestionNames.Count; i++)
        {
            string full = _suggestionNames[i];
            string matched = full.Length <= typed.Length ? full : full[..typed.Length];
            string rest = full[matched.Length..];
            var item = new SuggestionItem(matched, rest, i == _suggestionIndex) { Index = i };
            items.Add(item);
        }

        CommandSuggestionList.ItemsSource = items;
    }

    /// <summary>↑/↓ 在候选间移动；列表为空时不吞按键。返回是否处理了。</summary>
    private bool MoveSuggestion(int delta)
    {
        if (_suggestionNames.Count == 0)
            return false;

        _suggestionIndex = Math.Clamp(_suggestionIndex + delta, 0, _suggestionNames.Count - 1);
        RenderSuggestions(CommandBox.Text ?? string.Empty);
        return true;
    }

    /// <summary>Tab（或鼠标点）填入选项：只换文字，不发送；光标停在末尾，接着敲参数即可。</summary>
    private void AcceptSuggestion(int index)
    {
        if (index < 0 || index >= _suggestionNames.Count)
            return;

        string picked = _suggestionNames[index];

        _suggestionRefilling = true;
        _suggestionRefillText = picked;
        try
        {
            CommandBox.Text = picked;
        }
        finally
        {
            _suggestionRefilling = false;
        }

        CaretToEnd(CommandBox);
        // 填完继续敲（原版 Tab 后还能补空格与参数）→ 收掉这一轮候选，下次改字再弹
        CloseSuggestions();
        CommandBox.Focus(FocusState.Programmatic);
    }

    /// <summary>鼠标按住某条候选 → 同 Tab 填入。</summary>
    private void CommandSuggestion_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SuggestionItem item)
            return;

        e.Handled = true;
        int index = item.Index;
        AcceptSuggestion(index);
    }

    private void CloseSuggestions()
    {
        CommandSuggestionPopup.IsOpen = false;
        _suggestionNames.Clear();
        _suggestionIndex = -1;
        CommandSuggestionList.ItemsSource = null;
    }

    /// <summary>
    /// 建议框贴在输入框<b>上方</b>、<b>左边缘和输入框对齐</b>（原版 MC 观感）。
    /// WinUI 的 Popup 没有 Placement=Top，也不按 PlacementTarget 定位：默认以“父级 Grid 左上角”
    /// 为原点，所以这里自己按 CommandBox 相对父级 Grid 的坐标设 HorizontalOffset，并给一个负
    /// VerticalOffset 把它抬到 CommandBox 之上（4 = 与输入框之间的小缝）。内容高度每次渲染都会变，
    /// 故挂在 SizeChanged 上，弹开时与增删候选都会重新算。
    /// </summary>
    private void CommandSuggestionHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement host)
            return;

        CommandSuggestionPopup.VerticalOffset = -(host.ActualHeight + 4);

        // 左对齐输入框：把 CommandBox 的 X（相对 Popup 的父级 Grid）量出来当水平偏移。
        if (CommandSuggestionPopup.Parent is FrameworkElement parent)
        {
            try
            {
                Windows.Foundation.Point origin = CommandBox
                    .TransformToVisual(parent)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                CommandSuggestionPopup.HorizontalOffset = origin.X;
            }
            catch
            {
                // 不在同一可视树（极少见）时保持默认偏移，不因为定位失败把浮层弄崩
            }
        }
    }

    #endregion

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

    /// <summary>
    /// 没打开的账号上排着的"后台自动登录"（2026-11-01 需求）：不建 ContentDialog，
    /// 直接等 1 秒用记住的密码提交。一次只有一条，用列表只是防两条账号同时弹框。
    /// </summary>
    private readonly List<HeadlessAutoLogin> _headlessAutoLogins = [];

    private void OnServerDialogRequested(AccountViewModel account, MccDialogInfo info)
    {
        if (!ReferenceEquals(account, ViewModel.SelectedAccount))
        {
            // 没选中它：先存在账号的 PendingDialog 里，切过去时补弹；
            // 但勾了"自动登录"的账号不等用户切过去——后台填好密码，1 秒后直接提交
            //（2026-11-01 用户要求：不在当前打开的账号窗口也要自动点登录）。
            bool headless = TryScheduleHeadlessAutoLogin(account, info);
            account.WriteNote(headless
                ? $"§8收到对话框但当前未选中该账号：已填入记住的密码，1 秒后在后台自动提交（不用切过去）：「{info.Title}」（编号 {info.Revision}）"
                : $"§8收到对话框但当前未选中该账号，已暂存（切换账号后补弹）：「{info.Title}」（编号 {info.Revision}）");
            return;
        }

        _ = ShowServerDialogAsync(account, info);
    }

    private void OnServerDialogClosed(AccountViewModel account, int revision)
    {
        // 服务器自己把这条收回去了：排着的后台自动登录没意义了
        CancelHeadlessAutoLogin(account, revision);
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
            // 窗口还没就绪（或已关）：能后台自动登录就先排上，别白白错过这条
            if (TryScheduleHeadlessAutoLogin(account, info))
                account.WriteNote($"§7窗口未就绪：已填入记住的密码，1 秒后在后台自动提交：「{info.Title}」（编号 {info.Revision}）");
            else
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
        {
            // 等弹窗的功夫用户切了账号：这条已经存在 PendingDialog 里——
            // 支持自动登录的就改走后台（不然要等用户再切回来才点登录）
            if (TryScheduleHeadlessAutoLogin(account, info))
                account.WriteNote($"§8弹出对话框时切走了账号：已改为 1 秒后后台自动提交：「{info.Title}」（编号 {info.Revision}）");
            return;
        }

        // 等弹窗的功夫它已经被后台自动登录提交 / 服务器收回：别再弹一条过期的
        if (account.PendingDialog?.Revision != info.Revision)
            return;

        // 界面这条要弹出来了：撤掉后台计时器，交给弹窗自己的 ScheduleAutoLogin（避免提交两次）
        CancelHeadlessAutoLogin(account, info.Revision);

        ServerDialogSession session = new()
        {
            Account = account,
            Info = info,
        };

        ContentDialog dialog = BuildServerDialog(session);
        session.Dialog = dialog;

        _serverDialog = dialog;
        _serverDialogAccount = account;
        _serverDialogRevision = info.Revision;

        // 发起：与“接收”行配对——有接收没发起=界面链路断，两行都没有=包没到
        account.WriteNote($"§7弹出服务器对话框输入框：「{info.Title}」（编号 {info.Revision}）");

        // 自动登录（2026-10-23 需求）：等窗真正弹出来再开始计时，密码已预填，1 秒后自动提交
        dialog.Opened += (_, _) => ScheduleAutoLogin(session);

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
            session.AutoLoginTimer?.Stop();
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
        if (!session.Submitted && owner && account.PendingDialog is null)
            account.CancelDialog();
    }

    /// <summary>一次服务器对话框的界面状态：密码框、两个勾选框、提交标记与自动登录计时器。</summary>
    private sealed class ServerDialogSession
    {
        public AccountViewModel Account = null!;
        public MccDialogInfo Info = null!;
        public Dictionary<string, Control> Fields { get; } = new(StringComparer.Ordinal);
        public ContentDialog Dialog = null!;
        public PasswordBox? SecretBox { get; set; }
        public CheckBox? RememberBox { get; set; }
        public CheckBox? AutoLoginBox { get; set; }
        // 注意：Windows.System 里也有同名的 DispatcherQueueTimer，这里必须是 WinUI 的那个
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? AutoLoginTimer { get; set; }
        public bool Submitted { get; set; }
    }

    /// <summary>
    /// 自动登录（2026-10-23 需求）：对话框弹出来后等 1 秒，密码没被用户改过就点"登录"类动作。
    /// 只对"已经记住密码 + 勾了自动登录"的账号生效；用户在 1 秒里改了密码就以用户为准。
    /// </summary>
    private void ScheduleAutoLogin(ServerDialogSession session)
    {
        if (session.SecretBox is null
            || session.RememberBox?.IsChecked != true
            || session.AutoLoginBox?.IsChecked != true)
            return;

        if (session.Account.ServerPassword is not { Length: > 0 } remembered)
            return;

        // "登录"类动作 = 第一个非取消动作（MCC 的动作从 1 开始编号）
        int actionIndex = DialogAutoLogin.FindLoginActionIndex(session.Info);
        if (actionIndex < 0)
            return;

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (session.Submitted || !ReferenceEquals(_serverDialog, session.Dialog))
                return;

            // 这一秒里用户改了密码：不抢用户的输入
            if (!string.Equals(session.SecretBox.Password, remembered, StringComparison.Ordinal))
                return;

            session.Account.WriteNote("§7自动登录：已填入记住的密码，1 秒后自动提交。");
            if (SubmitServerDialog(session, actionIndex))
                HideServerDialog(session);
        };

        session.AutoLoginTimer = timer;
        timer.Start();
    }

    /// <summary>提交成功后收窗（保留原来的"先清 Pending、再 Hide"顺序）。</summary>
    private static void HideServerDialog(ServerDialogSession session)
    {
        session.Submitted = true;
        session.AutoLoginTimer?.Stop();
        session.Account.ClearPendingDialog(session.Info.Revision);

        try
        {
            session.Dialog.Hide();
        }
        catch
        {
            // 关不掉就算了，ShowAsync 那边会正常返回
        }
    }

    /// <summary>一条排着的后台自动登录：账号 + 对话框 + 要提交的取值 + 计时器。</summary>
    private sealed class HeadlessAutoLogin
    {
        public AccountViewModel Account = null!;
        public MccDialogInfo Info = null!;
        public Dictionary<string, string> Values = null!;
        public int ActionIndex;
        // 注意：Windows.System 里也有同名的 DispatcherQueueTimer，这里必须是 WinUI 的那个
        public Microsoft.UI.Dispatching.DispatcherQueueTimer Timer = null!;
    }

    /// <summary>
    /// 后台自动登录（2026-11-01 需求）：没打开的账号弹登录框时，不等用户切过去，
    /// 与界面上那条一样"填好密码 → 1 秒后点登录"，只是不建 ContentDialog（建了就会抢当前账号的窗口，
    /// 而 WinUI 同时只允许一个 ContentDialog）。
    /// 条件与 <see cref="ScheduleAutoLogin"/> 一致：记住密码 + 勾了自动登录 + 真存了密码 + 有非取消动作；
    /// 另外要求除密码外的输入项都自带非空初值——后台弹不出来也改不了，盲填空串只会白白失败一次，
    /// 这种（比如要验证码的）仍按老规矩暂存，等切过去再让用户自己填。
    /// 返回是否真的排上了计时器。
    /// </summary>
    private bool TryScheduleHeadlessAutoLogin(AccountViewModel account, MccDialogInfo info)
    {
        // 同一条已经排着（比如切账号/窗口未就绪时又走了一次）就别再排第二个计时器
        if (_headlessAutoLogins.Any(task => ReferenceEquals(task.Account, account) && task.Info.Revision == info.Revision))
            return true;

        if (!account.RememberServerPassword || !account.AutoLoginServer)
            return false;

        if (account.ServerPassword is not { Length: > 0 } remembered)
            return false;

        if (!info.Inputs.Any(field => field.IsSecret))
            return false; // 不是登录 / 密码类对话框，别替用户乱点

        // "登录"类动作 = 第一个非取消动作（MCC 的动作从 1 开始编号）
        int actionIndex = DialogAutoLogin.FindLoginActionIndex(info);
        if (actionIndex < 0)
            return false;

        Dictionary<string, string>? values = DialogAutoLogin.TryBuildValues(info, remembered);
        if (values is null)
            return false;

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        HeadlessAutoLogin task = new()
        {
            Account = account,
            Info = info,
            Values = values,
            ActionIndex = actionIndex,
            Timer = timer,
        };

        _headlessAutoLogins.Add(task);
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => CompleteHeadlessAutoLogin(task);
        timer.Start();
        return true;
    }

    /// <summary>后台自动登录到点：没被界面接管、对话框还在同一条，就按记住的密码提交。</summary>
    private void CompleteHeadlessAutoLogin(HeadlessAutoLogin task)
    {
        task.Timer.Stop();
        _headlessAutoLogins.Remove(task);

        AccountViewModel account = task.Account;
        MccDialogInfo info = task.Info;

        // 这一秒里用户切过来了：界面那条 ContentDialog 已经弹出（或正要弹），交给它自己的计时器
        if (ReferenceEquals(_serverDialogAccount, account) && _serverDialogRevision == info.Revision)
            return;

        // 已经被提交 / 被服务器收回：什么都不做
        if (account.PendingDialog?.Revision != info.Revision)
            return;

        if (!account.SubmitDialog(task.Values, task.ActionIndex))
        {
            account.WriteNote($"§c自动登录（后台）提交失败：对话框可能已经失效（编号 {info.Revision}）。");
            return;
        }

        account.ClearPendingDialog(info.Revision);
        account.WriteNote($"§7自动登录（后台）：账号未打开，已用记住的密码自动提交（编号 {info.Revision}）。");
    }

    /// <summary>撤掉排着的后台自动登录；<paramref name="revision"/> 小于 0 表示该账号的全部。</summary>
    private void CancelHeadlessAutoLogin(AccountViewModel account, int revision = -1)
    {
        for (int i = _headlessAutoLogins.Count - 1; i >= 0; i--)
        {
            HeadlessAutoLogin task = _headlessAutoLogins[i];
            if (!ReferenceEquals(task.Account, account))
                continue;

            if (revision >= 0 && task.Info.Revision != revision)
                continue;

            task.Timer.Stop();
            _headlessAutoLogins.RemoveAt(i);
        }
    }

    /// <summary>组装输入窗：正文 + 每个输入项一个控件 + 密码框下方的两个勾选框 + 一行动作按钮。</summary>
    private ContentDialog BuildServerDialog(ServerDialogSession session)
    {
        MccDialogInfo info = session.Info;
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
            // 密码框（2026-10-23）：自带"按住显示"改成点一下常驻显示，下方再带记住密码/自动登录
            if (field.Kind == MccDialogKind.Text && field.IsSecret)
            {
                string initial = session.Account.RememberServerPassword && session.Account.ServerPassword is { Length: > 0 } remembered
                    ? remembered
                    : field.InitialValue;

                Grid secret = CreateSecretFieldControl(field, initial, session);
                session.Fields[field.Key] = session.SecretBox!;

                panel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label,
                    FontSize = 12,
                    Margin = new Thickness(0, 6, 0, 0),
                });
                panel.Children.Add(secret);
                AddServerPasswordOptions(panel, session);

                if (initial.Length > 0 && !string.Equals(initial, field.InitialValue, StringComparison.Ordinal))
                    session.Account.WriteNote("§8密码框已填入记住的密码。");

                continue;
            }

            Control control = CreateDialogFieldControl(field);
            session.Fields[field.Key] = control;

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
                    if (SubmitServerDialog(session, index))
                        HideServerDialog(session);
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
        dialog.CloseButtonClick += (_, _) => session.Account.CancelDialog();

        // 暗色模式下弹窗要跟着黑（ContentDialog 不继承窗口主题，见 SyncDialogTheme）
        SyncDialogTheme(dialog);
        return dialog;
    }

    /// <summary>
    /// 密码输入控件（2026-10-23 需求）：左边是 PasswordBox / 明文 TextBox 二选一，
    /// 右边一个眼睛按钮，点一下常驻显示、再点一下隐藏（PasswordBox 自带的"按住显示"不够用）。
    /// 两个输入框的取值实时同步，回写时只读 PasswordBox。
    /// </summary>
    private static Grid CreateSecretFieldControl(MccDialogField field, string initialValue, ServerDialogSession session)
    {
        string placeholder = field.MaxLength > 0 ? $"最多 {field.MaxLength} 字符" : string.Empty;

        // 不用 out 参数：下面的事件闭包要捕获它（out 参数不能进 lambda）
        PasswordBox passwordBox = new()
        {
            Password = initialValue,
            PlaceholderText = placeholder,
            // 自带的眼睛是"按住才显示"，这里关掉，换成右边的常驻切换
            PasswordRevealMode = PasswordRevealMode.Hidden,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        TextBox plainBox = new()
        {
            Text = initialValue,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            PlaceholderText = placeholder,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        bool syncing = false;
        passwordBox.PasswordChanged += (_, _) =>
        {
            if (syncing)
                return;

            syncing = true;
            plainBox.Text = passwordBox.Password;
            syncing = false;
        };
        plainBox.TextChanged += (_, _) =>
        {
            if (syncing)
                return;

            syncing = true;
            passwordBox.Password = plainBox.Text;
            syncing = false;
        };

        ToggleButton reveal = new()
        {
            Content = new FontIcon { Glyph = "\uE7B3", FontSize = 14 },
            Width = 36,
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(reveal, "显示密码");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reveal, "显示密码");

        // 用 IsChecked 的属性变更回调而不是 Checked/Unchecked 事件：
        // UI Automation 拨 ToggleButton 时不一定走事件，但属性一定变。
        void ApplyReveal()
        {
            if (reveal.IsChecked == true)
            {
                plainBox.Text = passwordBox.Password;
                passwordBox.Visibility = Visibility.Collapsed;
                plainBox.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(reveal, "隐藏密码");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reveal, "隐藏密码");
                plainBox.Focus(FocusState.Programmatic);
                plainBox.SelectionStart = plainBox.Text.Length;
            }
            else
            {
                passwordBox.Password = plainBox.Text;
                plainBox.Visibility = Visibility.Collapsed;
                passwordBox.Visibility = Visibility.Visible;
                ToolTipService.SetToolTip(reveal, "显示密码");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reveal, "显示密码");
                passwordBox.Focus(FocusState.Programmatic);
            }
        }

        reveal.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => ApplyReveal());

        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(passwordBox, 0);
        Grid.SetColumn(plainBox, 0);
        Grid.SetColumn(reveal, 1);
        grid.Children.Add(passwordBox);
        grid.Children.Add(plainBox);
        grid.Children.Add(reveal);

        session.SecretBox = passwordBox;
        return grid;
    }

    /// <summary>
    /// 密码框下方：左边"记住密码"、右边"自动登录"（2026-10-23 需求）。
    /// 自动登录在"记住密码"勾上之前一直是禁用的；取消"记住密码"时顺手把自动登录也取消掉。
    /// </summary>
    private static void AddServerPasswordOptions(StackPanel panel, ServerDialogSession session)
    {
        CheckBox remember = new()
        {
            Content = "记住密码",
            IsChecked = session.Account.RememberServerPassword,
            MinWidth = 0,
        };

        CheckBox autoLogin = new()
        {
            Content = "自动登录",
            // 数据里要是出现过"没勾记住密码却存了自动登录"的脏状态，这里以"记住密码"为准
            IsChecked = remember.IsChecked == true && session.Account.AutoLoginServer,
            IsEnabled = remember.IsChecked == true,
            MinWidth = 0,
        };

        remember.Checked += (_, _) => autoLogin.IsEnabled = true;
        remember.Unchecked += (_, _) =>
        {
            autoLogin.IsChecked = false;
            autoLogin.IsEnabled = false;
        };

        session.RememberBox = remember;
        session.AutoLoginBox = autoLogin;

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            Margin = new Thickness(0, 4, 0, 0),
        };
        row.Children.Add(remember);
        row.Children.Add(autoLogin);
        panel.Children.Add(row);
    }

    /// <summary>把界面上的取值写回会话并点动作；失败原因由会话写进该账号日志，弹窗留着让用户改。</summary>
    private static bool SubmitServerDialog(ServerDialogSession session, int actionIndex)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Control> pair in session.Fields)
            values[pair.Key] = ReadDialogFieldValue(pair.Value);

        if (!session.Account.SubmitDialog(values, actionIndex))
            return false;

        // 记住密码 / 自动登录跟着这次提交一起落盘（没勾"记住密码"就把已存的密码清掉）
        if (session.SecretBox is not null)
        {
            session.Account.SaveServerPassword(
                session.RememberBox?.IsChecked == true,
                session.AutoLoginBox?.IsChecked == true,
                session.SecretBox.Password);
        }

        session.Account.WriteNote($"§7已提交服务器对话框（动作 {actionIndex}）。");
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

    /// <summary>按输入项类型建控件；密码类走 <see cref="CreateSecretFieldControl"/>（见调用处）。</summary>
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

        // 长度超过“按钮下方到窗口底”的余量时在弹层内部滚，不再让 WinUI 把弹层往上推
        ApplyFlyoutMaxHeight(flyout.Content as ScrollViewer);

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

    /// <summary>弹层内容第一次进树时先按窗口高度封顶（否则第一次打开会被 WinUI 往上推）。</summary>
    private void FlyoutScroller_Loaded(object sender, RoutedEventArgs e) =>
        ApplyFlyoutMaxHeight(sender as ScrollViewer);

    /// <summary>
    /// 给参数弹层的内容按“功能按钮下方到窗口底”的实际余量封顶（2026-10-21 用户要求）。
    /// <para>
    /// 不封顶时，内容比余量高，WinUI 会把整块弹层往上挪去避让窗口边（顶到窗口顶、离按钮很远），
    /// 看起来像"漂移"。封顶后超出部分由弹层内部的 ScrollViewer 出滚动条。
    /// 窗口很矮时仍留 140 的最小高度，不至于只剩一条缝。
    /// </para>
    /// </summary>
    private void ApplyFlyoutMaxHeight(ScrollViewer? scroller)
    {
        if (scroller is null)
            return;

        double rootHeight = XamlRoot?.Size.Height ?? 0;
        if (rootHeight <= 0)
            rootHeight = ActualHeight;
        if (rootHeight <= 0)
            return; // 还没布局：这次不算，等 Loaded / 下次打开

        // 功能按钮所在那一行的底边（所有参数弹层都贴在它下面展开）
        double rowBottom = 0;
        if (AutomationRow is not null)
        {
            try
            {
                rowBottom = AutomationRow.TransformToVisual(null).TransformPoint(new Point(0, 0)).Y
                            + AutomationRow.ActualHeight;
            }
            catch
            {
                rowBottom = 0;
            }
        }

        if (rowBottom <= 0)
            rowBottom = 150; // 兜底：拿不到坐标时按功能区的常识位置估

        // 按钮下方 8 的间距 + 窗口底 12 的留白 + presenter 上下内边距（8+8）
        double available = rootHeight - rowBottom - 8 - 12 - 16;
        scroller.MaxHeight = Math.Max(140, available);
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

    #region 服务器容器窗口（箱子 / 商店 GUI）

    /// <summary>
    /// 格子边长（DIP）。32 是"看得清图标 + 9x3 箱子连同联动的物品栏（7 行）一屏放得下"的折中：
    /// 40 时物品栏最后一行会被挤进滚动条。再大的容器（双层箱子 = 10 行）本来就比日志区高，滚一下。
    /// </summary>
    private const double ContainerSlotSize = 32;

    /// <summary>
    /// 图标边长 = 格子边长：原版就是贴图铺满格子、数量角标压在右下角，留白反而更不像。
    /// 尺寸也是配合原版贴图来的：缓存里存的是 16px 最近邻放大 4 倍（64px），
    /// 32 DIP 在 200% 缩放下正好是 64 物理像素，一像素对一像素，像素画的边不会糊。
    /// </summary>
    private const double ContainerIconSize = 32;

    /// <summary>耐久条宽度（显式定宽，材料只算比例，不用等布局量）：约格子的八成，和原版比例一致。</summary>
    private const double ContainerDurabilityWidth = 26;

    /// <summary>
    /// 玩家背包面板里装备栏与副手之间的空档（同一行里，副手接在装备右边；
    /// 2026-10-18 用户要求横排，之前竖排"竖直占比太高"）。
    /// </summary>
    private const double PlayerOffhandGap = 10;

    private static readonly Brush ContainerSlotBackgroundBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0x80, 0x80, 0x80));

    private static readonly Brush ContainerSlotBorderBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x80, 0x80, 0x80));

    private static readonly Brush ContainerDurabilityTrackBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00));

    /// <summary>数量字用白色（MC 固有观感），底下垫一层半透明黑保证浅色底上也看得清。</summary>
    private static readonly Brush ContainerCountBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static readonly Brush ContainerCountShadowBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0x00, 0x00, 0x00));

    /// <summary>面板当前显示的账号（没显示时为 null）。</summary>
    private AccountViewModel? _containerAccount;

    /// <summary>面板当前显示的窗口编号（null = 面板没开）。玩家背包是窗口 0，所以不能用 0 当"没开"。</summary>
    private int? _containerWindowId;

    /// <summary>面板显示的是玩家背包（窗口 0）而不是服务器开的窗口。</summary>
    private bool _containerIsPlayer;

    /// <summary>格子控件（按 MCC 格子号排：玩家背包的显示顺序和格子号对不上，位置当不了下标）。</summary>
    private readonly Dictionary<int, ContainerSlotVisual> _containerSlotVisuals = [];

    /// <summary>光标上那一摞的说明（状态行用，空 = 光标空着）。</summary>
    private string _containerCursorText = string.Empty;

    /// <summary>
    /// 一个格子的控件 + 它现在显示的是哪个物品。
    /// 图标是异步下载的，回来时要确认这一格还没换成别的物品（<see cref="ItemId"/> 对不上就丢弃）。
    /// </summary>
    private sealed class ContainerSlotVisual
    {
        public required Button Root { get; init; }

        public required Image Icon { get; init; }

        public required TextBlock CountText { get; init; }

        /// <summary>数量字的黑色垫底（跟 <see cref="CountText"/> 内容同步）。</summary>
        public required TextBlock CountShadowText { get; init; }

        public required Grid DurabilityHost { get; init; }

        public required Rectangle DurabilityFill { get; init; }

        /// <summary>格子号（UIA 名字里带，自动化测试据此断言与点击）。</summary>
        public required int SlotIndex { get; init; }

        public string ItemId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 格子按钮的 Tag：点下去要发给<b>哪个窗口</b>的哪一格。
    /// 玩家背包（窗口 0）和服务器窗口（1 起）共用同一套格子控件，所以要连窗口号一起带着。
    /// </summary>
    private sealed record ContainerSlotTag(int WindowId, int Slot);

    /// <summary>子进程报来窗口内容：全量就重画网格，增量就只刷变化的格子。</summary>
    private void OnContainerWindowChanged(AccountViewModel account, MccWindowInfo info)
    {
        // 只有选中账号的窗口才画（其它账号的窗口在切过去时用它自己的快照重画）
        if (!ReferenceEquals(account, ViewModel.SelectedAccount))
            return;

        // 面板没在显示这个窗口时只认全量。增量是"面板上这个窗口"的变化：面板已经关掉
        // （用户按了关闭 / 切到别的窗口 / 切了账号）就别再拿一条在途的增量把它弹回来。
        bool showing = _containerAccount is not null && _containerWindowId == info.Id;
        if (!info.Full && !showing)
            return;

        if (_containerAccount is not null && !ReferenceEquals(_containerAccount, account))
            CloseContainerPanel(sendClose: false);

        _containerAccount = account;

        if (!showing || info.Full || _containerSlotVisuals.Count == 0)
            BuildContainerPanel(info);

        ApplyContainerSlots(info);
    }

    /// <summary>服务器关掉了窗口：收掉面板（不再回发关窗包）。</summary>
    private void OnContainerWindowClosed(AccountViewModel account, int windowId)
    {
        if (!ReferenceEquals(account, _containerAccount) || windowId != _containerWindowId)
            return;

        CloseContainerPanel(sendClose: false);
    }

    /// <summary>
    /// 按窗口快照铺一遍格子：玩家背包按原版背包的摆法（装备横排 + 副手挂右边 / 3×9 + 1×9，
    /// 不摆玩家人像那块，见 <see cref="BuildPlayerInventoryPanel"/>）；服务器窗口是"不分组就按 列 × 行 铺一张网格，
    /// 分组就一组一块、带小标题（容器自己那块 + 联动的"物品栏"）"。
    /// </summary>
    private void BuildContainerPanel(MccWindowInfo info)
    {
        _containerWindowId = info.Id;
        _containerIsPlayer = info.Player;

        ContainerSlotStack.Children.Clear();
        _containerSlotVisuals.Clear();

        List<MccWindowGroup> groups = info.Groups is { Count: > 0 } prepared
            ? prepared
            : [new MccWindowGroup { Columns = Math.Clamp(info.Columns, 1, 9), Slots = info.DisplaySlots() }];

        // 玩家背包走原版布局；认不出分组里的位置（老版本子进程 / 兜底）就退回通用分组画法
        if (!info.Player || !BuildPlayerInventoryPanel(info.Id, groups))
        {
            foreach (MccWindowGroup group in groups)
            {
                if (group.Slots.Count == 0)
                    continue;

                Grid grid = BuildSlotGrid(info.Id, group.Slots, group.Columns);

                if (group.Label.Length == 0)
                {
                    ContainerSlotStack.Children.Add(grid);
                    continue;
                }

                // 组标题：小字 + 次要文字色（浅色/暗色主题都跟着走）
                TextBlock label = new() { Text = group.Label, FontSize = 12 };
                ApplyMccTextColor(label, null, emphasize: false);

                StackPanel section = new() { Spacing = 2 };
                section.Children.Add(label);
                section.Children.Add(grid);
                ContainerSlotStack.Children.Add(section);
            }
        }

        (string titleText, Windows.UI.Color? titleColor) = MccTextStyle.Parse(info.Title);
        titleText = VanillaContainerTitle(titleText.Trim());

        ContainerTitle.Text = string.IsNullOrWhiteSpace(titleText)
            ? ContainerTypeLabel(info.Type)
            : titleText;
        ApplyMccTextColor(ContainerTitle, titleColor, emphasize: true);

        ContainerSubtitle.Text = info.Player
            ? "背包 · 快捷栏 · 装备（服务器实时同步）"
            : $"{ContainerTypeLabel(info.Type)} · 第 {info.Id} 号窗口";

        _containerCursorText = string.Empty;
        ContainerPanel.Visibility = Visibility.Visible;
    }

    /// <summary>按"每行几个"把一组格子号铺成一张网格；格子控件同时登记进 <see cref="_containerSlotVisuals"/>。</summary>
    private Grid BuildSlotGrid(int windowId, List<int> slots, int columns)
    {
        columns = Math.Clamp(columns, 1, 9);
        int rows = Math.Max(1, (slots.Count + columns - 1) / columns);

        Grid grid = new() { HorizontalAlignment = HorizontalAlignment.Center };
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < slots.Count; i++)
        {
            ContainerSlotVisual visual = CreateRegisteredSlot(windowId, slots[i]);
            Grid.SetRow(visual.Root, i / columns);
            Grid.SetColumn(visual.Root, i % columns);
            grid.Children.Add(visual.Root);
        }

        return grid;
    }

    /// <summary>造一个格子并登记进 <see cref="_containerSlotVisuals"/>（增量刷新按格子号找控件）。</summary>
    private ContainerSlotVisual CreateRegisteredSlot(int windowId, int slotIndex)
    {
        ContainerSlotVisual visual = CreateContainerSlot(windowId, slotIndex);
        _containerSlotVisuals[slotIndex] = visual;
        return visual;
    }

    /// <summary>
    /// 玩家背包（MCC 的窗口 0）按<b>原版背包界面</b>的样子摆（2026-10-18 用户要求）：
    /// 顶上一行是装备横排（头盔 / 胸甲 / 护腿 / 靴子）+ 空一小段接副手，
    /// 下面 3×9 背包、再下面 1×9 快捷栏。原版右侧的合成台不做（MCCX 里点不出配方）。
    ///
    /// <para>不摆"玩家人像"那块：MCC 没有皮肤数据画不出来，留个空白反而像布局漏了；
    /// 装备与副手横排之后整块面板也矮下来了（用户 2026-10-18 反馈"竖直占比太高"）。</para>
    ///
    /// <para>哪一块摆在哪儿由子进程给的分组 <see cref="MccWindowGroup.Role"/> 决定
    /// （那边才知道"这一组是窗口里的哪几个格子"），界面只负责摆。</para>
    ///
    /// <para>不画小标题：原版那三块没有标题（看位置就知道），副标题里也写着每块是什么。</para>
    ///
    /// <para>返回 false = 分组里没有位置标记（老版本子进程 / 兜底数据）：交给通用的分组画法。</para>
    /// </summary>
    private bool BuildPlayerInventoryPanel(int windowId, List<MccWindowGroup> groups)
    {
        MccWindowGroup? armor = FindGroup(groups, MccWindowGroupRole.Armor);
        MccWindowGroup? offhand = FindGroup(groups, MccWindowGroupRole.Offhand);
        MccWindowGroup? main = FindGroup(groups, MccWindowGroupRole.Main);
        MccWindowGroup? hotbar = FindGroup(groups, MccWindowGroupRole.Hotbar);

        if (armor is null || main is null || hotbar is null)
            return false;

        // 顶上：装备横排（头盔 → 靴子），空一小段接副手
        StackPanel topRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        foreach (int slot in armor.Slots)
            topRow.Children.Add(CreateRegisteredSlot(windowId, slot).Root);

        if (offhand is not null)
        {
            StackPanel offhandRow = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(PlayerOffhandGap, 0, 0, 0),
            };
            foreach (int slot in offhand.Slots)
                offhandRow.Children.Add(CreateRegisteredSlot(windowId, slot).Root);
            topRow.Children.Add(offhandRow);
        }

        // 背包与快捷栏都从面板左边起排：和装备那一行左对齐
        Grid mainGrid = BuildSlotGrid(windowId, main.Slots, main.Columns);
        mainGrid.HorizontalAlignment = HorizontalAlignment.Left;

        Grid hotbarGrid = BuildSlotGrid(windowId, hotbar.Slots, hotbar.Columns);
        hotbarGrid.HorizontalAlignment = HorizontalAlignment.Left;

        ContainerSlotStack.Children.Add(topRow);
        ContainerSlotStack.Children.Add(mainGrid);
        ContainerSlotStack.Children.Add(hotbarGrid);
        return true;
    }

    /// <summary>按位置标记找一组格子（没标记 / 空组返回 null）。</summary>
    private static MccWindowGroup? FindGroup(List<MccWindowGroup> groups, string role)
    {
        foreach (MccWindowGroup group in groups)
        {
            if (group.Slots.Count > 0 && string.Equals(group.Role, role, StringComparison.Ordinal))
                return group;
        }

        return null;
    }

    /// <summary>造一个空格子：底 + 图标 + 数量 + 耐久条，点击发窗口动作。</summary>
    private ContainerSlotVisual CreateContainerSlot(int windowId, int slotIndex)
    {
        Image icon = new()
        {
            Width = ContainerIconSize,
            Height = ContainerIconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        // 数量：MC 是白字带黑描边；这里用"黑字错位一格垫底 + 白字"凑同样的观感，
        // 否则浅色主题下白字压在浅色格子/浅色图标上会糊成一片看不见。
        TextBlock countShadow = new()
        {
            FontSize = 11,
            Text = string.Empty,
            Foreground = ContainerCountShadowBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 2, 0),
        };

        TextBlock count = new()
        {
            FontSize = 11,
            Text = string.Empty,
            Foreground = ContainerCountBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 3, 1),
        };

        Rectangle durabilityFill = new()
        {
            Width = ContainerDurabilityWidth,
            Height = 3,
            RadiusX = 1,
            RadiusY = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Grid durability = new()
        {
            Width = ContainerDurabilityWidth,
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
            Visibility = Visibility.Collapsed,
        };
        durability.Children.Add(new Rectangle
        {
            Width = ContainerDurabilityWidth,
            Height = 3,
            RadiusX = 1,
            RadiusY = 1,
            Fill = ContainerDurabilityTrackBrush,
        });
        durability.Children.Add(durabilityFill);

        Grid content = new();
        content.Children.Add(icon);
        content.Children.Add(countShadow);
        content.Children.Add(count);
        content.Children.Add(durability);

        // 用 Button 而不是 Border：Button 有 UIA 自动化对等体（自动化测试能按名字找到格子、
        // 还能用 InvokePattern 直接点），Border 没有对等体、在 UIA 树里根本看不见；
        // 顺带白拿一套悬停 / 按下的视觉反馈。样式压到格子大小，Padding 归零。
        Button root = new()
        {
            Width = ContainerSlotSize,
            Height = ContainerSlotSize,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            BorderBrush = ContainerSlotBorderBrush,
            Background = ContainerSlotBackgroundBrush,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = content,
            Tag = new ContainerSlotTag(windowId, slotIndex),
        };
        root.Click += ContainerSlot_Click;
        root.RightTapped += ContainerSlot_RightTapped;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(root, $"格子 {slotIndex}：空");

        return new ContainerSlotVisual
        {
            Root = root,
            Icon = icon,
            CountText = count,
            CountShadowText = countShadow,
            DurabilityHost = durability,
            DurabilityFill = durabilityFill,
            SlotIndex = slotIndex,
        };
    }

    private void ApplyContainerSlots(MccWindowInfo info)
    {
        foreach (MccWindowSlot slot in info.Slots)
        {
            if (slot.Index == MccWindowSlot.CursorIndex)
            {
                _containerCursorText = slot.IsEmpty
                    ? string.Empty
                    : $"光标拿着 {SlotSummary(slot)}";
                continue;
            }

            if (_containerSlotVisuals.TryGetValue(slot.Index, out ContainerSlotVisual? visual))
                ApplyContainerSlot(visual, slot);
        }

        UpdateContainerStatus(info.ItemCount);
    }

    private void ApplyContainerSlot(ContainerSlotVisual visual, MccWindowSlot slot)
    {
        visual.ItemId = slot.ItemId;

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            visual.Root,
            slot.IsEmpty ? $"格子 {visual.SlotIndex}：空" : $"格子 {visual.SlotIndex}：{SlotSummary(slot)}");

        if (slot.IsEmpty)
        {
            visual.Icon.Source = null;
            visual.Icon.Visibility = Visibility.Collapsed;
            SetSlotCount(visual, 0);
            visual.DurabilityHost.Visibility = Visibility.Collapsed;
            ToolTipService.SetToolTip(visual.Root, null);
            return;
        }

        SetSlotCount(visual, slot.Count);

        if (slot.MaxDamage > 0 && slot.Damage > 0)
        {
            double remaining = Math.Clamp((slot.MaxDamage - slot.Damage) / (double)slot.MaxDamage, 0, 1);
            visual.DurabilityFill.Width = ContainerDurabilityWidth * remaining;
            visual.DurabilityFill.Fill = new SolidColorBrush(DurabilityColor(remaining));
            visual.DurabilityHost.Visibility = Visibility.Visible;
        }
        else
        {
            visual.DurabilityHost.Visibility = Visibility.Collapsed;
        }

        ToolTipService.SetToolTip(visual.Root, BuildContainerToolTip(slot));

        // 图标：缓存里有就直接显示，没有就后台下一张（一个物品只下一次，磁盘上也留一份）
        string itemId = slot.ItemId;
        string? file = ItemIconProvider.TryGetFile(
            itemId,
            minecraftVersion: null,
            block: slot.Block,
            onReady: path => DispatcherQueue.TryEnqueue(() =>
            {
                if (string.Equals(visual.ItemId, itemId, StringComparison.Ordinal))
                    visual.Icon.Source = CreateIconSource(path);
            }));

        visual.Icon.Source = file is null ? null : CreateIconSource(file);
        visual.Icon.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 图标换新了（后台把兜底渲染图补成了原版贴图）：把面板上同一种物品的格子重画一遍。
    ///
    /// <para>2026-10-16 用户要求"画风统一"：只重画触发补正的那一格，旁边同一种物品还留着旧图，
    /// 画风照样是混的。</para>
    /// </summary>
    private void OnItemIconAvailable(string itemId)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (ContainerSlotVisual visual in _containerSlotVisuals.Values)
            {
                if (!string.Equals(visual.ItemId, itemId, StringComparison.Ordinal))
                    continue;

                // 补好的那张已经在缓存里了，这一下立刻返回路径
                string? file = ItemIconProvider.TryGetFile(
                    itemId,
                    minecraftVersion: null,
                    block: false,
                    onReady: null);
                if (file is not null)
                    visual.Icon.Source = CreateIconSource(file);
            }
        });
    }

    /// <summary>数量字（白字 + 黑色垫底）同步设置：两层的文本必须一致。</summary>
    private static void SetSlotCount(ContainerSlotVisual visual, int count)
    {
        string text = count > 1 ? count.ToString() : string.Empty;
        visual.CountText.Text = text;
        visual.CountShadowText.Text = text;
    }

    /// <summary>物品提示：名字 + 附魔 + lore + 耐久，颜色按各行的 § 颜色码走。</summary>
    private StackPanel BuildContainerToolTip(MccWindowSlot slot)
    {
        StackPanel panel = new() { Spacing = 2, MaxWidth = 320 };

        (string name, Windows.UI.Color? nameColor) = MccTextStyle.Parse(slot.Name);
        TextBlock title = new()
        {
            Text = name,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        ApplyMccTextColor(title, nameColor, emphasize: true);
        panel.Children.Add(title);

        if (slot.Enchantments is { Count: > 0 })
        {
            foreach (string line in slot.Enchantments)
            {
                (string text, Windows.UI.Color? color) = MccTextStyle.Parse(line);
                TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                ApplyMccTextColor(block, color ?? Windows.UI.Color.FromArgb(0xFF, 0x55, 0x55, 0xFF), emphasize: false);
                panel.Children.Add(block);
            }
        }

        if (slot.Lore is { Count: > 0 })
        {
            foreach (string line in slot.Lore)
            {
                (string text, Windows.UI.Color? color) = MccTextStyle.Parse(line);
                TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                ApplyMccTextColor(block, color ?? Windows.UI.Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA), emphasize: false);
                panel.Children.Add(block);
            }
        }

        if (slot.MaxDamage > 0)
        {
            TextBlock durability = new()
            {
                Text = $"耐久 {slot.MaxDamage - slot.Damage} / {slot.MaxDamage}",
                FontSize = 12,
            };
            ApplyMccTextColor(durability, null, emphasize: false);
            panel.Children.Add(durability);
        }

        // 原始物品名放最后：服务器自定义名看不出是哪个物品时，这行能对上号
        TextBlock identifier = new() { Text = slot.ItemId, FontSize = 11, Opacity = 0.6 };
        ApplyMccTextColor(identifier, null, emphasize: false);
        panel.Children.Add(identifier);

        return panel;
    }

    /// <summary>
    /// 带 § 颜色码的文字着色（物品名字 / lore / 附魔 / 窗口标题）：
    /// 暗色主题照抄 MC 的颜色码；浅色主题下白 / 黄这类浅色看不清，
    /// 退回主题文字色（面板是浅底卡片）。
    /// </summary>
    private void ApplyMccTextColor(TextBlock block, Windows.UI.Color? color, bool emphasize)
    {
        if (color is { } value
            && (ActualTheme == ElementTheme.Dark || MccTextStyle.IsReadableOnLightBackground(value)))
        {
            block.Foreground = new SolidColorBrush(value);
            return;
        }

        block.Foreground = (Brush)Application.Current.Resources[
            emphasize ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
    }

    private void UpdateContainerStatus(int itemCount)
    {
        // 提示行保持一行简短：面板高度紧张（玩家背包三组），长句会被挤成两行
        string head = $"共 {itemCount} 件物品";
        ContainerStatus.Text = _containerCursorText.Length == 0
            ? $"{head} · 左键拿起 / 放下 · 右键分一半 · Shift+左键快速移动 · Esc 关闭"
            : $"{head} · {_containerCursorText} · 左键放下 · 右键放一个";
    }

    /// <summary>
    /// 功能区的「背包」按钮：开 / 收玩家背包面板。
    /// 打开时让子进程把窗口 0（装备 + 背包 + 快捷栏）推过来（有旧快照就先顶上，子进程马上发新的全量）；
    /// 收起时也让它停下，不然背包一变面板就又冒出来。
    /// </summary>
    private void PlayerInventoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedAccount is not { } account)
            return;

        if (_containerIsPlayer && ReferenceEquals(_containerAccount, account))
        {
            CloseContainerPanel(sendClose: false);
            return;
        }

        if (account.WindowSnapshot is { Player: true } snapshot)
            OnContainerWindowChanged(account, snapshot);

        account.WatchPlayerInventory(true);
    }

    /// <summary>左键：点一下格子（Shift 按住 = 快速移动整摞）。</summary>
    private void ContainerSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ContainerSlotTag tag })
            ClickContainerSlot(tag, IsShiftDown() ? MccWindowAction.ShiftLeft : MccWindowAction.Left);
    }

    /// <summary>右键：拿一半 / 放一个（Shift 按住 = 快速移动整摞）。</summary>
    private void ContainerSlot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ContainerSlotTag tag })
            ClickContainerSlot(tag, IsShiftDown() ? MccWindowAction.ShiftRight : MccWindowAction.Right);
    }

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ClickContainerSlot(ContainerSlotTag tag, string action)
    {
        if (_containerAccount is not { } account)
            return;

        account.WindowClick(new MccWindowClick
        {
            WindowId = tag.WindowId,
            Slot = tag.Slot,
            Action = action,
        });
    }

    private void ContainerCloseButton_Click(object sender, RoutedEventArgs e) => CloseContainerPanel(sendClose: true);

    private void ContainerEscape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ContainerPanel.Visibility != Visibility.Visible)
            return;

        // 命令建议浮层开着时把 Esc 让给它（面板开着也不耽误关浮层）
        if (CommandSuggestionPopup.IsOpen)
            return;

        args.Handled = true;
        CloseContainerPanel(sendClose: true);
    }

    /// <summary>
    /// 收起面板。服务器窗口在 <paramref name="sendClose"/> = true 时给服务端发一个"关窗口"包
    /// （服务端随后回的关窗包到达时面板已经收掉了，不会重复动作）；
    /// 玩家背包是常驻窗口，关掉只表示"不再看"：不发关窗包，只让子进程停止推送。
    /// </summary>
    private void CloseContainerPanel(bool sendClose)
    {
        if (_containerAccount is { } account)
        {
            if (_containerIsPlayer)
            {
                // 服务器窗口还开着的话，子进程会顺手把它按全量重发回来（界面回到刚才那个窗口）
                account.WatchPlayerInventory(false);
            }
            else if (sendClose && _containerWindowId is { } windowId)
            {
                account.WindowClick(new MccWindowClick
                {
                    WindowId = windowId,
                    Slot = MccWindowClick.CloseWindowSlot,
                    Action = MccWindowAction.Close,
                });
            }
        }

        _containerAccount = null;
        _containerWindowId = null;
        _containerIsPlayer = false;
        _containerCursorText = string.Empty;
        _containerSlotVisuals.Clear();
        ContainerSlotStack.Children.Clear();
        ContainerPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>"物品 x2"这种一句话描述（光标那行用）。</summary>
    private static string SlotSummary(MccWindowSlot slot)
    {
        (string name, _) = MccTextStyle.Parse(slot.Name);
        return slot.Count > 1 ? $"{name} x{slot.Count}" : name;
    }

    private static ImageSource? CreateIconSource(string path)
    {
        try
        {
            return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        }
        catch
        {
            return null; // 文件被别的进程删了之类：当作没有图标
        }
    }

    /// <summary>耐久条颜色：满绿 → 半黄 → 空红（和 MC 的耐久条一个观感）。</summary>
    private static Windows.UI.Color DurabilityColor(double remaining)
    {
        remaining = Math.Clamp(remaining, 0, 1);

        double r, g;
        if (remaining <= 0.5)
        {
            // 红 → 黄
            double t = remaining * 2;
            r = 200 + 20 * t;
            g = 40 + 160 * t;
        }
        else
        {
            // 黄 → 绿
            double t = (remaining - 0.5) * 2;
            r = 220 - 160 * t;
            g = 200;
        }

        return Windows.UI.Color.FromArgb(0xFF, (byte)r, (byte)g, 40);
    }

    /// <summary>
    /// 原版容器的窗口标题是服务端发来的英文名（名字来自 MCC 内嵌的 en_us.json，
    /// 例如箱子的 <c>container.chest</c> → "Chest"）。这里把"对得上号的原版标题"换成中文，
    /// 免得满屏中文里孤零零站着一个 "Chest"；服务器自定义的标题（商店 GUI 之类）原样显示。
    /// </summary>
    private static string VanillaContainerTitle(string title)
    {
        return title switch
        {
            "Chest" => "箱子",
            "Large Chest" => "大箱子",
            "Barrel" => "木桶",
            "Ender Chest" => "末影箱",
            "Shulker Box" => "潜影盒",
            "Dispenser" => "发射器",
            "Dropper" => "投掷器",
            "Crafter" => "合成器",
            "Crafting" => "工作台",
            "Furnace" => "熔炉",
            "Blast Furnace" => "高炉",
            "Smoker" => "烟熏炉",
            "Brewing Stand" => "酿造台",
            "Enchant" => "附魔台",
            "Anvil" => "铁砧",
            "Repair & Name" => "铁砧",
            "Repair & Disenchant" => "砂轮",
            "Beacon" => "信标",
            "Stonecutter" => "切石机",
            "Loom" => "织布机",
            "Cartography Table" => "制图台",
            "Upgrade Gear" => "锻造台",
            "Lectern" => "讲台",
            "Villager" => "村民",
            "Wandering Trader" => "流浪商人",
            // MCC 给玩家背包（窗口 0）起的名字，界面固定显示成"背包"
            "Player Inventory" => "背包",
            _ => title,
        };
    }

    /// <summary>容器类型 → 中文标签（认不出来就直接显示类型的原名）。</summary>
    private static string ContainerTypeLabel(string type)
    {
        return type switch
        {
            "Generic_9x1" or "Generic_9x2" or "Generic_9x3" or "Generic_9x4" or "Generic_9x5" or "Generic_9x6" => "箱子",
            "ShulkerBox" => "潜影盒",
            "Generic_3x3" => "发射器 / 投掷器",
            "Crafter" => "合成器",
            "Crafting" => "工作台",
            "Furnace" => "熔炉",
            "BlastFurnace" => "高炉",
            "Smoker" => "烟熏炉",
            "Enchantment" => "附魔台",
            "BrewingStand" => "酿造台",
            "Hopper" => "漏斗",
            "Merchant" => "村民交易",
            "Anvil" => "铁砧",
            "Beacon" => "信标",
            "Grindstone" => "砂轮",
            "Stonecutter" => "切石机",
            "Loom" => "织布机",
            "Cartography" => "制图台",
            "SmightingTable" => "锻造台",
            "Lectern" => "讲台",
            "PlayerInventory" => "背包",
            _ => type,
        };
    }

    #endregion

    // ---- 自动指令（2026-10-22 需求）----

    /// <summary>弹层里的「新建指令」：追加一条空指令（默认 60 秒、不勾进服立即执行）。</summary>
    private void AddAutoCommand_Click(object sender, RoutedEventArgs e) => ViewModel.AddAutoCommand();

    /// <summary>行尾 ×：删掉 Tag 里那条指令。</summary>
    private void RemoveAutoCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AutoCommandItem item })
            ViewModel.RemoveAutoCommand(item);
    }
}
