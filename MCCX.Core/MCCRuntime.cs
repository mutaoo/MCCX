using System.IO;
using MinecraftClient;

namespace MCCX.Core;

/// <summary>
/// MCCX 对 MCC 运行时的一次性初始化：
/// 安装 GUI 控制台后端、设置 MCC 的进程级运行参数。
/// 所有改动只作用于内存中的配置对象，绝不回写 .ini。
/// </summary>
public static class MCCRuntime
{
    private static readonly object InitLock = new();
    private static MCCUiBackend? backend;

    /// <summary>诊断 tee 的写锁（见 <see cref="Initialize"/> 里的 MCCX_VIEW_CAPTURE 分支）。</summary>
    private static readonly object CaptureGate = new();

    /// <summary>已安装的 GUI 后端。调用 <see cref="Initialize"/> 之前访问会抛异常。</summary>
    public static MCCUiBackend Backend =>
        backend ?? throw new InvalidOperationException("MCCRuntime.Initialize() 尚未调用。");

    public static bool IsInitialized => backend is not null;

    /// <summary>
    /// 幂等初始化。必须在创建任何 UI/会话对象之前调用（App.OnLaunched）。
    /// </summary>
    public static MCCUiBackend Initialize()
    {
        lock (InitLock)
        {
            if (backend is not null)
                return backend;

            MCCUiBackend uiBackend = new();

            // MCC 的所有日志都走 Backend.WriteLineFormatted（BasicIO=false）
            ConsoleIO.BasicIO = false;
            ConsoleIO.Backend = uiBackend;

            // 关键：InteractiveMode=true 会让 MCC 的失败路径进入离线提示而不是 Environment.Exit，
            // 否则一次连接失败就会杀掉整个 GUI 进程。
            Settings.InternalConfig.InteractiveMode = true;

            // 挂机 Bot（钓鱼/攻击/寻路/背包）依赖的三大处理开关，仅在内存里打开。
            Settings.Config.Main.Advanced.TerrainAndMovements = true;
            Settings.Config.Main.Advanced.InventoryHandling = true;
            Settings.Config.Main.Advanced.EntityHandling = true;

            // 死亡后自动重生（MCC 默认关闭，关掉就会卡在死亡界面等玩家手动按重生）。
            // 打开后 MCC 在收到血量 <= 0 时等 1 秒自己发重生包。
            Settings.Config.Main.Advanced.AutoRespawn = true;

            // 行走时头部跟随路点（MCC 默认开启）必须关：它是"视角被行走自动改变"的唯一源头。
            // 开着时每次路点 dequeue 都把路点方位写进 _yaw 发给服务端（McClient.UpdatePathfindingInput
            // 里 MoveHeadWhileWalking 的两处 UpdateLocation），绕障/下坡能偏出 90°+；而路点之间
            // _yaw=null 不发旋转，服务端会一直保持最后一次路点朝向——实测行走 6 秒里服务端视角
            // 被钉在 -52° 之类（g_view G7 根因）。关掉后服务端朝向的唯一写手是 ViewControlBot
            // （按钮转向 + 保持期），与"进服视角不被自动改变、与 MCC 一致"的设计一致；
            // 行进方向不受影响：SetInputToward 照常按路点驱动移动，只是不再把头部发给服务端
            //（与原版玩家"头不动、方向相对头走"的行为一致）。
            Settings.Config.Main.Advanced.MoveHeadWhileWalking = false;

            // 诊断口子（2026-10-05 现场"进服后视角很快变正南"）：把 MCC 的全部输出 tee 到文件，
            // 供离线复盘。只在环境变量 MCCX_VIEW_CAPTURE=1 时启用——正常使用完全不落盘、不多写一行，
            // 也不影响界面日志；文件路径可用 MCCX_VIEW_CAPTURE_FILE 覆盖。
            // 不改 MCC 的 Log（换成 FileLogLogger 会顶掉 FilteredLogger，界面反而收不到日志）。
            if (System.Environment.GetEnvironmentVariable("MCCX_VIEW_CAPTURE") == "1")
            {
                // 包级日志：确认"进服后视角变南"到底是服务器又推了个位置包，还是本地写的。
                // DebugEnabled 在 McClient 构造时读一次，所以必须在这里（建会话之前）打开。
                Settings.Config.Logging.DebugMessages = true;
                Settings.Config.Logging.PacketDebugMessages = true;

                string capturePath = System.Environment.GetEnvironmentVariable("MCCX_VIEW_CAPTURE_FILE")
                    ?? Path.Combine(Path.GetTempPath(), "mccx-view-capture.log");

                uiBackend.OutputReceived += line =>
                {
                    try
                    {
                        lock (CaptureGate)
                        {
                            File.AppendAllText(capturePath, line + Environment.NewLine);
                        }
                    }
                    catch
                    {
                        // 诊断口子写失败绝不能影响挂机
                    }
                };
            }

            backend = uiBackend;
            return uiBackend;
        }
    }
}
