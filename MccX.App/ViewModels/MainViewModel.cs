using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MccX.Core;
using MccX.Core.Networking;
using MinecraftClient.Scripting;
using Microsoft.UI.Dispatching;

namespace MccX_App.ViewModels;

/// <summary>
/// 主界面 ViewModel：连接参数、日志列表、命令输入。
/// MCC 线程产生的事件统一经 DispatcherQueue 切回 UI 线程再改绑定数据。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>日志最大保留行数，超出后按批裁剪，避免长时间挂机内存无限增长。</summary>
    private const int MaxLogLines = 1000;

    private const int LogTrimBatch = 100;

    private readonly MccSession _session;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AccountStore _accountStore = new();

    private string _serverHost = "127.0.0.1";
    private string _serverPort = "25565";
    private string _username = "Player";
    private string _minecraftVersion = "auto";
    private string _commandInput = string.Empty;
    private string _stateText = "未连接";
    private bool _isConnected;
    private AccountProfile? _selectedAccount;

    public MainViewModel(DispatcherQueue dispatcherQueue, MccSession session)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        SendCommand = new RelayCommand(SendInput, () => IsConnected);
        ClearLogCommand = new RelayCommand(ClearLog);
        SaveAccountCommand = new RelayCommand(SaveCurrentAccount);
        DeleteAccountCommand = new RelayCommand(DeleteSelectedAccount, () => SelectedAccount is not null);

        _session.LogReceived += OnLogReceived;
        _session.StateChanged += OnStateChanged;

        ReloadAccounts();

        AppendLog("§8[MccX] 就绪：填写服务器与用户名后点击“连接”。（离线模式）");
        AppendLog($"§8账号文件：{AccountStore.DefaultDirectory}");
        if (_accountStore.MigratedLegacyData)
            AppendLog("§8已把旧的 %APPDATA%\\MccX 账号文件迁移到程序目录。");
        if (!string.IsNullOrEmpty(_accountStore.LastError))
            AppendLog($"§e账号列表解密失败，已按空列表启动：{_accountStore.LastError}");
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>历史账号列表（已解密，按最近使用排序）。</summary>
    public ObservableCollection<AccountProfile> Accounts { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    /// <summary>把当前连接参数保存/更新为一个历史账号。</summary>
    public RelayCommand SaveAccountCommand { get; }

    public RelayCommand DeleteAccountCommand { get; }

    /// <summary>
    /// 左侧列表当前选中的账号。选中后自动回填连接参数（不自动连接）。
    /// </summary>
    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
                return;

            DeleteAccountCommand.RaiseCanExecuteChanged();

            if (value is null)
                return; // 列表重载或点击空白处时不要清空输入框

            ServerHost = value.ServerHost;
            ServerPort = value.Port.ToString();
            Username = value.Username;
            MinecraftVersion = string.IsNullOrWhiteSpace(value.MinecraftVersion) ? "auto" : value.MinecraftVersion;
        }
    }

    public string ServerHost
    {
        get => _serverHost;
        set => SetProperty(ref _serverHost, value);
    }

    public string ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value);
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
        private set => SetProperty(ref _stateText, value);
    }

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

    public void Dispose()
    {
        _session.LogReceived -= OnLogReceived;
        _session.StateChanged -= OnStateChanged;
        _session.Dispose();
        _accountStore.Dispose();
    }

    #region 自动化（需求 3.2）

    private bool _attackEnabled;
    private string _attackRange = "3.0";
    private string _attackCooldownMin = "800";
    private string _attackCooldownMax = "1600";

    private bool _mouseEnabled;
    private int _mouseModeIndex = (int)MouseMode.IntervalClick;
    private int _mouseSideIndex = (int)MouseSide.Right;
    private string _mouseHoldMs = "1000";
    private string _mouseIntervalMs = "600";
    private string _mouseJitterPercent = "20";

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

    /// <summary>鼠标模式：0 长按 / 1 间隔点击 / 2 间隔长按。</summary>
    public int MouseModeIndex
    {
        get => _mouseModeIndex;
        set
        {
            if (SetProperty(ref _mouseModeIndex, value))
                ApplyMouse();
        }
    }

    /// <summary>鼠标按键：0 左键 / 1 右键。</summary>
    public int MouseSideIndex
    {
        get => _mouseSideIndex;
        set
        {
            if (SetProperty(ref _mouseSideIndex, value))
                ApplyMouse();
        }
    }

    /// <summary>保持（按住/蓄力）时长，毫秒。</summary>
    public string MouseHoldMs
    {
        get => _mouseHoldMs;
        set
        {
            if (SetProperty(ref _mouseHoldMs, value))
                ApplyMouse();
        }
    }

    /// <summary>点击间隔 / 冷却时长，毫秒。</summary>
    public string MouseIntervalMs
    {
        get => _mouseIntervalMs;
        set
        {
            if (SetProperty(ref _mouseIntervalMs, value))
                ApplyMouse();
        }
    }

    /// <summary>随机抖动百分比（0-90）。</summary>
    public string MouseJitterPercent
    {
        get => _mouseJitterPercent;
        set
        {
            if (SetProperty(ref _mouseJitterPercent, value))
                ApplyMouse();
        }
    }

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
        });

    private void ApplyMouse() =>
        _session.ConfigureMouse(MouseEnabled, new MouseOptions
        {
            Mode = (MouseMode)Math.Clamp(MouseModeIndex, 0, 2),
            Side = (MouseSide)Math.Clamp(MouseSideIndex, 0, 1),
            HoldMs = ParseInt(MouseHoldMs, 1000, 50, 60_000),
            IntervalMs = ParseInt(MouseIntervalMs, 600, 50, 60_000),
            JitterPercent = ParseInt(MouseJitterPercent, 20, 0, 90),
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

    private async Task ConnectAsync()
    {
        (string host, ushort port) = await ResolveServerAsync();
        if (port == 0)
            return; // 地址/端口有问题，原因已经在日志里说明

        try
        {
            await _session.ConnectAsync(new MccConnectionOptions
            {
                ServerHost = host,
                Port = port,
                Username = Username,
                MinecraftVersion = MinecraftVersion,
            });
        }
        catch (Exception ex)
        {
            AppendLog($"§c连接出错：{ex.Message}");
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
        ushort? srvPort = await ServerAddress.TrySrvPortAsync(host);
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

    #region 账号持久化（需求 3.1）

    private void ReloadAccounts(string? selectId = null)
    {
        IReadOnlyList<AccountProfile> list = _accountStore.Load();

        Accounts.Clear();
        foreach (AccountProfile profile in list)
            Accounts.Add(profile);

        SelectedAccount = selectId is null ? null : list.FirstOrDefault(p => p.Id == selectId);
    }

    private void SaveCurrentAccount()
    {
        AccountProfile? candidate = BuildProfileFromInputs();
        if (candidate is null)
            return;

        try
        {
            AccountProfile saved = _accountStore.Upsert(candidate);
            ReloadAccounts(saved.Id);
            AppendLog($"§8已保存账号 {saved.DisplayName}（{saved.ServerSummary}）。");

            if (!string.IsNullOrEmpty(_accountStore.LastError))
                AppendLog($"§c写入账号文件失败：{_accountStore.LastError}");
        }
        catch (Exception ex)
        {
            AppendLog($"§c保存账号失败：{ex.Message}");
        }
    }

    private void DeleteSelectedAccount()
    {
        AccountProfile? target = SelectedAccount;
        if (target is null)
            return;

        try
        {
            _accountStore.Remove(target.Id);
            ReloadAccounts();
            AppendLog($"§8已删除账号 {target.DisplayName}。");
        }
        catch (Exception ex)
        {
            AppendLog($"§c删除账号失败：{ex.Message}");
        }
    }

    /// <summary>连接成功后把当前参数静默入库，实现“历史登录过的账号”自动沉淀。</summary>
    private void AutoSaveConnectedAccount()
    {
        AccountProfile? candidate = BuildProfileFromInputs(silent: true);
        if (candidate is null)
            return;

        try
        {
            AccountProfile saved = _accountStore.Upsert(candidate);
            ReloadAccounts(saved.Id);
        }
        catch (Exception ex)
        {
            AppendLog($"§c自动保存账号失败：{ex.Message}");
        }
    }

    private AccountProfile? BuildProfileFromInputs(bool silent = false)
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
            Username = username,
            ServerHost = host,
            Port = port,
            MinecraftVersion = MinecraftVersion.Trim(),
            DisplayName = username,
            CreatedAt = DateTimeOffset.Now,
            LastUsedAt = DateTimeOffset.Now,
        };
    }

    #endregion

    private void OnLogReceived(string rawText)
    {
        // MCC 线程 → UI 线程
        _dispatcherQueue.TryEnqueue(() => AppendLog(rawText));
    }

    private void OnStateChanged(MccConnectionState state)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsConnected = state == MccConnectionState.Connected;
            StateText = state switch
            {
                MccConnectionState.Connecting => "连接中…",
                MccConnectionState.Connected => $"已连接 {ServerHost}:{ServerPort}",
                MccConnectionState.Disconnecting => "断开中…",
                _ => "未连接",
            };

            if (state == MccConnectionState.Connected)
                AutoSaveConnectedAccount();
        });
    }

    private void AppendLog(string rawText)
    {
        foreach (string line in rawText.Replace("\r\n", "\n").Split('\n'))
        {
            string text = ChatBot.GetVerbatim(line);
            Logs.Add(new LogEntry(text, line));
        }

        while (Logs.Count > MaxLogLines + LogTrimBatch)
            Logs.RemoveAt(0);
    }
}
