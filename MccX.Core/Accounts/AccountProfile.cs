using System.Text.Json.Serialization;

namespace MccX.Core;

/// <summary>
/// 一个已保存的历史账号（对应需求 3.1「账号持久化」的本地记录）。
/// 离线模式没有真正的密码，<see cref="Credential"/> 用于预留在线模式 Token，
/// 无论是否有值都会随整体数据一起加密落盘。
/// </summary>
public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>离线模式玩家名。</summary>
    public string Username { get; set; } = string.Empty;

    public string ServerHost { get; set; } = "127.0.0.1";

    public ushort Port { get; set; } = 25565;

    /// <summary>Minecraft 版本，"auto" 表示连接时 Ping 探测。</summary>
    public string MinecraftVersion { get; set; } = "auto";

    /// <summary>列表里显示的名字，保存时若为空会回填为 <see cref="Username"/>。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>凭据（离线模式为 null，加密存储）。</summary>
    public string? Credential { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>列表副标题，不参与序列化。</summary>
    [JsonIgnore]
    public string ServerSummary => $"{ServerHost}:{Port}";
}
