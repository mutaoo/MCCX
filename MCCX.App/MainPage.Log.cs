using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using MCCX_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace MCCX_App;

// ============================================================================
// 日志区（D 方案：虚拟化列表）—— 2026-10-18 重写
//
// 背景：1.0.2 之前日志区是**一个**撑到内容全高的大只读 TextBox（外层 LogScrollHost
// 负责滚动）。全量 Text= 重建的代价随行数线性上涨（实测 1100 行 393 ms、1 万行 5.3 s、
// 5 万行 73 s），每来一批日志都要重建一次，越用越卡。
//
// D 方案：外层 ScrollViewer(LogScrollHost) 里放 **ItemsRepeater**（虚拟化，只实体化
// 可见的那些行），每行一个**只读 TextBox**（行模板）。代价与可见行数成正比、与总行数
// 无关（实测每次刷新 0.4~1.8 ms，容器恒 25 个）。
//
// 为什么行模板用只读 TextBox 而不是 TextBlock：自动化测试链要靠
//   Get-RecentLogText → LogScrollHost 下收集 **Edit + ValuePattern** 读日志，
//   g_search 还要 LogBox 的 TextPattern 选区；TextBlock 拿不到这一套（会读出空串）。
//
// 这个文件把日志区的**全部**既有能力按 D 重新实现一遍：
//   ① O(可见) 更新：集合直接做 ItemsSource，增删一条只动一行的容器；
//   ② 跨行选区 + Ctrl+C 多行复制：行内/跨行都自己算（TextBox 行不参与命中测试，
//      指针全部由行外壳接管），行为与旧的"一个大 TextBox"一致；
//   ③ 右键复制：行外壳挂 LogContextMenu，选中就复制选中的字、没选中复制命中的那一行；
//   ④ 搜索：有词时列表只喂"命中的行"（ItemsSource 换成筛过的集合）、计数、Enter 跳转；
//   ⑤ Ctrl+左键点链接 + 链接下划线：每行自己的 Canvas 覆盖层，字符级定位；
//   ⑥ 贴底跟随 + 选区暂停跟随；
//   ⑦ UIA 链路：LogScrollHost 的 ScrollPattern、行 Edit 的 ValuePattern、LogBox 的
//      TextPattern 选区，全部保留。
// ============================================================================

public sealed partial class MainPage
{
    // ===================== 滚动 / 跟随状态 =====================

    /// <summary>用户手动滚上去看历史后，暂时不再自动跟随；自己滚回底部附近（≤ 这个距离）就恢复。</summary>
    private const double LogFollowResumeThresholdPx = 24;

    private ScrollViewer? _logScrollViewer;
    private bool _logScrollPending;

    /// <summary>用户正在翻历史：期间新日志不再把视图拽到底部。</summary>
    private bool _logFollowPaused;

    /// <summary>
    /// 贴底重试用的一次性定时器（每 tick 一次尝试）。**必须跨帧**：从 Low 优先级回调里再
    /// TryEnqueue(Low) 不会等下一帧——队列会被连续抽干，12 次重试全在同一个 tick 里跑完，
    /// 而 ChangeView 的落点和列表 extent 都要等下一帧布局才更新（实测 12 次全打空，视图停在 0）。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _logSettleTimer;

    private int _logSettleAttempts;

    /// <summary>贴底重试上限（定时器 tick 数，16 ms 一个 ≈ 0.5 秒的追赶窗口）。</summary>
    private const int MaxLogSettlePasses = 30;

    /// <summary>
    /// 用户自己翻日志的动作时刻（滚轮 / 拖滚动条 / 触摸拖动 / 翻页键）。
    /// 只有这类动作才把"偏移变小"当成"用户在看历史"：程序化 ChangeView 的落点会延迟一两帧
    /// 才生效、还会被虚拟化列表变动的 extent 钳回去，也不是用户在翻。
    /// </summary>
    private long _logUserScrollTicks;

    /// <summary>用户滚动动作之后的这段时间里，偏移变化都算"用户在翻历史"。</summary>
    private const long LogUserScrollGraceMs = 800;

    /// <summary>
    /// 我们自己刚滚过之后的这段时间里，偏移怎么变都算自己滚动的余波（不改判成用户上翻）。
    /// 虚拟化列表的 ChangeView 是异步的：落点要等下一帧，extent 还会重估一次把偏移钳回去。
    /// 实测把自己的落点误判成用户上翻之后，贴底会**永久**停住（停在 85.8%，38 秒不动）。
    /// </summary>
    private long _logSelfScrollUntilTicks;

    private const long LogSelfScrollGraceMs = 600;

    /// <summary>
    /// 自己滚动的落点被虚拟化 extent 钳回去的余波最多大概这么远；偏移比"自己上次要滚到的位置"
    /// 还往回走了这么多，才值得怀疑是用户在翻历史。
    /// </summary>
    private const double LogFollowSelfSlackPx = 80;

    /// <summary>偏移离底部还有这么远才算"用户在翻历史"（一两行的回弹不算）。</summary>
    private const double LogFollowPauseMinPx = 80;

    /// <summary>LayoutUpdated 兜底的限流：距上次兜底至少间隔这么多毫秒。</summary>
    private const long LogLayoutFollowIntervalMs = 60;

    private long _logLayoutFollowTicks;

    /// <summary>当前挂在日志上的那份集合（换账号 = 换一份）。</summary>
    private ObservableCollection<LogEntry>? _hookedLogs;

    /// <summary>
    /// 上一次**我们自己**要滚到的偏移（-1 = 还没滚过）。
    /// 用来分辨"偏移变小"是自己滚完的余波还是用户在翻历史：虚拟化列表滚一下就会重估 extent，
    /// 把落点往回钳一两行的距离，那不是用户在翻。
    /// </summary>
    private double _logSelfTarget = -1;

    /// <summary>搜索筛选用：只放"命中的行"，作为 ItemsRepeater 的临时 ItemsSource。</summary>
    private readonly ObservableCollection<LogEntry> _filteredLogs = [];

    /// <summary>当前是否处于"搜索筛选"模式（ItemsSource 是 _filteredLogs）。</summary>
    private bool _logFilterMode;

    private bool _logViewHooked;

    // ===================== 初始化 =====================

    /// <summary>
    /// 把日志集合挂到本页（换账号 = 换一份集合），并把 ItemsRepeater 的 ItemsSource 指过去。
    ///
    /// 两层通知：集合本身（增删 / 清空）走 <see cref="OnLogsCollectionChanged"/>；
    /// 单条日志**就地改文本**（连续重复合并成"原文 xN"）走 LogEntry.PropertyChanged——
    /// 合并不增删条目，只听集合会漏掉计数刷新。
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

            ClearLogSelection();
        }

        ApplyLogItemsSource();
        ForceScrollLogToEnd();
    }

    /// <summary>把 ItemsRepeater 的源设成"全量"或"筛选后的"那份集合。</summary>
    private void ApplyLogItemsSource()
    {
        if (LogRepeater is null)
            return;

        if (_logFilterMode)
            RebuildFilteredLogs();

        LogRepeater.ItemsSource = _logFilterMode ? _filteredLogs : _hookedLogs;
    }

    /// <summary>把当前可见行数（全量或筛选后）取出来。</summary>
    private int GetLogItemCount() => _logFilterMode ? _filteredLogs.Count : _hookedLogs?.Count ?? 0;

    /// <summary>取可见列表里第 index 行的文本。</summary>
    private string GetLogLineText(int index)
    {
        if (_logFilterMode)
            return index >= 0 && index < _filteredLogs.Count ? _filteredLogs[index].Text : string.Empty;

        return _hookedLogs is { } logs && index >= 0 && index < logs.Count
            ? logs[index].Text
            : string.Empty;
    }

    /// <summary>日志区的 ScrollViewer 只有模板套用后才有，这里挂上 ViewChanged 并准备键盘处理。</summary>
    private void LogScrollHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (_logViewHooked)
            return;

        _logViewHooked = true;
        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is { } scroll)
        {
            scroll.ViewChanged += LogScrollViewer_ViewChanged;
            // 焦点落在日志区（点一下就会 Focus 到宿主）时的键盘：Ctrl+C 复制、Ctrl+A 全选。
            scroll.AddHandler(KeyDownEvent, new KeyEventHandler(LogScrollHost_KeyDown), handledEventsToo: true);

            // 用户"自己在翻日志"的手势：滚轮 / 在滚动条上按下 / 触摸拖动。
            // 只有这些才让跟随暂停——程序化滚动 + 虚拟化列表变动的 extent 也会让偏移变小。
            scroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(LogScrollHost_PointerWheelChanged), handledEventsToo: true);
            scroll.AddHandler(PointerPressedEvent, new PointerEventHandler(LogScrollHost_PointerPressed), handledEventsToo: true);
        }

        // 版面兜底：重新折行、列表换源都会改行数，这些不走 CollectionChanged
        LogRepeater.LayoutUpdated -= LogLayoutUpdated;
        LogRepeater.LayoutUpdated += LogLayoutUpdated;

        // 行被实体化（滚动进出视口、换源重建）时重画它的下划线与选区
        LogRepeater.ElementPrepared += OnLogElementPrepared;
        LogRepeater.ElementClearing += OnLogElementClearing;

        // 首次布局可能早于这里（ItemsSource 在构造函数里就设好了），补画一遍初始可见行
        RedrawAllLogRows();

        ForceScrollLogToEnd();
    }

    /// <summary>取日志区的滚动宿主：只认 XAML 里显式写出来的 LogScrollHost。</summary>
    private ScrollViewer? ResolveLogScrollHost() => LogScrollHost;

    // ===================== 集合变化 =====================

    /// <summary>
    /// 一条日志可能拆成多行、MCC 又会成批刷日志，CollectionChanged 会在一帧内连发几十次。
    /// 这里只做三件事：订阅/退订合并计数通知、筛选模式下标脏、按帧合并成一次贴底。
    /// </summary>
    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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

        // 裁行（从头部删）时可见下标整体前移：选区跟着挪；没删到的行就把选区留着。
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex == 0 && !_logFilterMode)
            ShiftLogSelection(-e.OldItems?.Count ?? 0);

        if (e.Action == NotifyCollectionChangedAction.Reset)
            ClearLogSelection();

        if (_logFilterMode)
        {
            // 头部被裁掉 = 筛选结果里的下标全乱了，只能整份重建；单纯追加则增量补。
            bool headTrimmed = e.Action == NotifyCollectionChangedAction.Remove && e.OldStartingIndex == 0;
            MarkFilterDirty(fullRebuild: headTrimmed || e.Action == NotifyCollectionChangedAction.Reset);
        }

        if (_logScrollPending)
            return;

        _logScrollPending = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FollowLogToEnd))
            _logScrollPending = false;
    }

    /// <summary>某条日志的展示文本被就地改了（重复合并成"原文 xN"）。绑定的 TextBox 会自己刷，这里只重画下划线/选区。</summary>
    private void OnLogEntryTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogEntry.Text))
        {
            // 合并计数会改文本 → 命中与否也可能变；这种少量变化直接整份重建（成本很低）。
            if (_logFilterMode)
                MarkFilterDirty(fullRebuild: true);

            // 文本变了、下划线位置也变；实体行由 x:Bind 刷新 TextBox.Text → TextChanged 里重画。
            MarkAllLogRowsDirty();
        }
    }

    // ===================== 贴底跟随 =====================

    /// <summary>
    /// 把日志列表滚到底部最新一行，并恢复跟随（切账号、清空日志搜索筛选用）。
    /// 先等 ItemsSource 换好、再等一帧量完布局，两次入队才能滚到真正的末尾。
    /// </summary>
    private void ForceScrollLogToEnd()
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _logScrollViewer ??= ResolveLogScrollHost();

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_logScrollViewer is { } scroll)
                {
                    // 换账号等于换一份日志：无视用户之前翻到哪儿，强制恢复跟随
                    _logFollowPaused = false;
                    ScrollLogTo(scroll, scroll.ScrollableHeight);
                    ScheduleScrollSettle();
                }
            });
        });
    }

    private void FollowLogToEnd()
    {
        _logScrollPending = false;

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

        ScrollLogTo(scroll, end);
        ScheduleScrollSettle();
    }

    /// <summary>
    /// 刚插入的行往往还没参与布局，而且列表的 ScrollableHeight 是**估算值**（实体化一批行就变），
    /// 一次 ChangeView 只滚到"当前估出来的"底部：隔一帧再量一次，没贴住就再滚，直到真贴住或到上限。
    ///
    /// 重试必须跑在**定时器 tick** 上，不能靠嵌套 `TryEnqueue`——同一个优先级被连续抽干，
    /// 十几次重试会在同一个 tick 里全部跑完，而 ChangeView 的落点和 extent 都要等下一帧才更新。
    /// </summary>
    private void ScheduleScrollSettle()
    {
        _logSettleAttempts = 0;

        if (_logSettleTimer is null)
        {
            _logSettleTimer = DispatcherQueue.CreateTimer();
            _logSettleTimer.Interval = TimeSpan.FromMilliseconds(16);
            _logSettleTimer.IsRepeating = true;
            _logSettleTimer.Tick += LogSettleTimer_Tick;
        }

        // 重新计时：新一批日志来了就从"下一帧"重新开始追
        _logSettleTimer.Stop();
        _logSettleTimer.Start();
    }

    private void LogSettleTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // 用户翻历史 / 正在框选 / 追够了：停
        if (_logFollowPaused || LogSelectionInProgress || ++_logSettleAttempts > MaxLogSettlePasses)
        {
            sender.Stop();
            return;
        }

        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is not { } scroll)
        {
            sender.Stop();
            return;
        }

        double end = scroll.ScrollableHeight;
        if (end - scroll.VerticalOffset <= 1)
        {
            sender.Stop(); // 真贴住了
            return;
        }

        ScrollLogTo(scroll, end);
    }

    /// <summary>版面变化的兜底：窗口宽度变了要重新折行、列表换源也会改行数，这些都不走 CollectionChanged。</summary>
    private void LogLayoutUpdated(object? sender, object e)
    {
        if (_logFollowPaused)
            return;

        if (LogSelectionInProgress)
            return;

        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is not { } scroll)
            return;

        double end = scroll.ScrollableHeight;
        if (end - scroll.VerticalOffset <= 1)
            return;

        // 和 ViewChanged 同一套判据：视图离底部很远、又不是我们自己刚滚的 → 判定"用户在翻历史"，
        // 这里也**不能**抢着往回拽（否则用户刚滚上去就被 LayoutUpdated 弹回底部）。
        if (ShouldPauseLogFollow(scroll, end))
        {
            _logFollowPaused = true;
            return;
        }

        long now = Environment.TickCount64;
        if (now - _logLayoutFollowTicks < LogLayoutFollowIntervalMs)
            return;

        _logLayoutFollowTicks = now;
        ScrollLogTo(scroll, end);
    }

    /// <summary>
    /// 这次视图变化该不该判定为"用户在翻历史"。
    ///   · 有用户滚动手势（滚轮 / 拖滚动条 / 触摸拖动 / 翻页键）= 是；
    ///   · 我们刚自己滚过（<see cref="LogSelfScrollGraceMs"/> 内）= 不是（异步落点 + extent 重估的余波）；
    ///   · 否则偏移比"我们自己上次要滚到的位置"往回走了很远、而且离底部也很远 = 是
    ///     （UIA/程序化"滚到顶部"、用户拖走一屏都落在这里）。
    /// </summary>
    private bool ShouldPauseLogFollow(ScrollViewer scroll, double end)
    {
        double offset = scroll.VerticalOffset;
        long now = Environment.TickCount64;

        if (now - _logUserScrollTicks < LogUserScrollGraceMs)
            return true;

        if (now <= _logSelfScrollUntilTicks)
            return false;

        bool farFromOurTarget = _logSelfTarget < 0 || offset < _logSelfTarget - LogFollowSelfSlackPx;
        bool farFromEnd = end - offset > LogFollowPauseMinPx;
        return farFromOurTarget && farFromEnd;
    }

    /// <summary>滚到底部（关闭动画：动画帧同样会被新一轮日志打断，观感上就是闪）。</summary>
    private void ScrollLogTo(ScrollViewer scroll, double offset)
    {
        // 记下"我们自己要滚到哪儿 / 到什么时候"：ViewChanged 与 LayoutUpdated 靠它把
        // 自己滚动的异步余波和用户上翻区分开。
        _logSelfTarget = offset;
        _logSelfScrollUntilTicks = Environment.TickCount64 + LogSelfScrollGraceMs;
        scroll.ChangeView(null, offset, null, disableAnimation: true);
    }

    /// <summary>
    /// 视图变化：回到底部附近 → 恢复跟随；确实是**用户在翻历史** → 暂停跟随。
    /// 判据见 <see cref="ShouldPauseLogFollow"/>。
    /// </summary>
    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;

        double end = scroll.ScrollableHeight;
        double offset = scroll.VerticalOffset;

        if (end - offset <= LogFollowResumeThresholdPx)
            _logFollowPaused = false;
        else if (ShouldPauseLogFollow(scroll, end))
            _logFollowPaused = true;
    }

    /// <summary>用户主动滚动（滚轮 / 在滚动条上按下 / 触摸拖动 / 翻页键）：记一笔时刻。</summary>
    private void MarkLogUserScroll() => _logUserScrollTicks = Environment.TickCount64;

    private void LogScrollHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => MarkLogUserScroll();

    private void LogScrollHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 触摸/笔在日志区按下 = 准备拖动滚动（鼠标按下只可能是"点行/框选/拖滚动条"）。
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
        {
            MarkLogUserScroll();
            return;
        }

        if (IsInsideScrollBar(e.OriginalSource as DependencyObject))
            MarkLogUserScroll();
    }

    private static bool IsInsideScrollBar(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar)
                return true;

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    // ===================== 行实体化：重画 =====================

    private void OnLogElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is Grid grid)
        {
            ApplyLogRowWidth(grid);
            RedrawLogRow(grid);
        }
    }

    /// <summary>
    /// 给日志行钉死宽度（2026-10-18 修"日志之间大片空白"的实体修法）。
    ///
    /// 行高是 `TextBox TextWrapping="Wrap"` 折出来的，而虚拟化列表**第一屏**元素拿到的可用宽度
    /// 有时还没定下来（实测按 ~64px 量，短行被折成 4~13 行 → 28px 的行变成 56/112/140/366px），
    /// StackLayout 又把那个错高度当元素尺寸缓存住不再重测，于是行下面拖一大条空白。
    ///
    /// 对照实验里**只有改宽度**能把错高度纠正回来（只改高度不行），所以这里把行的宽度显式钉成
    /// 日志区视口宽度：宽度一变就会重新折行、行高回到正确值，之后每次测量也都用这个宽度、不再看
    /// 当时那个还没定下来的可用宽度。视口宽度变化时由 <see cref="LogScrollHost_SizeChanged"/> 全量同步。
    /// </summary>
    private void ApplyLogRowWidth(Grid root)
    {
        if (root.Children.Count < 1 || root.Children[0] is not TextBox box)
            return;

        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is not { } scroll)
            return;

        double width = scroll.ViewportWidth;
        if (double.IsNaN(width) || width <= 0)
            return;

        if (double.IsNaN(box.Width) || Math.Abs(box.Width - width) > 0.5)
            box.Width = width;
    }

    /// <summary>日志区视口宽度变了（窗口拉宽/变窄、命令行收起等）：所有已实体行的宽度重钉一遍。</summary>
    private void LogScrollHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int n = VisualTreeHelper.GetChildrenCount(LogRepeater);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(LogRepeater, i) is Grid grid)
                ApplyLogRowWidth(grid);
        }
    }

    private void OnLogElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        // 容器要被回收：清掉它身上残留的选区高亮/下划线，免得复用给别人时闪一下。
        if (args.Element is Grid grid && grid.Children.Count >= 2 && grid.Children[1] is Canvas layer)
            layer.Children.Clear();
    }

    /// <summary>把所有已实体行的下划线与选区重画一遍（选区变化、整体文本变化时用）。</summary>
    private void RedrawAllLogRows()
    {
        int n = VisualTreeHelper.GetChildrenCount(LogRepeater);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(LogRepeater, i) is Grid grid)
                RedrawLogRow(grid);
        }
    }

    /// <summary>标脏所有已实体行（文本变化后用；实际就是重画一次）。</summary>
    private void MarkAllLogRowsDirty() => RedrawAllLogRows();

    private void LogLineBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && FindLogRowRoot(box) is { } root)
            RedrawLogRow(root);
    }

    private void LogLineBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is TextBox box && FindLogRowRoot(box) is { } root)
            RedrawLogRow(root);
    }

    /// <summary>从行里任意一个控件往上找"行外壳"（ItemsRepeater 的直接子元素）。</summary>
    private Grid? FindLogRowRoot(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Grid grid && LogRepeater.GetElementIndex(grid) >= 0)
                return grid;

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// 重画一行的覆盖层：链接下划线 + 选区高亮 + 搜索命中的原生选区（给 UIA 读）。
    /// 行是虚拟化的，只有可见的那几十行会走到这里，代价 O(可见)。
    /// </summary>
    private void RedrawLogRow(Grid root)
    {
        int index = LogRepeater.GetElementIndex(root);
        if (index < 0 || root.Children.Count < 2)
            return;

        if (root.Children[0] is not TextBox box || root.Children[1] is not Canvas layer)
            return;

        layer.Children.Clear();

        string text = box.Text ?? string.Empty;

        DrawLogLinkUnderlines(box, layer, text);
        DrawLogSelectionForRow(box, layer, text, index);
        ApplyNativeSearchSelection(box, text, index);
    }

    // ===================== 字符几何 =====================

    /// <summary>
    /// 某个字符的右边缘 X。WinUI 的 GetRectFromCharacterIndex(trailingEdge: false) 返回**字符左边**
    /// 的零宽"字符边"矩形（Width 恒为 0），右边只能取下一个字符的左边；下标越界或下一个字符已换行（折行）
    /// 时，退回用同一行上一个字符的宽度（等宽字体步长）推算。
    /// </summary>
    private static double GetCharRightEdge(TextBox box, string text, int ci, Rect r)
    {
        if (ci + 1 < text.Length)
        {
            Rect next = box.GetRectFromCharacterIndex(ci + 1, trailingEdge: false);
            if (!double.IsNaN(next.Left) && next.Left > r.Left && Math.Abs(next.Top - r.Top) < 0.5)
                return next.Left;
        }

        if (ci > 0)
        {
            Rect prev = box.GetRectFromCharacterIndex(ci - 1, trailingEdge: false);
            if (!double.IsNaN(prev.Left) && prev.Left < r.Left && Math.Abs(prev.Top - r.Top) < 0.5)
                return r.Left + (r.Left - prev.Left);
        }

        return r.Left + 7; // 兜底：Consolas 12 号一个字宽约 6.5~7 DIP
    }

    /// <summary>
    /// 把一个字符区间 [from, to) 拆成"同一视觉行上的矩形段"。折行时一个逻辑行有多个视觉行，
    /// 靠字符矩形的 Top 是不是同一个来分组；每段返回 (x1, x2, top, height)。
    /// </summary>
    private static IEnumerable<(double X1, double X2, double Top, double Height)> MeasureCharRange(
        TextBox box, string text, int from, int to)
    {
        from = Math.Clamp(from, 0, text.Length);
        to = Math.Clamp(to, 0, text.Length);
        if (to <= from)
            yield break;

        double lastTop = double.NaN;
        double segStart = 0;
        double segEnd = 0;
        double segHeight = 0;

        for (int ci = from; ci < to; ci++)
        {
            Rect r = box.GetRectFromCharacterIndex(ci, trailingEdge: false);
            if (double.IsNaN(r.Top) || double.IsNaN(r.Left))
                continue;

            double right = GetCharRightEdge(box, text, ci, r);
            // 2026-10-09 ②(b) 实测：字符矩形高 = 2×行高（行高 14 → 矩形高 28），除以 2 才是本行行高。
            double height = double.IsNaN(r.Height) ? 14 : r.Height / 2;
            if (height <= 0)
                height = 14;

            if (double.IsNaN(lastTop) || Math.Abs(r.Top - lastTop) > 0.5)
            {
                if (!double.IsNaN(lastTop))
                    yield return (segStart, segEnd, lastTop, segHeight);

                lastTop = r.Top;
                segStart = r.Left;
                segEnd = right;
                segHeight = height;
            }
            else
            {
                segEnd = right;
            }
        }

        if (!double.IsNaN(lastTop))
            yield return (segStart, segEnd, lastTop, segHeight);
    }

    // ===================== 链接下划线 + Ctrl+左键跳转 =====================

    /// <summary>
    /// 日志行里的链接（截到空白与中英文标点前）。
    /// 除 http(s):// 外也认 www. 前缀与常见 TLD 结尾的裸域名；TLD 用白名单，
    /// 避开 server.jar / test.out.log 这类文件名假阳性。
    /// </summary>
    private static readonly Regex UrlRegex = new(
        @"(?:https?://|www\.)[^\s""'<>\[\]()（），。；、]+|(?<![\w.-])(?:[a-z0-9-]+\.)+(?:com|cn|net|org|io|co|me|cc|xyz|top|vip|club|app|dev|site|online|shop|fun|live|tv|info|biz|store|tech|space|pro|life|work|day|wiki|news|game|win|today|gov|edu|mil)\b(?!\.[a-z0-9])(?!\\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>下划线颜色：次级文字色，浅/暗色主题都跟着走（只作"可点"提示，不抢正文）。</summary>
    private static Brush UnderlineBrush =>
        (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>在一行的覆盖层上画它所有链接的下划线（链接可能因折行拆成多段）。</summary>
    private void DrawLogLinkUnderlines(TextBox box, Canvas layer, string text)
    {
        if (text.Length == 0)
            return;

        foreach (Match m in UrlRegex.Matches(text))
        {
            string url = m.Value.TrimEnd('.', ':', ';', ',', '!', '?', '，', '。');
            if (url.Length == 0)
                continue;

            foreach ((double x1, double x2, double top, double height) in MeasureCharRange(box, text, m.Index, m.Index + url.Length))
            {
                if (x2 <= x1)
                    continue;

                var line = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = x2 - x1,
                    Height = 1,
                    Fill = UnderlineBrush,
                };
                double y = top + height - 1; // 压在字脚下，不盖住下一行
                if (y < top)
                    y = top;
                Canvas.SetLeft(line, x1);
                Canvas.SetTop(line, y);
                layer.Children.Add(line);
            }
        }
    }

    /// <summary>系统级 Ctrl 虚拟键（左右 Ctrl 都算）。</summary>
    private const int VkControl = 0x11;

    /// <summary>问 Windows 物理按键状态（不是本线程的消息队列状态，见 <see cref="IsCtrlDown"/>）。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Ctrl 是否按住。三个来源问一遍，任一为真就算按住——单独任何一个都不可靠：
    ///   ① <paramref name="mods"/>（指针事件的 <see cref="PointerRoutedEventArgs.KeyModifiers"/>）最贴现场；
    ///   ② <c>GetKeyStateForCurrentThread</c> 查的是**本线程键盘消息队列**的状态：先按住 Ctrl 再激活窗口、
    ///      或 Ctrl 的按键消息没进过本线程，它就报"没按"（实测按下 Ctrl 去点日志行，指针事件里
    ///      ① ② 都报没按，于是 Ctrl+左键点链接永远打不开）；
    ///   ③ <c>GetAsyncKeyState</c> 读**物理按键**，不受消息路由影响，兜底。
    /// </summary>
    private static bool IsCtrlDown(Windows.System.VirtualKeyModifiers mods = Windows.System.VirtualKeyModifiers.None)
    {
        if ((mods & Windows.System.VirtualKeyModifiers.Control) != 0)
            return true;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            return true;

        return (GetAsyncKeyState(VkControl) & 0x8000) != 0;
    }

    /// <summary>
    /// 打开某段日志文本里的链接（Ctrl+点击行）。<paramref name="preferCol"/> ≥ 0 时取**离该列最近**的
    /// 匹配——一行有多个链接时点哪个都开第一个（连接行排最前的正是服务器地址）。
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

        // 裸域名（www.baidu.com / baidu.com）没协议头，new Uri 会抛——无协议头统一补 https:// 再开。
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

    // ===================== 选区（行内 / 跨行） =====================

    /// <summary>选区颜色：和旧的 Single TextBox 选中高亮同一个色（#4080D0FF）。</summary>
    private static readonly Brush LogSelectionBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x80, 0xD0, 0xFF));

    /// <summary>选区锚点（按下处）所在的行下标 / 行内字符列。行下标是"可见列表"的下标。</summary>
    private int _logSelAnchorLine = -1;
    private int _logSelAnchorCol;

    /// <summary>选区另一端（拖动处）。</summary>
    private int _logSelFocusLine = -1;
    private int _logSelFocusCol;

    /// <summary>指针正按着在拉选区。</summary>
    private bool _logSelecting;

    /// <summary>这次按下的起点行/列（Ctrl+点击判"是不是一次点击"、以及定位链接用）。松开时清成 -1。</summary>
    private int _logPressLine = -1;
    private int _logPressCol;

    /// <summary>
    /// 按下那一刻 Ctrl 是否按住——Ctrl+左键点链接**只看这个**，松开时不再重新问。
    /// 实测：按住 Ctrl 点日志行，按下时 <c>KeyModifiers=Control</c>，125 ms 后松开时
    /// KeyModifiers / 线程键盘状态 / 物理状态**三个都变成"没按"**（按下到松开之间框架把修饰键
    /// 状态清了一次），于是松开时判 Ctrl 永远判不到、链接打不开。用户的手势是"按着 Ctrl 点下去"，
    /// 所以按下时记一笔才是对的。
    /// </summary>
    private bool _logPressCtrl;

    /// <summary>有非空选区。</summary>
    private bool LogSelectionActive =>
        _logSelAnchorLine >= 0 && _logSelFocusLine >= 0
        && !(_logSelAnchorLine == _logSelFocusLine && _logSelAnchorCol == _logSelFocusCol);

    /// <summary>
    /// 用户此刻正在日志区做选择操作（按着指针 或 已经拉出了选区）。
    /// 自动跟随靠 ChangeView 挪视图，而挪一下就把正在拖的选区作废——这段时间不跟随、也不重排列表。
    /// </summary>
    private bool LogSelectionInProgress => _logSelecting || LogSelectionActive;

    private void ClearLogSelection()
    {
        _logSelAnchorLine = -1;
        _logSelAnchorCol = 0;
        _logSelFocusLine = -1;
        _logSelFocusCol = 0;
        _logSelecting = false;
    }

    /// <summary>裁行时可见下标整体前移，选区跟着挪；挪没了就清掉。</summary>
    private void ShiftLogSelection(int delta)
    {
        if (_logSelAnchorLine < 0 && _logSelFocusLine < 0)
            return;

        _logSelAnchorLine += delta;
        _logSelFocusLine += delta;
        if (_logSelAnchorLine < 0 || _logSelFocusLine < 0)
            ClearLogSelection();
    }

    /// <summary>把选区（可能反向）整理成 start ≤ end 的三个量。</summary>
    private void GetSelectedRange(out int startLine, out int startCol, out int endLine, out int endCol)
    {
        bool reversed = _logSelFocusLine < _logSelAnchorLine
            || (_logSelFocusLine == _logSelAnchorLine && _logSelFocusCol < _logSelAnchorCol);

        if (reversed)
        {
            startLine = _logSelFocusLine;
            startCol = _logSelFocusCol;
            endLine = _logSelAnchorLine;
            endCol = _logSelAnchorCol;
        }
        else
        {
            startLine = _logSelAnchorLine;
            startCol = _logSelAnchorCol;
            endLine = _logSelFocusLine;
            endCol = _logSelFocusCol;
        }
    }

    /// <summary>把选区里的字拼出来（多行用 \n 连）。没有选区返回 null。</summary>
    private string? GetSelectedLogText()
    {
        if (!LogSelectionActive)
            return null;

        GetSelectedRange(out int startLine, out int startCol, out int endLine, out int endCol);
        int count = GetLogItemCount();
        if (endLine >= count)
            return null;

        var sb = new StringBuilder();
        for (int i = startLine; i <= endLine; i++)
        {
            string line = GetLogLineText(i);
            int from = i == startLine ? Math.Clamp(startCol, 0, line.Length) : 0;
            int to = i == endLine ? Math.Clamp(endCol, 0, line.Length) : line.Length;
            if (to < from)
                to = from;

            if (i > startLine)
                sb.Append('\n');

            sb.Append(line, from, to - from);
        }

        return sb.ToString();
    }

    /// <summary>画一行在选区里的那段高亮（整行全选时把该视觉行拖到右边缘，像正常编辑器那样）。</summary>
    private void DrawLogSelectionForRow(TextBox box, Canvas layer, string text, int index)
    {
        if (!LogSelectionActive)
            return;

        GetSelectedRange(out int startLine, out int startCol, out int endLine, out int endCol);
        if (index < startLine || index > endLine)
            return;

        int from = index == startLine ? startCol : 0;
        int to = index == endLine ? endCol : text.Length;
        if (to < from)
            to = from;

        bool extendRight = index < endLine || to >= text.Length;

        // 折行的行会拆成多个视觉行：除最后一个视觉行外，整段都铺到行右边缘（像正常编辑器）。
        var segments = new List<(double X1, double X2, double Top, double Height)>();
        segments.AddRange(MeasureCharRange(box, text, from, to));

        for (int s = 0; s < segments.Count; s++)
        {
            (double x1, double x2, double top, double height) = segments[s];
            double right = x2;
            if (extendRight && s < segments.Count - 1)
            {
                double edge = box.ActualWidth;
                if (edge > right)
                    right = edge;
            }

            if (right <= x1)
                continue;

            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = right - x1,
                Height = height,
                Fill = LogSelectionBrush,
            };
            Canvas.SetLeft(rect, x1);
            Canvas.SetTop(rect, top);
            layer.Children.Add(rect);
        }
    }

    /// <summary>
    /// 搜索命中行的**原生**选区：给自动化测试的 TextPattern.GetSelection() 读
    /// （g_search 用它在日志里找"选中了命中处"）。颜色设成透明，界面靠自己的高亮层画。
    /// </summary>
    private void ApplyNativeSearchSelection(TextBox box, string text, int index)
    {
        if (!_logFilterMode || _logSearchIndex < 0 || index != _logSearchIndex)
        {
            if (box.SelectionLength > 0)
                box.Select(0, 0);
            return;
        }

        string query = LogSearchBox?.Text ?? string.Empty;
        if (query.Length == 0)
        {
            if (box.SelectionLength > 0)
                box.Select(0, 0);
            return;
        }

        int col = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (col < 0)
            col = 0;
        int len = Math.Min(query.Length, Math.Max(0, text.Length - col));

        // 只在选区真的变了才 Select：Select 会请求 BringIntoView，重复调用会让视图抖。
        if (box.SelectionStart != col || box.SelectionLength != len)
            box.Select(col, len);
    }

    // ===================== 指针：选择 / 跳转 / 右键 =====================

    private void LogRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid root || root.Children.Count < 2 || root.Children[0] is not TextBox box)
            return;

        var props = e.GetCurrentPoint(root).Properties;
        if (!props.IsLeftButtonPressed)
            return; // 右键交给 ContextFlyout

        int index = LogRepeater.GetElementIndex(root);
        if (index < 0)
            return;

        Point pInBox = e.GetCurrentPoint(box).Position;
        int col = CharIndexInBox(box, pInBox);

        _logSelecting = true;
        _logSelAnchorLine = index;
        _logSelAnchorCol = col;
        _logSelFocusLine = index;
        _logSelFocusCol = col;
        _logPressLine = index;
        _logPressCol = col;
        _logPressCtrl = IsCtrlDown(e.KeyModifiers);

        root.CapturePointer(e.Pointer);
        LogScrollHost?.Focus(FocusState.Programmatic);

        RedrawAllLogRows();
        e.Handled = true;
    }

    private void LogRow_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_logSelecting)
            return;

        Point p = e.GetCurrentPoint(LogRepeater).Position;
        if (!TryHitLogRow(p, out int index, out _, out TextBox? box))
            return;

        int col = CharIndexInBox(box!, e.GetCurrentPoint(box!).Position);
        if (_logSelFocusLine == index && _logSelFocusCol == col)
            return;

        _logSelFocusLine = index;
        _logSelFocusCol = col;
        RedrawAllLogRows();
        e.Handled = true;
    }

    private void LogRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid root)
            root.ReleasePointerCapture(e.Pointer);

        _logSelecting = false;

        // Ctrl+左键点链接：两个"别用错信号"的坑都在这里：
        //   ① 判"刚才是不是一次点击"**不能看 _logSelecting**——松开鼠标时 PointerCaptureLost
        //      会先于 PointerReleased 到达，_logSelecting 已经被清成 false，于是永远打不开链接；
        //   ② 判"Ctrl 按着没有"**不能现在问**——按下到松开之间修饰键状态会被清（实测都变没按），
        //      用按下时记的 _logPressCtrl。
        // 点击 = 这次松开跟着我们的一次按下 + 按下和松开落在同一处（没拖出选区）。
        if (_logPressCtrl && _logPressLine >= 0
            && _logSelAnchorLine == _logSelFocusLine && _logSelAnchorCol == _logSelFocusCol)
        {
            int index = _logPressLine;
            if (TryOpenLinkInText(GetLogLineText(index), _logPressCol))
                e.Handled = true;
        }

        _logPressLine = -1;
        RedrawAllLogRows();
    }

    /// <summary>
    /// 指针捕获丢了。**不要在这里清 _logPressLine**：松开鼠标时 PointerCaptureLost 会先于
    /// PointerReleased 到达，清掉它 Ctrl+左键点链接就再也判不出"刚才是一次点击"了。
    /// </summary>
    private void LogRow_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid root)
            root.ReleasePointerCapture(e.Pointer);

        _logSelecting = false;
    }

    private void LogRow_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _logSelecting = false;
    }

    /// <summary>
    /// 右键日志行：记下命中那一行的文本（菜单的"复制"在没选中时退回用它），
    /// 然后让行外壳上的 ContextFlyout（LogContextMenu）照常弹出。
    /// </summary>
    private void LogRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Grid root && root.Children.Count >= 1 && root.Children[0] is TextBox box)
            _logContextText = box.Text;
    }

    /// <summary>最近一次右键命中的那一行文本。</summary>
    private string? _logContextText;

    /// <summary>右键菜单「全选」：和 Ctrl+A 走同一条逻辑（见 <see cref="LogSelectAll"/>）。</summary>
    private void LogContextSelectAll_Click(object sender, RoutedEventArgs e) => LogSelectAll();

    /// <summary>整份可见日志全选（Ctrl+A / 右键菜单「全选」共用）。返回是否真的选中了东西。</summary>
    private bool LogSelectAll()
    {
        int count = GetLogItemCount();
        if (count <= 0)
            return false;

        _logSelAnchorLine = 0;
        _logSelAnchorCol = 0;
        _logSelFocusLine = count - 1;
        _logSelFocusCol = GetLogLineText(count - 1).Length;
        RedrawAllLogRows();
        return true;
    }

    /// <summary>
    /// 右键菜单「复制」：有选中就只复制选中的那几个字（选区可以横跨多行，正是"像记事本一样复制好几行"）；
    /// 没选中才退回右键命中的那一行。
    /// </summary>
    private void LogContextCopy_Click(object sender, RoutedEventArgs e)
    {
        string? selected = GetSelectedLogText();
        if (!string.IsNullOrEmpty(selected))
        {
            CopyLinesToClipboard([selected]);
            return;
        }

        if (!string.IsNullOrEmpty(_logContextText))
            CopyLinesToClipboard([_logContextText]);
    }

    private static void CopyLinesToClipboard(IReadOnlyList<string> lines)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(string.Join(Environment.NewLine, lines));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    /// <summary>当前焦点是否落在**输入框**里（命令框 / 日志搜索框）。加速键要让输入框自己的 Ctrl+C / Ctrl+A 优先。</summary>
    private bool IsFocusInTextBox(out TextBox? box)
    {
        box = FocusManager.GetFocusedElement(XamlRoot) as TextBox;
        return box is not null;
    }

    /// <summary>
    /// Ctrl+C：挂在 Page 上的**窗口级**加速键，不要求焦点在日志区。
    /// 焦点还留在日志搜索框/命令框上是常态（点完搜索框再看日志），只靠 LogScrollHost 的 KeyDown
    /// 收不到按键。规则：
    ///   · 焦点在输入框里**且框里有选中**= 用户要复制自己输入的文字 → 不插手，交给输入框原生处理；
    ///   · 否则日志区有选区 → 复制它。
    /// </summary>
    private void LogCopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusInTextBox(out TextBox? tb) && tb!.SelectionLength > 0)
            return;

        string? text = GetSelectedLogText();
        if (string.IsNullOrEmpty(text))
            return;

        CopyLinesToClipboard([text]);
        args.Handled = true;
    }

    /// <summary>Ctrl+A：日志区全选（焦点在输入框里就放手，让输入框全选自己的文字）。</summary>
    private void LogSelectAllAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusInTextBox(out _))
            return;

        if (LogSelectAll())
            args.Handled = true;
    }

    /// <summary>
    /// 日志区的键盘。Ctrl+C / Ctrl+A 走 Page 上的加速键（<see cref="LogCopyAccelerator_Invoked"/> /
    /// <see cref="LogSelectAllAccelerator_Invoked"/>，不要求焦点在这里）；这里只兜底一次
    /// （加速键没接住时还能用），以及把方向键/翻页键记成"用户自己在翻日志"。
    /// </summary>
    private void LogScrollHost_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsCtrlDown())
        {
            // 方向键/翻页键 = 用户自己在翻日志（ScrollViewer 自己会滚，这里只记一笔）
            switch (e.Key)
            {
                case VirtualKey.Up:
                case VirtualKey.Down:
                case VirtualKey.Left:
                case VirtualKey.Right:
                case VirtualKey.PageUp:
                case VirtualKey.PageDown:
                case VirtualKey.Home:
                case VirtualKey.End:
                case VirtualKey.Space:
                    MarkLogUserScroll();
                    break;
            }

            return;
        }

        // Ctrl 组合键已被 Page 上的加速键处理过了（复制/全选都做完了），别再重复一遍。
        // 注意这个判据只能放在这里：方向键是被 ScrollViewer 先处理掉的（handledEventsToo
        // 才收得到），放前面会把"按键翻日志"的跟随暂停一起吞掉。
        if (e.Handled)
            return;

        if (e.Key == VirtualKey.C)
        {
            string? text = GetSelectedLogText();
            if (!string.IsNullOrEmpty(text))
            {
                CopyLinesToClipboard([text]);
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.A)
        {
            if (LogSelectAll())
                e.Handled = true;
        }
    }

    /// <summary>
    /// 指针（相对 ItemsRepeater）落在哪一行：遍历已实体行、按行高做命中测试。
    /// 行没实体化（在视口外）就返回 false——拖动时指针一般都在可见区域内。
    /// </summary>
    private bool TryHitLogRow(Point p, out int index, out Grid? root, out TextBox? box)
    {
        index = -1;
        root = null;
        box = null;

        int n = VisualTreeHelper.GetChildrenCount(LogRepeater);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(LogRepeater, i) is not Grid grid || grid.Children.Count < 2)
                continue;

            Point origin = grid.TransformToVisual(LogRepeater).TransformPoint(new Point(0, 0));
            if (p.Y >= origin.Y && p.Y <= origin.Y + grid.ActualHeight)
            {
                root = grid;
                box = grid.Children[0] as TextBox;
                index = LogRepeater.GetElementIndex(grid);
                return index >= 0 && box is not null;
            }
        }

        return false;
    }

    /// <summary>
    /// 行内某个坐标对应的字符下标（选择、Ctrl+点击链接都用它）。WinUI 的 TextBox 没有
    /// GetCharacterIndexFromPoint，改用 GetRectFromCharacterIndex 两段扫描：
    /// 先按 Y 二分出点击的视觉行，再把范围收窄到该视觉行、按 X 扫出点击列。
    /// </summary>
    private static int CharIndexInBox(TextBox box, Point p)
    {
        string text = box.Text ?? string.Empty;
        if (text.Length == 0)
            return 0;

        // 阶段一：Y 二分 → best = 最后一个 Top ≤ p.Y 的下标
        int lo = 0;
        int hi = text.Length - 1;
        int best = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            Rect rect = box.GetRectFromCharacterIndex(mid, trailingEdge: false);
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
            return 0;

        double rowTop = box.GetRectFromCharacterIndex(best, trailingEdge: false).Top;
        if (double.IsNaN(rowTop))
            return best;

        // 收窄到 best 所在的视觉行：同一 Top 的连续下标段（折行时一个逻辑行有多个视觉行）
        int rowStart = best;
        while (rowStart > 0)
        {
            Rect r = box.GetRectFromCharacterIndex(rowStart - 1, trailingEdge: false);
            if (double.IsNaN(r.Top) || Math.Abs(r.Top - rowTop) > 0.5)
                break;
            rowStart--;
        }

        int rowEnd = best + 1;
        while (rowEnd < text.Length)
        {
            Rect r = box.GetRectFromCharacterIndex(rowEnd, trailingEdge: false);
            if (double.IsNaN(r.Top) || Math.Abs(r.Top - rowTop) > 0.5)
                break;
            rowEnd++;
        }

        // 阶段二：行内按 X 扫出点击列
        int colIdx = rowStart;
        for (int i = rowStart; i < rowEnd; i++)
        {
            Rect r = box.GetRectFromCharacterIndex(i, trailingEdge: false);
            if (double.IsNaN(r.Left))
                break;
            if (r.Left > p.X)
                break;
            colIdx = i;
        }

        return colIdx;
    }

    // ===================== 搜索（筛行 + 计数 + Enter 跳转） =====================

    /// <summary>当前命中的序号（可见列表里的下标，-1 = 没有）。</summary>
    private int _logSearchIndex = -1;

    /// <summary>筛选集合重建已入队（日志成批来的时候不能每来一条都重建）。</summary>
    private bool _logFilterQueued;

    /// <summary>_hookedLogs 里已经扫过、判过命中的条数（增量补筛选结果用）。</summary>
    private int _filterScanned;

    /// <summary>筛选结果需要整体重建（换账号、裁行把头部删掉了）。</summary>
    private bool _filterNeedsRebuild;

    private void MarkFilterDirty(bool fullRebuild = false)
    {
        if (fullRebuild)
            _filterNeedsRebuild = true;

        if (_logFilterQueued)
            return;

        _logFilterQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                _logFilterQueued = false;
                if (!_logFilterMode)
                    return;

                if (_filterNeedsRebuild)
                    RebuildFilteredLogs();
                else
                    AppendMatchingLogs();

                int old = _logSearchIndex;
                if (_logSearchIndex >= _filteredLogs.Count)
                    _logSearchIndex = _filteredLogs.Count - 1;
                if (_logSearchIndex != old)
                    UpdateSearchCount();
            }))
        {
            _logFilterQueued = false;
        }
    }

    /// <summary>按当前搜索词把命中的行整份收进 <see cref="_filteredLogs"/>（大小写不敏感的子串命中）。</summary>
    private void RebuildFilteredLogs()
    {
        _filterNeedsRebuild = false;
        _filterScanned = 0;
        _filteredLogs.Clear();

        string query = LogSearchBox?.Text ?? string.Empty;
        if (query.Length > 0)
            AppendMatchingLogs();
    }

    /// <summary>增量：只扫 _hookedLogs 里还没看过的新条目，把命中的补进筛选结果（日志流式到来时避免整表重建）。</summary>
    private void AppendMatchingLogs()
    {
        string query = LogSearchBox?.Text ?? string.Empty;
        if (query.Length == 0 || _hookedLogs is not { } logs)
            return;

        while (_filterScanned < logs.Count)
        {
            LogEntry entry = logs[_filterScanned];
            _filterScanned++;
            if (entry.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                _filteredLogs.Add(entry);
        }
    }

    /// <summary>
    /// 搜索框改词：平面/筛选两种模式切换。
    ///   有词 = ItemsSource 换成"只含命中的行"、暂停跟随、选中第一条命中并滚到它；
    ///   清空 = 换回全量、恢复跟随并贴底（用户反馈：筛选结束后视图总停在顶部）。
    /// </summary>
    private void LogSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 框里一有字就把放大镜收起来（别和输入的文字挤在一起），清空后再出现；
        // 有字时左边也不再给图标留位（Padding 左 26 → 4），输入的文字直接顶到框左边。
        bool empty = string.IsNullOrEmpty(LogSearchBox.Text);
        LogSearchGlyph.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LogSearchBox.Padding = empty ? new Thickness(26, 4, 4, 0) : new Thickness(4, 4, 4, 0);

        _logSearchIndex = -1;
        ClearLogSelection();

        if (empty)
        {
            _logFilterMode = false;
            ApplyLogItemsSource();
            UpdateSearchCount();

            _logFollowPaused = false;
            ForceScrollLogToEnd();
        }
        else
        {
            _logFilterMode = true;
            ApplyLogItemsSource();

            // 换源后视口位置对新的（更短的）列表没意义：先回顶部，第一条命中就在最上面。
            _logScrollViewer ??= ResolveLogScrollHost();
            if (_logScrollViewer is { } scroll)
                ScrollLogTo(scroll, 0);

            RunLogSearch(selectFirst: true);
            _logFollowPaused = true;
        }
    }

    private void LogSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            StepLogSearch(shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (!string.IsNullOrEmpty(LogSearchBox.Text))
                LogSearchBox.Text = string.Empty; // 触发 TextChanged → 清匹配与计数

            e.Handled = true;
        }
    }

    /// <summary>重新扫描（筛选模式下"命中"就是筛选集合里的每一行）。</summary>
    private void RunLogSearch(bool selectFirst)
    {
        if (!_logFilterMode || _filteredLogs.Count == 0)
        {
            _logSearchIndex = -1;
            UpdateSearchCount();
            return;
        }

        if (selectFirst)
        {
            _logSearchIndex = 0;
            SelectSearchHit(0, scroll: true);
            return;
        }

        UpdateSearchCount();
    }

    /// <summary>上一条/下一条（到头绕回）。</summary>
    private void StepLogSearch(int delta)
    {
        if (_filteredLogs.Count == 0)
            return;

        int next;
        if (_logSearchIndex < 0)
            next = delta > 0 ? 0 : _filteredLogs.Count - 1;
        else
            next = ((_logSearchIndex + delta) % _filteredLogs.Count + _filteredLogs.Count) % _filteredLogs.Count;

        SelectSearchHit(next, scroll: true);
    }

    /// <summary>选中第 index 条命中：置选区、滚过去、把原生选区摆好（给 UIA 读）。</summary>
    private void SelectSearchHit(int index, bool scroll)
    {
        if (!_logFilterMode || index < 0 || index >= _filteredLogs.Count)
            return;

        string query = LogSearchBox?.Text ?? string.Empty;
        if (query.Length == 0)
            return;

        _logSearchIndex = index;
        UpdateSearchCount();

        string line = _filteredLogs[index].Text;
        int col = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (col < 0)
            col = 0;

        _logSelAnchorLine = index;
        _logSelAnchorCol = col;
        _logSelFocusLine = index;
        _logSelFocusCol = Math.Min(line.Length, col + query.Length);

        RedrawAllLogRows();

        if (scroll)
            ScrollLogRowIntoView(index);
    }

    /// <summary>把某一行滚进视口；行还没实体化时先按比例粗滚，下一帧再补（最多 5 轮，避免死循环）。</summary>
    private void ScrollLogRowIntoView(int index) => ScrollLogRowIntoView(index, attempt: 0);

    private void ScrollLogRowIntoView(int index, int attempt)
    {
        _logScrollViewer ??= ResolveLogScrollHost();
        if (_logScrollViewer is not { } scroll)
            return;

        if (LogRepeater.TryGetElement(index) is FrameworkElement element)
        {
            Point origin = element.TransformToVisual(LogRepeater).TransformPoint(new Point(0, 0));
            double top = origin.Y;
            double bottom = top + element.ActualHeight;
            double viewTop = scroll.VerticalOffset;
            double viewHeight = scroll.ViewportHeight;

            if (top < viewTop)
                ScrollLogTo(scroll, top);
            else if (bottom > viewTop + viewHeight)
                ScrollLogTo(scroll, bottom - viewHeight);

            return;
        }

        if (attempt >= 5)
            return;

        // 没实体化：按"行号占比"粗滚一次，然后下一帧再试。
        int count = GetLogItemCount();
        if (count > 1 && scroll.ScrollableHeight > 0)
            ScrollLogTo(scroll, (double)index / (count - 1) * scroll.ScrollableHeight);

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ScrollLogRowIntoView(index, attempt + 1));
    }

    /// <summary>匹配数文案：空查询 = 空白（右侧徽标整块收起）；有查询 = "命中 3/12" 或 "无匹配"。</summary>
    private void UpdateSearchCount()
    {
        string query = LogSearchBox?.Text ?? string.Empty;
        LogSearchCount.Text = query.Length == 0
            ? string.Empty
            : _filteredLogs.Count == 0
                ? "无匹配"
                : $"命中 {_logSearchIndex + 1}/{_filteredLogs.Count}";

        LogSearchCountBadge.Visibility = LogSearchCount.Text.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
