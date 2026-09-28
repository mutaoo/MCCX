namespace MCCX.Core;

/// <summary>一次连接所需的全部参数（离线模式）。</summary>
public sealed class MCCConnectionOptions
{
    /// <summary>服务器地址，可为域名或 IP。</summary>
    public string ServerHost { get; set; } = "127.0.0.1";

    public ushort Port { get; set; } = 25565;

    /// <summary>离线模式玩家名。</summary>
    public string Username { get; set; } = "Player";

    /// <summary>
    /// Minecraft 版本（如 "1.21.11"）。留空或 "auto" 表示连接前先 Ping 服务器自动探测。
    /// </summary>
    public string MinecraftVersion { get; set; } = "auto";
}

/// <summary>会话连接状态。</summary>
public enum MCCConnectionState
{
    /// <summary>未连接。</summary>
    Disconnected,

    /// <summary>正在 Ping/登录。</summary>
    Connecting,

    /// <summary>已进入游戏。</summary>
    Connected,

    /// <summary>界面主动断开中。</summary>
    Disconnecting,
}
