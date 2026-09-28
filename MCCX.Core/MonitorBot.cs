using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 连接状态监视 Bot：只负责把“进入游戏”和“断开连接”上报给 <see cref="MCCSession"/>，
/// 自身不执行任何游戏操作。OnDisconnect 永远返回 false，表示不接管 MCC 的重连逻辑。
/// </summary>
internal sealed class MonitorBot : ChatBot
{
    /// <summary>进入游戏（AfterGameJoined）。可能在 MCC 主线程触发。</summary>
    public event Action? Joined;

    /// <summary>断开连接（原因 + 服务器消息）。可能在 MCC 主线程触发。</summary>
    public event Action<DisconnectReason, string>? Disconnected;

    public override void AfterGameJoined()
    {
        Joined?.Invoke();
    }

    public override bool OnDisconnect(DisconnectReason reason, string message)
    {
        Disconnected?.Invoke(reason, message);
        return false;
    }
}
