using MinecraftClient;

namespace MccX.Core;

/// <summary>
/// MccX 对 MCC 运行时的一次性初始化：
/// 安装 GUI 控制台后端、设置 MCC 的进程级运行参数。
/// 所有改动只作用于内存中的配置对象，绝不回写 .ini。
/// </summary>
public static class MccRuntime
{
    private static readonly object InitLock = new();
    private static MccUiBackend? backend;

    /// <summary>已安装的 GUI 后端。调用 <see cref="Initialize"/> 之前访问会抛异常。</summary>
    public static MccUiBackend Backend =>
        backend ?? throw new InvalidOperationException("MccRuntime.Initialize() 尚未调用。");

    public static bool IsInitialized => backend is not null;

    /// <summary>
    /// 幂等初始化。必须在创建任何 UI/会话对象之前调用（App.OnLaunched）。
    /// </summary>
    public static MccUiBackend Initialize()
    {
        lock (InitLock)
        {
            if (backend is not null)
                return backend;

            MccUiBackend uiBackend = new();

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

            backend = uiBackend;
            return uiBackend;
        }
    }
}
