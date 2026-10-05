using System.Collections.ObjectModel;
using System.ComponentModel;
using MCCX.Core;
using MCCX.Core.Dialogs;
using MCCX.Core.Networking;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;

namespace MCCX_App.ViewModels;

/// <summary>
/// 主界面 ViewModel（外壳）：左侧账号列表 + 选中账号的右侧面板镜像 + 添加/删除账号。
///
/// 多开模型：每个账号是独立的 <see cref="AccountViewModel"/>，各自拥有子进程会话、日志、
/// 连接状态与自动化开关；右侧“窗口”只是把 <see cref="SelectedAccount"/> 的数据镜像出来显示，
/// 切换左侧账号 = 切换右侧窗口，账号为空时右侧面板整体隐藏（空白页）。
///
/// MCC/子进程线程产生的事件统一经 DispatcherQueue 切回 UI 线程再改绑定数据。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// 右侧面板镜像的属性名：选中账号的同名属性变化时转发给绑定。
    /// </summary>
    private static readonly string[] ProxyNames =
    [
        nameof(ServerHost),
        nameof(ServerPort),
        nameof(Username),
        nameof(MinecraftVersion),
        nameof(CommandInput),
        nameof(StateText),
        nameof(IsConnected),
        nameof(AttackEnabled),
        nameof(AttackRange),
        nameof(AttackCooldownMin),
        nameof(AttackCooldownMax),
        nameof(AttackFilterModeIndex),
        nameof(AttackFilterHostile),
        nameof(AttackFilterNeutral),
        nameof(AttackFilterFriendly),
        nameof(AttackFilterHostileAll),
        nameof(AttackFilterNeutralAll),
        nameof(AttackFilterFriendlyAll),
        nameof(MouseEnabled),
        nameof(MouseLeftEnabled),
        nameof(MouseLeftModeIndex),
        nameof(MouseLeftHoldMs),
        nameof(MouseLeftIntervalMs),
        nameof(MouseLeftJitterPercent),
        nameof(MouseRightEnabled),
        nameof(MouseRightModeIndex),
        nameof(MouseRightHoldMs),
        nameof(MouseRightIntervalMs),
        nameof(MouseRightJitterPercent),
        nameof(MouseAimReach),
        nameof(MouseAimReachText),
        nameof(MouseLeftTimingVisible),
        nameof(MouseRightTimingVisible),
        nameof(MouseLeftHoldVisible),
        nameof(MouseRightHoldVisible),
        nameof(FishingEnabled),
        nameof(FishingSoundDetection),
        nameof(FishingVelocityDetection),
        nameof(FishingTimeout),
        nameof(FishingCastDelay),
        // AutoRefillEnabled 之前漏登记了（切换账号时"自动补充"开关不刷新），随本次补上
        nameof(AutoRefillEnabled),
        nameof(AutoWalkEnabled),
        nameof(ServerFilterModeIndex),
        nameof(ServerFilterShowPrefix),
        nameof(ServerFilterBlockPrefix),
        nameof(ReconnectEnabled),
        nameof(ReconnectAttempts),
        nameof(ReconnectDelayMs),
        nameof(Logs),
    ];

    private static readonly HashSet<string> ProxyNameSet = new(ProxyNames, StringComparer.Ordinal);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AccountStore _accountStore = new();
    private readonly ObservableCollection<LogEntry> _noAccountLogs = [];

    /// <summary>参数落盘节流窗口（毫秒）：窗口内只写一次，避免输入框每敲一个字符就加密落盘。</summary>
    private const long ParameterSaveIntervalMs = 800;

    /// <summary>每个账号"上次落盘的参数签名"：值没变就不再写一次。</summary>
    private readonly Dictionary<string, string> _lastSavedSignature = new(StringComparer.Ordinal);

    /// <summary>每个账号上次落盘时刻（Environment.TickCount64）。</summary>
    private readonly Dictionary<string, long> _lastSavedTicks = new(StringComparer.Ordinal);

    /// <summary>被时间节流推迟的账号 Id：连接成功 / 退出前补写，保证参数不丢。</summary>
    private readonly HashSet<string> _pendingParameters = new(StringComparer.Ordinal);

    /// <summary>没有选中账号时过滤列表的占位（空；这时右侧面板本来就是隐藏的）。</summary>
    private static readonly ObservableCollection<MobFilterItem> NoFilterItems = [];

    /// <summary>常用命令库（自动记录 + 手动增删，明文 JSON 落在程序目录）。</summary>
    private readonly FrequentCommandStore _frequentCommandStore = new();

    private AccountViewModel? _selectedAccount;

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));

        // 账号列表的分组视图：先挂上空源（账号在构造末尾 ReloadAccounts 里进来），
        // 起始状态按上次记住的来（ui-settings.json，默认不分组 = 保持单列）。
        _accountsViewSource.Source = _accountListSource;
        AccountsView = _accountsViewSource.View;
        SetGroupAccountsByServer(UiSettingsStore.ReadGroupAccountsByServer());

        // 常用命令：读盘 → 挂集合通知 → 按使用频率排一次序
        foreach (FrequentCommand entry in _frequentCommandStore.Load())
            FrequentCommands.Add(entry);

        FrequentCommands.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFrequentCommands));
        ResortFrequentCommands();

        ConnectCommand = new AsyncRelayCommand(
            () =>
            {
                SelectedAccount?.ConnectCommand.Execute(null);
                return Task.CompletedTask;
            },
            () => SelectedAccount?.ConnectCommand.CanExecute(null) == true);
        DisconnectCommand = new RelayCommand(
            () => SelectedAccount?.DisconnectCommand.Execute(null),
            () => SelectedAccount?.DisconnectCommand.CanExecute(null) == true);
        SendCommand = new RelayCommand(
            () => SelectedAccount?.SendCommand.Execute(null),
            () => SelectedAccount?.SendCommand.CanExecute(null) == true);
        ClearLogCommand = new RelayCommand(
            () => SelectedAccount?.ClearLogCommand.Execute(null),
            () => SelectedAccount is not null);
        AddAccountCommand = new RelayCommand(() => RequestAddAccount?.Invoke());

        // 删除由账号项右侧的叉号触发（带二次确认），参数就是被点的那个账号，不一定是当前选中项
        DeleteAccountCommand = new RelayCommand<AccountViewModel>(DeleteAccount, account => account is not null);

        ReloadAccounts();

        AccountViewModel? first = SelectedAccount;
        first?.WriteNote("§8[MCCX] 就绪：左侧“添加账号”可新增多开账号，点击左侧账号切换右侧窗口。");
        first?.WriteNote($"§8账号文件：{_accountStore.StorageDirectory}");
        if (AccountStore.UsingFallbackDirectory)
            first?.WriteNote($"§e程序目录不可写，账号文件已改存到：{AccountStore.FallbackDirectory}");
        if (_accountStore.MigratedLegacyData)
            first?.WriteNote("§8已把旧的 %APPDATA%\\MCCX 账号文件迁移到程序目录。");
        if (!string.IsNullOrEmpty(_accountStore.LastError))
            first?.WriteNote($"§e账号列表解密失败，已按空列表启动：{_accountStore.LastError}");
    }

    /// <summary>左侧列表：每个账号一个独立会话（子进程）。</summary>
    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    /// <summary>
    /// 左侧账号列表<b>绑定的那个源</b>（2026-10-05 用户要求"按服务器分类"）：
    /// 不分组时里面直接就是账号；分组时是"表头 + 该服的账号"交替排列
    /// （表头是 <see cref="AccountGroupHeader"/>），由 <c>AccountTemplateSelector</c>
    /// 按类型渲染成两种外观。
    ///
    /// 为什么不直接给 ListView 绑 <see cref="Accounts"/>：WinUI 3 移除了 UWP 的分组接口
    /// （<c>IGroupable</c>/<c>GroupStyle</c>），用一个中间源来切换就不会动账号集合本身，
    /// 也就不会打乱"最近使用"排序。
    /// </summary>
    private readonly ObservableCollection<object> _accountListSource = [];

    private readonly CollectionViewSource _accountsViewSource = new();

    /// <summary>左侧账号列表绑定的视图（元素：账号，或分组时的 <see cref="AccountGroupHeader"/>）。</summary>
    public ICollectionView AccountsView { get; }

    /// <summary>账号是否正在按服务器分组（对应左侧"按服务器分组"按钮）。</summary>
    public bool GroupAccountsByServer { get; private set; }

    /// <summary>
    /// 切换"按服务器分组"，返回切换后的状态（界面用它改按钮文案）。
    /// 打开时按服务器地址分数组、组内与组之间都按名称升序；关闭时恢复 <see cref="Accounts"/> 的原顺序。
    /// </summary>
    public bool SetGroupAccountsByServer(bool enabled)
    {
        GroupAccountsByServer = enabled;
        RebuildAccountListSource();
        return enabled;
    }

    /// <summary>
    /// 按当前分组状态重建列表源。改的是同一个 <see cref="ObservableCollection"/>，
    /// 视图会跟着收到 CollectionChanged 自动刷新，不需要手动 Refresh。
    /// </summary>
    private void RebuildAccountListSource()
    {
        _accountListSource.Clear();

        if (!GroupAccountsByServer)
        {
            foreach (AccountViewModel account in Accounts)
                _accountListSource.Add(account);

            return;
        }

        // 按 GroupKey（即服务器地址，端口不参与）归组，组名与组内都按名称升序，
        // 免得同服的账号顺序跟着"最近使用"乱跳。
        foreach (IGrouping<string, AccountViewModel> group in Accounts
                     .GroupBy(a => a.GroupKey, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            AccountViewModel[] members = group.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
            _accountListSource.Add(new AccountGroupHeader(group.Key, members.Length));
            foreach (AccountViewModel member in members)
                _accountListSource.Add(member);
        }
    }

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    /// <summary>弹出“添加账号”窗口（界面负责显示窗口，拿到结果后回调 <see cref="AddAccount"/>）。</summary>
    public RelayCommand AddAccountCommand { get; }

    /// <summary>删除指定账号（结束它的子进程 + 从加密账号库移除）。参数是被点叉号的那个账号。</summary>
    public RelayCommand<AccountViewModel> DeleteAccountCommand { get; }

    /// <summary>界面订阅：用户点了“添加账号”按钮。</summary>
    public event Action? RequestAddAccount;

    /// <summary>界面订阅：某个账号收到了服务器对话框（MCC 的 Dialog 系统），要弹输入框。</summary>
    public event Action<AccountViewModel, MccDialogInfo>? RequestServerDialog;

    /// <summary>界面订阅：服务器关闭了某账号编号为该值的对话框（输入框要跟着收）。</summary>
    public event Action<AccountViewModel, int>? CloseServerDialog;

    /// <summary>常用命令列表（左边命令栏“常用命令”按钮的下拉内容）。</summary>
    public ObservableCollection<FrequentCommand> FrequentCommands { get; } = [];

    /// <summary>有没有常用命令：空列表时下拉里显示占位文案。</summary>
    public bool HasFrequentCommands => FrequentCommands.Count > 0;

    /// <summary>上方向键：回翻一条历史；返回 false 表示没历史可翻（按键走默认行为）。</summary>
    public bool HistoryPrev() => SelectedAccount?.HistoryPrev() ?? false;

    /// <summary>下方向键：往下翻一条。</summary>
    public bool HistoryNext() => SelectedAccount?.HistoryNext() ?? false;

    /// <summary>是否有选中账号：为 false 时右侧面板整体隐藏（空白页）。</summary>
    public bool HasSelection => SelectedAccount is not null;

    /// <summary>左侧列表当前选中的账号，右侧“窗口”显示它的数据。</summary>
    public AccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (ReferenceEquals(_selectedAccount, value))
                return;

            if (_selectedAccount is not null)
            {
                _selectedAccount.PropertyChanged -= OnAccountPropertyChanged;
                UnwatchCommands(_selectedAccount);
            }

            _selectedAccount = value;

            if (_selectedAccount is not null)
            {
                _selectedAccount.PropertyChanged += OnAccountPropertyChanged;
                WatchCommands(_selectedAccount);
            }

            DeleteAccountCommand.RaiseCanExecuteChanged();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            RaiseProxies();
            RaiseCommands();
        }
    }

    #region 右侧面板镜像（绑定这里，切换账号时自动跟随）

    public string ServerHost
    {
        get => Pick(a => a.ServerHost, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.ServerHost = value;
        }
    }

    public string ServerPort
    {
        get => Pick(a => a.ServerPort, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.ServerPort = value;
        }
    }

    public string Username
    {
        get => Pick(a => a.Username, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.Username = value;
        }
    }

    public string MinecraftVersion
    {
        get => Pick(a => a.MinecraftVersion, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.MinecraftVersion = value;
        }
    }

    public string CommandInput
    {
        get => Pick(a => a.CommandInput, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.CommandInput = value;
        }
    }

    public string StateText => Pick(a => a.StateText, "未连接");

    public bool IsConnected => Pick(a => a.IsConnected, false);

    public ObservableCollection<LogEntry> Logs => Pick(a => a.Logs, _noAccountLogs);

    public bool AttackEnabled
    {
        get => Pick(a => a.AttackEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.AttackEnabled = value;
        }
    }

    public string AttackRange
    {
        get => Pick(a => a.AttackRange, "3.0");
        set
        {
            if (SelectedAccount is { } account)
                account.AttackRange = value;
        }
    }

    public string AttackCooldownMin
    {
        get => Pick(a => a.AttackCooldownMin, "800");
        set
        {
            if (SelectedAccount is { } account)
                account.AttackCooldownMin = value;
        }
    }

    public string AttackCooldownMax
    {
        get => Pick(a => a.AttackCooldownMax, "1600");
        set
        {
            if (SelectedAccount is { } account)
                account.AttackCooldownMax = value;
        }
    }

    /// <summary>攻击生物过滤模式：0 不过滤（仅敌对）/ 1 白名单 / 2 黑名单。</summary>
    public int AttackFilterModeIndex
    {
        get => Pick(a => a.AttackFilterModeIndex, 0);
        set
        {
            if (SelectedAccount is { } account)
                account.AttackFilterModeIndex = value;
        }
    }

    /// <summary>过滤列表：敌对生物（选中账号的列表，切账号时整体换掉）。</summary>
    public ObservableCollection<MobFilterItem> AttackFilterHostile =>
        Pick(a => a.AttackFilterHostile, NoFilterItems);

    /// <summary>过滤列表：中立生物。</summary>
    public ObservableCollection<MobFilterItem> AttackFilterNeutral =>
        Pick(a => a.AttackFilterNeutral, NoFilterItems);

    /// <summary>过滤列表：友好生物。</summary>
    public ObservableCollection<MobFilterItem> AttackFilterFriendly =>
        Pick(a => a.AttackFilterFriendly, NoFilterItems);

    /// <summary>“全选”框（敌对分类）：读值=该分类是否全勾，写入=整类全选/全不选。</summary>
    public bool AttackFilterHostileAll
    {
        get => Pick(a => a.AttackFilterHostileAll, false);
        set
        {
            if (SelectedAccount is { } account)
                account.AttackFilterHostileAll = value;
        }
    }

    /// <summary>“全选”框（中立分类）。</summary>
    public bool AttackFilterNeutralAll
    {
        get => Pick(a => a.AttackFilterNeutralAll, false);
        set
        {
            if (SelectedAccount is { } account)
                account.AttackFilterNeutralAll = value;
        }
    }

    /// <summary>“全选”框（友好分类）。</summary>
    public bool AttackFilterFriendlyAll
    {
        get => Pick(a => a.AttackFilterFriendlyAll, false);
        set
        {
            if (SelectedAccount is { } account)
                account.AttackFilterFriendlyAll = value;
        }
    }

    /// <summary>分组标题（带数量）。候选目录对所有账号都一样，所以不参与账号镜像。</summary>
    public string AttackFilterHostileHeader => $"敌对生物（{MobCatalog.Hostile.Count}）";

    public string AttackFilterNeutralHeader => $"中立生物（{MobCatalog.Neutral.Count}）";

    public string AttackFilterFriendlyHeader => $"友好生物（{MobCatalog.Friendly.Count}）";

    public bool MouseEnabled
    {
        get => Pick(a => a.MouseEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseEnabled = value;
        }
    }

    public bool MouseLeftEnabled
    {
        get => Pick(a => a.MouseLeftEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseLeftEnabled = value;
        }
    }

    public int MouseLeftModeIndex
    {
        get => Pick(a => a.MouseLeftModeIndex, (int)MouseMode.IntervalClick);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseLeftModeIndex = value;
        }
    }

    public string MouseLeftHoldMs
    {
        get => Pick(a => a.MouseLeftHoldMs, "1000");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseLeftHoldMs = value;
        }
    }

    public string MouseLeftIntervalMs
    {
        get => Pick(a => a.MouseLeftIntervalMs, "600");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseLeftIntervalMs = value;
        }
    }

    public string MouseLeftJitterPercent
    {
        get => Pick(a => a.MouseLeftJitterPercent, "20");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseLeftJitterPercent = value;
        }
    }

    public bool MouseRightEnabled
    {
        get => Pick(a => a.MouseRightEnabled, true);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseRightEnabled = value;
        }
    }

    public int MouseRightModeIndex
    {
        get => Pick(a => a.MouseRightModeIndex, (int)MouseMode.IntervalClick);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseRightModeIndex = value;
        }
    }

    public string MouseRightHoldMs
    {
        get => Pick(a => a.MouseRightHoldMs, "1000");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseRightHoldMs = value;
        }
    }

    public string MouseRightIntervalMs
    {
        get => Pick(a => a.MouseRightIntervalMs, "600");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseRightIntervalMs = value;
        }
    }

    public string MouseRightJitterPercent
    {
        get => Pick(a => a.MouseRightJitterPercent, "20");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseRightJitterPercent = value;
        }
    }

    /// <summary>准星探测距离（格，1-7，默认 5）。</summary>
    public double MouseAimReach
    {
        get => Pick(a => a.MouseAimReach, 5.0);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseAimReach = value;
        }
    }

    /// <summary>距离滑块旁的数值文案，如“5 格”。</summary>
    public string MouseAimReachText => Pick(a => a.MouseAimReachText, "5 格");

    /// <summary>左键“间隔 ms”行：长按模式下不生效即隐藏（间隔点击/间隔长按显示）。</summary>
    public bool MouseLeftTimingVisible => MouseLeftModeIndex != 0;

    /// <summary>右键“间隔 ms”行：长按模式下不生效即隐藏（间隔点击/间隔长按显示）。</summary>
    public bool MouseRightTimingVisible => MouseRightModeIndex != 0;

    /// <summary>左键“按住 ms”行：按住时长是间隔长按的参数，只有它显示（其余模式隐藏）。</summary>
    public bool MouseLeftHoldVisible => MouseLeftModeIndex == 2;

    /// <summary>右键“按住 ms”行：按住时长是间隔长按的参数，只有它显示（其余模式隐藏）。</summary>
    public bool MouseRightHoldVisible => MouseRightModeIndex == 2;

    public bool FishingEnabled
    {
        get => Pick(a => a.FishingEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.FishingEnabled = value;
        }
    }

    /// <summary>收杆检测·水花声音（默认开）。</summary>
    public bool FishingSoundDetection
    {
        get => Pick(a => a.FishingSoundDetection, true);
        set
        {
            if (SelectedAccount is { } account)
                account.FishingSoundDetection = value;
        }
    }

    /// <summary>收杆检测·浮漂实体速度包（默认开）。</summary>
    public bool FishingVelocityDetection
    {
        get => Pick(a => a.FishingVelocityDetection, true);
        set
        {
            if (SelectedAccount is { } account)
                account.FishingVelocityDetection = value;
        }
    }

    /// <summary>抛竿超时（秒，默认 300）。</summary>
    public string FishingTimeout
    {
        get => Pick(a => a.FishingTimeout, "300");
        set
        {
            if (SelectedAccount is { } account)
                account.FishingTimeout = value;
        }
    }

    /// <summary>重抛间隔（秒，默认 0.4）。</summary>
    public string FishingCastDelay
    {
        get => Pick(a => a.FishingCastDelay, "0.4");
        set
        {
            if (SelectedAccount is { } account)
                account.FishingCastDelay = value;
        }
    }

    /// <summary>自动补充开关（2026-10-03 第五批需求：手持用完自动从背包补同款）。</summary>
    public bool AutoRefillEnabled
    {
        get => Pick(a => a.AutoRefillEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.AutoRefillEnabled = value;
        }
    }

    /// <summary>自动行走开关（2026-10-04 需求：一直朝当前朝向前进）。</summary>
    public bool AutoWalkEnabled
    {
        get => Pick(a => a.AutoWalkEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.AutoWalkEnabled = value;
        }
    }

    /// <summary>服务器信息过滤模式（0 不过滤 / 1 全屏蔽 / 2 只屏蔽玩家消息 / 3 只显示指定前缀 / 4 只屏蔽指定前缀）。</summary>
    public int ServerFilterModeIndex
    {
        get => Pick(a => a.ServerFilterModeIndex, 0);
        set
        {
            if (SelectedAccount is { } account)
                account.ServerFilterModeIndex = value;
        }
    }

    /// <summary>服务器信息过滤·"只显示指定前缀"用的前缀（空 = 全放行）。与下面那份相互独立。</summary>
    public string ServerFilterShowPrefix
    {
        get => Pick(a => a.ServerFilterShowPrefix, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.ServerFilterShowPrefix = value;
        }
    }

    /// <summary>服务器信息过滤·"只屏蔽指定前缀"用的前缀（空 = 全显示）。</summary>
    public string ServerFilterBlockPrefix
    {
        get => Pick(a => a.ServerFilterBlockPrefix, string.Empty);
        set
        {
            if (SelectedAccount is { } account)
                account.ServerFilterBlockPrefix = value;
        }
    }

    public bool ReconnectEnabled
    {
        get => Pick(a => a.ReconnectEnabled, true);
        set
        {
            if (SelectedAccount is { } account)
                account.ReconnectEnabled = value;
        }
    }

    public string ReconnectAttempts
    {
        get => Pick(a => a.ReconnectAttempts, "0");
        set
        {
            if (SelectedAccount is { } account)
                account.ReconnectAttempts = value;
        }
    }

    public string ReconnectDelayMs
    {
        get => Pick(a => a.ReconnectDelayMs, "3000");
        set
        {
            if (SelectedAccount is { } account)
                account.ReconnectDelayMs = value;
        }
    }

    /// <summary>
    /// 视角移动（需求 4）：把「视角调整」下拉里的方向按钮（east/south/west/north/up/down）
    /// 转成 <see cref="MccLookDirection"/> 交给当前账号。
    /// </summary>
    public void LookDirection(string? tag)
    {
        if (SelectedAccount is not { } account)
            return;

        MccLookDirection? direction = tag switch
        {
            "east" => MccLookDirection.East,
            "south" => MccLookDirection.South,
            "west" => MccLookDirection.West,
            "north" => MccLookDirection.North,
            "up" => MccLookDirection.Up,
            "down" => MccLookDirection.Down,
            _ => null,
        };

        if (direction is { } look)
            account.LookAt(look);
    }

    private T Pick<T>(Func<AccountViewModel, T> getter, T fallback) =>
        SelectedAccount is { } account ? getter(account) : fallback;

    /// <summary>把面板镜像整体刷新一遍（切换账号时调用）。</summary>
    private void RaiseProxies()
    {
        foreach (string name in ProxyNames)
            OnPropertyChanged(name);
    }

    private void RaiseCommands()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SendCommand.RaiseCanExecuteChanged();
        ClearLogCommand.RaiseCanExecuteChanged();
        AddAccountCommand.RaiseCanExecuteChanged();
        DeleteAccountCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 把选中账号各命令的可用性变化转发到外壳命令上。
    /// 外壳“连接/断开/发送”按钮绑定的是 MainViewModel 的命令实例，只靠 StateText/IsConnected
    /// 刷新会漏掉“连接任务刚跑完”这一刻（那时状态文字已经回填、但命令还在收尾），按钮会一直灰着。
    /// </summary>
    private void WatchCommands(AccountViewModel account)
    {
        account.ConnectCommand.CanExecuteChanged += OnAccountCommandChanged;
        account.DisconnectCommand.CanExecuteChanged += OnAccountCommandChanged;
        account.SendCommand.CanExecuteChanged += OnAccountCommandChanged;
    }

    private void UnwatchCommands(AccountViewModel account)
    {
        account.ConnectCommand.CanExecuteChanged -= OnAccountCommandChanged;
        account.DisconnectCommand.CanExecuteChanged -= OnAccountCommandChanged;
        account.SendCommand.CanExecuteChanged -= OnAccountCommandChanged;
    }

    private void OnAccountCommandChanged(object? sender, EventArgs e) => RaiseCommands();

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || !ProxyNameSet.Contains(e.PropertyName))
            return;

        OnPropertyChanged(e.PropertyName);

        if (e.PropertyName == nameof(IsConnected) || e.PropertyName == nameof(StateText))
            RaiseCommands();
    }

    #endregion

    #region 账号增删与持久化（需求 3.1 / 5. 多账号导航）

    /// <summary>创建一个账号会话（子进程）并挂上“连接成功即写回账号库”“参数变化即写回”。</summary>
    private AccountViewModel CreateAccount(AccountProfile profile)
    {
        AccountViewModel account = new(profile, _dispatcherQueue);
        account.Connected += OnAccountConnected;
        account.ParametersChanged += OnAccountParametersChanged;
        account.CommandSent += OnAccountCommandSent;
        account.DialogRequested += OnAccountDialogRequested;
        account.DialogClosed += OnAccountDialogClosed;
        return account;
    }

    private void DetachAccount(AccountViewModel account)
    {
        account.Connected -= OnAccountConnected;
        account.ParametersChanged -= OnAccountParametersChanged;
        account.CommandSent -= OnAccountCommandSent;
        account.DialogRequested -= OnAccountDialogRequested;
        account.DialogClosed -= OnAccountDialogClosed;
    }

    private void ReloadAccounts()
    {
        IReadOnlyList<AccountProfile> list = _accountStore.Load();

        foreach (AccountViewModel old in Accounts)
        {
            DetachAccount(old);
            old.Dispose();
        }

        Accounts.Clear();

        foreach (AccountProfile profile in list)
            Accounts.Add(CreateAccount(profile));

        RebuildAccountListSource();
        SelectedAccount = Accounts.Count > 0 ? Accounts[0] : null;
    }

    /// <summary>
    /// 新增一个账号：写入加密账号库 → 出现在左侧列表 → 选中它 →（勾选了“立即连接”）直接上线。
    /// 参数非法时把原因写进当前选中账号的日志并返回 false。
    /// </summary>
    public bool AddAccount(string host, string port, string version, string username, bool autoConnect)
    {
        host = host.Trim();
        username = username.Trim();

        if (host.Length == 0 || username.Length == 0)
        {
            SelectedAccount?.WriteNote("§c服务器地址与用户名不能为空。");
            return false;
        }

        string portText = port.Trim();
        if (portText.Length == 0)
            portText = ServerAddress.DefaultPort.ToString();

        if (!ushort.TryParse(portText, out ushort portValue) || portValue == 0)
        {
            SelectedAccount?.WriteNote($"§c端口“{port}”无效，请输入 1-65535 之间的数字。");
            return false;
        }

        AccountProfile profile = new()
        {
            Username = username,
            ServerHost = host,
            Port = portValue,
            MinecraftVersion = string.IsNullOrWhiteSpace(version) ? "auto" : version.Trim(),
            DisplayName = username,
        };

        try
        {
            profile = _accountStore.Upsert(profile);
            if (!string.IsNullOrEmpty(_accountStore.LastError))
                SelectedAccount?.WriteNote($"§c写入账号文件失败：{_accountStore.LastError}");
        }
        catch (Exception ex)
        {
            SelectedAccount?.WriteNote($"§c保存账号失败：{ex.Message}");
            return false;
        }

        // 同一个（用户名+服务器+端口）可能已经在列表里，避免重复开进程
        AccountViewModel? existing = Accounts.FirstOrDefault(a => a.Id == profile.Id);
        if (existing is null)
        {
            existing = CreateAccount(profile);
            Accounts.Add(existing);
        }

        SelectedAccount = existing;
        RebuildAccountListSource();
        existing.WriteNote($"§8已添加账号 {existing.DisplayName}（{existing.ServerSummary}）。");

        if (autoConnect)
            existing.ConnectCommand.Execute(null);

        return true;
    }

    /// <summary>
    /// 删除指定账号：从左侧列表移除 → 关掉它的子进程 → 从加密账号库删掉 → 选中项顺延。
    /// 由账号项右侧的叉号（二次确认后）调用，删的不一定是当前选中的账号。
    /// </summary>
    private void DeleteAccount(AccountViewModel? target)
    {
        if (target is null || !Accounts.Contains(target))
            return;

        int index = Accounts.IndexOf(target);

        Accounts.Remove(target);
        DetachAccount(target);
        target.Dispose();
        RebuildAccountListSource();

        // 删除的是最后一个账号 → 右侧面板回到空白页；否则选中相邻项
        SelectedAccount = Accounts.Count == 0
            ? null
            : Accounts[Math.Clamp(index, 0, Accounts.Count - 1)];

        try
        {
            _accountStore.Remove(target.Id);
            if (!string.IsNullOrEmpty(_accountStore.LastError))
                SelectedAccount?.WriteNote($"§c删除账号失败：{_accountStore.LastError}");
        }
        catch (Exception ex)
        {
            SelectedAccount?.WriteNote($"§c删除账号失败：{ex.Message}");
        }

        SelectedAccount?.WriteNote($"§8已删除账号 {target.DisplayName}。");
    }

    /// <summary>连接成功后把当前参数静默写回账号库（登录过的账号自动沉淀最新地址与参数）。</summary>
    private void OnAccountConnected(AccountViewModel account)
    {
        AccountProfile? candidate = account.TryBuildProfile(silent: true);
        if (candidate is null)
            return;

        try
        {
            _accountStore.Upsert(candidate);
            _lastSavedSignature[account.Id] = DescribeParameters(candidate);
            _lastSavedTicks[account.Id] = Environment.TickCount64;
            _pendingParameters.Remove(account.Id);
        }
        catch (Exception ex)
        {
            account.WriteNote($"§c自动保存账号失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 任一功能参数变了 → 写回加密账号库，重启后跟随账号恢复
    /// （用户 2026-10-03 要求：参数必须跟随账号存取）。
    ///
    /// 为什么用 <c>Upsert</c> 而不是专门的 Update*：参数现在都在 <see cref="AccountProfile"/> 里，
    /// 整条记录一次写回最简单、也不会漏字段。副作用是 LastUsedAt 会被刷新（左侧列表按最近使用排序），
    /// 这是"用户正在调这个账号的参数"的合理体现。
    ///
    /// 落盘做了两层减负，避免在输入框里每敲一个字符就加密落盘一次：
    ///   1) 值级去重：参数与上次落盘完全一致就直接跳过；
    ///   2) 时间节流：距上次落盘不足 <see cref="ParameterSaveIntervalMs"/> 且不是"非写不可"时延后，
    ///      连接成功时会强制落盘一次，保证不会丢。
    /// </summary>
    private void OnAccountParametersChanged(AccountViewModel account)
    {
        try
        {
            AccountProfile? candidate = account.TryBuildProfile(silent: true);
            if (candidate is null)
                return;

            string signature = DescribeParameters(candidate);
            bool unchanged = _lastSavedSignature.TryGetValue(account.Id, out string? previous)
                             && string.Equals(previous, signature, StringComparison.Ordinal);

            bool due = !_lastSavedTicks.TryGetValue(account.Id, out long last)
                       || (Environment.TickCount64 - last) >= ParameterSaveIntervalMs;

            if (unchanged && due)
                return; // 值没变，不用再写一次

            if (!due)
                _pendingParameters.Add(account.Id);

            _accountStore.Upsert(candidate);
            _lastSavedSignature[account.Id] = signature;
            _lastSavedTicks[account.Id] = Environment.TickCount64;

            if (!string.IsNullOrEmpty(_accountStore.LastError))
                account.WriteNote($"§c参数保存失败：{_accountStore.LastError}");
        }
        catch (Exception ex)
        {
            account.WriteNote($"§c参数保存失败：{ex.Message}");
        }
    }

    /// <summary>某账号成功发出了一条命令 → 自动记进常用命令（含口令的命令不记）。</summary>
    private void OnAccountCommandSent(AccountViewModel account, string text) => RecordFrequentCommand(text);

    /// <summary>某账号收到服务器对话框 → 转发给界面弹输入框。</summary>
    private void OnAccountDialogRequested(AccountViewModel account, MccDialogInfo info)
        => RequestServerDialog?.Invoke(account, info);

    /// <summary>服务器关掉了某账号的对话框 → 转发给界面收掉输入框。</summary>
    private void OnAccountDialogClosed(AccountViewModel account, int revision)
        => CloseServerDialog?.Invoke(account, revision);

    #region 常用命令

    /// <summary>自动记录一条发出过的命令：只记 MCC 内部命令，含口令的一律不记。</summary>
    private void RecordFrequentCommand(string text)
    {
        if (FrequentCommand.IsAutoRecordable(text))
            TouchFrequentCommand(text);
    }

    /// <summary>手动添加一条常用命令（用户自己敲的，不过滤）。返回是否添加成功。</summary>
    public bool AddFrequentCommand(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        TouchFrequentCommand(text);
        return true;
    }

    /// <summary>从下拉里删掉一条常用命令。</summary>
    public void RemoveFrequentCommand(FrequentCommand? entry)
    {
        if (entry is null || !FrequentCommands.Remove(entry))
            return;

        SaveFrequentCommands();
    }

    /// <summary>命中一次常用命令：计数 +1、重排、落盘。任何异常都不能把界面带崩。</summary>
    private void TouchFrequentCommand(string text)
    {
        try
        {
            FrequentCommand? existing = FrequentCommands.FirstOrDefault(
                entry => string.Equals(entry.Text, text, StringComparison.Ordinal));

            if (existing is null)
            {
                existing = new FrequentCommand { Text = text, Count = 0 };
                FrequentCommands.Add(existing);
            }

            existing.Count++;
            existing.LastUsedAt = DateTimeOffset.Now;

            ResortFrequentCommands();
            SaveFrequentCommands();
        }
        catch (Exception ex)
        {
            SelectedAccount?.WriteNote($"§e记录常用命令失败：{ex.Message}");
        }
    }

    /// <summary>按"用得多的在前"重排；超过上限丢最不常用的那条。</summary>
    private void ResortFrequentCommands()
    {
        while (FrequentCommands.Count > FrequentCommandStore.MaxEntries)
        {
            FrequentCommand? victim = FrequentCommands
                .OrderBy(entry => entry.Count)
                .ThenBy(entry => entry.LastUsedAt)
                .FirstOrDefault();

            if (victim is null)
                break;

            FrequentCommands.Remove(victim);
        }

        List<FrequentCommand> ordered =
            [.. FrequentCommands.OrderByDescending(entry => entry.Count).ThenByDescending(entry => entry.LastUsedAt)];

        for (int i = 0; i < ordered.Count; i++)
        {
            int current = FrequentCommands.IndexOf(ordered[i]);
            if (current > i)
                FrequentCommands.Move(current, i);
        }
    }

    private void SaveFrequentCommands()
    {
        try
        {
            if (!_frequentCommandStore.Save(FrequentCommands) && _frequentCommandStore.LastError is { } error)
                SelectedAccount?.WriteNote($"§e常用命令保存失败：{error}");
        }
        catch (Exception ex)
        {
            SelectedAccount?.WriteNote($"§e常用命令保存失败：{ex.Message}");
        }
    }

    #endregion

    /// <summary>参数字典序签名：用来判断"参数到底变没变"，避免重复落盘。</summary>
    private static string DescribeParameters(AccountProfile p)
    {
        AttackOptions a = p.Attack ?? new AttackOptions();
        MouseOptions m = p.Mouse ?? new MouseOptions();
        ReconnectOptions r = p.Reconnect ?? new ReconnectOptions();
        FishingOptions f = p.Fishing ?? new FishingOptions();

        return string.Join('|',
            a.Range, a.CooldownMinMs, a.CooldownMaxMs, a.FilterMode, string.Join(',', a.Mobs),
            m.AimReach,
            m.Left.Enabled, m.Left.Mode, m.Left.HoldMs, m.Left.IntervalMs, m.Left.JitterPercent,
            m.Right.Enabled, m.Right.Mode, m.Right.HoldMs, m.Right.IntervalMs, m.Right.JitterPercent,
            p.FishingEnabled, f.SoundDetection, f.VelocityDetection, f.TimeoutSeconds, f.CastDelaySeconds,
            p.AutoRefillEnabled, p.AutoWalkEnabled, p.ServerFilterMode,
            p.ServerFilterShowPrefix, p.ServerFilterBlockPrefix,
            r.Enabled, r.MaxAttempts, r.DelayMs);
    }

    /// <summary>把因节流被推迟的账号参数补写一次（连接成功、程序退出前调用，保证不丢）。</summary>
    private void FlushPendingParameters()
    {
        if (_pendingParameters.Count == 0)
            return;

        string[] ids = [.. _pendingParameters];
        _pendingParameters.Clear();

        foreach (string id in ids)
        {
            AccountViewModel? account = Accounts.FirstOrDefault(a => a.Id == id);
            if (account is null)
                continue;

            OnAccountParametersChanged(account);
        }
    }

    #endregion

    public void Dispose()
    {
        // 退出前把节流推迟的参数补写一次，否则最后几秒改的参数会丢
        FlushPendingParameters();

        foreach (AccountViewModel account in Accounts)
        {
            DetachAccount(account);
            account.Dispose();
        }

        Accounts.Clear();
        _accountStore.Dispose();
    }
}
