using System.Text;
using MccX.Core;

// 无界面冒烟测试：
//   1) 账号加解密存储自测: dotnet run --project MccX.SmokeTest -- --accounts
//   2) MCC 连服链路自测  : dotnet run --project MccX.SmokeTest -- [host] [port] [version]
//   例                   : dotnet run --project MccX.SmokeTest -- 127.0.0.1 25565 auto

if (args.Length > 0 && args[0] == "--accounts")
    return RunAccountStoreTest();

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
