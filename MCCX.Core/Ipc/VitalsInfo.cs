using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core.Ipc;

/// <summary>
/// 血量 / 饥饿值的 IPC 载荷（<see cref="RunnerMessage.EvtVitals"/> 的 <c>Text</c>）。
/// <para>
/// 两个值都是 Minecraft 的 0-20 口径：<c>Health</c> 是血量（半颗心 = 0.5），<c>Food</c> 是饥饿值。
/// 字段名用短名，和 <see cref="RunnerMessage"/> 的其余事件保持一致的风格。
/// </para>
/// </summary>
public sealed class VitalsInfo
{
    [JsonPropertyName("h")]
    public float Health { get; set; }

    [JsonPropertyName("f")]
    public int Food { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, RunnerMessage.SharedJsonOptions);

    /// <summary>解析载荷；空 / 坏数据返回 null（坏行不能把父进程弄崩）。</summary>
    public static VitalsInfo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonSerializer.Deserialize<VitalsInfo>(text, RunnerMessage.SharedJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
