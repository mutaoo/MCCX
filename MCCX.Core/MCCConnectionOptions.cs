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

    /// <summary>客户端标识跟随全局设置（ui-settings.json 里的「客户端标识」）。</summary>
    public const int BrandModeFollowGlobal = 0;

    /// <summary>客户端标识固定为 vanilla（原版标识，MCC 发送字面量 "vanilla"）。</summary>
    public const int BrandModeVanilla = 1;

    /// <summary>客户端标识固定为 mcc（"Minecraft-Console-Client/版本"）。</summary>
    public const int BrandModeMcc = 2;

    /// <summary>不发送客户端标识。</summary>
    public const int BrandModeEmpty = 3;

    /// <summary>
    /// 客户端标识（<c>minecraft:brand</c>，2026-11-05 需求：按账号覆盖全局设置）。
    /// 取值见 <see cref="BrandModeFollowGlobal"/> 等常量；超出范围按跟随全局处理。
    /// </summary>
    public int BrandMode { get; set; } = BrandModeFollowGlobal;
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
