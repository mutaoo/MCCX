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
    public async Task ConnectAsync(MccConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        if (!TryBeginConnect())
        {
            LogUi("§e当前已处于连接流程中，忽略本次请求。");
            return;
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

            LogUi("§a连接成功。");
            SetState(MccConnectionState.Connected);
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

    /// <summary>主动断开（在后台线程执行，避免阻塞 UI）。</summary>
    public void Disconnect()
    {
        McClient? client;
        lock (_gate)
        {
            if (_state != MccConnectionState.Connected || _client is null)
                return;

            _state = MccConnectionState.Disconnecting;
            client = _client;
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
    }

    private void OnMonitorJoined()
    {
        _gameJoined = true;
        LogUi("§8已进入游戏，可以发送聊天与命令。");
        GameJoined?.Invoke();
    }

    private void OnMonitorDisconnected(ChatBot.DisconnectReason reason, string message)
    {
        lock (_gate)
        {
            _client = null;
            _monitor = null;
            _gameJoined = false;
        }

        string detail = string.IsNullOrWhiteSpace(message) ? reason.ToString() : message;
        LogUi($"§8连接已断开（{detail}）。");
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
