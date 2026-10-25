using MCCX.Core.Containers;
using MCCX.Core.Dialogs;
using MinecraftClient;
using MinecraftClient.Dialogs;
using MinecraftClient.Protocol;
using MinecraftClient.Protocol.Handlers.Forge;
using MinecraftClient.Protocol.Session;
using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 一个离线挂机会话：封装 MCC 的启动链路（版本探测 → 构造 McClient → 挂载监视 Bot），
/// 并把 MCC 的日志/输入/连接状态以事件形式暴露给 UI。
///
/// 线程模型：所有 MCC 相关工作都在后台线程执行，事件可能从任意线程抛出，
/// 订阅方（ViewModel）负责切换到 UI 线程。
/// </summary>
public sealed class MCCSession : IAccountSession
{
    /// <summary>
    /// 会拦截的 MCC 内部命令：这些命令会直接杀进程或另起一个 MCC 客户端，
    /// 在 GUI 里必须改用界面按钮。
    /// </summary>
    private static readonly string[] BlockedInternalCommands =
    [
        "exit", "quit", "reco", "reconnect", "restart", "connect", "reload",
    ];

    /// <summary>
    /// 这条 MCC 内部命令名是否属于"会杀连接/进程"的禁区（命令栏与自动指令共用同一份表）。
    /// </summary>
    internal static bool IsBlockedInternalCommand(string? commandName) =>
        commandName is not null && Array.IndexOf(BlockedInternalCommands, commandName) >= 0;

    /// <summary>BotLoad 需要 MCC 主线程响应，超过该时间即认为挂了。</summary>
    private const int BotLoadTimeoutMs = 15000;

    private readonly MCCUiBackend _backend;
    private readonly object _gate = new();
    private readonly List<string> _outputDuringConnect = [];

    private MCCConnectionState _state = MCCConnectionState.Disconnected;
    private volatile bool _gameJoined;
    private bool _capturing;
    private bool _disposed;
    private McClient? _client;
    private MonitorBot? _monitor;

    // ---- 自动化组件（需求 3.2）：状态保存在会话里，连接成功后按开关重新挂载 ----
    private bool _attackEnabled;
    private AttackOptions _attackOptions = new();
    private bool _mouseEnabled;
    private MouseOptions _mouseOptions = new();
    private bool _fishingEnabled;

    /// <summary>自动钓鱼参数（收杆检测 / 抛竿超时 / 重抛间隔），默认值 = MCC 配置默认值。</summary>
    private FishingOptions _fishingOptions = new();

    private bool _autoRefillEnabled;

    /// <summary>自动行走开关（2026-10-04 需求：一直朝当前朝向前进，没有参数）。</summary>
    private bool _walkEnabled;

    /// <summary>自动指令总开关（2026-10-22 需求：按各自间隔定时执行指令）。</summary>
    private bool _autoCommandsEnabled;

    /// <summary>自动指令表（每条自己带间隔与"进服立即执行"；改一次整份下发给 Bot）。</summary>
    private List<AutoCommandDefinition> _autoCommands = [];

    // ---- 服务器信息过滤（2026-10-04 需求）：只拦服务器发来的聊天/系统消息 ----

    /// <summary>过滤模式（见 <see cref="ServerFilterMode"/>）；连接成功时包进 <c>McClient.Log</c>。</summary>
    private ServerFilterMode _serverFilterMode = ServerFilterMode.None;

    /// <summary>
    /// "只显示指定前缀"用的前缀列表；"只屏蔽指定前缀"用 <see cref="_serverFilterBlockPrefix"/>，
    /// 两份各存各的（2026-10-05 用户要求：以前两种方式共用一个输入框，切方式就得重填）。
    /// </summary>
    private string _serverFilterShowPrefix = string.Empty;

    /// <summary>"只屏蔽指定前缀"用的前缀列表（与 <see cref="_serverFilterShowPrefix"/> 相互独立）。</summary>
    private string _serverFilterBlockPrefix = string.Empty;

    /// <summary>当前连接上的过滤器包装；断开后指向旧连接（无害，下次连接会换新的）。</summary>
    private ServerFilterLogger? _serverFilterLogger;

    private ReconnectOptions _reconnectOptions = new();

    private CustomAutoAttackBot? _attackBot;
    private MouseControlBot? _mouseBot;
    private MinecraftClient.ChatBots.AutoFishing? _fishingBot;
    private AutoRefillBot? _autoRefillBot;
    private AutoWalkBot? _walkBot;
    private ViewControlBot? _viewBot;
    private AutoCommandBot? _autoCommandBot;

    /// <summary>
    /// 各功能的“过程日志”开关（2026-10-20 用户要求：开关控制日志的<b>发出</b>）：
    /// 键见 <see cref="FeatureLogKeys"/>，缺键视为开（老配置不需要迁移）。
    /// <para>
    /// 实现是直接改 Bot 的 <c>ChatBot.ProcessLogEnabled</c>——关掉后该功能的过程日志
    /// 在 Bot 里就不产生，既不进控制台/日志文件，也不会经管道送到界面。
    /// 旧做法（Bot 照常输出、MCCX 界面按 <c>[标签]</c> 文本吞行）在 MCC 汉化后
    /// 标签从 <c>AutoFishing</c> 变成 <c>自动钓鱼</c>，匹配失败开关就"看起来失效"。
    /// </para>
    /// </summary>
    private readonly Dictionary<string, bool> _featureLogs = new(StringComparer.Ordinal);

    /// <summary>容器窗口桥（服务器 GUI）：窗口内容 → 界面、界面点击 → MCC。</summary>
    private WindowBridgeBot? _windowBot;

    /// <summary>
    /// 界面是不是开着"背包"面板（窗口 0）。记在会话上而不是 Bot 上：
    /// 重连时 MCC 会换 / 复活 Bot 实例，这里要在挂载后把状态交给它，接着往下推。
    /// </summary>
    private bool _windowWatchPlayer;

    // ---- 服务器对话框（MCC Dialog 系统，2026-10-03 用户需求：密码要用界面输入框填）----
    private DialogManager? _dialogManager;

    /// <summary>最近一次弹给界面的对话框（带动作的取消标记），供 <see cref="SubmitDialog"/> 认取消动作。</summary>
    private MccDialogInfo? _lastDialog;

    /// <summary>Bot 挂载/卸载串行队列：避免开关连点导致加载与卸载交错。</summary>
    private Task _botOps = Task.CompletedTask;

    // ---- 断线自动重连（需求 3.2）----
    private MCCConnectionOptions? _lastOptions;
    private MCCConnectionOptions? _pendingOptions;
    private bool _reconnectPending;
    private bool _userRequestedDisconnect;

    /// <summary>
    /// 用户取消服务器对话框的时刻（关窗按钮/ESC，或点了服务器的“取消”类动作）。
    /// 窗口内的那次断开不自动重连：否则“取消 → 服务器踢人 → 自动重连 → 又弹框”会无限循环
    /// （现场实测每 ~30 秒一轮、计数永远显示第 1 次）。
    /// </summary>
    private long _dialogCancelTick = long.MinValue;

    /// <summary>取消后多久内的断开算“用户不想连了”：覆盖服务器对话框超时（实测 ~30 秒）再留余量。</summary>
    private const long DialogCancelReconnectWindowMs = 60_000;

    /// <summary>本会话对应的账号 Id：视角 Bot 按账号认领，用它当键。</summary>
    private readonly string _accountId;

    /// <summary>使用 <see cref="MCCRuntime"/> 已初始化的默认后端。</summary>
    public MCCSession() : this(MCCRuntime.Backend, null)
    {
    }

    /// <param name="accountId">账号 Id（视角 Bot 按账号认领）；为 null 时退化为空键。</param>
    public MCCSession(string? accountId) : this(MCCRuntime.Backend, accountId)
    {
    }

    public MCCSession(MCCUiBackend backend) : this(backend, null)
    {
    }

    public MCCSession(MCCUiBackend backend, string? accountId)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _accountId = accountId ?? string.Empty;
        _backend.OutputReceived += OnBackendOutput;
        // MCCX: 服务器命令列表是 ConsoleIO 的静态事件，会话级订阅，Dispose 时摘除
        MinecraftClient.ConsoleIO.ServerCommandsUpdated += OnServerCommandsUpdated;
    }

    /// <summary>当前连接状态（线程安全）。</summary>
    public MCCConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>MCC 输出的原始日志行（可能带 § 颜色码/换行）。可能从任意线程触发。</summary>
    public event Action<string>? LogReceived;

    /// <summary>连接状态变化。可能从任意线程触发。</summary>
    public event Action<MCCConnectionState>? StateChanged;

    /// <summary>
    /// 服务器完全进入游戏（配置阶段结束、可以发聊天）。
    /// 注意：<see cref="StateChanged"/> 变为 Connected 只代表登录成功，比这个事件早。
    /// 可能从任意线程触发。
    /// </summary>
    public event Action? GameJoined;

    /// <summary>服务器弹出对话框。可能从任意线程触发（MCC 网络线程）。</summary>
    public event Action<MccDialogInfo>? DialogRequested;

    /// <summary>服务器关闭了编号为该值的对话框。可能从任意线程触发。</summary>
    public event Action<int>? DialogClosed;

    /// <summary>
    /// 服务器命令列表（declare_commands / tab-complete 合并结果）发生变化。
    /// 载荷是根命令名列表（不含前导 '/'）。可能从任意线程触发。
    /// </summary>
    public event Action<IReadOnlyList<string>>? ServerCommandsChanged;

    /// <summary>
    /// 服务器打开/刷新了一个容器窗口（箱子、商店 GUI 等）。<c>Full</c> 为 true 表示全量快照，
    /// 否则载荷里只有变化的格子。可能从任意线程触发。
    /// </summary>
    public event Action<MccWindowInfo>? WindowChanged;

    /// <summary>服务器关掉了编号为该值的容器窗口。可能从任意线程触发。</summary>
    public event Action<int>? WindowClosed;

    /// <summary>是否已完全进入游戏（可发聊天）。断开后自动回到 false。</summary>
    public bool IsGameJoined => _gameJoined;

    /// <summary>
    /// 连接服务器（同步阻塞的 MCC 登录流程会在后台线程里跑完）。
    /// 返回时状态要么是 Connected，要么已回到 Disconnected。
    /// </summary>
    public Task ConnectAsync(MCCConnectionOptions options, CancellationToken cancellationToken = default)
        => ConnectAsyncCore(options, isAutoReconnect: false, cancellationToken);

    /// <param name="isAutoReconnect">
    /// true 表示由自动重连循环发起：失败后不清空 <c>_lastOptions</c>，也不允许触发新的重连循环。
    /// </param>
    private async Task ConnectAsyncCore(
        MCCConnectionOptions options,
        bool isAutoReconnect,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryBeginConnect())
        {
            LogUi("§e当前已处于连接流程中，忽略本次请求。");
            return;
        }

        lock (_gate)
        {
            _pendingOptions = options;
            _userRequestedDisconnect = false;
            // 新一轮连接开始：清掉“取消过对话框”的压制，别让它串到这次会话
            _dialogCancelTick = long.MinValue;

            // 手动发起的连接失败时，不应拿上一次成功的参数去自动重连
            if (!isAutoReconnect)
                _lastOptions = null;
        }

        try
        {
            string host = options.ServerHost.Trim();
            string username = options.Username.Trim();
            if (host.Length == 0 || username.Length == 0)
            {
                LogUi("§c服务器地址与用户名不能为空。");
                SetState(MCCConnectionState.Disconnected);
                return;
            }

            // 1. 解析/探测协议版本
            int protocolVersion = 0;
            ForgeInfo? forgeInfo = null;
            string version = options.MinecraftVersion.Trim();

            if (version.Length > 0 && !version.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                protocolVersion = ProtocolHandler.MCVer2ProtocolVersion(version);
                if (protocolVersion == 0)
                {
                    LogUi($"§c无法识别的 Minecraft 版本：{version}");
                    SetState(MCCConnectionState.Disconnected);
                    return;
                }

                LogUi($"§8使用指定协议版本 {protocolVersion}（{version}）。");
            }
            else
            {
                LogUi("§8正在 Ping 服务器获取版本信息…");
                bool pinged = await Task.Run(
                    () => ProtocolHandler.GetServerInfo(host, options.Port, ref protocolVersion, ref forgeInfo),
                    cancellationToken).ConfigureAwait(false);

                if (!pinged)
                {
                    LogUi("§cPing 服务器失败，请检查地址、端口和网络。");
                    SetState(MCCConnectionState.Disconnected);
                    return;
                }

                LogUi($"§8服务器协议版本 {protocolVersion}。");
            }

            // 2. 写入离线账号与服务器信息（与 MCC 启动流程一致，仅内存配置，不回写 .ini）
            Settings.InternalConfig.Account.Login = username;
            Settings.InternalConfig.Account.Password = "-"; // "-" 表示离线模式
            Settings.InternalConfig.Username = username;
            Settings.InternalConfig.ServerIP = host;
            Settings.InternalConfig.ServerPort = options.Port;
            Settings.InternalConfig.MinecraftVersion = string.Empty;

            // 调试模式（2026-10-09 需求⑤）：开着时把 MCC 的 DebugMessages + PacketDebugMessages
            // 打开，网络循环退出原因（LogNetworkLoopExit）、包级收发才流到日志区；关着时强制回 False，
            // 避免上一条会话留下的开关把日志刷屏。DebugEnabled 在 McClient 构造时读一次，
            // 所以必须在 new McClient 之前落好——对下一次（重）连生效。
            bool debugMode = UiSettingsStore.ReadDebugMode();
            Settings.Config.Logging.DebugMessages = debugMode;
            Settings.Config.Logging.PacketDebugMessages = debugMode;

            SessionToken session = new()
            {
                PlayerID = "0",
                PlayerName = username,
            };

            // 3. 构造 McClient：构造函数会同步完成整个登录流程，必须放到后台线程
            string joinedMarker = BuildJoinedMarker();
            StartOutputCapture();

            McClient? client = null;
            Exception? connectError = null;
            try
            {
                await Task.Run(
                    () =>
                    {
                        try
                        {
                            client = new McClient(session, null, host, options.Port, protocolVersion, forgeInfo);

                            // 服务器信息过滤（2026-10-04）：把过滤器包在 MCC 日志器外面——
                            // 只有 Log.Chat（服务器聊天/系统消息，OnTextReceived 的唯一出口）会按
                            // 模式拦截，Info/Warn/Bot 日志原样转发；对 Bot 事件零影响。
                            lock (_gate)
                            {
                                ServerFilterLogger filter = new(client.Log, _serverFilterMode, _serverFilterShowPrefix, _serverFilterBlockPrefix);
                                _serverFilterLogger = filter;
                                client.Log = filter;
                            }
                        }
                        catch (Exception ex)
                        {
                            connectError = ex;
                        }
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetState(MCCConnectionState.Disconnected);
                return;
            }

            List<string> captured = StopOutputCapture();
            bool joined = captured.Exists(line => line.Contains(joinedMarker, StringComparison.Ordinal));

            if (connectError is not null)
            {
                LogUi($"§c连接过程发生异常：{connectError.Message}");
            }

            if (client is null || !joined)
            {
                // 失败原因 MCC 已经写进日志，这里只需要把状态收回去
                if (connectError is null)
                    LogUi("§c连接失败，未收到进入游戏的确认。");
                SetState(MCCConnectionState.Disconnected);
                return;
            }

            // 4. 挂载监视 Bot，上报断线
            MonitorBot monitor = new();
            monitor.Joined += OnMonitorJoined;
            monitor.Disconnected += OnMonitorDisconnected;
            _client = client;
            _monitor = monitor;

            // 服务器可能在登录过程中就已经弹了对话框（事件还没挂上），
            // HookDialogs 会把"当前在场"的那一条补发出去
            HookDialogs(client);

            bool loaded = await Task.Run(() => LoadMonitorBot(client, monitor)).ConfigureAwait(false);
            if (!loaded)
            {
                _client = null;
                _monitor = null;
                UnhookDialogs();
                LogUi("§c监视 Bot 挂载超时，连接状态不可靠，已放弃本次连接。");
                SetState(MCCConnectionState.Disconnected);
                return;
            }

            lock (_gate)
            {
                _lastOptions = options;
            }

            LogUi("§a连接成功。");
            SetState(MCCConnectionState.Connected);

            // 按当前开关重新挂载自动化 Bot（首次连接与自动重连后都会执行）
            ApplyAutomation();
        }
        catch (OperationCanceledException)
        {
            SetState(MCCConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            LogUi($"§c连接失败：{ex.Message}");
            SetState(MCCConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// 把一行输入送进 MCC（内部命令或聊天）。
    /// 会拦截会杀进程/另起客户端的内部命令。返回是否已投递。
    /// </summary>
    public bool SendInput(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        if (State != MCCConnectionState.Connected || _client is null)
        {
            LogUi("§8当前未连接，输入已忽略。");
            return false;
        }

        string? internalCommand = GetInternalCommandName(text);
        if (IsBlockedInternalCommand(internalCommand))
        {
            LogUi($"§e已拦截 /{internalCommand}：在界面里请使用连接/断开按钮。");
            return false;
        }

        _backend.SubmitInput(text);
        return true;
    }

    /// <summary>主动断开（在后台线程执行，避免阻塞 UI）。也可用于取消待执行的自动重连。</summary>
    public void Disconnect()
    {
        McClient client;
        bool cancelledReconnect;

        lock (_gate)
        {
            cancelledReconnect = _reconnectPending && _state == MCCConnectionState.Disconnected;
            if (cancelledReconnect)
            {
                _userRequestedDisconnect = true;
                client = null!; // 取消分支随后立即 return，不会用到该值
            }
            else
            {
                if (_state != MCCConnectionState.Connected || _client is null)
                    return;

                _state = MCCConnectionState.Disconnecting;
                // 用户主动断开：绝不能被自动重连逻辑重新拉起
                _userRequestedDisconnect = true;
                client = _client;
            }
        }

        if (cancelledReconnect)
        {
            LogUi("§8已取消自动重连。");
            return;
        }

        StateChanged?.Invoke(MCCConnectionState.Disconnecting);

        _ = Task.Run(() =>
        {
            try
            {
                client.Disconnect();
            }
            catch (Exception ex)
            {
                LogUi($"§c断开连接时出错：{ex.Message}");
            }
            finally
            {
                // 兜底：即使监视 Bot 没来得及上报，也必须回到 Disconnected
                lock (_gate)
                {
                    _client = null;
                    _monitor = null;
                    _gameJoined = false;
                    _attackBot = null;
                    _mouseBot = null;
                    _fishingBot = null;
                    _autoRefillBot = null;
                    _walkBot = null;
                    _viewBot = null;
                    _autoCommandBot = null;
                    UnhookDialogs();
                }

                SetState(MCCConnectionState.Disconnected);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _backend.OutputReceived -= OnBackendOutput;
        MinecraftClient.ConsoleIO.ServerCommandsUpdated -= OnServerCommandsUpdated;

        McClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _monitor = null;
            UnhookDialogs();
        }

        if (client is not null)
        {
            try
            {
                client.Disconnect();
            }
            catch
            {
                // 关窗时尽力断开即可
            }
        }

        GC.SuppressFinalize(this);
    }

    #region 自动化（需求 3.2）

    /// <summary>
    /// 设置自动砍怪。开关变化会给出提示；参数变化静默生效（Bot 已挂载时直接替换参数）。
    /// </summary>
    public void ConfigureAttack(bool enabled, AttackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool changed;
        bool filterChanged;
        lock (_gate)
        {
            changed = _attackEnabled != enabled;
            filterChanged = _attackOptions.FilterMode != options.FilterMode
                            || !_attackOptions.Mobs.SequenceEqual(options.Mobs);
            _attackEnabled = enabled;
            _attackOptions = options;
        }

        if (changed)
            LogUi(enabled ? "§a自动砍怪已开启，进入游戏后生效。" : "§8自动砍怪已关闭。");
        else if (filterChanged && enabled)
            LogUi($"§7攻击过滤已更新（{CustomAutoAttackBot.DescribeFilter(options)}）。");

        ApplyAutomation();
    }

    /// <summary>设置鼠标按键控制（左键、右键两套独立参数，可同时触发）。</summary>
    public void ConfigureMouse(bool enabled, MouseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool changed;
        lock (_gate)
        {
            changed = _mouseEnabled != enabled;
            _mouseEnabled = enabled;
            _mouseOptions = options;
        }

        if (changed)
        {
            if (enabled)
            {
                LogUi("§a鼠标控制已开启，进入游戏后生效。");
                if (!options.Left.Enabled && !options.Right.Enabled)
                    LogUi("§8鼠标控制：左键、右键都未启用，不会产生点击。");
            }
            else
            {
                LogUi("§8鼠标控制已关闭。");
            }
        }

        ApplyAutomation();
    }

    /// <summary>设置自动钓鱼（复用 MCC 内置 AutoFishing Bot）：开关 + 收杆检测/超时/重抛参数。</summary>
    public void ConfigureFishing(bool enabled, FishingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool changed;
        lock (_gate)
        {
            changed = _fishingEnabled != enabled;
            _fishingEnabled = enabled;
            _fishingOptions = options;
        }

        if (changed)
            LogUi(enabled ? "§a自动钓鱼已开启（手中需持有钓竿）。" : "§8自动钓鱼已关闭。");

        ApplyAutomation();
    }

    /// <summary>
    /// 设置自动补充（用户 2026-10-03 第五批需求）：手持格「非空 → 空」时自动从背包补同款。
    /// </summary>
    public void ConfigureAutoRefill(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = _autoRefillEnabled != enabled;
            _autoRefillEnabled = enabled;
        }

        if (changed)
            LogUi(enabled
                ? "§a自动补充已开启：手持用完/损坏时从背包补同款（主背包优先，其他快捷栏兜底）。"
                : "§8自动补充已关闭。");

        ApplyAutomation();
    }

    /// <summary>
    /// 设置自动行走（用户 2026-10-04 需求）：一直朝当前朝向前进，没有参数，只有开关。
    /// </summary>
    public void ConfigureWalk(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = _walkEnabled != enabled;
            _walkEnabled = enabled;
        }

        if (changed)
            LogUi(enabled
                ? "§a自动行走已开启：进服后持续朝当前朝向前进（关掉开关或断开即停）。"
                : "§8自动行走已关闭。");

        ApplyAutomation();
    }

    /// <summary>
    /// 设置自动指令（用户 2026-10-22 需求）：总开关 + 整份指令表。
    /// 每条指令各自带间隔（秒）与"进服立即执行"；开着时改表立即生效（已连接）。
    /// </summary>
    public void ConfigureAutoCommands(bool enabled, IReadOnlyList<AutoCommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        bool changed;
        int count;
        lock (_gate)
        {
            changed = _autoCommandsEnabled != enabled;
            _autoCommandsEnabled = enabled;
            _autoCommands = [.. commands];
            count = _autoCommands.Count;
        }

        if (changed)
        {
            LogUi(enabled
                ? $"§a自动指令已开启：{count} 条指令按各自间隔执行（进服立即执行的会先跑一次）。"
                : "§8自动指令已关闭。");
        }

        ApplyAutomation();
    }

    /// <summary>
    /// 设置某功能的“过程日志”开关（2026-10-20 用户要求：开关控制日志的<b>发出</b>）。
    /// <para>
    /// 已挂载的 Bot 立刻跟着变；尚未挂载（没连上/功能关着）时只记在会话里，
    /// 之后 Bot 新建或重连认领时会按这里再刷一次。
    /// 状态提示（“已开启/已就绪/需要××”）不走 <c>LogProcess</c>，关掉日志后仍会显示。
    /// </para>
    /// </summary>
    public void SetFeatureLog(string key, bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(key);

        lock (_gate)
        {
            _featureLogs[key] = enabled;

            ChatBot? bot = key switch
            {
                FeatureLogKeys.Attack => _attackBot,
                FeatureLogKeys.Mouse => _mouseBot,
                FeatureLogKeys.Fishing => _fishingBot,
                FeatureLogKeys.Refill => _autoRefillBot,
                FeatureLogKeys.Walk => _walkBot,
                FeatureLogKeys.View => _viewBot,
                _ => null, // reconnect：没有 Bot，日志点自己查开关；自动指令改成了每条指令自己的开关（见 AutoCommandBot）
            };

            if (bot is not null)
                bot.ProcessLogEnabled = enabled;
        }
    }

    /// <summary>该功能的过程日志是否要发出（缺键 = 开）。</summary>
    private bool IsFeatureLogEnabled(string key)
    {
        lock (_gate)
            return !_featureLogs.TryGetValue(key, out bool enabled) || enabled;
    }

    /// <summary>
    /// 设置服务器信息过滤（2026-10-04 用户需求）：全屏蔽 / 只屏蔽玩家消息 / 只显示或只屏蔽指定前缀。
    /// 前缀支持列表（逗号等分隔，命中任一项）。只拦"服务器发来的聊天/系统消息"
    /// （MCC 的 <c>Log.Chat</c> 频道），连接提示与 Bot 日志不受影响；
    /// 已连接时改，立即对当前连接生效（<see cref="ServerFilterLogger"/> 持有模式）。
    /// </summary>
    public void ConfigureServerFilter(ServerFilterMode mode, string showPrefix, string blockPrefix)
    {
        if ((int)mode < (int)ServerFilterMode.None || (int)mode > (int)ServerFilterMode.BlockPrefix)
            mode = ServerFilterMode.None;

        showPrefix = showPrefix ?? string.Empty;
        blockPrefix = blockPrefix ?? string.Empty;

        bool changed;
        lock (_gate)
        {
            changed = _serverFilterMode != mode;
            _serverFilterMode = mode;
            _serverFilterShowPrefix = showPrefix;
            _serverFilterBlockPrefix = blockPrefix;
            _serverFilterLogger?.SetFilter(mode, showPrefix, blockPrefix);
        }

        // 只在模式变化时说话：前缀是逐字符输入的，跟着敲会刷屏
        if (changed)
        {
            LogUi(mode switch
            {
                ServerFilterMode.BlockAll =>
                    "§a服务器信息过滤：全屏蔽（服务器消息不再进日志）。",
                ServerFilterMode.BlockPlayerChat =>
                    "§a服务器信息过滤：只屏蔽玩家消息（<名字> 开头），系统消息照常显示。",
                ServerFilterMode.PrefixWhitelist =>
                    $"§a服务器信息过滤：只显示以「{showPrefix}」开头的消息（多个前缀用逗号或换行分隔，为空等于不过滤）。",
                ServerFilterMode.BlockPrefix =>
                    $"§a服务器信息过滤：只屏蔽以「{blockPrefix}」开头的消息（多个前缀用逗号或换行分隔，为空等于全显示）。",
                _ => "§8服务器信息过滤：不过滤（全部显示）。",
            });
        }

        // 不调 ApplyAutomation：这是显示层过滤，与 Bot 挂载无关
    }

    /// <summary>
    /// 设置断线自动重连参数。运行中的重连循环会实时读这份配置，所以：
    /// 关掉开关会立刻停止正在进行的重连（并打一行说明，避免"关了还在连"的错觉）。
    /// </summary>
    public void ConfigureReconnect(ReconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        bool wasEnabled;
        bool wasPending;
        lock (_gate)
        {
            wasEnabled = _reconnectOptions.Enabled;
            wasPending = _reconnectPending;
            _reconnectOptions = options;
        }

        if (wasEnabled && !options.Enabled && wasPending)
            LogUi("§e自动重连已关闭，正在进行的重连已停止。");
    }

    /// <summary>按当前开关同步自动化 Bot；未连接时只记状态，连接成功后会再执行一次。</summary>
    private void ApplyAutomation()
    {
        McClient? client;
        lock (_gate)
        {
            client = _client;
            // _gameJoined 必须为真：MCC 的 Login() 在“登录成功”就返回，此时服务器还在
            // Configuration 阶段，这时挂载 Bot 会发出 Play 阶段的数据包，
            // 服务器会按 “Received unknown packet id …” 直接踢人（偶发连不上的根因）。
            if (_state != MCCConnectionState.Connected || client is null || !_gameJoined)
                return;
        }

        EnqueueBotOp(client, SyncAutomationBots);
    }

    private void SyncAutomationBots(McClient client)
    {
        SyncAttack(client);
        SyncMouse(client);
        SyncFishing(client);
        SyncAutoRefill(client);
        SyncWalk(client);
        SyncView(client);
        SyncAutoCommands(client);
        SyncWindowBridge(client);
    }

    /// <summary>
    /// 容器窗口桥：**连上就挂着**（和视角 Bot 一样没有开关）——服务器什么时候开窗口不由我们决定，
    /// 挂晚了第一个窗口就漏了。窗口内容只是转发给界面，挂机时多一个 Bot 不产生任何动作。
    /// </summary>
    private void SyncWindowBridge(McClient client)
    {
        WindowBridgeBot? existing = client.GetLoadedChatBots().OfType<WindowBridgeBot>().FirstOrDefault();

        if (existing is null)
        {
            existing = new WindowBridgeBot();
            client.BotLoad(existing);
        }

        // 幂等挂事件：重连后 MCC 会把旧实例恢复进新连接（botsOnHold），这里认领同一个实例
        existing.WindowChanged -= OnWindowChanged;
        existing.WindowClosed -= OnWindowClosed;
        existing.WindowChanged += OnWindowChanged;
        existing.WindowClosed += OnWindowClosed;

        bool watchPlayer;
        lock (_gate)
        {
            _windowBot = existing;
            watchPlayer = _windowWatchPlayer;
        }

        // 界面开着背包面板时（比如断线自动重连后），把状态交给刚挂上的 Bot 继续推
        existing.WatchPlayerInventory(watchPlayer);
    }

    private void OnWindowChanged(MccWindowInfo info) => WindowChanged?.Invoke(info);

    private void OnWindowClosed(int windowId) => WindowClosed?.Invoke(windowId);

    /// <summary>
    /// 开 / 收"玩家背包"面板。只由界面调用：打开后把玩家背包（窗口 0）当普通窗口内容推过来
    /// （<see cref="WindowChanged"/>，<c>Player</c> 为 true），收起后停止推。
    /// 默认是关着的：没人看的时候不该为背包变动往界面发消息。返回是否已投递（没连上时返回 false）。
    /// </summary>
    public bool WatchPlayerInventory(bool watch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WindowBridgeBot? bot;
        lock (_gate)
        {
            _windowWatchPlayer = watch;
            bot = _windowBot;
        }

        if (bot is null)
            return false;

        bot.WatchPlayerInventory(watch);
        return true;
    }

    /// <summary>
    /// 点容器里的一个格子（或关掉窗口）。返回是否已投递给 MCC。
    /// 只由界面调用：MCC 的 <c>DoWindowAction</c> 会自己算 stateId 和 changed slots，
    /// MCCX 不碰协议，也不做节流（服务器对点击有它自己的规则）。
    /// </summary>
    public bool WindowClick(MccWindowClick click)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WindowBridgeBot? bot;
        bool connected;
        lock (_gate)
        {
            bot = _windowBot;
            connected = _state == MCCConnectionState.Connected;
        }

        if (!connected || bot is null)
        {
            LogUi("§8当前未连接，容器操作已忽略。");
            return false;
        }

        bool close = string.Equals(click.Action, MccWindowAction.Close, StringComparison.Ordinal);
        bool ok = bot.Click(click.WindowId, click.Slot, click.Action);
        if (!ok && !close)
            LogUi($"§e容器窗口 #{click.WindowId} 的这次点击没生效（窗口可能已经被服务器关掉了）。");

        return ok;
    }

    /// <summary>
    /// 视角 Bot：<b>连上就挂着</b>（2026-10-04 起没有开关）：进服沿用服务器记住的朝向、本地不存视角
    /// （只有进服头 10 秒的「纠正包守卫」会有限度钉回，见 <see cref="ViewControlBot"/>），
    /// 「视角移动」按钮全靠它生效——卸了按钮就失灵，所以每次连接都确保挂载/认领。
    /// </summary>
    private void SyncView(McClient client)
    {
        ViewControlBot? bot;
        lock (_gate)
        {
            bot = _viewBot;
        }

        if (bot is not null)
            return; // 已挂载：没有开关/参数需要同步

        // 重连场景：上个连接的视角 Bot 会被 MCC 经静态 botsOnHold 恢复进新连接
        //（OnGameJoined 时它已经在跑），此时必须"认领"那个已加载的实例，
        // 否则会同时挂两个视角 Bot（重复持有，日志也会重复）。
        ViewControlBot? existing = client.GetLoadedChatBots().OfType<ViewControlBot>()
            .FirstOrDefault(b => b.AccountId == _accountId);

        if (existing is not null)
        {
            existing.ProcessLogEnabled = IsFeatureLogEnabled(FeatureLogKeys.View);

            // 旧实例不会重跑 Initialize，这里补一条状态日志（重连进服的可见反馈）
            existing.AnnounceStatus();
            lock (_gate)
            {
                _viewBot = existing;
            }

            return;
        }

        ViewControlBot created = new(_accountId);
        created.ProcessLogEnabled = IsFeatureLogEnabled(FeatureLogKeys.View);
        client.BotLoad(created);
        lock (_gate)
        {
            _viewBot = created;
        }
    }

    /// <summary>
    /// 视角移动（需求 4）：把视角立刻转向指定方向——Bot 内进入保持期（每 tick 重写直到钉死朝向），
    /// 不是发一次包就完。没进服时写一行提示（不抛异常，按钮点了要有反馈）。
    /// </summary>
    public void LookAt(MccLookDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ViewControlBot? bot;
        AutoWalkBot? walkBot;
        lock (_gate)
        {
            bot = _viewBot;
            walkBot = _walkBot;
        }

        if (bot is null)
        {
            LogUi("§e视角移动需要先进服（当前还没进入游戏）。");
            return;
        }

        bot.LookTo(direction);

        // 视角点击要立刻改变行进方向：把按钮方向作为「方向锚点」交给行走 Bot——
        // 段末/看门狗重规划一律按锚点走，绝不读可能被绕障路点带偏的 GetYaw()
        // （2026-10-04 G7 实测根因：北段绕障收尾 307° 被继承成新方向，轨迹漂向东南）。
        // 同时掐掉在途路径（每 tick 压朝向的写手），否则"点向北，走的和看的都还是南"。
        // 抬头/低头不改水平朝向：行走不掐、锚点不动。
        float? targetYaw = direction switch
        {
            MccLookDirection.East => 270f,
            MccLookDirection.South => 0f,
            MccLookDirection.West => 90f,
            MccLookDirection.North => 180f,
            _ => null,
        };

        if (targetYaw is { } yaw)
            walkBot?.RequestReplan(yaw);
    }

    /// <summary>
    /// 自动化 Bot 挂载通用逻辑（2026-10-04 修复重连重复挂载/僵尸挂载）：
    /// <para>
    /// 断线时 MCC 把 Bot 放进静态 botsOnHold（不走 OnUnload），新连接构造时又原样恢复挂载，
    /// 而会话侧的 bot 字段在断开时已置空——老逻辑「开着且字段空 → 新建」会在恢复实例之外再挂
    /// 一个（挂两个 = 双倍砍怪/双开钓鱼），「关着且字段空 → 不动」会让恢复的旧实例照跑（关不掉）。
    /// </para>
    /// <para>
    /// 认领/清退：开着 → 优先认领已加载的同型实例（没有才新建），并执行
    /// <paramref name="configure"/> 刷参数（认领来的实例参数可能是断线前的旧值）；
    /// 关着 → 卸载同型实例。返回值直接写回会话字段（null = 未挂载）。
    /// </para>
    /// <para>
    /// <paramref name="featureLogKey"/> 非空时，新建/认领的实例都会按会话里的日志开关刷
    /// <c>ProcessLogEnabled</c>——新建的要在 <c>BotLoad</c> <b>之前</b>刷好，
    /// 否则 Initialize 里那批过程日志会照发。
    /// </para>
    /// </summary>
    private T? SyncBot<T>(McClient client, bool enabled, Func<T> create, Action<T>? configure,
        string? featureLogKey = null) where T : ChatBot
    {
        T? existing = client.GetLoadedChatBots().OfType<T>().FirstOrDefault();

        if (enabled)
        {
            if (existing is null)
            {
                existing = create();
                if (featureLogKey is not null)
                    existing.ProcessLogEnabled = IsFeatureLogEnabled(featureLogKey);
                client.BotLoad(existing);
            }
            else if (featureLogKey is not null)
            {
                existing.ProcessLogEnabled = IsFeatureLogEnabled(featureLogKey);
            }

            configure?.Invoke(existing);
            return existing;
        }

        if (existing is not null)
            client.BotUnLoad(existing);

        return null;
    }

    private void SyncAttack(McClient client)
    {
        bool enabled;
        AttackOptions options;

        lock (_gate)
        {
            enabled = _attackEnabled;
            options = _attackOptions;
        }

        CustomAutoAttackBot? bot = SyncBot<CustomAutoAttackBot>(client, enabled,
            create: () => new CustomAutoAttackBot(options),
            configure: b => b.Options = options,
            featureLogKey: FeatureLogKeys.Attack);

        lock (_gate)
        {
            _attackBot = bot;
        }
    }

    private void SyncMouse(McClient client)
    {
        bool enabled;
        MouseOptions options;

        lock (_gate)
        {
            enabled = _mouseEnabled;
            options = _mouseOptions;
        }

        MouseControlBot? bot = SyncBot<MouseControlBot>(client, enabled,
            create: () => new MouseControlBot(options),
            configure: b => b.Options = options,
            featureLogKey: FeatureLogKeys.Mouse);

        lock (_gate)
        {
            _mouseBot = bot;
        }
    }

    private void SyncFishing(McClient client)
    {
        bool enabled;
        FishingOptions options;

        lock (_gate)
        {
            enabled = _fishingEnabled;
            options = _fishingOptions;
        }

        // 参数写 MCC 的静态配置：AutoFishing.Config 是 static，改完对已挂/将挂的 Bot 即时生效。
        // 无论开关状态都下发，保证下次开启时用的就是界面参数（默认值 = MCC 配置默认值）。
        ApplyFishingConfig(options);

        MinecraftClient.ChatBots.AutoFishing? bot = SyncBot<MinecraftClient.ChatBots.AutoFishing>(client, enabled,
            create: () => new MinecraftClient.ChatBots.AutoFishing(),
            configure: null,
            featureLogKey: FeatureLogKeys.Fishing);

        lock (_gate)
        {
            _fishingBot = bot;
        }
    }

    /// <summary>把钓鱼参数写进 MCC 内置 AutoFishing 的静态配置（对应字段见 <see cref="FishingOptions"/>）。</summary>
    private static void ApplyFishingConfig(FishingOptions options)
    {
        MinecraftClient.ChatBots.AutoFishing.Config.Enable_Sound_Detection = options.SoundDetection;
        MinecraftClient.ChatBots.AutoFishing.Config.Enable_Velocity_Detection = options.VelocityDetection;
        MinecraftClient.ChatBots.AutoFishing.Config.Fishing_Timeout = options.TimeoutSeconds;
        MinecraftClient.ChatBots.AutoFishing.Config.Cast_Delay = options.CastDelaySeconds;

        // 2026-10-05 用户要求「MCCX 所有功能都不自动移动视角」：显式关掉 AutoFishing 的移动。
        // 它开着时会走 Movements 列表并 LookAtLocation(facing.yaw)（默认第一条是 12.34），
        // 也就是抛竿前会自己把视角拧一下。默认虽是 false，但显式钉死，免得哪条配置路径把它打开。
        MinecraftClient.ChatBots.AutoFishing.Config.Enable_Move = false;
    }

    private void SyncAutoRefill(McClient client)
    {
        bool enabled;
        lock (_gate)
        {
            enabled = _autoRefillEnabled;
        }

        AutoRefillBot? bot = SyncBot<AutoRefillBot>(client, enabled,
            create: () => new AutoRefillBot(),
            configure: null,
            featureLogKey: FeatureLogKeys.Refill);

        lock (_gate)
        {
            _autoRefillBot = bot;
        }
    }

    /// <summary>
    /// 自动行走 Bot（2026-10-04 需求）：开 = 挂载，关 = 卸载。
    /// <para>
    /// 重连场景必须先"认领/清退"：断线时字段被置空，而上个连接的实例会被 MCC 经静态
    /// botsOnHold 恢复进新连接（进服时它已经在跑）。开着就接着用它，关着就卸掉——
    /// 否则要么同时挂两个行走 Bot 抢寻路，要么关了开关它还在走。
    /// </para>
    /// </summary>
    private void SyncWalk(McClient client)
    {
        bool enabled;
        lock (_gate)
        {
            enabled = _walkEnabled;
        }

        AutoWalkBot? existing = client.GetLoadedChatBots().OfType<AutoWalkBot>()
            .FirstOrDefault(b => b.AccountId == _accountId);

        if (enabled)
        {
            if (existing is null)
            {
                AutoWalkBot created = new(_accountId);
                created.ProcessLogEnabled = IsFeatureLogEnabled(FeatureLogKeys.Walk);
                client.BotLoad(created);
                existing = created;
            }
            else
            {
                // 认领断线前恢复进新连接的旧实例：日志开关可能在这期间被关掉
                existing.ProcessLogEnabled = IsFeatureLogEnabled(FeatureLogKeys.Walk);
            }

            lock (_gate)
            {
                _walkBot = existing;
            }
        }
        else if (existing is not null)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_walkBot, existing))
                    _walkBot = null;
            }

            client.BotUnLoad(existing);
        }
        else
        {
            lock (_gate)
            {
                _walkBot = null;
            }
        }
    }

    /// <summary>
    /// 自动指令 Bot（2026-10-22 需求）：开 = 挂载 / 关 = 卸载。
    /// <para>
    /// 与自动行走一样先"认领/清退"：断线时字段被置空，而上个连接的实例会被 MCC 经静态
    /// botsOnHold 恢复进新连接（进服时它已经在跑）。开着就接着用它、并刷一遍指令表，
    /// 关着就卸掉——否则重连后会同时挂两个 Bot，同一批指令发两遍。
    /// </para>
    /// </summary>
    private void SyncAutoCommands(McClient client)
    {
        bool enabled;
        AutoCommandDefinition[] commands;

        lock (_gate)
        {
            enabled = _autoCommandsEnabled;
            commands = [.. _autoCommands];
        }

        AutoCommandBot? existing = client.GetLoadedChatBots().OfType<AutoCommandBot>()
            .FirstOrDefault(bot => bot.AccountId == _accountId);

        if (enabled)
        {
            if (existing is null)
            {
                AutoCommandBot created = new(_accountId);
                created.SetCommands(commands);
                client.BotLoad(created);

                // ApplyAutomation 只在进服之后才跑，而 MCC 只在"进服那一刻"回调 AfterGameJoined：
                // 用户先连上、再打开开关时新挂的 Bot 收不到，必须补一次，否则要等下次重连才开始执行。
                if (!created.IsJoined)
                    created.NotifyGameJoined();

                existing = created;
            }
            else
            {
                // 认领断线前恢复进新连接的旧实例：指令表可能在这期间改过
                // （过程日志是每条指令自己的开关，在 Bot 里判断，这里不用刷）
                existing.SetCommands(commands);

                if (!existing.IsJoined)
                    existing.NotifyGameJoined();
            }

            lock (_gate)
            {
                _autoCommandBot = existing;
            }
        }
        else if (existing is not null)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_autoCommandBot, existing))
                    _autoCommandBot = null;
            }

            client.BotUnLoad(existing);
        }
        else
        {
            lock (_gate)
            {
                _autoCommandBot = null;
            }
        }
    }

    /// <summary>
    /// Bot 挂载/卸载会阻塞等待 MCC 主线程，必须排队到后台线程串行执行；
    /// 排队后若连接已被更换/断开，则丢弃该操作。
    /// </summary>
    private void EnqueueBotOp(McClient client, Action<McClient> operation)
    {
        lock (_gate)
        {
            _botOps = _botOps.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (_disposed || !ReferenceEquals(_client, client))
                        return;
                }

                try
                {
                    operation(client);
                }
                catch (Exception ex)
                {
                    LogUi($"§c自动化组件操作失败：{ex.Message}");
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 断线（或连接流程结束）后按需启动自动重连循环。任何时刻只会有一个循环在跑。
    /// </summary>
    private void ScheduleReconnect()
    {
        MCCConnectionOptions? options;
        ReconnectOptions rc;

        // 先单独判定“刚取消过对话框”：这笔日志必须在锁外打，LogReceived 的订阅方可能反过来拿锁
        bool suppressedByDialogCancel = false;
        lock (_gate)
        {
            long cancelTick = _dialogCancelTick;
            if (!_disposed && !_reconnectPending && !_userRequestedDisconnect
                && _state == MCCConnectionState.Disconnected
                && cancelTick != long.MinValue
                && Environment.TickCount64 - cancelTick <= DialogCancelReconnectWindowMs)
            {
                _dialogCancelTick = long.MinValue; // 一次性：只压制这一窗口内的这次断开
                suppressedByDialogCancel = true;
            }
        }

        if (suppressedByDialogCancel)
        {
            LogUi("§e这次断开不再自动重连（你刚取消过对话框），点“连接”可重新登录。");
            return;
        }

        lock (_gate)
        {
            if (_disposed || _reconnectPending || _userRequestedDisconnect)
                return;
            if (_state != MCCConnectionState.Disconnected)
                return;

            rc = _reconnectOptions;
            if (!rc.Enabled)
                return;

            options = _lastOptions;
            if (options is null)
                return;

            _reconnectPending = true;
        }

        _ = Task.Run(() => RunReconnectLoopAsync(options, rc));
    }

    /// <summary>
    /// 持续重连直到成功、达上限或被用户取消。
    /// 循环期间会**实时**复查开关：用户中途关掉“自动重连”必须立刻停下
    /// （原实现只在进入循环前看一次 Enabled，关掉后仍会把 50 次试完）。
    /// <para><c>rc.MaxAttempts &lt;= 0</c> 表示无限重连：只由开关/断开/退出终止。</para>
    /// </summary>
    private async Task RunReconnectLoopAsync(MCCConnectionOptions options, ReconnectOptions rc)
    {
        string reason = "连接已断开";

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                if (ReconnectShouldStop(out ReconnectOptions live))
                    return;

                bool unlimited = live.MaxAttempts <= 0;
                string limitText = unlimited ? "无限" : live.MaxAttempts.ToString();
                int delay = AutomationJitter.Apply(live.DelayMs, live.JitterPercent);

                // 过程日志（2026-10-20）：自动重连日志开关关掉时这行不产生。
                // 结果/状态类提示（已连续失败 N 次、已关闭…）不受影响。
                if (IsFeatureLogEnabled(FeatureLogKeys.Reconnect))
                    LogUi($"§e{reason}，{delay / 1000.0:0.#} 秒后自动重连（第 {attempt}/{limitText} 次）…");

                await Task.Delay(delay).ConfigureAwait(false);

                if (ReconnectShouldStop(out _))
                    return;

                await ConnectAsyncCore(options, isAutoReconnect: true, CancellationToken.None)
                    .ConfigureAwait(false);

                if (State == MCCConnectionState.Connected)
                    return; // 重连成功

                if (State != MCCConnectionState.Disconnected)
                    return; // 用户接管了连接流程

                if (ReconnectShouldStop(out live))
                    return;

                if (live.MaxAttempts > 0 && attempt >= live.MaxAttempts)
                {
                    LogUi($"§c自动重连已连续失败 {attempt} 次，停止重试。可手动点击“连接”重试。");
                    return;
                }

                reason = "连接失败";
            }
        }
        catch (Exception ex)
        {
            LogUi($"§c自动重连出错：{ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _reconnectPending = false;
            }
        }
    }

    /// <summary>
    /// 重连循环是否该停。顺带把**当前**的重连参数取出来（用户随时可能改次数/间隔/开关）。
    /// 关掉开关时会打一行日志，避免用户以为"关了还在连"。
    /// </summary>
    private bool ReconnectShouldStop(out ReconnectOptions current)
    {
        lock (_gate)
        {
            current = _reconnectOptions;

            if (_disposed || _userRequestedDisconnect)
                return true;

            if (!current.Enabled)
                return true;
        }

        return false;
    }

    #endregion

    #region 服务器对话框（Dialog，2026-10-03 用户需求：密码要能用界面输入框填）

    /// <summary>
    /// 挂上 MCC 的对话框事件。登录过程中就已经弹出的那一条，挂载时还没收到事件，
    /// 这里按"当前在场"补发一次，否则密码框会漏弹。
    /// </summary>
    private void HookDialogs(McClient client)
    {
        UnhookDialogs();

        DialogManager manager = client.Dialogs;
        lock (_gate)
        {
            _dialogManager = manager;
        }

        manager.DialogShown += OnDialogShown;
        manager.DialogCleared += OnDialogCleared;

        if (manager.Current is { } current)
            OnDialogShown(current);
    }

    /// <summary>摘掉对话框事件（断开/关会话时），避免断线后还往界面推对话框。</summary>
    private void UnhookDialogs()
    {
        DialogManager? manager;
        lock (_gate)
        {
            manager = _dialogManager;
            _dialogManager = null;
        }

        if (manager is null)
            return;

        manager.DialogShown -= OnDialogShown;
        manager.DialogCleared -= OnDialogCleared;
    }

    private void OnDialogShown(DialogInstance instance)
    {
        MccDialogInfo info = BuildDialogInfo(instance);

        // 接收：MCC 自己那行 “Server showed custom dialog” 只证明收到包，
        // 这行证明事件已到达会话层——控制台据此可分辨“收到了但没弹”还是“压根没收到”
        LogUi($"§e收到服务器对话框：「{info.Title}」（编号 {info.Revision}）");

        // 留一份给 SubmitDialog 认“取消”类动作：点它也要压掉紧随其后的自动重连
        lock (_gate)
        {
            _lastDialog = info;
        }

        Action<MccDialogInfo>? handler = DialogRequested;
        if (handler is null)
            return;

        // 控制台同时会渲染一遍这个对话框（MCC 自己的日志），这里只做界面入口
        handler(info);
    }

    private void OnDialogCleared(int revision)
    {
        lock (_gate)
        {
            if (_lastDialog?.Revision == revision)
                _lastDialog = null;
        }

        DialogClosed?.Invoke(revision);
    }

    /// <summary>服务器命令列表更新（ConsoleIO 静态事件转发给界面层）。可能从任意线程触发。</summary>
    private void OnServerCommandsUpdated(IReadOnlyList<string> commands)
    {
        if (_disposed)
            return;

        ServerCommandsChanged?.Invoke(commands);
    }

    /// <summary>
    /// 把 MCC 的对话框模型投影成与 MCC 无关的纯数据（可直接过 IPC）。
    ///
    /// 展示用文本一律过 <see cref="ChatBot.GetVerbatim"/>：服务器下发的标题/正文/标签
    /// 带 <c>§6</c> 这类颜色码，直接显示会变成"乱码"（2026-10-03 用户截图反馈）。
    /// **取值（<c>InitialValue</c>）不过滤** —— 那是用户输入的内容，可能是密码，
    /// 里面的 § 是数据本身，洗掉就改了密码。
    /// </summary>
    private static MccDialogInfo BuildDialogInfo(DialogInstance instance)
    {
        DialogDefinition definition = instance.Definition;

        MccDialogInfo info = new()
        {
            Revision = instance.Revision,
            Title = ChatBot.GetVerbatim(definition.DisplayTitle()),
            Body = string.Join("\n", definition.Body
                .Select(static body => ChatBot.GetVerbatim(body.Text))
                .Where(static text => !string.IsNullOrWhiteSpace(text))),
        };

        foreach (DialogInput input in definition.Inputs)
        {
            instance.Values.TryGetValue(input.Key, out string? value);

            info.Inputs.Add(new MccDialogField
            {
                Key = input.Key,
                Label = input.LabelVisible ? ChatBot.GetVerbatim(input.Label) : string.Empty,
                Kind = input.Kind switch
                {
                    DialogInputKind.Text => MccDialogKind.Text,
                    DialogInputKind.Boolean => MccDialogKind.Boolean,
                    DialogInputKind.SingleOption => MccDialogKind.Option,
                    DialogInputKind.NumberRange => MccDialogKind.Number,
                    _ => MccDialogKind.Unknown,
                },
                InitialValue = value ?? input.InitialValue,
                MaxLength = input.MaxLength,
                Multiline = input.Multiline,
                Options = input.Options is null ? null : [.. input.Options.Select(static option => option.Id)],
                Start = input.Start,
                End = input.End,
                IsSecret = LooksLikeSecret(input),
            });
        }

        foreach (DialogButton button in definition.Actions)
        {
            string label = ChatBot.GetVerbatim(button.Label);

            info.Actions.Add(new MccDialogAction
            {
                Index = button.Index,
                Label = label,
                IsCancel = button.IsCancel || IsCancelLabel(label),
            });
        }

        return info;
    }

    /// <summary>按键名/标签猜这是不是密码类输入（MCC 的对话框没有"密码"类型，只是普通文本）。</summary>
    private static bool LooksLikeSecret(DialogInput input)
    {
        string text = $"{input.Key} {input.Label}";
        return text.Contains("password", StringComparison.OrdinalIgnoreCase)
               || text.Contains("passwd", StringComparison.OrdinalIgnoreCase)
               || text.Contains("pwd", StringComparison.OrdinalIgnoreCase)
               || text.Contains("密码", StringComparison.Ordinal)
               || text.Contains("口令", StringComparison.Ordinal);
    }

    private static bool IsCancelLabel(string label) =>
        label.Equals("cancel", StringComparison.OrdinalIgnoreCase)
        || label.Equals("abort", StringComparison.OrdinalIgnoreCase)
        || label.Contains("取消", StringComparison.Ordinal);

    /// <summary>
    /// 把界面填好的取值写回 MCC 并点击动作。**不走聊天/命令通道**：
    /// 密码既不会被打进聊天历史，也不会有被当成公屏消息发出去的风险。
    /// 点的是“取消”类动作时，紧随其后的那次断开不自动重连（与关窗取消同一套语义）。
    /// </summary>
    public bool SubmitDialog(IReadOnlyDictionary<string, string> values, int actionIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(values);

        bool cancelAction;
        lock (_gate)
        {
            cancelAction = _lastDialog?.Actions
                .FirstOrDefault(action => action.Index == actionIndex)?.IsCancel == true;
        }

        DialogManager? dialogs;
        lock (_gate)
        {
            dialogs = _dialogManager;
        }

        if (dialogs is null)
        {
            LogUi("§c当前没有可交互的对话框（连接可能已断开）。");
            return false;
        }

        DialogInstance? current = dialogs.Current;
        if (current is null)
        {
            LogUi("§e服务器已经关闭了这个对话框，输入未提交。");
            return false;
        }

        // 只回写界面给到的键；写入失败的报错文案里只有键名与长度，不含取值
        foreach (DialogInput input in current.Definition.Inputs)
        {
            if (!values.TryGetValue(input.Key, out string? value))
                continue;

            DialogActionResult result = dialogs.SetInput(input.Key, value);
            if (!result.Success)
            {
                LogUi($"§e对话框输入 {input.Key} 写入失败：{result.Message}");
                return false;
            }
        }

        DialogActionResult click = dialogs.Click(actionIndex);
        if (!click.Success)
        {
            LogUi($"§e对话框动作执行失败：{click.Message}");
            return false;
        }

        if (cancelAction)
        {
            lock (_gate)
            {
                _dialogCancelTick = Environment.TickCount64;
            }
        }

        return true;
    }

    /// <summary>
    /// 取消服务器弹出的对话框。没有进行中的对话框时安静返回 false（界面关窗兜底用）。
    /// 取消成功会记一笔“下一次断开不自动重连”：服务器对取消的回应通常就是踢人，
    /// 不压制的话会陷入“取消 → 断开 → 自动重连 → 又弹框”的死循环。
    /// </summary>
    public bool CancelDialog()
    {
        if (_disposed)
            return false;

        DialogManager? dialogs;
        lock (_gate)
        {
            dialogs = _dialogManager;
        }

        if (dialogs is null || dialogs.Current is null)
            return false;

        bool cancelled = dialogs.Cancel().Success;
        if (!cancelled)
        {
            // 定义上不允许取消的对话框：退一步只把它从本地关掉，不留半截状态
            cancelled = dialogs.Dismiss().Success;
        }

        if (!cancelled)
            return false;

        lock (_gate)
        {
            _dialogCancelTick = Environment.TickCount64;
        }

        return true;
    }

    #endregion

    #region 内部实现

    private bool TryBeginConnect()
    {
        lock (_gate)
        {
            if (_state != MCCConnectionState.Disconnected)
                return false;

            _state = MCCConnectionState.Connecting;
            _gameJoined = false;
        }

        StateChanged?.Invoke(MCCConnectionState.Connecting);
        return true;
    }

    private void SetState(MCCConnectionState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;

            _state = state;
        }

        StateChanged?.Invoke(state);

        if (state == MCCConnectionState.Disconnected)
        {
            // 断开后面板已经收掉了（界面在 IsConnected 变 false 时收的），重连时不该自己再冒出来
            StopPlayerInventoryWatch();
            ScheduleReconnect();
        }
    }

    /// <summary>断开时把"背包面板开着"的状态清掉，并让 Bot 停下（Bot 可能已经不在了，忽略即可）。</summary>
    private void StopPlayerInventoryWatch()
    {
        WindowBridgeBot? bot;
        lock (_gate)
        {
            if (!_windowWatchPlayer)
                return;

            _windowWatchPlayer = false;
            bot = _windowBot;
        }

        try
        {
            // 只清状态不重发服务器窗口：界面这时候正因为断开在收面板
            bot?.StopPlayerInventoryWatch();
        }
        catch
        {
            // Bot 正在拆 / 会话已经没了：没有什么要收的
        }
    }

    private void OnMonitorJoined()
    {
        lock (_gate)
        {
            // MCC 在“登录 + 重生/维度切换”时会多次回调 AfterGameJoined，只上报第一次
            if (_gameJoined)
                return;

            _gameJoined = true;
        }

        LogUi("§8已进入游戏，可以发送聊天与命令。");
        GameJoined?.Invoke();

        // 服务器配置阶段到此才结束，此时才允许挂载自动化 Bot（ApplyAutomation 依赖 _gameJoined）
        ApplyAutomation();
    }

    private void OnMonitorDisconnected(ChatBot.DisconnectReason reason, string message)
    {
        lock (_gate)
        {
            // MCC 断线可能被多次回调，只处理第一次，避免重复日志与重复重连调度
            if (_state == MCCConnectionState.Disconnected)
                return;

            _client = null;
            _monitor = null;
            _gameJoined = false;
            _attackBot = null;
            _mouseBot = null;
            _fishingBot = null;
            _autoRefillBot = null;
            _walkBot = null;
            _viewBot = null;
            _autoCommandBot = null;
            UnhookDialogs();
        }

        string detail = string.IsNullOrWhiteSpace(message) ? reason.ToString() : message;
        LogUi($"§8连接已断开（{detail}）。");

        // 服务器不认识我们发出的数据包 id：只有客户端版本和服务器实际版本对不上才会出现
        if (detail.Contains("unknown packet id", StringComparison.OrdinalIgnoreCase))
        {
            LogUi("§e提示：MCCX 的协议版本与服务器实际版本不一致（Ping 结果只是服务器自己上报的），" +
                  "可以在“版本”框手动指定服务器真实版本后重连。");
        }

        // 服务器要求客户端装 Fabric 模组才准进（握手成功后被服务端踢出）：
        // 这不是 MCCX 断线，是服务器侧的准入规则——MCCX 是控制台客户端，带不了 Fabric Loader / Fabric API。
        if (detail.Contains("Fabric", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("installed on your client", StringComparison.OrdinalIgnoreCase))
        {
            LogUi("§e提示：这台服务器要求客户端安装 Fabric Loader / Fabric API（模组）才准入，" +
                  "MCCX 是控制台客户端、带不了 Fabric 模组，所以登录成功后立刻被服务器踢下线。" +
                  "要进这个服：让服主把该模组改成非必需（或关掉它的客户端检查），否则只能用带 Fabric 的普通客户端进。");
        }

        SetState(MCCConnectionState.Disconnected);
    }

    /// <summary>返回 false 表示超时，连接状态不可用。</summary>
    private static bool LoadMonitorBot(McClient client, MonitorBot monitor)
    {
        Task loadTask = Task.Run(() => client.BotLoad(monitor));
        return loadTask.Wait(BotLoadTimeoutMs);
    }

    private void OnBackendOutput(string text)
    {
        lock (_gate)
        {
            if (_capturing)
                _outputDuringConnect.Add(text);
        }

        LogReceived?.Invoke(text);
    }

    private void StartOutputCapture()
    {
        lock (_gate)
        {
            _outputDuringConnect.Clear();
            _capturing = true;
        }
    }

    private List<string> StopOutputCapture()
    {
        lock (_gate)
        {
            _capturing = false;
            return [.. _outputDuringConnect];
        }
    }

    /// <summary>MCC 进入游戏时必定写出的文案，用它判定登录成功。</summary>
    private static string BuildJoinedMarker()
    {
        return string.Format(
            Translations.mcc_joined,
            Settings.Config.Main.Advanced.InternalCmdChar.ToLogString());
    }

    /// <summary>识别以内部命令符开头的命令名；聊天文本返回 null。</summary>
    private static string? GetInternalCommandName(string text)
    {
        if (text.Length == 0 || text[0] != '/')
            return null;

        // "//" 开头不算内部命令（2026-10-16 移除 "//xxx=给服务器发 /xxx" 的转义后仍保持这条：
        // 不在这里拦截，原样交给 MCC 处理，免得误伤 // 开头的普通输入）
        if (text.Length > 1 && text[1] == '/')
            return null;

        int end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        return text[1..end].ToLowerInvariant();
    }

    private void LogUi(string text) => LogReceived?.Invoke(text);

    #endregion
}
