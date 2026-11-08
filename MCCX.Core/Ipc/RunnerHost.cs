using System.Globalization;
using System.IO.Pipes;
using System.Text;
using MCCX.Core.Containers;
using MCCX.Core.Dialogs;

namespace MCCX.Core.Ipc;

/// <summary>
/// 多开子进程侧的宿主：界面用 <c>MCCX.exe --runner --cmd &lt;句柄&gt; --evt &lt;句柄&gt;</c>
/// 启动本类，跑一个完全独立的 MCC 会话（日志、输入、状态互不串台）。
///
/// 与父进程的通道是两条匿名管道：
///   --cmd 父写子读（命令行协议，见 <see cref="RunnerMessage"/>），
///   --evt 子写父读（日志/状态事件）。
/// 父进程消失 → 命令管道 EOF → 本方法返回，进程随之退出（不会留僵尸进程）。
/// </summary>
public static class RunnerHost
{
    /// <summary>进程入口。返回值作为进程退出码。<paramref name="accountId"/> 用于按账号存取视角记录。</summary>
    /// <param name="parentPid">
    /// 父进程 PID（0 = 不监视）。父进程以任何方式结束（含强杀）后本进程立即退出，
    /// 保证不会留下"主程序没了、账号还挂在服务器上"的孤儿进程（2026-10-23）。
    /// </param>
    public static int Run(string? cmdHandle, string? evtHandle, string? accountId = null, int parentPid = 0)
    {
        WatchParent(parentPid);

        int rc = RunCore(cmdHandle, evtHandle, accountId);

        // MCC 会话会在前台线程上留尾巴（如连接超时检测线程：Thread.Sleep 15 秒醒一次才检查取消标记），
        // 正常 return 会让 CLR 一直等这个前台线程结束，子进程迟迟不退、父进程白等 3 秒超时后才 Kill。
        // 清理已全部完成，这里直接结束进程；Environment.Exit 仍会照常触发 ProcessExit 事件。
        Environment.Exit(rc);
        return rc; // 到不了这里
    }

    /// <summary>
    /// 父进程看门狗：命令管道靠 EOF 通知父进程消失，但 Windows 上后续启动的兄弟子进程会
    /// 继承前面的管道写端，EOF 可能永远不来（详见 <see cref="ChildProcessGuard"/>）。
    /// 这里直接盯 PID，父进程没了就退出 —— 套接字随进程结束由系统关闭，服务器立刻看到离线。
    /// 用 IsBackground 线程 + 每秒轮询，开销可以忽略。
    /// </summary>
    private static void WatchParent(int parentPid)
    {
        if (parentPid <= 0)
            return;

        Thread thread = new(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);

                try
                {
                    using System.Diagnostics.Process parent = System.Diagnostics.Process.GetProcessById(parentPid);
                    if (parent.HasExited)
                        break;
                }
                catch (ArgumentException)
                {
                    break; // 进程不存在
                }
                catch
                {
                    // 一时拿不到（权限等）：下一轮再看
                }
            }

            // MCC 在主线程上可能还留着前台线程（Thread.Sleep 轮询连接超时），正常 return 会被拖住
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "MCCX parent watch",
        };
        thread.Start();
    }

    private static int RunCore(string? cmdHandle, string? evtHandle, string? accountId)
    {
        if (string.IsNullOrEmpty(cmdHandle) || string.IsNullOrEmpty(evtHandle))
            return 2;

        AnonymousPipeClientStream? cmdPipe = null;
        AnonymousPipeClientStream? evtPipe = null;

        try
        {
            cmdPipe = new AnonymousPipeClientStream(PipeDirection.In, cmdHandle);
            evtPipe = new AnonymousPipeClientStream(PipeDirection.Out, evtHandle);
        }
        catch
        {
            cmdPipe?.Dispose();
            evtPipe?.Dispose();
            return 3;
        }

        using AnonymousPipeClientStream cmd = cmdPipe;
        using AnonymousPipeClientStream evt = evtPipe;
        using RunnerEventWriter writer = new(evt);

        MCCSession? session = null;
        try
        {
            // MCC 自己的程序文案（Translations / AsciiArt / ConfigComments）走 .NET 卫星程序集。
            // MCCSource 里装了官方的简体中文（Resources\**\*.zh-Hans.resx），这里把子进程的
            // **界面文化**钉成 zh-Hans：不看系统语言，英文系统上 MCC 也照样说中文，和 MCCX 界面一致。
            // 只动 UICulture，CurrentCulture 仍跟随系统，数字/日期/日志时间格式不跟着变。
            CultureInfo mccUiCulture = CultureInfo.GetCultureInfo("zh-Hans");
            CultureInfo.DefaultThreadCurrentUICulture = mccUiCulture;
            Thread.CurrentThread.CurrentUICulture = mccUiCulture;

            // 与主程序完全一致的一次性初始化（只改内存配置，不写 .ini）
            MCCRuntime.Initialize();

            session = new MCCSession(accountId);
            session.LogReceived += text => writer.Emit(RunnerMessage.EvtLog, text);
            session.StateChanged += state => writer.Emit(RunnerMessage.EvtState, state.ToString());
            session.GameJoined += () => writer.Emit(RunnerMessage.EvtJoined);
            session.DialogRequested += info => writer.Emit(RunnerMessage.EvtDialog, info.ToJson());
            session.DialogClosed += revision => writer.Emit(RunnerMessage.EvtDialogEnd, revision.ToString());
            session.WindowChanged += info => writer.Emit(RunnerMessage.EvtWindow, info.ToJson());
            session.WindowClosed += windowId => writer.Emit(RunnerMessage.EvtWindowEnd, windowId.ToString());
            session.ServerCommandsChanged += commands =>
                writer.Emit(RunnerMessage.EvtCommands, System.Text.Json.JsonSerializer.Serialize(commands));
            session.VitalsChanged += (health, food) =>
                writer.Emit(RunnerMessage.EvtVitals, new VitalsInfo { Health = health, Food = food }.ToJson());
            session.ServerPortChanged += port =>
                writer.Emit(RunnerMessage.EvtServerPort, port.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            writer.Emit(RunnerMessage.EvtError, $"子进程初始化失败：{ex.Message}");
            session?.Dispose();
            return 4;
        }

        using MCCSession live = session;
        writer.Emit(RunnerMessage.EvtReady);
        writer.Emit(RunnerMessage.EvtLog, $"§8[MCCX] 账号子进程已就绪（PID {Environment.ProcessId}）。");

        // 命令改成"读线程只入队、专门线程执行"：
        // 有些命令会同步等 MCC 主线程（提交对话框、点容器格子…），
        // 旧写法会让读线程卡在那条命令上，后面的断开/退出全排在它后面 —— 子进程看起来就"卡死"了。
        // 队列有上限：执行线程真的卡住时只丢新命令，读线程永远不阻塞（退出命令仍走原地处理）。
        using System.Collections.Concurrent.BlockingCollection<RunnerMessage> queue =
            new(new System.Collections.Concurrent.ConcurrentQueue<RunnerMessage>(), 1024);

        Thread dispatcher = new(() =>
        {
            foreach (RunnerMessage message in queue.GetConsumingEnumerable())
            {
                try
                {
                    Dispatch(message, live);
                }
                catch (Exception ex)
                {
                    // 单条命令失败不能把整个子进程带崩
                    writer.Emit(RunnerMessage.EvtError, $"命令 {message.Type} 执行失败：{ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "MCCX runner command dispatch",
        };
        dispatcher.Start();

        try
        {
            using StreamReader reader = new(cmd, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                RunnerMessage? message = RunnerMessage.Parse(line);
                if (message is null)
                    continue;

                if (string.Equals(message.Type, RunnerMessage.CmdQuit, StringComparison.Ordinal))
                    break;

                if (!queue.TryAdd(message))
                    writer.Emit(RunnerMessage.EvtError, "命令队列已满：子进程处理不过来，这条命令已丢弃。");
            }
        }
        catch
        {
            // 管道被父进程关闭（主程序退出/崩溃）→ 直接收尾
        }

        queue.CompleteAdding();

        // 退出前断开连接；MCCSession.Dispose 内部会断开并回收 MCC 线程
        live.Dispose();
        return 0;
    }

    private static void Dispatch(RunnerMessage message, MCCSession session)
    {
        switch (message.Type)
        {
            case RunnerMessage.CmdConnect:
                _ = session.ConnectAsync(new MCCConnectionOptions
                {
                    ServerHost = message.Host ?? string.Empty,
                    Port = (ushort)Math.Clamp(message.Port, 0, ushort.MaxValue),
                    Username = message.User ?? string.Empty,
                    MinecraftVersion = string.IsNullOrWhiteSpace(message.Version) ? "auto" : message.Version!,
                    BrandMode = message.Brand,
                });
                break;

            case RunnerMessage.CmdDisconnect:
                session.Disconnect();
                break;

            case RunnerMessage.CmdInput:
                session.SendInput(message.Text ?? string.Empty);
                break;

            case RunnerMessage.CmdDialog:
                if (MccDialogSubmit.Parse(message.Text) is { } submit)
                    session.SubmitDialog(submit.Values, submit.ActionIndex);
                break;

            case RunnerMessage.CmdDialogCancel:
                session.CancelDialog();
                break;

            case RunnerMessage.CmdWindowClick:
                if (MccWindowClick.Parse(message.Text) is { } click)
                    session.WindowClick(click);
                break;

            case RunnerMessage.CmdWindowWatch:
                session.WatchPlayerInventory(message.On);
                break;

            case RunnerMessage.CmdAttack:
                session.ConfigureAttack(message.On, new AttackOptions
                {
                    Range = message.Range,
                    CooldownMinMs = message.CooldownMinMs,
                    CooldownMaxMs = message.CooldownMaxMs,
                    FilterMode = (MobFilterMode)Math.Clamp(message.FilterMode, 0, 2),
                    Mobs = message.Mobs ?? [],
                });
                break;

            case RunnerMessage.CmdMouse:
                session.ConfigureMouse(message.On, new MouseOptions
                {
                    Left = new MouseButtonOptions
                    {
                        Enabled = message.LeftOn,
                        Mode = (MouseMode)Math.Clamp(message.LeftMode, 0, 2),
                        HoldMs = message.LeftHoldMs,
                        IntervalMs = message.LeftIntervalMs,
                        JitterPercent = message.LeftJitterPercent,
                    },
                    Right = new MouseButtonOptions
                    {
                        Enabled = message.RightOn,
                        Mode = (MouseMode)Math.Clamp(message.RightMode, 0, 2),
                        HoldMs = message.RightHoldMs,
                        IntervalMs = message.RightIntervalMs,
                        JitterPercent = message.RightJitterPercent,
                    },
                    // 探测距离（1-7 格）：<=0 视为没带字段，回默认 5
                    AimReach = message.Reach > 0 ? Math.Clamp(message.Reach, 1, 7) : 5.0,
                });
                break;

            case RunnerMessage.CmdFishing:
                session.ConfigureFishing(message.On, new FishingOptions
                {
                    SoundDetection = message.FishingSound,
                    VelocityDetection = message.FishingVelocity,
                    TimeoutSeconds = message.FishingTimeout,
                    CastDelaySeconds = message.FishingCastDelay,
                });
                break;

            case RunnerMessage.CmdAutoRefill:
                session.ConfigureAutoRefill(message.On);
                break;

            case RunnerMessage.CmdWalk:
                session.ConfigureWalk(message.On);
                break;

            case RunnerMessage.CmdAutoCommands:
                session.ConfigureAutoCommands(message.On, message.AutoCommands ?? []);
                break;

            case RunnerMessage.CmdMsgFilter:
                session.ConfigureServerFilter(
                    (ServerFilterMode)Math.Clamp(message.ServerFilterMode, 0, (int)ServerFilterMode.BlockPrefix),
                    message.ServerFilterShowPrefix,
                    message.ServerFilterBlockPrefix);
                break;

            case RunnerMessage.CmdReconnect:
                session.ConfigureReconnect(new ReconnectOptions
                {
                    Enabled = message.On,
                    MaxAttempts = message.Attempts,
                    DelayMs = message.DelayMs,
                });
                break;

            case RunnerMessage.CmdLook:
                session.LookAt((MccLookDirection)Math.Clamp(message.Direction, 0, 5));
                break;

            case RunnerMessage.CmdLogSwitch:
                if (!string.IsNullOrEmpty(message.Key))
                    session.SetFeatureLog(message.Key, message.On);
                break;

            default:
                // 未知命令忽略：父子版本不一致时也不能崩
                break;
        }
    }
}

/// <summary>
/// 把子进程事件按行写进管道：<b>单线程排队写</b>，调用方（MCC 主线程、各种 Bot 线程）只入队不等待。
/// <para>
/// 2026-10-23 改：旧写法在调用线程上加锁 + 同步写管道 + Flush。父进程那边一旦读得慢
/// （界面卡顿、线程池被占满），管道写满后 <c>MCC 主线程</c> 就停在日志输出上 —— 不再回 KeepAlive、
/// 不再处理包，服务器 30 秒后把人踢掉；界面上看就是"MCCX 卡死 + 账号掉线"。
/// 现在写不出去只会丢几条日志，绝不影响 MCC 的主循环。
/// </para>
/// </summary>
internal sealed class RunnerEventWriter : IDisposable
{
    /// <summary>积压上限：父进程卡住时最多堆这么多行（够一分钟的刷屏量），再多就丢。</summary>
    private const int QueueCapacity = 16_384;

    private readonly System.Collections.Concurrent.BlockingCollection<string> _queue =
        new(new System.Collections.Concurrent.ConcurrentQueue<string>(), QueueCapacity);

    private readonly StreamWriter _writer;
    private readonly Thread _thread;
    private volatile bool _closed;

    public RunnerEventWriter(Stream stream)
    {
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        _thread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "MCCX runner event writer",
        };
        _thread.Start();
    }

    public void Emit(string type, string? text = null)
    {
        if (_closed)
            return;

        RunnerMessage message = new() { Type = type, Text = text };

        // 队列满了就丢这一条：宁可少几行日志，也不能把 MCC 主线程堵死
        _queue.TryAdd(message.ToJson());
    }

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                _writer.Write(line);
                _writer.Write('\n');
                _writer.Flush();
            }
            catch
            {
                // 父进程关闭管道：停止写入即可，后续 Emit 直接短路
                _closed = true;
            }
        }
    }

    public void Dispose()
    {
        _closed = true;
        _queue.CompleteAdding();

        // 尽量把剩下的几行冲出去；父进程已经不在了就最多等半秒
        try
        {
            _thread.Join(500);
        }
        catch
        {
            // 忽略
        }

        try
        {
            _writer.Dispose();
        }
        catch
        {
            // 忽略：只影响收尾
        }
    }
}
