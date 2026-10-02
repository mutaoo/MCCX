using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MCCX.Core;
using MCCX.Core.Ipc;
using MCCX.Core.Networking;
using MinecraftClient.Scripting;
using Microsoft.UI.Dispatching;

namespace MCCX_App.ViewModels;

/// <summary>
/// 左侧列表里的一个账号：一个账号 = 一个独立会话（多开时对应一个独立子进程）。
/// 连接参数、日志、连接状态、自动化开关全部按账号隔离；右侧“窗口”显示的就是选中账号的这份数据。
///
/// 线程模型：会话事件可能从 MCC 线程或子进程管道线程抛出，
/// 统一经 DispatcherQueue 切回 UI 线程再改绑定数据。
/// </summary>
public sealed class AccountViewModel : ObservableObject, IDisposable
{
    /// <summary>日志最大保留行数，超出后按批裁剪，避免长时间挂机内存无限增长。</summary>
    private const int MaxLogLines = 1000;

    private const int LogTrimBatch = 100;

    private readonly IAccountSession _session;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AccountProfile _profile;

    private string _serverHost;
    private string _serverPort;
    private string _username;
    private string _minecraftVersion;
    private string _commandInput = string.Empty;
    private string _stateText = "未连接";
    private bool _isConnected;
    private bool _disposed;

    public AccountViewModel(
        AccountProfile profile,
        DispatcherQueue dispatcherQueue,
        IAccountSession? session = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _session = session ?? new RunnerProcess();

        _serverHost = profile.ServerHost;
        _serverPort = profile.Port.ToString(CultureInfo.InvariantCulture);
        _username = profile.Username;
        _minecraftVersion = string.IsNullOrWhiteSpace(profile.MinecraftVersion) ? "auto" : profile.MinecraftVersion;
        _attackFilterModeIndex = Math.Clamp(profile.AttackFilterMode, 0, 2);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        SendCommand = new RelayCommand(SendInput, () => IsConnected);
        ClearLogCommand = new RelayCommand(ClearLog);

        BuildAttackFilterItems(profile);

        _session.LogReceived += OnLogReceived;
        _session.StateChanged += OnStateChanged;

        if (_session is RunnerProcess runner)
            runner.ProcessExited += OnProcessExited;

        // 默认的自动化参数先登记好，子进程拉起时会补发（见 RunnerProcess 的配置记账）
        ApplyAttack();
        ApplyMouse();
        ApplyReconnect();
    }

    /// <summary>
    /// 按候选生物目录生成本账号的过滤勾选列表（敌对 / 中立 / 友好各一份），
    /// 勾选状态从账号库里读出来的名单回填。事件在回填之后才挂，避免启动就写盘。
    /// </summary>
    private void BuildAttackFilterItems(AccountProfile profile)
    {
        HashSet<string> checkedKeys = profile.AttackFilterMobs is { Count: > 0 } keys
            ? new(keys, StringComparer.Ordinal)
            : [];

        foreach (MobCandidate candidate in MobCatalog.All)
        {
            MobFilterItem item = new(candidate, checkedKeys.Contains(candidate.Key));
            item.CheckedChanged += OnAttackFilterChanged;
            _attackFilterItems.Add(item);
            AttackFilterItems(candidate.Category).Add(item);
        }
    }

    /// <summary>某个分类的勾选列表。</summary>
    private ObservableCollection<MobFilterItem> AttackFilterItems(MobCategory category) => category switch
    {
        MobCategory.Hostile => AttackFilterHostile,
        MobCategory.Neutral => AttackFilterNeutral,
        _ => AttackFilterFriendly,
    };

    /// <summary>连接成功（子会话进入 Connected）：主 ViewModel 用它把最新参数写回加密账号库。</summary>
    public event Action<AccountViewModel>? Connected;

    public string Id => _profile.Id;

    /// <summary>列表标题：账号库里存的展示名，空则退回游戏名。</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_profile.DisplayName) ? _profile.Username : _profile.DisplayName;

    /// <summary>列表副标题，跟随面板里改过的服务器/端口。</summary>
    public string ServerSummary => $"{ServerHost}:{ServerPort}";

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    /// <summary>会话对象（界面不直接用，测试/诊断时可拿到）。</summary>
    public IAccountSession Session => _session;

    #region 连接参数

    public string ServerHost
    {
        get => _serverHost;
        set
        {
            if (SetProperty(ref _serverHost, value))
                OnPropertyChanged(nameof(ServerSummary));
        }
    }

    public string ServerPort
    {
        get => _serverPort;
        set
        {
            if (SetProperty(ref _serverPort, value))
                OnPropertyChanged(nameof(ServerSummary));
        }
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>Minecraft 版本，auto 表示自动 Ping 探测。</summary>
    public string MinecraftVersion
    {
        get => _minecraftVersion;
        set => SetProperty(ref _minecraftVersion, value);
    }

    public string CommandInput
    {
        get => _commandInput;
        set => SetProperty(ref _commandInput, value);
    }

    public string StateText
    {
        get => _stateText;
        private set
        {
            if (SetProperty(ref _stateText, value))
                OnPropertyChanged(nameof(StateBadge));
        }
    }

    /// <summary>
    /// 列表项右侧的紧凑状态：只留状态词（已连接 / 连接中… / 未连接）。
    /// 完整的“已连接 host:port”在右侧面板状态条里显示，窄列塞不下地址。
    /// </summary>
    public string StateBadge => _stateText.StartsWith("已连接", StringComparison.Ordinal)
        ? "已连接"
        : _stateText;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    #endregion

    #region 自动化（需求 3.2）

    private bool _attackEnabled;
    private string _attackRange = "3.0";
    private string _attackCooldownMin = "800";
    private string _attackCooldownMax = "1600";

    private int _attackFilterModeIndex;
    private readonly List<MobFilterItem> _attackFilterItems = [];

    private bool _mouseEnabled;
    private bool _mouseLeftEnabled;
    private int _mouseLeftModeIndex = (int)MouseMode.IntervalClick;
    private string _mouseLeftHoldMs = "1000";
    private string _mouseLeftIntervalMs = "600";
    private string _mouseLeftJitterPercent = "20";
    private bool _mouseRightEnabled = true;
    private int _mouseRightModeIndex = (int)MouseMode.IntervalClick;
    private string _mouseRightHoldMs = "1000";
    private string _mouseRightIntervalMs = "600";
    private string _mouseRightJitterPercent = "20";
    private double _mouseAimReach = 5.0;

    private bool _fishingEnabled;

    private bool _reconnectEnabled = true;
    private string _reconnectAttempts = "5";
    private string _reconnectDelayMs = "3000";

    /// <summary>自动砍怪开关。</summary>
    public bool AttackEnabled
    {
        get => _attackEnabled;
        set
        {
            if (SetProperty(ref _attackEnabled, value))
                ApplyAttack();
        }
    }

    /// <summary>攻击距离（格，1-4）。</summary>
    public string AttackRange
    {
        get => _attackRange;
        set
        {
            if (SetProperty(ref _attackRange, value))
                ApplyAttack();
        }
    }

    /// <summary>攻击冷却下限（毫秒）。</summary>
    public string AttackCooldownMin
    {
        get => _attackCooldownMin;
        set
        {
            if (SetProperty(ref _attackCooldownMin, value))
                ApplyAttack();
        }
    }

    /// <summary>攻击冷却上限（毫秒）。</summary>
    public string AttackCooldownMax
    {
        get => _attackCooldownMax;
        set
        {
            if (SetProperty(ref _attackCooldownMax, value))
                ApplyAttack();
        }
    }

    /// <summary>攻击生物过滤：敌对生物（“不过滤”模式打的就是这一桶）。</summary>
    public ObservableCollection<MobFilterItem> AttackFilterHostile { get; } = [];

    /// <summary>攻击生物过滤：中立生物。</summary>
    public ObservableCollection<MobFilterItem> AttackFilterNeutral { get; } = [];

    /// <summary>攻击生物过滤：友好生物。</summary>
    public ObservableCollection<MobFilterItem> AttackFilterFriendly { get; } = [];

    /// <summary>“全选”框：敌对分类是不是已经全勾（勾上=整类全选，取消=整类全不选）。</summary>
    public bool AttackFilterHostileAll
    {
        get => IsCategoryAllChecked(MobCategory.Hostile);
        set => SetCategoryAllChecked(MobCategory.Hostile, value);
    }

    /// <summary>“全选”框：中立分类。</summary>
    public bool AttackFilterNeutralAll
    {
        get => IsCategoryAllChecked(MobCategory.Neutral);
        set => SetCategoryAllChecked(MobCategory.Neutral, value);
    }

    /// <summary>“全选”框：友好分类。</summary>
    public bool AttackFilterFriendlyAll
    {
        get => IsCategoryAllChecked(MobCategory.Friendly);
        set => SetCategoryAllChecked(MobCategory.Friendly, value);
    }

    /// <summary>整类勾选进行中：挡住单项回调，最后统一下发一次。</summary>
    private bool _bulkFilterUpdate;

    /// <summary>攻击生物过滤模式：0 不过滤（仅敌对）/ 1 白名单 / 2 黑名单。</summary>
    public int AttackFilterModeIndex
    {
        get => _attackFilterModeIndex;
        set
        {
            if (SetProperty(ref _attackFilterModeIndex, value))
            {
                ApplyAttack();
                AttackFilterChanged?.Invoke(this);
            }
        }
    }

    /// <summary>当前勾选的生物名单（EntityType 名）。</summary>
    public IReadOnlyList<string> SelectedAttackMobs =>
        _attackFilterItems.Where(static item => item.IsChecked).Select(static item => item.Key).ToArray();

    /// <summary>过滤设置变了（UI 线程触发）：外壳用它把设置写回加密账号库。</summary>
    public event Action<AccountViewModel>? AttackFilterChanged;

    /// <summary>某个生物被勾/取消：重新下发攻击参数并写回账号库。</summary>
    private void OnAttackFilterChanged()
    {
        // 整类勾选进行中（全选框）：单项回调先不处理，结束时统一下发一次
        if (_bulkFilterUpdate)
            return;

        ApplyAttack();
        AttackFilterChanged?.Invoke(this);
        RaiseCategoryAllCheckedChanged();
    }

    /// <summary>某一分类是不是已经全勾（全选框的读值）。</summary>
    private bool IsCategoryAllChecked(MobCategory category)
    {
        ObservableCollection<MobFilterItem> items = AttackFilterItems(category);
        return items.Count > 0 && items.All(static item => item.IsChecked);
    }

    /// <summary>
    /// 全选框写入：整类一起勾/取消。
    /// 批量期间挡住单项回调（否则敌对 41 项会触发 41 次下发+写盘），结束时统一下发一次。
    /// </summary>
    private void SetCategoryAllChecked(MobCategory category, bool value)
    {
        ObservableCollection<MobFilterItem> items = AttackFilterItems(category);
        if (items.Count == 0 || items.All(item => item.IsChecked == value))
        {
            RaiseCategoryAllCheckedChanged();
            return;
        }

        _bulkFilterUpdate = true;
        try
        {
            foreach (MobFilterItem item in items)
                item.IsChecked = value;
        }
        finally
        {
            _bulkFilterUpdate = false;
        }

        OnAttackFilterChanged();
    }

    /// <summary>三个全选框的读值刷新给界面（单项勾选变化时也会走，全选框跟着变）。</summary>
    private void RaiseCategoryAllCheckedChanged()
    {
        OnPropertyChanged(nameof(AttackFilterHostileAll));
        OnPropertyChanged(nameof(AttackFilterNeutralAll));
        OnPropertyChanged(nameof(AttackFilterFriendlyAll));
    }

    /// <summary>鼠标按键控制开关。</summary>
    public bool MouseEnabled
    {
        get => _mouseEnabled;
        set
        {
            if (SetProperty(ref _mouseEnabled, value))
                ApplyMouse();
        }
    }

    /// <summary>左键启用（与右键相互独立，可以同时开启）。</summary>
    public bool MouseLeftEnabled
    {
        get => _mouseLeftEnabled;
        set
        {
            if (SetProperty(ref _mouseLeftEnabled, value))
                ApplyMouse();
        }
    }

    /// <summary>左键模式：0 长按 / 1 间隔点击 / 2 间隔长按。</summary>
    public int MouseLeftModeIndex
    {
        get => _mouseLeftModeIndex;
        set
        {
            if (SetProperty(ref _mouseLeftModeIndex, value))
            {
                OnPropertyChanged(nameof(MouseLeftTimingVisible));
                OnPropertyChanged(nameof(MouseLeftHoldVisible));
                ApplyMouse();
            }
        }
    }

    /// <summary>“间隔 ms”行：长按模式下不生效，隐藏；间隔点击/间隔长按显示。</summary>
    public bool MouseLeftTimingVisible => MouseLeftModeIndex != 0;

    /// <summary>“按住 ms”行：按住时长是间隔长按的参数，只有它显示；长按/间隔点击下不生效即隐藏。</summary>
    public bool MouseLeftHoldVisible => MouseLeftModeIndex == 2;

    /// <summary>左键保持（按住/蓄力）时长，毫秒。</summary>
    public string MouseLeftHoldMs
    {
        get => _mouseLeftHoldMs;
        set
        {
            if (SetProperty(ref _mouseLeftHoldMs, value))
                ApplyMouse();
        }
    }

    /// <summary>左键点击间隔 / 冷却时长，毫秒。</summary>
    public string MouseLeftIntervalMs
    {
        get => _mouseLeftIntervalMs;
        set
        {
            if (SetProperty(ref _mouseLeftIntervalMs, value))
                ApplyMouse();
        }
    }

    /// <summary>左键随机抖动百分比（0-90）。</summary>
    public string MouseLeftJitterPercent
    {
        get => _mouseLeftJitterPercent;
        set
        {
            if (SetProperty(ref _mouseLeftJitterPercent, value))
                ApplyMouse();
        }
    }

    /// <summary>右键启用（与左键相互独立，可以同时开启）。</summary>
    public bool MouseRightEnabled
    {
        get => _mouseRightEnabled;
        set
        {
            if (SetProperty(ref _mouseRightEnabled, value))
                ApplyMouse();
        }
    }

    /// <summary>右键模式：0 长按 / 1 间隔点击 / 2 间隔长按。</summary>
    public int MouseRightModeIndex
    {
        get => _mouseRightModeIndex;
        set
        {
            if (SetProperty(ref _mouseRightModeIndex, value))
            {
                OnPropertyChanged(nameof(MouseRightTimingVisible));
                OnPropertyChanged(nameof(MouseRightHoldVisible));
                ApplyMouse();
            }
        }
    }

    /// <summary>“间隔 ms”行：长按模式下不生效，隐藏；间隔点击/间隔长按显示。</summary>
    public bool MouseRightTimingVisible => MouseRightModeIndex != 0;

    /// <summary>“按住 ms”行：按住时长是间隔长按的参数，只有它显示；长按/间隔点击下不生效即隐藏。</summary>
    public bool MouseRightHoldVisible => MouseRightModeIndex == 2;

    /// <summary>右键保持（按住/蓄力）时长，毫秒。</summary>
    public string MouseRightHoldMs
    {
        get => _mouseRightHoldMs;
        set
        {
            if (SetProperty(ref _mouseRightHoldMs, value))
                ApplyMouse();
        }
    }

    /// <summary>右键点击间隔 / 冷却时长，毫秒。</summary>
    public string MouseRightIntervalMs
    {
        get => _mouseRightIntervalMs;
        set
        {
            if (SetProperty(ref _mouseRightIntervalMs, value))
                ApplyMouse();
        }
    }

    /// <summary>右键随机抖动百分比（0-90）。</summary>
    public string MouseRightJitterPercent
    {
        get => _mouseRightJitterPercent;
        set
        {
            if (SetProperty(ref _mouseRightJitterPercent, value))
                ApplyMouse();
        }
    }

    /// <summary>准星探测距离（格，1-7，默认 5）：滑动条 TwoWay 绑定，写入时夹到范围并取整（等效吸附整格）。</summary>
    public double MouseAimReach
    {
        get => _mouseAimReach;
        set
        {
            double clamped = Math.Round(Math.Clamp(value, 1, 7));
            if (SetProperty(ref _mouseAimReach, clamped))
            {
                OnPropertyChanged(nameof(MouseAimReachText));
                ApplyMouse();
            }
        }
    }

    /// <summary>距离滑动条右侧的数值文案，如“5 格”。</summary>
    public string MouseAimReachText => $"{_mouseAimReach:0.#} 格";

    /// <summary>自动钓鱼开关（复用 MCC 内置 AutoFishing）。</summary>
    public bool FishingEnabled
    {
        get => _fishingEnabled;
        set
        {
            if (SetProperty(ref _fishingEnabled, value))
                _session.ConfigureFishing(value);
        }
    }

    /// <summary>断线自动重连开关。</summary>
    public bool ReconnectEnabled
    {
        get => _reconnectEnabled;
        set
        {
            if (!SetProperty(ref _reconnectEnabled, value))
                return;

            ApplyReconnect();

            AppendLog(value
                ? $"§8自动重连已开启：最多 {ParseInt(_reconnectAttempts, 5, 1, 50)} 次，"
                  + $"间隔约 {ParseInt(_reconnectDelayMs, 3000, 500, 300_000) / 1000.0:0.#} 秒（含随机抖动）。"
                : "§8自动重连已关闭。");
        }
    }

    /// <summary>最大重连次数。</summary>
    public string ReconnectAttempts
    {
        get => _reconnectAttempts;
        set
        {
            if (SetProperty(ref _reconnectAttempts, value))
                ApplyReconnect();
        }
    }

    /// <summary>重连前的等待时长，毫秒。</summary>
    public string ReconnectDelayMs
    {
        get => _reconnectDelayMs;
        set
        {
            if (SetProperty(ref _reconnectDelayMs, value))
                ApplyReconnect();
        }
    }

    private void ApplyAttack() =>
        _session.ConfigureAttack(AttackEnabled, new AttackOptions
        {
            Range = ParseDouble(AttackRange, 3.0, 1.0, 4.0),
            CooldownMinMs = ParseInt(AttackCooldownMin, 800, 50, 60_000),
            CooldownMaxMs = ParseInt(AttackCooldownMax, 1600, 50, 60_000),
            FilterMode = (MobFilterMode)Math.Clamp(AttackFilterModeIndex, 0, 2),
            Mobs = SelectedAttackMobs,
        });

    private void ApplyMouse() =>
        _session.ConfigureMouse(MouseEnabled, new MouseOptions
        {
            Left = new MouseButtonOptions
            {
                Enabled = MouseLeftEnabled,
                Mode = (MouseMode)Math.Clamp(MouseLeftModeIndex, 0, 2),
                HoldMs = ParseInt(MouseLeftHoldMs, 1000, 50, 60_000),
                IntervalMs = ParseInt(MouseLeftIntervalMs, 600, 50, 60_000),
                JitterPercent = ParseInt(MouseLeftJitterPercent, 20, 0, 90),
            },
            Right = new MouseButtonOptions
            {
                Enabled = MouseRightEnabled,
                Mode = (MouseMode)Math.Clamp(MouseRightModeIndex, 0, 2),
                HoldMs = ParseInt(MouseRightHoldMs, 1000, 50, 60_000),
                IntervalMs = ParseInt(MouseRightIntervalMs, 600, 50, 60_000),
                JitterPercent = ParseInt(MouseRightJitterPercent, 20, 0, 90),
            },
            AimReach = Math.Clamp(MouseAimReach, 1, 7),
        });

    private void ApplyReconnect() =>
        _session.ConfigureReconnect(new ReconnectOptions
        {
            Enabled = ReconnectEnabled,
            MaxAttempts = ParseInt(ReconnectAttempts, 5, 1, 50),
            DelayMs = ParseInt(ReconnectDelayMs, 3000, 500, 300_000),
        });

    /// <summary>解析输入框：非法或留空时退回默认值，并把结果限制在合理区间。</summary>
    private static int ParseInt(string? text, int fallback, int min, int max)
        => int.TryParse(text?.Trim(), out int value) ? Math.Clamp(value, min, max) : fallback;

    private static double ParseDouble(string? text, double fallback, double min, double max)
        => double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? Math.Clamp(value, min, max)
            : fallback;

    #endregion

    #region 连接流程

    private async Task ConnectAsync()
    {
        try
        {
            (string host, ushort port) = await ResolveServerAsync().ConfigureAwait(true);
            if (port == 0)
                return; // 地址/端口有问题，原因已经在日志里说明

            try
            {
                await _session.ConnectAsync(new MCCConnectionOptions
                {
                    ServerHost = host,
                    Port = port,
                    Username = Username,
                    MinecraftVersion = MinecraftVersion,
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog($"§c连接出错：{ex.Message}");
            }
        }
        finally
        {
            // 命令结束（成功/失败/参数不合法都算）必须重新报一次可用性：
            // 外壳“连接”按钮的 CanExecute 全靠这个事件刷新，漏了按钮就一直灰着点不动。
            ConnectCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 解析要连接的地址。
    /// ① “服务器”一栏允许直接写 <c>域名:端口</c>（IPv6 写 <c>[地址]:端口</c>），会把端口拆出来回填到端口框；
    /// ② 端口没填时先查 DNS SRV 记录 <c>_minecraft._tcp</c>（与原版客户端一致），查到就用查到的；
    /// ③ 查不到（含 IP 地址、无网络）就用默认端口 25565。
    /// 全程只回填内存里的输入框，不写任何配置文件。
    /// </summary>
    /// <returns>端口为 0 表示输入非法，已在日志说明，不要发起连接。</returns>
    private async Task<(string Host, ushort Port)> ResolveServerAsync()
    {
        string raw = ServerHost.Trim();
        if (raw.Length == 0)
        {
            AppendLog("§c服务器地址不能为空。");
            return (raw, 0);
        }

        (string host, string? embeddedPort) = ServerAddress.Split(raw);
        host = host.Trim();
        if (!string.Equals(host, ServerHost, StringComparison.Ordinal))
            ServerHost = host; // 写成 “域名:端口” 时，把拆剩的纯主机名回填

        // 端口优先级：地址里显式写的 > 端口框里填的 > 自动解析
        string portText = embeddedPort ?? ServerPort.Trim();
        if (portText.Length > 0)
        {
            if (!ushort.TryParse(portText, out ushort port) || port == 0)
            {
                AppendLog($"§c端口“{portText}”无效，请输入 1-65535 之间的数字。");
                return (host, 0);
            }

            string normalized = port.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(ServerPort, normalized, StringComparison.Ordinal))
                ServerPort = normalized; // 从地址里拆出来的端口同步显示到端口框

            return (host, port);
        }

        // 端口留空 → 自动解析
        if (ServerAddress.IsIpLiteral(host))
        {
            ServerPort = ServerAddress.DefaultPort.ToString(CultureInfo.InvariantCulture);
            AppendLog($"§7未填端口：IP 地址查 SRV 没有意义，直接用默认端口 {ServerAddress.DefaultPort}。");
            return (host, ServerAddress.DefaultPort);
        }

        AppendLog("§8未填端口，正在查询 DNS SRV 记录 _minecraft._tcp …");
        ushort? srvPort = await ServerAddress.TrySrvPortAsync(host).ConfigureAwait(true);
        ushort resolved = srvPort ?? ServerAddress.DefaultPort;
        ServerPort = resolved.ToString(CultureInfo.InvariantCulture);
        AppendLog(srvPort is null
            ? $"§7没查到 SRV 记录，使用默认端口 {ServerAddress.DefaultPort}。"
            : $"§7SRV 解析完成：{host} → 端口 {resolved}。");
        return (host, resolved);
    }

    private void Disconnect() => _session.Disconnect();

    private void SendInput()
    {
        string text = CommandInput;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_session.SendInput(text))
            return;

        AppendLog($"§7> {text.Trim()}");
        CommandInput = string.Empty;
    }

    private void ClearLog() => Logs.Clear();

    #endregion

    #region 账号库

    /// <summary>
    /// 把当前面板上的参数整理成账号库记录（保留原 Id 与创建时间）。
    /// 返回 null 表示参数不完整，原因已写进日志（silent 时不写）。
    /// </summary>
    public AccountProfile? TryBuildProfile(bool silent = false)
    {
        string host = ServerHost.Trim();
        string username = Username.Trim();

        if (host.Length == 0 || username.Length == 0)
        {
            if (!silent)
                AppendLog("§c服务器地址与用户名不能为空，无法保存账号。");
            return null;
        }

        // 端口没填时的兜底：先看地址里有没有写“:端口”，都没有就按默认端口记
        (host, string? embeddedPort) = ServerAddress.Split(host);
        host = host.Trim();
        if (!string.Equals(host, ServerHost, StringComparison.Ordinal))
            ServerHost = host;

        string portText = embeddedPort ?? ServerPort.Trim();
        if (portText.Length == 0)
        {
            portText = ServerAddress.DefaultPort.ToString(CultureInfo.InvariantCulture);
            ServerPort = portText;
            if (!silent)
                AppendLog($"§7没填端口，保存时按默认端口 {ServerAddress.DefaultPort} 记录（连接时仍会先查 SRV）。");
        }

        if (!ushort.TryParse(portText, out ushort port) || port == 0)
        {
            if (!silent)
                AppendLog("§c端口无效，无法保存账号。");
            return null;
        }

        return new AccountProfile
        {
            Id = Id,
            Username = username,
            ServerHost = host,
            Port = port,
            MinecraftVersion = MinecraftVersion.Trim(),
            DisplayName = DisplayName,
            AttackFilterMode = Math.Clamp(AttackFilterModeIndex, 0, 2),
            AttackFilterMobs = [.. SelectedAttackMobs],
            CreatedAt = _profile.CreatedAt,
            LastUsedAt = DateTimeOffset.Now,
        };
    }

    #endregion

    #region 日志与状态

    private void OnLogReceived(string rawText)
    {
        // 子进程管道线程 / MCC 线程 → UI 线程
        _dispatcherQueue.TryEnqueue(() => AppendLog(rawText));
    }

    private void OnStateChanged(MCCConnectionState state)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsConnected = state == MCCConnectionState.Connected;
            StateText = state switch
            {
                MCCConnectionState.Connecting => "连接中…",
                MCCConnectionState.Connected => $"已连接 {ServerHost}:{ServerPort}",
                MCCConnectionState.Disconnecting => "断开中…",
                _ => "未连接",
            };

            if (state == MCCConnectionState.Connected)
                Connected?.Invoke(this);
        });
    }

    private void OnProcessExited(int exitCode)
    {
        _dispatcherQueue.TryEnqueue(() =>
            AppendLog(exitCode == 0
                ? "§8账号进程已结束。"
                : $"§c账号进程异常退出（退出码 {exitCode}），可点击“连接”重新拉起。"));
    }

    /// <summary>主界面外壳往这个账号的日志里写一行系统提示（账号为空时静默丢弃）。</summary>
    public void WriteNote(string text) => AppendLog(text);

    private void AppendLog(string rawText)
    {
        foreach (string line in rawText.Replace("\r\n", "\n").Split('\n'))
        {
            string text = ChatBot.GetVerbatim(line);

            // 连续重复合并（需求：多条相同日志不再刷屏）：
            // 与上一条完全相同的行不新增条目，直接把上一条就地更新成 “原文 xN”。
            // 只比相邻两条（等价于 uniq -c），O(1) 无额外状态；空行不合并，保留排版。
            if (text.Length > 0 && Logs.Count > 0
                && string.Equals(Logs[^1].BaseText, text, StringComparison.Ordinal))
            {
                Logs[^1].MergeDuplicate();
                continue;
            }

            Logs.Add(new LogEntry(text, line));
        }

        while (Logs.Count > MaxLogLines + LogTrimBatch)
            Logs.RemoveAt(0);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _session.LogReceived -= OnLogReceived;
        _session.StateChanged -= OnStateChanged;

        if (_session is RunnerProcess runner)
            runner.ProcessExited -= OnProcessExited;

        _session.Dispose();
    }
}
