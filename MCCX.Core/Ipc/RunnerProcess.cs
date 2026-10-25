using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using MCCX.Core.Containers;
using MCCX.Core.Dialogs;

namespace MCCX.Core.Ipc;

/// <summary>
/// 多开子进程在主进程这一侧的代理：实现 <see cref="IAccountSession"/>，
/// 界面把它当普通会话用（连接/断开/发输入/自动化开关），实际工作都在子进程里完成。
///
/// 进程模型：每个账号一个独立的 <c>MCCX.exe --runner</c> 子进程，
/// 两条匿名管道（父写子读=命令、子写父读=事件）承担全部通信。
/// 好处：日志天然不串台、输入天然不抢路由、一个账号崩了不影响其它账号。
///
/// 生命周期：首次连接（或需要立刻生效的配置）时拉起进程；父进程退出时子进程因管道
/// EOF 自动退出；显式 <see cref="Dispose"/> 会先礼后兵（quit → 等待 → Kill）。
/// </summary>
public sealed class RunnerProcess : IAccountSession
{
    /// <summary>连接等待上限：超过就判定失败（子进程可能已经卡死）。</summary>
    private const int ConnectTimeoutMs = 180_000;

    /// <summary>退出时给子进程的收尾时间，超时直接 Kill。</summary>
    private const int ExitWaitMs = 3000;

    /// <summary>
    /// 点了断开后等子进程确认的时间。超时直接结束子进程（套接字由系统关闭），
    /// 宁可多花一次重连，也不能让服务器那边一直挂着"在线"（2026-10-23 用户报的现象）。
    /// </summary>
    private const int DisconnectForceKillMs = 5000;

    /// <summary>命令队列上限：子进程卡住时最多堆这么多条，再往里塞就丢弃并报错（绝不阻塞界面线程）。</summary>
    private const int WriteQueueCapacity = 4096;

    private readonly string _exePath;
    private readonly string _accountId;
    private readonly object _gate = new();

    /// <summary>待写进命令管道的消息（JSON 行）。所有写入都由 <see cref="_writerThread"/> 单线程完成。</summary>
    private readonly System.Collections.Concurrent.BlockingCollection<string> _writeQueue =
        new(new System.Collections.Concurrent.ConcurrentQueue<string>(), WriteQueueCapacity);

    /// <summary>
    /// 断开看门狗代号：每次"真的发过断开 / 连上 / 确认断开"都会 +1，
    /// 看门狗醒来时代号变了就说明状态已经推进过，不再杀进程。
    /// </summary>
    private int _disconnectEpoch;

    private Thread? _writerThread;

    /// <summary>进程还没起来时收到的配置命令（按类型去重），拉起进程后立刻补发。</summary>
    private readonly Dictionary<string, RunnerMessage> _config = new(StringComparer.Ordinal);

    private AnonymousPipeServerStream? _cmdPipe;
    private AnonymousPipeServerStream? _evtPipe;
    private StreamWriter? _cmdWriter;
    private Process? _process;
    private MCCConnectionState _state = MCCConnectionState.Disconnected;
    private bool _gameJoined;
    private bool _running;
    private bool _disposed;
    private TaskCompletionSource<MCCConnectionState>? _connectTcs;

    /// <summary>exePath 留空时用当前进程（界面里就是 MCCX.exe 自己）。</summary>
    /// <param name="accountId">账号 Id：透传给子进程，用于按账号存取视角记录（需求 1）。</param>
    public RunnerProcess(string? exePath = null, string? accountId = null)
    {
        _exePath = exePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径，不能启动多开子进程。");
        _accountId = accountId ?? string.Empty;
    }

    public event Action<string>? LogReceived;

    public event Action<MCCConnectionState>? StateChanged;

    public event Action? GameJoined;

    /// <summary>服务器弹出了对话框（子进程推过来的）。可能从事件管道线程触发。</summary>
    public event Action<MccDialogInfo>? DialogRequested;

    /// <summary>服务器关闭了编号为该值的对话框。</summary>
    public event Action<int>? DialogClosed;

    /// <summary>服务器命令列表更新（子进程推过来的 JSON 字符组）。可能从事件管道线程触发。</summary>
    public event Action<IReadOnlyList<string>>? ServerCommandsChanged;

    /// <summary>服务器打开/刷新了容器窗口（子进程推过来的）。可能从事件管道线程触发。</summary>
    public event Action<MccWindowInfo>? WindowChanged;

    /// <summary>服务器关掉了编号为该值的容器窗口。</summary>
    public event Action<int>? WindowClosed;

    /// <summary>子进程退出（含崩溃）。参数是进程退出码，取不到时为 -1。</summary>
    public event Action<int>? ProcessExited;

    /// <summary>解析子进程推来的命令列表 JSON（字符串数组）；坏数据返回 null，不影响会话。</summary>
    private static IReadOnlyList<string>? ParseCommandList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(json, RunnerMessage.SharedJsonOptions)
                is { } names
                ? names
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public MCCConnectionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public bool IsGameJoined
    {
        get
        {
            lock (_gate)
                return _gameJoined;
        }
    }

    /// <summary>子进程是否还活着（调试/测试用）。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    /// <summary>子进程 PID（未启动为 null）。</summary>
    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false } ? _process.Id : null;
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    #region 生命周期

    /// <summary>拉起子进程（幂等）。失败时抛异常，调用方负责把原因显示给用户。</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
                return;
        }

        // 必须 Inheritable：子进程是靠“继承到的句柄值”打开管道的，None 会让子进程一启动就打不开管道退出
        AnonymousPipeServerStream cmdPipe = new(PipeDirection.Out, HandleInheritability.Inheritable);
        AnonymousPipeServerStream evtPipe = new(PipeDirection.In, HandleInheritability.Inheritable);

        ProcessStartInfo psi = new(_exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 与主程序同目录：资源、依赖、相对路径行为完全一致
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--runner");
        psi.ArgumentList.Add("--cmd");
        psi.ArgumentList.Add(cmdPipe.GetClientHandleAsString());
        psi.ArgumentList.Add("--evt");
        psi.ArgumentList.Add(evtPipe.GetClientHandleAsString());

        // 账号 Id 透传给子进程：视角记录按账号落盘（业务参数一律走这里，不写 .ini）
        if (_accountId.Length > 0)
        {
            psi.ArgumentList.Add("--account");
            psi.ArgumentList.Add(_accountId);
        }

        // 父进程 PID 透传给子进程：子进程自己盯着父进程，父进程一没就退出（作业对象之外的第二层保险）
        psi.ArgumentList.Add("--parent");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null。");
        }
        catch
        {
            cmdPipe.Dispose();
            evtPipe.Dispose();
            throw;
        }

        // 句柄已经通过参数交给子进程，父进程这边的副本要立刻关掉
        cmdPipe.DisposeLocalCopyOfClientHandle();
        evtPipe.DisposeLocalCopyOfClientHandle();

        // 兜底：主程序以任何方式结束（含强杀）时，系统把这些子进程一并结束，
        // 否则子进程会一直握着服务器连接，表现为"关掉 MCCX 后账号还在线"（2026-10-23）
        ChildProcessGuard.Attach(process);

        StreamWriter writer = new(cmdPipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        lock (_gate)
        {
            _cmdPipe = cmdPipe;
            _evtPipe = evtPipe;
            _cmdWriter = writer;
            _process = process;
            _running = true;
            _state = MCCConnectionState.Disconnected;
            _gameJoined = false;
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => HandleChildExit();
        }
        catch
        {
            // 拿不到退出事件也不能算失败：事件管道 EOF 会兜底
        }

        // 命令管道改成"界面线程只管入队、专门线程负责写"：
        // 子进程一旦卡住不再读管道，旧写法会让 Flush/SendOrThrow 停在界面线程上（整个窗口未响应）
        EnsureWriterThread();

        Thread eventReader = new(() => ReadEvents(evtPipe))
        {
            IsBackground = true,
            Name = "MCCX runner event reader",
        };
        eventReader.Start();

        // 子进程的 stdout/stderr 必须有人读，否则写满了会把子进程卡死
        StartDrain(process.StandardOutput, "§7");
        StartDrain(process.StandardError, "§8[err] ");

        // 补发此前积累的自动化配置，保证“先调参后启动”与“进程重启”都不丢设置
        FlushConfig();
    }

    private void StartDrain(StreamReader reader, string prefix)
    {
        Thread thread = new(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0)
                        continue;
                    LogReceived?.Invoke(prefix + line);
                }
            }
            catch
            {
                // 流被关闭（子进程退出）→ 正常结束
            }
        })
        {
            IsBackground = true,
            Name = "MCCX runner stdout drain",
        };
        thread.Start();
    }

    private void ReadEvents(AnonymousPipeServerStream evtPipe)
    {
        try
        {
            using StreamReader reader = new(evtPipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string? line;
            while ((line = reader.ReadLine()) is not null)
                Dispatch(line);
        }
        catch
        {
            // 管道断开 = 子进程没了，交给 HandleChildExit 统一收尾
        }

        HandleChildExit();
    }

    private void Dispatch(string line)
    {
        RunnerMessage? message = RunnerMessage.Parse(line);
        if (message is null)
            return;

        switch (message.Type)
        {
            case RunnerMessage.EvtLog:
                LogReceived?.Invoke(message.Text ?? string.Empty);
                break;

            case RunnerMessage.EvtState:
                if (TryParseState(message.Text, out MCCConnectionState state))
                    ApplyState(state);
                break;

            case RunnerMessage.EvtJoined:
                lock (_gate)
                {
                    _gameJoined = true;
                }

                GameJoined?.Invoke();
                break;

            case RunnerMessage.EvtError:
                LogReceived?.Invoke($"§c{message.Text}");
                break;

            case RunnerMessage.EvtDialog:
                if (MccDialogInfo.Parse(message.Text) is { } dialog)
                    DialogRequested?.Invoke(dialog);
                break;

            case RunnerMessage.EvtDialogEnd:
                if (int.TryParse(message.Text, out int revision))
                    DialogClosed?.Invoke(revision);
                break;

            case RunnerMessage.EvtWindow:
                if (MccWindowInfo.Parse(message.Text) is { } window)
                    WindowChanged?.Invoke(window);
                break;

            case RunnerMessage.EvtWindowEnd:
                if (int.TryParse(message.Text, out int windowId))
                    WindowClosed?.Invoke(windowId);
                break;

            case RunnerMessage.EvtCommands:
                if (ParseCommandList(message.Text) is { } commands)
                    ServerCommandsChanged?.Invoke(commands);
                break;

            case RunnerMessage.EvtReady:
            default:
                break;
        }
    }

    /// <summary>子进程退出（事件管道 EOF 或 Process.Exited）：只处理第一次。</summary>
    private void HandleChildExit()
    {
        TaskCompletionSource<MCCConnectionState>? tcs;
        int exitCode = -1;

        lock (_gate)
        {
            if (!_running)
                return;

            _running = false;
            _state = MCCConnectionState.Disconnected;
            _gameJoined = false;
            tcs = _connectTcs;
            _connectTcs = null;

            // 断开看门狗作废（这次断开已经由"进程没了"完成）
            Interlocked.Increment(ref _disconnectEpoch);

            try
            {
                if (_process is { HasExited: true })
                    exitCode = _process.ExitCode;
            }
            catch
            {
                // 拿不到退出码就算了
            }

            DisposePipesLocked();
        }

        // 上一个子进程没写出去的命令全部丢掉：绝不能落到下一次启动的新进程里
        // （例如上一条 CmdInput 在重连后突然发出去）
        while (_writeQueue.TryTake(out _))
        {
        }

        tcs?.TrySetResult(MCCConnectionState.Disconnected);
        StateChanged?.Invoke(MCCConnectionState.Disconnected);
        ProcessExited?.Invoke(exitCode);
    }

    private void DisposePipesLocked()
    {
        try
        {
            _cmdWriter?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _cmdPipe?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _evtPipe?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _cmdWriter = null;
        _cmdPipe = null;
        _evtPipe = null;
    }

    #endregion

    #region 会话接口

    public async Task ConnectAsync(MCCConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        // 新的连接流程：上一次断开留下的看门狗作废（别把新连接掐掉）
        Interlocked.Increment(ref _disconnectEpoch);

        TaskCompletionSource<MCCConnectionState> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued;

        lock (_gate)
        {
            queued = _state == MCCConnectionState.Disconnected;
            if (queued)
                _connectTcs = tcs;
        }

        if (!queued)
        {
            LogReceived?.Invoke("§e当前已处于连接流程中，忽略本次请求。");
            return;
        }

        try
        {
            Start();
            SendOrThrow(new RunnerMessage
            {
                Type = RunnerMessage.CmdConnect,
                Host = options.ServerHost,
                Port = options.Port,
                User = options.Username,
                Version = options.MinecraftVersion,
            });
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _connectTcs = null;
            }

            tcs.TrySetResult(MCCConnectionState.Disconnected);
            ApplyState(MCCConnectionState.Disconnected); // 状态没抬起来时去重，不会多发事件
            LogReceived?.Invoke($"§c启动账号子进程失败：{ex.Message}");
            return;
        }

        // 从点“连接”到子进程回话有几百毫秒空窗，先把状态抬成“连接中”，
        // 否则这段时间状态条还停在“未连接”，看起来像点了没反应（也让测试的等待条件不会踩空）。
        ApplyState(MCCConnectionState.Connecting);

        try
        {
            await tcs.Task
                .WaitAsync(TimeSpan.FromMilliseconds(ConnectTimeoutMs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _connectTcs = null;
            }

            LogReceived?.Invoke("§c连接已取消。");
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                _connectTcs = null;
            }

            LogReceived?.Invoke("§c连接超时，已放弃等待。");
            Disconnect();
        }
    }

    public bool SendInput(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (State != MCCConnectionState.Connected)
        {
            LogReceived?.Invoke("§8当前未连接，输入已忽略。");
            return false;
        }

        try
        {
            SendOrThrow(new RunnerMessage { Type = RunnerMessage.CmdInput, Text = text.Trim() });
            return true;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"§c发送失败：{ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        if (_disposed)
            return;

        bool running;
        lock (_gate)
            running = _running;

        if (!running)
            return;

        try
        {
            SendOrConfig(new RunnerMessage { Type = RunnerMessage.CmdDisconnect });
        }
        catch
        {
            // 进程没了就没什么可断的
            return;
        }

        // 兜底：子进程要是卡在断开流程里，5 秒后直接结束它（见 ArmDisconnectWatchdog）
        ArmDisconnectWatchdog();
    }

    public void ConfigureAttack(bool enabled, AttackOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdAttack,
            On = enabled,
            Range = options.Range,
            CooldownMinMs = options.CooldownMinMs,
            CooldownMaxMs = options.CooldownMaxMs,
            FilterMode = (int)options.FilterMode,
            Mobs = [.. options.Mobs],
        });
    }

    public void ConfigureMouse(bool enabled, MouseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdMouse,
            On = enabled,
            LeftOn = options.Left.Enabled,
            LeftMode = (int)options.Left.Mode,
            LeftHoldMs = options.Left.HoldMs,
            LeftIntervalMs = options.Left.IntervalMs,
            LeftJitterPercent = options.Left.JitterPercent,
            RightOn = options.Right.Enabled,
            RightMode = (int)options.Right.Mode,
            RightHoldMs = options.Right.HoldMs,
            RightIntervalMs = options.Right.IntervalMs,
            RightJitterPercent = options.Right.JitterPercent,
            Reach = options.AimReach,
        });
    }

    public void ConfigureFishing(bool enabled, FishingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdFishing,
            On = enabled,
            FishingSound = options.SoundDetection,
            FishingVelocity = options.VelocityDetection,
            FishingTimeout = options.TimeoutSeconds,
            FishingCastDelay = options.CastDelaySeconds,
        });
    }

    public void ConfigureAutoRefill(bool enabled) =>
        SendOrConfig(new RunnerMessage { Type = RunnerMessage.CmdAutoRefill, On = enabled });

    /// <summary>自动行走（2026-10-04 需求）：一直朝当前朝向前进，没有参数，只有开关。</summary>
    public void ConfigureWalk(bool enabled) =>
        SendOrConfig(new RunnerMessage { Type = RunnerMessage.CmdWalk, On = enabled });

    /// <summary>
    /// 自动指令（2026-10-22 需求）：开关 + 整份指令表一起下发。
    /// 进程还没起来也先记账，拉起/重启后按最新一份补发。
    /// </summary>
    public void ConfigureAutoCommands(bool enabled, IReadOnlyList<AutoCommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdAutoCommands,
            On = enabled,
            AutoCommands = [.. commands],
        });
    }

    /// <summary>
    /// 功能“过程日志”开关（2026-10-20）：随配置一起记账，子进程重启/晚启动都会补发。
    /// 下发后由子进程改 Bot 的 <c>ProcessLogEnabled</c>，日志在 Bot 里就不产生。
    /// </summary>
    public void SetFeatureLog(string key, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdLogSwitch,
            Key = key,
            On = enabled,
        });
    }

    /// <summary>
    /// 服务器信息过滤（2026-10-04 需求）：模式 + 两份前缀随账号下发，连接中/已连接都生效。
    /// 两份前缀相互独立："只显示"用 showPrefix，"只屏蔽"用 blockPrefix（2026-10-05 用户要求）。
    /// </summary>
    public void ConfigureServerFilter(ServerFilterMode mode, string showPrefix, string blockPrefix) =>
        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdMsgFilter,
            ServerFilterMode = (int)mode,
            ServerFilterShowPrefix = showPrefix ?? string.Empty,
            ServerFilterBlockPrefix = blockPrefix ?? string.Empty,
        });

    public void ConfigureReconnect(ReconnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SendOrConfig(new RunnerMessage
        {
            Type = RunnerMessage.CmdReconnect,
            On = options.Enabled,
            Attempts = options.MaxAttempts,
            DelayMs = options.DelayMs,
            JitterPercent = options.JitterPercent,
        });
    }

    /// <summary>
    /// 视角移动（需求 4）：一次性命令（不是配置，不记账补发——进程没起来时点了也没意义）。
    /// </summary>
    public void LookAt(MccLookDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != MCCConnectionState.Connected)
        {
            LogReceived?.Invoke("§e视角移动需要先进服（当前还没连接）。");
            return;
        }

        try
        {
            SendOrThrow(new RunnerMessage
            {
                Type = RunnerMessage.CmdLook,
                Direction = (int)direction,
            });
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"§c视角移动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把界面填好的对话框取值交给子进程回写 MCC。密码等敏感取值只在这条管道里走，
    /// 不经过聊天框、也不写日志。
    /// </summary>
    public bool SubmitDialog(IReadOnlyDictionary<string, string> values, int actionIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(values);

        bool running;
        lock (_gate)
        {
            // 只要求子进程活着：对话框可能在"已连接"判定之前就弹出来（配置阶段）
            running = _running;
        }

        if (!running)
        {
            LogReceived?.Invoke("§c当前没有运行中的账号进程，对话框输入未提交。");
            return false;
        }

        try
        {
            MccDialogSubmit submit = new()
            {
                ActionIndex = actionIndex,
                Values = new Dictionary<string, string>(values, StringComparer.Ordinal),
            };

            SendOrThrow(new RunnerMessage { Type = RunnerMessage.CmdDialog, Text = submit.ToJson() });
            return true;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"§c对话框输入提交失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>取消服务器弹出的对话框（子进程里没有进行中的对话框时安静返回 false）。</summary>
    public bool CancelDialog()
    {
        if (_disposed)
            return false;

        bool running;
        lock (_gate)
        {
            running = _running;
        }

        if (!running)
            return false;

        try
        {
            SendOrThrow(new RunnerMessage { Type = RunnerMessage.CmdDialogCancel });
            return true;
        }
        catch
        {
            return false; // 进程刚好退出：没什么可取消的
        }
    }

    /// <summary>
    /// 容器窗口操作：点格子 / 关窗口。只要求子进程活着（窗口内容随时可能因为网络抖动重来一遍，
    /// 不用等"已连接"的判定）。发送失败会在日志里说明。
    /// </summary>
    public bool WindowClick(MccWindowClick click)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(click);

        bool running;
        lock (_gate)
        {
            running = _running;
        }

        if (!running)
        {
            LogReceived?.Invoke("§8当前没有运行中的账号进程，容器操作已忽略。");
            return false;
        }

        try
        {
            SendOrThrow(new RunnerMessage { Type = RunnerMessage.CmdWindowClick, Text = click.ToJson() });
            return true;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"§c容器操作发送失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 开 / 收"玩家背包"面板：打开后子进程会把玩家背包（窗口 0）的内容跟着推过来。
    /// 与容器点击一样只要求子进程活着（重连后会话会自己接着推，父进程不用重发）。返回是否已投递。
    /// </summary>
    public bool WatchPlayerInventory(bool watch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool running;
        lock (_gate)
        {
            running = _running;
        }

        if (!running)
            return false;

        try
        {
            SendOrThrow(new RunnerMessage { Type = RunnerMessage.CmdWindowWatch, On = watch });
            return true;
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"§c背包面板操作发送失败：{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Process? process;
        bool exited = true;

        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            process = _process;
            // 注意：_running 要等 CmdQuit 送完、进程退出（或超时 Kill）之后再翻，
            // 否则 SendOrThrow 检查 !_running 直接抛异常，CmdQuit 永远送不出去，
            // 每次关窗都白等 ExitWaitMs 超时后才 Kill（关窗卡顿的根因之一）。
        }

        try
        {
            if (process is { HasExited: false })
            {
                // 先礼后兵：给子进程一个断开连接、回收线程的机会
                try
                {
                    SendOrThrow(new RunnerMessage { Type = RunnerMessage.CmdQuit });
                }
                catch
                {
                    // 管道已经不通就直接等 Kill
                }

                // 命令是排队写的：给写线程一点时间把 CmdQuit 真正冲进管道
                WaitForWriteQueueDrain(300);

                exited = process.WaitForExit(ExitWaitMs);
                if (!exited)
                    process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 收尾尽力而为
        }

        lock (_gate)
        {
            _running = false;
            DisposePipesLocked();
            _state = MCCConnectionState.Disconnected;
            _gameJoined = false;
        }

        try
        {
            process?.Dispose();
        }
        catch
        {
            // 忽略
        }

        // 写线程是 GetConsumingEnumerable 上的阻塞循环：收尾后放它退出（不然每个销毁的账号都留一个挂住的线程）
        try
        {
            _writeQueue.CompleteAdding();
        }
        catch
        {
            // 已经 Complete 过
        }
    }

    #endregion

    #region 内部实现

    private void ApplyState(MCCConnectionState state)
    {
        TaskCompletionSource<MCCConnectionState>? tcs;

        lock (_gate)
        {
            if (_state == state)
                return;

            _state = state;
            if (state == MCCConnectionState.Disconnected)
            {
                _gameJoined = false;
                // 子进程确认断开了：断开看门狗作废
                Interlocked.Increment(ref _disconnectEpoch);
            }

            tcs = state is MCCConnectionState.Connected or MCCConnectionState.Disconnected
                ? _connectTcs
                : null;
            if (tcs is not null)
                _connectTcs = null;
        }

        StateChanged?.Invoke(state);
        tcs?.TrySetResult(state);
    }

    private static bool TryParseState(string? text, out MCCConnectionState state)
    {
        if (text is not null && Enum.TryParse(text, ignoreCase: false, out MCCConnectionState parsed))
        {
            state = parsed;
            return true;
        }

        state = MCCConnectionState.Disconnected;
        return false;
    }

    /// <summary>配置类命令：进程没起来就记下来（按类型去重），起来后立刻补发。</summary>
    private void SendOrConfig(RunnerMessage message)
    {
        if (_disposed)
            return;

        bool running;
        lock (_gate)
        {
            running = _running;

            // 配置类命令先记账：进程还没起来（或之后重启）都要按最新设置补发
            if (IsConfigCommand(message.Type))
                _config[ConfigKeyOf(message)] = message;
        }

        if (!running)
            return;

        try
        {
            SendOrThrow(message);
        }
        catch
        {
            // 进程刚好退出：配置已经记在 _config 里，下次 Start 会补发
        }
    }

    /// <summary>
    /// 记账用的键。日志开关是"一个功能一条"、共用 <see cref="RunnerMessage.CmdLogSwitch"/> 这一个
    /// <see cref="RunnerMessage.Type"/>，按类型存只会留下最后发的那一个（2026-10-22 用户报的
    /// "重启后自动钓鱼日志开关失效、再手动拨一次才好"就是这个：补发时只剩最后发的那一个键，
    /// 其余功能全退回默认"开"）——
    /// 所以日志开关必须按 类型 + Key 存。其它配置一个类型只有一份，照旧按类型。
    /// </summary>
    private static string ConfigKeyOf(RunnerMessage message) =>
        message.Type == RunnerMessage.CmdLogSwitch
            ? message.Type + ":" + (message.Key ?? string.Empty)
            : message.Type;

    private void FlushConfig()
    {
        RunnerMessage[] messages;
        lock (_gate)
        {
            messages = [.. _config.Values];
        }

        foreach (RunnerMessage message in messages)
        {
            try
            {
                SendOrThrow(message);
            }
            catch
            {
                break; // 进程立刻退出：下次启动再补
            }
        }
    }

    private static bool IsConfigCommand(string type) =>
        type is RunnerMessage.CmdAttack or RunnerMessage.CmdMouse
            or RunnerMessage.CmdFishing or RunnerMessage.CmdAutoRefill
            or RunnerMessage.CmdWalk
            or RunnerMessage.CmdAutoCommands
            or RunnerMessage.CmdMsgFilter
            or RunnerMessage.CmdReconnect
            or RunnerMessage.CmdLogSwitch;

    /// <summary>
    /// 把消息放进发送队列就返回（真正的管道写入在 <see cref="_writerThread"/> 上）。
    /// <para>
    /// 2026-10-23：旧写法直接在调用线程上写管道 + Flush。子进程一旦卡住（MCC 主线程死锁、
    /// 对话框回写阻塞…）管道缓冲区写满，调用线程就会永远停在这一行；
    /// 而调用方大多是界面线程（发输入、提交对话框、拨开关），表现就是整个窗口"未响应"。
    /// </para>
    /// </summary>
    private void SendOrThrow(RunnerMessage message)
    {
        lock (_gate)
        {
            if (!_running)
                throw new InvalidOperationException("账号子进程未运行。");

            if (_cmdWriter is null)
                throw new InvalidOperationException("命令管道未就绪。");
        }

        if (!_writeQueue.TryAdd(message.ToJson()))
            throw new InvalidOperationException("账号子进程长时间没有响应（命令队列已满），这条命令已丢弃。");
    }

    private void EnsureWriterThread()
    {
        if (_writerThread is not null)
            return;

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "MCCX runner command writer",
        };
        _writerThread.Start();
    }

    /// <summary>命令管道唯一的写入者：按入队顺序写，写失败只丢失这一条（连接状态由事件管道兜底）。</summary>
    private void WriteLoop()
    {
        foreach (string line in _writeQueue.GetConsumingEnumerable())
        {
            StreamWriter? writer;
            lock (_gate)
                writer = _cmdWriter;

            if (writer is null)
                continue; // 子进程已退出：丢掉（配置已经记账，下次启动补发）

            try
            {
                writer.Write(line);
                writer.Write('\n');
                writer.Flush();
            }
            catch
            {
                // 管道断了：子进程退出会走 HandleChildExit
            }
        }
    }

    /// <summary>等写线程把队列冲干净（退出前发 CmdQuit 用；等不到就直接进 Kill 流程）。</summary>
    private void WaitForWriteQueueDrain(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (_writeQueue.Count > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

    /// <summary>
    /// 断开看门狗（2026-10-23）：点了断开却迟迟收不到"已断开"，就把子进程结束掉。
    /// 子进程一死，套接字由操作系统关闭，服务器立刻看到玩家离线 ——
    /// 这是"界面显示未连接、服务器上却还挂着在线"的最后一道保险。
    /// </summary>
    private void ArmDisconnectWatchdog()
    {
        int epoch = Interlocked.Increment(ref _disconnectEpoch);

        _ = Task.Run(async () =>
        {
            await Task.Delay(DisconnectForceKillMs).ConfigureAwait(false);

            // 期间连上过 / 已经确认断开 / 又发起过新的断开：这一轮作废
            if (Volatile.Read(ref _disconnectEpoch) != epoch)
                return;

            Process? process;
            lock (_gate)
            {
                if (!_running || _state == MCCConnectionState.Disconnected)
                    return;

                process = _process;
            }

            try
            {
                LogReceived?.Invoke("§e断开超时：子进程 5 秒内没有确认断开，已强制结束它（服务器会看到离线）。");
                process?.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程刚好自己退了
            }
        });
    }

    #endregion
}
