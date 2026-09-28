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
        nameof(MouseEnabled),
        nameof(MouseModeIndex),
        nameof(MouseSideIndex),
        nameof(MouseHoldMs),
        nameof(MouseIntervalMs),
        nameof(MouseJitterPercent),
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
        DeleteAccountCommand = new RelayCommand(DeleteSelectedAccount, () => SelectedAccount is not null);

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

    public RelayCommand DeleteAccountCommand { get; }

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

    public bool MouseEnabled
    {
        get => Pick(a => a.MouseEnabled, false);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseEnabled = value;
        }
    }

    public int MouseModeIndex
    {
        get => Pick(a => a.MouseModeIndex, (int)MouseMode.IntervalClick);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseModeIndex = value;
        }
    }

    public int MouseSideIndex
    {
        get => Pick(a => a.MouseSideIndex, (int)MouseSide.Right);
        set
        {
            if (SelectedAccount is { } account)
                account.MouseSideIndex = value;
        }
    }

    public string MouseHoldMs
    {
        get => Pick(a => a.MouseHoldMs, "1000");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseHoldMs = value;
        }
    }

    public string MouseIntervalMs
    {
        get => Pick(a => a.MouseIntervalMs, "600");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseIntervalMs = value;
        }
    }

    public string MouseJitterPercent
    {
        get => Pick(a => a.MouseJitterPercent, "20");
        set
        {
            if (SelectedAccount is { } account)
                account.MouseJitterPercent = value;
        }
    }

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
        return account;
    }

    private void ReloadAccounts()
    {
        IReadOnlyList<AccountProfile> list = _accountStore.Load();

        foreach (AccountViewModel old in Accounts)
        {
            old.Connected -= OnAccountConnected;
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

    private void DeleteSelectedAccount()
    {
        AccountViewModel? target = SelectedAccount;
        if (target is null)
            return;

        int index = Accounts.IndexOf(target);

        Accounts.Remove(target);
        target.Connected -= OnAccountConnected;
        target.Dispose();

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

        // 删除的是最后一个账号 → 右侧面板回到空白页
        SelectedAccount = Accounts.Count == 0
            ? null
            : Accounts[Math.Clamp(index, 0, Accounts.Count - 1)];

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

    #endregion

    public void Dispose()
    {
        foreach (AccountViewModel account in Accounts)
        {
            account.Connected -= OnAccountConnected;
            account.Dispose();
        }

        Accounts.Clear();
        _accountStore.Dispose();
    }
}
