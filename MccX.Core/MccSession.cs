using MinecraftClient;
using MinecraftClient.Protocol;
using MinecraftClient.Protocol.Handlers.Forge;
using MinecraftClient.Protocol.Session;
using MinecraftClient.Scripting;

namespace MccX.Core;

/// <summary>
/// 一个离线挂机会话：封装 MCC 的启动链路（版本探测 → 构造 McClient → 挂载监视 Bot），
/// 并把 MCC 的日志/输入/连接状态以事件形式暴露给 UI。
///
/// 线程模型：所有 MCC 相关工作都在后台线程执行，事件可能从任意线程抛出，
/// 订阅方（ViewModel）负责切换到 UI 线程。
/// </summary>
public sealed class MccSession : IDisposable
{
    /// <summary>
    /// 会拦截的 MCC 内部命令：这些命令会直接杀进程或另起一个 MCC 客户端，
    /// 在 GUI 里必须改用界面按钮。
    /// </summary>
    private static readonly string[] BlockedInternalCommands =
    [
        "exit", "quit", "reco", "reconnect", "restart", "connect", "reload",
    ];

    /// <summary>BotLoad 需要 MCC 主线程响应，超过该时间即认为挂了。</summary>
    private const int BotLoadTimeoutMs = 15000;

    private readonly MccUiBackend _backend;
    private readonly object _gate = new();
    private readonly List<string> _outputDuringConnect = [];

    private MccConnectionState _state = MccConnectionState.Disconnected;
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
    private ReconnectOptions _reconnectOptions = new();

    private CustomAutoAttackBot? _attackBot;
    private MouseControlBot? _mouseBot;
    private MinecraftClient.ChatBots.AutoFishing? _fishingBot;

    /// <summary>Bot 挂载/卸载串行队列：避免开关连点导致加载与卸载交错。</summary>
    private Task _botOps = Task.CompletedTask;

    // ---- 断线自动重连（需求 3.2）----
    private MccConnectionOptions? _lastOptions;
    private MccConnectionOptions? _pendingOptions;
    private bool _reconnectPending;
    private bool _userRequestedDisconnect;

    /// <summary>使用 <see cref="MccRuntime"/> 已初始化的默认后端。</summary>
    public MccSession() : this(MccRuntime.Backend)
    {
    }

    public MccSession(MccUiBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _backend.OutputReceived += OnBackendOutput;
    }

    /// <summary>当前连接状态（线程安全）。</summary>
    public MccConnectionState State
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
    public event Action<MccConnectionState>? StateChanged;

    /// <summary>
    /// 服务器完全进入游戏（配置阶段结束、可以发聊天）。
    /// 注意：<see cref="StateChanged"/> 变为 Connected 只代表登录成功，比这个事件早。
    /// 可能从任意线程触发。
    /// </summary>
    public event Action? GameJoined;

    /// <summary>是否已完全进入游戏（可发聊天）。断开后自动回到 false。</summary>
    public bool IsGameJoined => _gameJoined;

    /// <summary>
    /// 连接服务器（同步阻塞的 MCC 登录流程会在后台线程里跑完）。
    /// 返回时状态要么是 Connected，要么已回到 Disconnected。
    /// </summary>
    public Task ConnectAsync(MccConnectionOptions options, CancellationToken cancellationToken = default)
        => ConnectAsyncCore(options, isAutoReconnect: false, cancellationToken);

    /// <param name="isAutoReconnect">
    /// true 表示由自动重连循环发起：失败后不清空 <c>_lastOptions</c>，也不允许触发新的重连循环。
    /// </param>
    private async Task ConnectAsyncCore(
        MccConnectionOptions options,
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
                SetState(MccConnectionState.Disconnected);
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
                    SetState(MccConnectionState.Disconnected);
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
                    SetState(MccConnectionState.Disconnected);
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
                SetState(MccConnectionState.Disconnected);
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
                SetState(MccConnectionState.Disconnected);
                return;
            }

            // 4. 挂载监视 Bot，上报断线
            MonitorBot monitor = new();
            monitor.Joined += OnMonitorJoined;
            monitor.Disconnected += OnMonitorDisconnected;
            _client = client;
            _monitor = monitor;

            bool loaded = await Task.Run(() => LoadMonitorBot(client, monitor)).ConfigureAwait(false);
            if (!loaded)
            {
                _client = null;
                _monitor = null;
                LogUi("§c监视 Bot 挂载超时，连接状态不可靠，已放弃本次连接。");
                SetState(MccConnectionState.Disconnected);
                return;
            }

            lock (_gate)
            {
                _lastOptions = options;
            }

            LogUi("§a连接成功。");
            SetState(MccConnectionState.Connected);

            // 按当前开关重新挂载自动化 Bot（首次连接与自动重连后都会执行）
            ApplyAutomation();
        }
        catch (OperationCanceledException)
        {
            SetState(MccConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            LogUi($"§c连接失败：{ex.Message}");
            SetState(MccConnectionState.Disconnected);
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

        if (State != MccConnectionState.Connected || _client is null)
        {
            LogUi("§8当前未连接，输入已忽略。");
            return false;
        }

        string? internalCommand = GetInternalCommandName(text);
        if (internalCommand is not null && Array.IndexOf(BlockedInternalCommands, internalCommand) >= 0)
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
            cancelledReconnect = _reconnectPending && _state == MccConnectionState.Disconnected;
            if (cancelledReconnect)
            {
                _userRequestedDisconnect = true;
                client = null!; // 取消分支随后立即 return，不会用到该值
            }
            else
            {
                if (_state != MccConnectionState.Connected || _client is null)
                    return;

                _state = MccConnectionState.Disconnecting;
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

        StateChanged?.Invoke(MccConnectionState.Disconnecting);

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
                }

                SetState(MccConnectionState.Disconnected);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _backend.OutputReceived -= OnBackendOutput;

        McClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            _monitor = null;
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
        lock (_gate)
        {
            changed = _attackEnabled != enabled;
            _attackEnabled = enabled;
            _attackOptions = options;
        }

        if (changed)
            LogUi(enabled ? "§a自动砍怪已开启，进入游戏后生效。" : "§8自动砍怪已关闭。");

        ApplyAutomation();
    }

    /// <summary>设置鼠标按键控制（长按/间隔点击/间隔长按）。</summary>
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
            LogUi(enabled ? "§a鼠标控制已开启，进入游戏后生效。" : "§8鼠标控制已关闭。");

        ApplyAutomation();
    }

    /// <summary>设置自动钓鱼（复用 MCC 内置 AutoFishing Bot）。</summary>
    public void ConfigureFishing(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            changed = _fishingEnabled != enabled;
            _fishingEnabled = enabled;
        }

        if (changed)
            LogUi(enabled ? "§a自动钓鱼已开启（手中需持有钓竿）。" : "§8自动钓鱼已关闭。");

        ApplyAutomation();
    }

    /// <summary>设置断线自动重连参数（只记录，断线时才生效）。</summary>
    public void ConfigureReconnect(ReconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            _reconnectOptions = options;
        }
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
            if (_state != MccConnectionState.Connected || client is null || !_gameJoined)
                return;
        }

        EnqueueBotOp(client, SyncAutomationBots);
    }

    private void SyncAutomationBots(McClient client)
    {
        SyncAttack(client);
        SyncMouse(client);
        SyncFishing(client);
    }

    private void SyncAttack(McClient client)
    {
        bool enabled;
        AttackOptions options;
        CustomAutoAttackBot? bot;

        lock (_gate)
        {
            enabled = _attackEnabled;
            options = _attackOptions;
            bot = _attackBot;
        }

        if (enabled && bot is null)
        {
            CustomAutoAttackBot created = new(options);
            client.BotLoad(created);
            lock (_gate)
            {
                _attackBot = created;
            }
        }
        else if (!enabled && bot is not null)
        {
            lock (_gate)
            {
                _attackBot = null;
            }

            client.BotUnLoad(bot);
        }
        else if (bot is not null)
        {
            bot.Options = options;
        }
    }

    private void SyncMouse(McClient client)
    {
        bool enabled;
        MouseOptions options;
        MouseControlBot? bot;

        lock (_gate)
        {
            enabled = _mouseEnabled;
            options = _mouseOptions;
            bot = _mouseBot;
        }

        if (enabled && bot is null)
        {
            MouseControlBot created = new(options);
            client.BotLoad(created);
            lock (_gate)
            {
                _mouseBot = created;
            }
        }
        else if (!enabled && bot is not null)
        {
            lock (_gate)
            {
                _mouseBot = null;
            }

            client.BotUnLoad(bot);
        }
        else if (bot is not null)
        {
            bot.Options = options;
        }
    }

    private void SyncFishing(McClient client)
    {
        bool enabled;
        MinecraftClient.ChatBots.AutoFishing? bot;

        lock (_gate)
        {
            enabled = _fishingEnabled;
            bot = _fishingBot;
        }

        if (enabled && bot is null)
        {
            MinecraftClient.ChatBots.AutoFishing created = new();
            client.BotLoad(created);
            lock (_gate)
            {
                _fishingBot = created;
            }
        }
        else if (!enabled && bot is not null)
        {
            lock (_gate)
            {
                _fishingBot = null;
            }

            client.BotUnLoad(bot);
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
        MccConnectionOptions? options;
        ReconnectOptions rc;

        lock (_gate)
        {
            if (_disposed || _reconnectPending || _userRequestedDisconnect)
                return;
            if (_state != MccConnectionState.Disconnected)
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

    /// <summary>持续重连直到成功、达上限或被用户取消。</summary>
    private async Task RunReconnectLoopAsync(MccConnectionOptions options, ReconnectOptions rc)
    {
        string reason = "连接已断开";

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                if (_disposed || _userRequestedDisconnect)
                    return;

                int delay = AutomationJitter.Apply(rc.DelayMs, rc.JitterPercent);
                LogUi($"§e{reason}，{delay / 1000.0:0.#} 秒后自动重连（第 {attempt}/{rc.MaxAttempts} 次）…");

                await Task.Delay(delay).ConfigureAwait(false);

                if (_disposed || _userRequestedDisconnect)
                    return;

                await ConnectAsyncCore(options, isAutoReconnect: true, CancellationToken.None)
                    .ConfigureAwait(false);

                if (State == MccConnectionState.Connected)
                    return; // 重连成功

                if (State != MccConnectionState.Disconnected)
                    return; // 用户接管了连接流程

                if (_disposed || _userRequestedDisconnect)
                    return;

                if (attempt >= rc.MaxAttempts)
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

    #endregion

    #region 内部实现

    private bool TryBeginConnect()
    {
        lock (_gate)
        {
            if (_state != MccConnectionState.Disconnected)
                return false;

            _state = MccConnectionState.Connecting;
            _gameJoined = false;
        }

        StateChanged?.Invoke(MccConnectionState.Connecting);
        return true;
    }

    private void SetState(MccConnectionState state)
    {
        lock (_gate)
        {
            if (_state == state)
                return;

            _state = state;
        }

        StateChanged?.Invoke(state);

        if (state == MccConnectionState.Disconnected)
            ScheduleReconnect();
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
            if (_state == MccConnectionState.Disconnected)
                return;

            _client = null;
            _monitor = null;
            _gameJoined = false;
            _attackBot = null;
            _mouseBot = null;
            _fishingBot = null;
        }

        string detail = string.IsNullOrWhiteSpace(message) ? reason.ToString() : message;
        LogUi($"§8连接已断开（{detail}）。");

        // 服务器不认识我们发出的数据包 id：只有客户端版本和服务器实际版本对不上才会出现
        if (detail.Contains("unknown packet id", StringComparison.OrdinalIgnoreCase))
        {
            LogUi("§e提示：MccX 的协议版本与服务器实际版本不一致（Ping 结果只是服务器自己上报的），" +
                  "可以在“版本”框手动指定服务器真实版本后重连。");
        }

        SetState(MccConnectionState.Disconnected);
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

        // "//xxx" 表示直接把 "/xxx" 发到服务器聊天，不是内部命令
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
