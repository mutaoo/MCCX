using System.Diagnostics;
using System.Text;
using MCCX.Core;
using MCCX.Core.Ipc;
using MCCX.Core.Networking;

// 无界面冒烟测试：
//   1) 账号加解密存储自测: dotnet run --project MCCX.SmokeTest -- --accounts
//   2) MCC 连服链路自测  : dotnet run --project MCCX.SmokeTest -- [host] [port] [version]
//   例                   : dotnet run --project MCCX.SmokeTest -- 127.0.0.1 25565 auto
//   3) 端口解析自测      : dotnet run --project MCCX.SmokeTest -- --port [srv <域名>]
//   4) 多开子进程自测    : dotnet run --project MCCX.SmokeTest -- --runner <MCCX.App.exe 路径> [host] [port]

if (args.Length > 0 && args[0] == "--accounts")
    return RunAccountStoreTest();

if (args.Length > 0 && args[0] == "--port")
    return await RunPortResolveTest(args.AsSpan(1).ToArray());

if (args.Length > 0 && args[0] == "--runner")
    return await RunRunnerProcessTest(args.AsSpan(1).ToArray());

string host = args.Length > 0 ? args[0] : "127.0.0.1";
ushort port = args.Length > 1 && ushort.TryParse(args[1], out ushort p) ? p : (ushort)25565;
string version = args.Length > 2 ? args[2] : "auto";

MCCRuntime.Initialize();

using MCCSession session = new();
session.LogReceived += text => Console.WriteLine(text);
session.StateChanged += state => Console.WriteLine($"[STATE] {state}");

Console.WriteLine($"[TEST] connecting to {host}:{port} ({version}) as MCCXBot …");

await session.ConnectAsync(new MCCConnectionOptions
{
    ServerHost = host,
    Port = port,
    Username = "MCCXBot",
    MinecraftVersion = version,
});

if (session.State != MCCConnectionState.Connected)
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
session.SendInput("hello from MCCX smoke test");

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

for (int i = 0; i < 20 && session.State != MCCConnectionState.Disconnected; i++)
    await Task.Delay(500);

if (session.State != MCCConnectionState.Disconnected)
{
    Console.WriteLine($"[TEST] FAIL: 断开超时，当前状态 {session.State}");
    return 3;
}

Console.WriteLine("[TEST] PASS");
return 0;

// 账号加密持久化自测：落盘 → 重开（模拟重启）→ 解密还原 → 合并/删除 → 密文不含明文 → 篡改容错。
static int RunAccountStoreTest()
{
    string dir = Path.Combine(Path.GetTempPath(), "mccx-accounts-" + Guid.NewGuid().ToString("N"));
    string dataPath = Path.Combine(dir, "accounts.dat");
    string aliceId;
    string bobId;

    using (var store = new AccountStore(dir))
    {
        if (store.Load().Count != 0)
        {
            Console.WriteLine("[TEST] FAIL: 空目录应返回空列表");
            return 10;
        }

        AccountProfile alice = store.Upsert(new AccountProfile
        {
            Username = "Alice",
            ServerHost = "mc.example.com",
            Port = 25565,
            MinecraftVersion = "auto",
        });

        bobId = store.Upsert(new AccountProfile
        {
            Username = "Bob",
            ServerHost = "10.0.0.2",
            Port = 25566,
            MinecraftVersion = "1.21.11",
        }).Id;

        aliceId = alice.Id;

        // 同一账号再存一次（按 用户名+主机+端口 识别）不应产生重复记录
        AccountProfile again = store.Upsert(new AccountProfile
        {
            Username = "Alice",
            ServerHost = "mc.example.com",
            Port = 25565,
        });

        if (again.Id != aliceId)
        {
            Console.WriteLine("[TEST] FAIL: 重复账号未合并");
            return 11;
        }

        if (store.LastError is not null)
        {
            Console.WriteLine($"[TEST] FAIL: 保存失败 {store.LastError}");
            return 12;
        }
    }

    // 重新打开 = 程序重启，必须能解密还原
    using (var store = new AccountStore(dir))
    {
        IReadOnlyList<AccountProfile> list = store.Load();
        if (list.Count != 2)
        {
            Console.WriteLine($"[TEST] FAIL: 重载数量 {list.Count} != 2，LastError={store.LastError}");
            return 13;
        }

        AccountProfile? alice = list.FirstOrDefault(p => p.Username == "Alice");
        if (alice is null || alice.ServerHost != "mc.example.com" || alice.Port != 25565
            || alice.MinecraftVersion != "auto" || alice.DisplayName != "Alice")
        {
            Console.WriteLine("[TEST] FAIL: 重载字段不一致");
            return 14;
        }

        if (!store.Remove(bobId))
        {
            Console.WriteLine("[TEST] FAIL: 删除未命中");
            return 15;
        }
    }

    using (var store = new AccountStore(dir))
    {
        if (store.Load().Count != 1)
        {
            Console.WriteLine($"[TEST] FAIL: 删除后数量应为 1，LastError={store.LastError}");
            return 16;
        }
    }

    // 落盘文件里不能出现明文用户名
    byte[] raw = File.ReadAllBytes(dataPath);
    if (raw.AsSpan().IndexOf(Encoding.ASCII.GetBytes("Alice")) >= 0)
    {
        Console.WriteLine("[TEST] FAIL: 数据文件中出现明文用户名");
        return 17;
    }

    // 篡改密文：应优雅失败（空列表 + 错误信息）而不是崩溃
    raw[^1] ^= 0xFF;
    File.WriteAllBytes(dataPath, raw);

    using (var store = new AccountStore(dir))
    {
        IReadOnlyList<AccountProfile> list = store.Load();
        if (list.Count != 0 || store.LastError is null)
        {
            Console.WriteLine($"[TEST] FAIL: 篡改后应返回空列表并记录错误，count={list.Count}");
            return 18;
        }
    }

    Directory.Delete(dir, recursive: true);
    Console.WriteLine("[TEST] accounts PASS");
    return 0;
}

// 端口自动解析自测：地址拆分 / IP 判定 / （可选）真实 DNS SRV 查询。
//   仅本地自测 : dotnet run --project MCCX.SmokeTest -- --port
//   真实 SRV   : dotnet run --project MCCX.SmokeTest -- --port srv <域名>
static async Task<int> RunPortResolveTest(string[] rest)
{
    int failures = 0;

    void Check(string input, string expectedHost, string? expectedPort)
    {
        (string host, string? port) = ServerAddress.Split(input);
        bool ok = host == expectedHost && port == expectedPort;
        string actual = $"(\"{host}\", {(port is null ? "null" : $"\"{port}\"")})";
        string expected = $"(\"{expectedHost}\", {(expectedPort is null ? "null" : $"\"{expectedPort}\"")})";
        Console.WriteLine($"[TEST] Split(\"{input}\") => {actual} {(ok ? "OK" : $"FAIL 期望 {expected}")}");
        if (!ok)
            failures++;
    }

    void CheckIp(string host, bool expected)
    {
        bool actual = ServerAddress.IsIpLiteral(host);
        bool ok = actual == expected;
        Console.WriteLine($"[TEST] IsIpLiteral(\"{host}\") = {actual} {(ok ? "OK" : $"FAIL 期望 {expected}")}");
        if (!ok)
            failures++;
    }

    Check("example.com", "example.com", null);
    Check("example.com:25566", "example.com", "25566");
    Check("  example.com:1 ", "example.com", "1");
    Check("127.0.0.1", "127.0.0.1", null);
    Check("127.0.0.1:25566", "127.0.0.1", "25566");
    Check("[::1]:25566", "::1", "25566");
    Check("[::1]", "::1", null);
    Check("::1", "::1", null);          // 裸 IPv6 有多个冒号，不拆
    Check("example.com:0", "example.com:0", null);      // 端口 0 不合法，不拆
    Check("example.com:abc", "example.com:abc", null);  // 端口非数字，不拆
    Check("", "", null);

    CheckIp("127.0.0.1", true);
    CheckIp("::1", true);
    CheckIp("example.com", false);

    if (rest.Length >= 2 && string.Equals(rest[0], "srv", StringComparison.OrdinalIgnoreCase))
    {
        string domain = rest[1];
        Console.WriteLine($"[TEST] SRV 查询 _minecraft._tcp.{domain} …");
        ushort? srvPort = await ServerAddress.TrySrvPortAsync(domain);
        Console.WriteLine(srvPort is null
            ? "[TEST] 未查到 SRV 记录（运行时会回退到 25565）"
            : $"[TEST] SRV 端口 = {srvPort}");
    }

    if (failures > 0)
    {
        Console.WriteLine($"[TEST] FAIL: {failures} 项不通过");
        return 20;
    }

    Console.WriteLine("[TEST] port PASS");
    return 0;
}

// 多开子进程链路自测：主进程（本测试）当“界面”，用 RunnerProcess 拉起 MCCX.App.exe --runner，
// 验证 管道通信 / 日志回传 / 连接 / 进入游戏 / 自动化配置 / 断开 / 子进程退出。
//   用法: dotnet run --project MCCX.SmokeTest -- --runner <MCCX.App.exe 路径> [host] [port]
static async Task<int> RunRunnerProcessTest(string[] rest)
{
    if (rest.Length == 0)
    {
        Console.WriteLine("[TEST] FAIL: 用法: --runner <MCCX.App.exe 路径> [host] [port]");
        return 30;
    }

    string exe = rest[0];
    string host = rest.Length > 1 ? rest[1] : "127.0.0.1";
    ushort port = rest.Length > 2 && ushort.TryParse(rest[2], out ushort p) ? p : (ushort)25565;

    if (!File.Exists(exe))
    {
        Console.WriteLine($"[TEST] FAIL: 找不到子程序 {exe}");
        return 31;
    }

    using RunnerProcess proc = new(exe);

    int logCount = 0;
    bool joined = false;
    int exitedCode = int.MinValue;

    proc.LogReceived += text =>
    {
        Interlocked.Increment(ref logCount);
        Console.WriteLine("[LOG] " + text);
    };
    proc.StateChanged += state => Console.WriteLine("[STATE] " + state);
    proc.GameJoined += () =>
    {
        joined = true;
        Console.WriteLine("[JOINED]");
    };
    proc.ProcessExited += code => Console.WriteLine($"[EXIT] {code}");

    Console.WriteLine("[TEST] 启动多开子进程 …");
    proc.Start();
    if (!proc.IsRunning || proc.ProcessId is null)
    {
        Console.WriteLine("[TEST] FAIL: 子进程没有启动");
        return 32;
    }

    int pid = proc.ProcessId.Value;
    Console.WriteLine($"[TEST] 子进程 PID = {pid}");

    // 子进程启动后应立刻回传就绪日志（无日志 = 管道没接通）
    for (int i = 0; i < 20 && logCount == 0; i++)
        await Task.Delay(250);

    if (logCount == 0)
    {
        Console.WriteLine("[TEST] FAIL: 子进程没有回传任何日志（管道不通）");
        proc.Dispose();
        return 33;
    }

    Console.WriteLine($"[TEST] 日志链路 OK（{logCount} 行）");
    Console.WriteLine($"[TEST] connecting to {host}:{port} …");

    await proc.ConnectAsync(new MCCConnectionOptions
    {
        ServerHost = host,
        Port = port,
        Username = "MCCXRunner",
        MinecraftVersion = "auto",
    });

    if (proc.State != MCCConnectionState.Connected)
    {
        Console.WriteLine($"[TEST] FAIL: 未进入连接状态（{proc.State}）");
        proc.Dispose();
        return 1;
    }

    for (int i = 0; i < 60 && !proc.IsGameJoined; i++)
        await Task.Delay(500);

    if (!joined || !proc.IsGameJoined)
    {
        Console.WriteLine("[TEST] FAIL: 等待进入游戏超时");
        proc.Dispose();
        return 4;
    }

    // 自动化配置：多开时这些参数走管道下发，子进程不能报错
    proc.ConfigureAttack(true, new AttackOptions { Range = 3.0 });
    proc.ConfigureMouse(false, new MouseOptions());
    proc.ConfigureFishing(false);
    proc.ConfigureReconnect(new ReconnectOptions { Enabled = false });
    await Task.Delay(500);
    if (exitedCode != int.MinValue)
    {
        Console.WriteLine($"[TEST] FAIL: 下发自动化配置后子进程退出（{exitedCode}）");
        proc.Dispose();
        return 34;
    }

    Console.WriteLine("[TEST] send input");
    if (!proc.SendInput("hello from MCCX runner test"))
    {
        Console.WriteLine("[TEST] FAIL: 输入没有投递");
        proc.Dispose();
        return 5;
    }

    await Task.Delay(1500);

    int logsBeforeDisconnect = logCount;
    Console.WriteLine("[TEST] disconnect");
    proc.Disconnect();

    for (int i = 0; i < 30 && proc.State != MCCConnectionState.Disconnected; i++)
        await Task.Delay(500);

    if (proc.State != MCCConnectionState.Disconnected)
    {
        Console.WriteLine($"[TEST] FAIL: 断开超时（{proc.State}）");
        proc.Dispose();
        return 3;
    }

    if (logCount < logsBeforeDisconnect)
    {
        Console.WriteLine("[TEST] FAIL: 断开后日志计数异常");
        proc.Dispose();
        return 35;
    }

    // Dispose 必须真正把子进程收干净（不能留僵尸进程）
    proc.Dispose();

    bool gone = false;
    for (int i = 0; i < 40 && !gone; i++)
    {
        try
        {
            using Process probe = Process.GetProcessById(pid);
            gone = probe.HasExited;
        }
        catch (ArgumentException)
        {
            gone = true; // 进程已不存在
        }

        if (!gone)
            await Task.Delay(250);
    }

    if (!gone)
    {
        Console.WriteLine($"[TEST] FAIL: Dispose 后子进程 PID={pid} 仍然存在（僵尸进程）");
        try
        {
            Process.GetProcessById(pid).Kill();
        }
        catch
        {
            // 已经没了就算了
        }

        return 36;
    }

    Console.WriteLine($"[TEST] runner PASS（日志 {logCount} 行，子进程已退出）");
    return 0;
}
