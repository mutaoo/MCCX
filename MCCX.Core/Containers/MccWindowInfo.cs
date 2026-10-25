using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core.Containers;

/// <summary>
/// 一个容器窗口（服务器开的箱子/商店/铁砧 GUI，或玩家自己的背包）在界面层的投影：
/// 标题、类型、网格尺寸（或分组）、格子内容，以及"光标上正拿着的那一摞"。
/// 纯数据、不含 MCC 类型，可以直接过 IPC 管道传输。
///
/// <para>为什么要单独建模：容器内容在子进程的 MCC 会话里，窗口却要画在主进程的 WinUI 里，
/// 父子之间只有一行一个 JSON 的管道（见 <c>Ipc.RunnerMessage</c>），传不了 MCC 的
/// <c>Container</c>/<c>Item</c>。</para>
///
/// <para>增量：窗口打开时发一次全量（<see cref="Full"/> = true），之后每次变化只发变化的格子
/// （<see cref="Full"/> = false，<see cref="Slots"/> 里就是那几个格子），免得每次 SetSlot
/// 都把 54 个格子重新序列化一遍。</para>
/// </summary>
public sealed class MccWindowInfo
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>窗口编号（MCC 的容器 ID，玩家背包是 0，服务器开的窗口从 1 起）。</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>窗口标题（已去掉 § 颜色码）。服务器没给标题时为空串。</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>容器类型名（MCC 的 <c>ContainerType</c>，如 Generic_9x3 / ShulkerBox / Furnace）。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>网格列数（界面按它铺格子）。</summary>
    [JsonPropertyName("cols")]
    public int Columns { get; set; }

    /// <summary>网格行数。</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; set; }

    /// <summary>true = 全量快照（窗口刚打开）；false = <see cref="Slots"/> 只含变化的格子。</summary>
    [JsonPropertyName("full")]
    public bool Full { get; set; }

    /// <summary>
    /// 这份快照是<b>玩家自己的背包</b>（MCC 的窗口 0）而不是服务器开的窗口。
    /// 界面对它的处理不同：标题固定、不发"关窗包"、关闭只表示"不再推"。
    /// </summary>
    [JsonPropertyName("player")]
    public bool Player { get; set; }

    /// <summary>
    /// 分组显示用（有值时界面按它铺，<see cref="Columns"/> / <see cref="Rows"/> 只当兜底）：
    /// 玩家背包是装备 / 背包 / 快捷栏三块；服务器窗口是容器自己那块（无小标题）+ 玩家物品栏那块
    /// （"物品栏"，窗口里最后 36 格）。组里带顺序，所以格子号可以不是 0,1,2,…。
    /// </summary>
    [JsonPropertyName("groups")]
    public List<MccWindowGroup>? Groups { get; set; }

    /// <summary>有东西的格子数（不含光标），给状态行显示。</summary>
    [JsonPropertyName("items")]
    public int ItemCount { get; set; }

    /// <summary>格子内容。光标上那一摞用 <see cref="MccWindowSlot.CursorIndex"/> 表示。</summary>
    [JsonPropertyName("slots")]
    public List<MccWindowSlot> Slots { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 面板会画出来的格子号（按显示顺序）。分组模式按组依次拼起来；不分组就是 0,1,2,…
    /// （服务器窗口的格子号就是网格位置，玩家背包不是，所以玩家背包必须走分组）。
    /// </summary>
    public List<int> DisplaySlots()
    {
        if (Groups is { Count: > 0 } groups)
        {
            int total = 0;
            foreach (MccWindowGroup group in groups)
                total += group.Slots.Count;

            List<int> all = new(total);
            foreach (MccWindowGroup group in groups)
                all.AddRange(group.Slots);

            return all;
        }

        int count = Math.Max(0, Columns) * Math.Max(0, Rows);
        List<int> slots = new(count);
        for (int i = 0; i < count; i++)
            slots.Add(i);

        return slots;
    }

    /// <summary>解析一段 JSON；坏数据返回 null（界面不能因为一条坏消息崩掉）。</summary>
    public static MccWindowInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MccWindowInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 面板里的一组格子（可带小标题）。
///
/// <para>存在的理由：玩家背包（MCC 的窗口 0）的格子号是 0-45 的"窗口槽位号"，
/// 显示顺序和格子号对不上（装备是 5-8、副手是 45、背包 9-35、快捷栏 36-44，中间还夹着合成格），
/// 所以由这里显式给出"这一组按什么顺序显示哪几个格子号"。</para>
/// </summary>
public sealed class MccWindowGroup
{
    /// <summary>组标题（空串 = 不画标题）。</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>这一组每行几个格子。</summary>
    [JsonPropertyName("cols")]
    public int Columns { get; set; } = 9;

    /// <summary>组内按显示顺序排的格子号。</summary>
    [JsonPropertyName("slots")]
    public List<int> Slots { get; set; } = [];

    /// <summary>
    /// 这一组摆在<b>原版背包界面</b>的哪个位置（取值见 <see cref="MccWindowGroupRole"/>）。
    ///
    /// <para>只有玩家背包（窗口 0）带这个：原版背包的左右关系不是"一组接一组从上往下堆"
    /// （装备横排在顶上、副手挂在同一行右边、背包和快捷栏在下面，见 MCCX.App 的玩家背包布局），
    /// 靠 <see cref="Columns"/> 排不出来，所以让子进程把位置一起说明白。</para>
    ///
    /// <para>空串 = 按 <see cref="Columns"/> 铺一张网格并画 <see cref="Label"/>（服务器窗口那条老路）。</para>
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

/// <summary>分组在原版背包界面里的位置（<see cref="MccWindowGroup.Role"/> 的取值）。</summary>
public static class MccWindowGroupRole
{
    /// <summary>左边竖排的装备栏（头盔 / 胸甲 / 护腿 / 靴子）。</summary>
    public const string Armor = "armor";

    /// <summary>副手那一格（装备栏下面）。</summary>
    public const string Offhand = "offhand";

    /// <summary>3×9 背包。</summary>
    public const string Main = "main";

    /// <summary>1×9 快捷栏。</summary>
    public const string Hotbar = "hotbar";
}

/// <summary>容器里的一个格子（或光标上那一摞，见 <see cref="CursorIndex"/>）。</summary>
public sealed class MccWindowSlot
{
    /// <summary>光标上拿着的那一摞的"格子号"（MCC 用 -1 表示光标）。</summary>
    public const int CursorIndex = -1;

    /// <summary>格子号。</summary>
    [JsonPropertyName("i")]
    public int Index { get; set; }

    /// <summary>
    /// 物品注册名（snake_case，如 diamond_sword / oak_planks），空串表示这个格子是空的
    /// （物品图标按它取，见 MCCX.App 的 ItemIconProvider）。
    /// </summary>
    [JsonPropertyName("item")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>
    /// 这是"方块的物品形态"（MCC 语言表里归在 <c>block.minecraft.*</c> 名下）。
    /// 界面取图标时靠它选来源：方块在游戏里显示的是三维模型，物品显示的是那张 16px 像素画。
    /// </summary>
    [JsonPropertyName("block")]
    public bool Block { get; set; }

    /// <summary>数量。</summary>
    [JsonPropertyName("n")]
    public int Count { get; set; }

    /// <summary>显示名：服务器自定义名优先，没有就查中文名表（查不到退回 MCC 的英文名）。可能带 § 颜色码。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>lore 各行（可能带 § 颜色码）。没有就是 null。</summary>
    [JsonPropertyName("lore")]
    public List<string>? Lore { get; set; }

    /// <summary>附魔（形如 "锋利 V"）。没有就是 null。</summary>
    [JsonPropertyName("ench")]
    public List<string>? Enchantments { get; set; }

    /// <summary>已损耗的耐久（0 = 满耐久或不可损耗）。</summary>
    [JsonPropertyName("dmg")]
    public int Damage { get; set; }

    /// <summary>最大耐久（0 = 没有这个信息）。</summary>
    [JsonPropertyName("maxdmg")]
    public int MaxDamage { get; set; }

    /// <summary>格子是否空着（空串注册名）。</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(ItemId);
}

/// <summary>界面点了一个格子：窗口编号 + 格子号 + 动作。</summary>
public sealed class MccWindowClick
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    [JsonPropertyName("id")]
    public int WindowId { get; set; }

    /// <summary>格子号；<see cref="MccWindowSlot.CursorIndex"/> = 光标那一摞；<see cref="CloseWindowSlot"/> = 关窗口。</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    /// <summary>动作，取值见 <see cref="MccWindowAction"/>。</summary>
    [JsonPropertyName("act")]
    public string Action { get; set; } = MccWindowAction.Left;

    /// <summary>"关掉这个窗口"用的假格子号（MCC 没有"关光标那一摞"的动作，借这个值区分）。</summary>
    public const int CloseWindowSlot = -2;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MccWindowClick? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MccWindowClick>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>界面能发起的容器动作名（与 MCC 的 <c>WindowActionType</c> 对应）。</summary>
public static class MccWindowAction
{
    public const string Left = "left";
    public const string Right = "right";
    public const string ShiftLeft = "shiftleft";
    public const string ShiftRight = "shiftright";
    public const string DropOne = "dropone";
    public const string DropStack = "dropstack";

    /// <summary>关掉容器窗口（不是点格子，借 <see cref="MccWindowClick.CloseWindowSlot"/> 一起下发）。</summary>
    public const string Close = "close";
}
