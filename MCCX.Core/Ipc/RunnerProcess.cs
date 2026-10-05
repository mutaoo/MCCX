using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
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

    private readonly string _exePath;
    private readonly string _accountId;
    private readonly object _gate = new();
    private readonly object _writeGate = new();

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

    /// <summary>子进程退出（含崩溃）。参数是进程退出码，取不到时为 -1。</summary>
    public event Action<int>? ProcessExited;

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

        try
        {
            SendOrConfig(new RunnerMessage { Type = RunnerMessage.CmdDisconnect });
        }
        catch
        {
            // 进程没了就没什么可断的
        }
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
                _gameJoined = false;

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
                _config[message.Type] = message;
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
            or RunnerMessage.CmdMsgFilter
            or RunnerMessage.CmdReconnect;

    private void SendOrThrow(RunnerMessage message)
    {
        StreamWriter writer;
        lock (_gate)
        {
            if (!_running)
                throw new InvalidOperationException("账号子进程未运行。");

            writer = _cmdWriter ?? throw new InvalidOperationException("命令管道未就绪。");
        }

        lock (_writeGate)
        {
            writer.Write(message.ToJson());
            writer.Write('\n');
            writer.Flush();
        }
    }

    #endregion
}
