using System.IO.Pipes;
using System.Text;

namespace MCCX.Core.Ipc;

/// <summary>
/// 多开子进程侧的宿主：界面用 <c>MCCX.App.exe --runner --cmd &lt;句柄&gt; --evt &lt;句柄&gt;</c>
/// 启动本类，跑一个完全独立的 MCC 会话（日志、输入、状态互不串台）。
///
/// 与父进程的通道是两条匿名管道：
///   --cmd 父写子读（命令行协议，见 <see cref="RunnerMessage"/>），
///   --evt 子写父读（日志/状态事件）。
/// 父进程消失 → 命令管道 EOF → 本方法返回，进程随之退出（不会留僵尸进程）。
/// </summary>
public static class RunnerHost
{
    /// <summary>进程入口。返回值作为进程退出码。</summary>
    public static int Run(string? cmdHandle, string? evtHandle)
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
            // 与主程序完全一致的一次性初始化（只改内存配置，不写 .ini）
            MCCRuntime.Initialize();

            session = new MCCSession();
            session.LogReceived += text => writer.Emit(RunnerMessage.EvtLog, text);
            session.StateChanged += state => writer.Emit(RunnerMessage.EvtState, state.ToString());
            session.GameJoined += () => writer.Emit(RunnerMessage.EvtJoined);
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
        }
        catch
        {
            // 管道被父进程关闭（主程序退出/崩溃）→ 直接收尾
        }

        // 退出前断开连接；MCCSession.Dispose 内部会断开并回收 MCC 线程
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
                });
                break;

            case RunnerMessage.CmdDisconnect:
                session.Disconnect();
                break;

            case RunnerMessage.CmdInput:
                session.SendInput(message.Text ?? string.Empty);
                break;

            case RunnerMessage.CmdAttack:
                session.ConfigureAttack(message.On, new AttackOptions
                {
                    Range = message.Range,
                    CooldownMinMs = message.CooldownMinMs,
                    CooldownMaxMs = message.CooldownMaxMs,
                });
                break;

            case RunnerMessage.CmdMouse:
                session.ConfigureMouse(message.On, new MouseOptions
                {
                    Mode = (MouseMode)Math.Clamp(message.Mode, 0, 2),
                    Side = (MouseSide)Math.Clamp(message.Side, 0, 1),
                    HoldMs = message.HoldMs,
                    IntervalMs = message.IntervalMs,
                    JitterPercent = message.JitterPercent,
                });
                break;

            case RunnerMessage.CmdFishing:
                session.ConfigureFishing(message.On);
                break;

            case RunnerMessage.CmdReconnect:
                session.ConfigureReconnect(new ReconnectOptions
                {
                    Enabled = message.On,
                    MaxAttempts = message.Attempts,
                    DelayMs = message.DelayMs,
                });
                break;

            default:
                // 未知命令忽略：父子版本不一致时也不能崩
                break;
        }
    }
}

/// <summary>把子进程事件按行写进管道：加锁串行化，写失败即静默停写（父进程已经不在了）。</summary>
internal sealed class RunnerEventWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _closed;

    public RunnerEventWriter(Stream stream)
    {
        _stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
    }

    public void Emit(string type, string? text = null)
    {
        RunnerMessage message = new() { Type = type, Text = text };
        lock (_gate)
        {
            if (_closed)
                return;

            try
            {
                _writer.Write(message.ToJson());
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
        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
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
}
