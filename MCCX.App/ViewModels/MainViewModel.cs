using System.Collections.ObjectModel;
using System.ComponentModel;
using MCCX.Core;
using MCCX.Core.Networking;
using Microsoft.UI.Dispatching;

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
    /// <summary>右侧面板镜像的属性名：选中账号的同名属性变化时转发给绑定。</summary>
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
        nameof(ReconnectEnabled),
        nameof(ReconnectAttempts),
        nameof(ReconnectDelayMs),
        nameof(Logs),
    ];

    private static readonly HashSet<string> ProxyNameSet = new(ProxyNames, StringComparer.Ordinal);

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AccountStore _accountStore = new();
    private readonly ObservableCollection<LogEntry> _noAccountLogs = [];

    /// <summary>没有选中账号时过滤列表的占位（空；这时右侧面板本来就是隐藏的）。</summary>
    private static readonly ObservableCollection<MobFilterItem> NoFilterItems = [];

    private AccountViewModel? _selectedAccount;

    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));

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
        first?.WriteNote($"§8账号文件：{AccountStore.DefaultDirectory}");
        if (_accountStore.MigratedLegacyData)
            first?.WriteNote("§8已把旧的 %APPDATA%\\MCCX 账号文件迁移到程序目录。");
        if (!string.IsNullOrEmpty(_accountStore.LastError))
            first?.WriteNote($"§e账号列表解密失败，已按空列表启动：{_accountStore.LastError}");
    }

    /// <summary>左侧列表：每个账号一个独立会话（子进程）。</summary>
    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

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
        get => Pick(a => a.ReconnectAttempts, "5");
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

    /// <summary>创建一个账号会话（子进程）并挂上“连接成功即写回账号库”。</summary>
    private AccountViewModel CreateAccount(AccountProfile profile)
    {
        AccountViewModel account = new(profile, _dispatcherQueue);
        account.Connected += OnAccountConnected;
        account.AttackFilterChanged += OnAccountFilterChanged;
        return account;
    }

    private void DetachAccount(AccountViewModel account)
    {
        account.Connected -= OnAccountConnected;
        account.AttackFilterChanged -= OnAccountFilterChanged;
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

    /// <summary>连接成功后把当前参数静默写回账号库（登录过的账号自动沉淀最新地址）。</summary>
    private void OnAccountConnected(AccountViewModel account)
    {
        AccountProfile? candidate = account.TryBuildProfile(silent: true);
        if (candidate is null)
            return;

        try
        {
            _accountStore.Upsert(candidate);
        }
        catch (Exception ex)
        {
            account.WriteNote($"§c自动保存账号失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 攻击生物过滤改了 → 写回加密账号库，重启后还在。
    /// 用专门的 UpdateAttackFilter 而不是 Upsert：改个勾选不该把账号顶到“最近使用”打乱左侧顺序。
    /// </summary>
    private void OnAccountFilterChanged(AccountViewModel account)
    {
        try
        {
            bool saved = _accountStore.UpdateAttackFilter(
                account.Id, account.AttackFilterModeIndex, account.SelectedAttackMobs);

            if (!saved)
                account.WriteNote("§e攻击过滤未保存：账号库里没有这个账号，重启后会恢复默认。");
            else if (!string.IsNullOrEmpty(_accountStore.LastError))
                account.WriteNote($"§c攻击过滤保存失败：{_accountStore.LastError}");
        }
        catch (Exception ex)
        {
            account.WriteNote($"§c攻击过滤保存失败：{ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        foreach (AccountViewModel account in Accounts)
        {
            DetachAccount(account);
            account.Dispose();
        }

        Accounts.Clear();
        _accountStore.Dispose();
    }
}
