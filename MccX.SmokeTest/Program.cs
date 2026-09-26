using MccX.Core;

// 无界面冒烟测试：验证 MccX.Core 的 MCC 启动链路（Ping → 登录 → 收发 → 断开）。
// 用法: dotnet run --project MccX.SmokeTest -- [host] [port] [version]
// 例  : dotnet run --project MccX.SmokeTest -- 127.0.0.1 25565 auto

string host = args.Length > 0 ? args[0] : "127.0.0.1";
ushort port = args.Length > 1 && ushort.TryParse(args[1], out ushort p) ? p : (ushort)25565;
string version = args.Length > 2 ? args[2] : "auto";

MccRuntime.Initialize();

using MccSession session = new();
session.LogReceived += text => Console.WriteLine(text);
session.StateChanged += state => Console.WriteLine($"[STATE] {state}");

Console.WriteLine($"[TEST] connecting to {host}:{port} ({version}) as MccXBot …");

await session.ConnectAsync(new MccConnectionOptions
{
    ServerHost = host,
    Port = port,
    Username = "MccXBot",
    MinecraftVersion = version,
});

if (session.State != MccConnectionState.Connected)
{
    Console.WriteLine("[TEST] FAIL: 未进入连接状态");
    return 1;
}

// 等待服务器配置阶段结束（之后才能发聊天）
if (!session.IsGameJoined)
{
    Console.WriteLine("[TEST] waiting for game join …");
    TaskCompletionSource joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
    session.GameJoined += () => joined.TrySetResult();

    if (!session.IsGameJoined)
    {
        Task completed = await Task.WhenAny(joined.Task, Task.Delay(30000));
        if (completed != joined.Task)
        {
            Console.WriteLine("[TEST] FAIL: 等待进入游戏超时");
            return 4;
        }
    }
}

await Task.Delay(1000);

// 普通文本 = 服务器聊天
Console.WriteLine("[TEST] send chat");
session.SendInput("hello from MccX smoke test");

await Task.Delay(1500);

// 内部命令（/ 开头）
Console.WriteLine("[TEST] send internal command");
session.SendInput("/help");

await Task.Delay(1500);

// 被拦截的命令不应改变状态
Console.WriteLine("[TEST] blocked command");
bool routed = session.SendInput("/exit");
Console.WriteLine($"[TEST] /exit routed = {routed} (期望 false)");
if (routed)
{
    Console.WriteLine("[TEST] FAIL: 危险命令未被拦截");
    return 2;
}

Console.WriteLine("[TEST] disconnect");
session.Disconnect();

for (int i = 0; i < 20 && session.State != MccConnectionState.Disconnected; i++)
    await Task.Delay(500);

if (session.State != MccConnectionState.Disconnected)
{
    Console.WriteLine($"[TEST] FAIL: 断开超时，当前状态 {session.State}");
    return 3;
}

Console.WriteLine("[TEST] PASS");
return 0;
