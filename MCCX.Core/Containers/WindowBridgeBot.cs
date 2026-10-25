using MCCX.Core.Localization;
using MinecraftClient;
using MinecraftClient.Inventory;
using MinecraftClient.Protocol;
using MinecraftClient.Protocol.Message;
using MinecraftClient.Scripting;
using MinecraftClient.Protocol.Handlers.StructuredComponents.Components._1_20_6;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace MCCX.Core.Containers;

/// <summary>
/// 把 MCC 的 <see cref="ItemType"/> 换成物品注册名（snake_case，如 diamond_sword / oak_planks）。
///
/// <para>MCC 的枚举名是注册名按 <c>mc_name_to_csharp</c> 生成的 PascalCase
/// （<c>diamond_sword</c> → <c>DiamondSword</c>），所以用 MCC 自己的
/// <c>ToUnderscoreCase</c> 基本能反推回来。例外是名字里带数字的（<c>music_disc_11</c> 生成成
/// <c>MusicDisc11</c>，反推成 <c>music_disc11</c> 少一个下划线）：这类拿内置的
/// <c>en_us.json</c> 语言表逐个候选试一次，试中哪个用哪个。</para>
///
/// <para>顺带分出"方块的物品形态"（名字归在 <c>block.minecraft.*</c> 名下）：界面上这两种
/// 在游戏里的样子不一样（方块是三维模型、物品是 16px 像素画），取图标走的路也不同。</para>
/// </summary>
public static class MccItemName
{
    private static readonly ConcurrentDictionary<ItemType, MccItemRef> Cache = new();

    /// <summary>
    /// 物品注册名 + 是不是方块（<c>block.minecraft.*</c> 里的名字）。
    /// 界面上"方块的物品形态"在游戏里显示的是三维模型，取图标走的路不一样，所以顺带带上这个。
    /// </summary>
    public static MccItemRef Resolve(ItemType type)
    {
        if (type is ItemType.Air or ItemType.Null or ItemType.Unknown)
            return new MccItemRef(string.Empty, false);

        return Cache.GetOrAdd(type, ResolveKind);
    }

    private static MccItemRef ResolveKind(ItemType type)
    {
        // 两个同名扩展（MCC 与 Inventory 里各一份，行为一致）在这里会二义，显式指定一个
        string snake = MinecraftClient.ExtensionMethods.ToUnderscoreCase(type.ToString());
        if (IsKnown(snake, out bool block))
            return new MccItemRef(snake, block);

        // 数字串前面补一个下划线再试（music_disc11 → music_disc_11）
        string underscored = Regex.Replace(snake, "(?<=[a-z])(?=[0-9])", "_");
        if (!string.Equals(underscored, snake, StringComparison.Ordinal) && IsKnown(underscored, out block))
            return new MccItemRef(underscored, block);

        // 名称不在语言表里（新版物品、未覆盖的名称）：原样返回、当物品处理，图标取不到就当没图标
        return new MccItemRef(snake, false);
    }

    /// <summary>名字在 MCC 内置的 en_us.json 里存在；<paramref name="block"/> = 只在方块名下有。</summary>
    private static bool IsKnown(string name, out bool block)
    {
        block = false;

        if (ChatParser.TranslateString("item.minecraft." + name) is not null)
            return true;

        if (ChatParser.TranslateString("block.minecraft." + name) is not null)
        {
            block = true;
            return true;
        }

        return false;
    }

    /// <summary>去掉 § 颜色码（日志/提示这类纯文本用；格子名和 lore 保留颜色码给界面着色）。</summary>
    public static string StripColor(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return Regex.Replace(text, "§.", string.Empty);
    }
}

/// <summary>物品注册名，以及它是不是"方块的物品形态"。</summary>
public readonly record struct MccItemRef(string Name, bool Block);

/// <summary>
/// 容器窗口桥（2026-10-16 需求：在 MCCX 里实现服务器 GUI 窗口，点格子执行操作）。
///
/// <para>只做三件事：MCC 的窗口事件 → <see cref="MccWindowInfo"/>（纯数据、过 IPC），
/// 界面的点击 → MCC 的 <see cref="ChatBot.WindowAction"/>，界面的关闭 → <see cref="ChatBot.CloseInventory"/>。</para>
///
/// <para>不碰协议：点击包里的 stateId、"changed slots" 都由 MCC 的
/// <c>McClient.DoWindowAction</c> 自己算（还会顺手做本地预测，所以点完立刻刷一版界面，
/// 服务端确认包到了再刷一次）。</para>
///
/// <para>玩家窗口 0 有两份用途：服务器窗口开着时读它拿"光标上那一摞"（MCC 把光标记在
/// <c>inventories[0].Items[-1]</c>）；界面点开"背包"面板时（<see cref="WatchPlayerInventory"/>）
/// 把窗口 0 的装备 / 背包 / 快捷栏按分组推给界面。</para>
///
/// <para>服务器窗口发的是"整个菜单"：容器自己那块 + 玩家物品栏那块（窗口里最后 36 格，
/// 见 <see cref="BuildWindowGroups"/>），所以界面把物品栏一并画在容器下面（窗口与背包联动），
/// 点物品栏里的一格走的就是这个窗口的真实槽位号。</para>
///
/// <para>格子上的名字用的是中文（<see cref="MccChineseNames"/>）：服务器自定义名原样显示，
/// MCC 的英文名查中文名表换掉；表还没下来（没网/第一次开）就先发英文，表好了重发一次。</para>
/// </summary>
public sealed class WindowBridgeBot : ChatBot
{
    /// <summary>玩家自己的物品栏占 36 格（背包 27 + 快捷栏 9）：窗口里排在最后，容器内容 = 总槽位数 - 36。</summary>
    private const int PlayerInventorySlots = 36;

    private readonly object _gate = new();

    /// <summary>窗口 → 格子 → 内容签名（算增量用）；光标那一摞记在键 <see cref="MccWindowSlot.CursorIndex"/> 上。</summary>
    private readonly Dictionary<int, Dictionary<int, string>> _sent = new();

    /// <summary>最近一次打开的服务器窗口（同屏一般只有一个）。</summary>
    private int _activeWindowId;

    /// <summary>界面开着"背包"面板：打开时连玩家背包（窗口 0）的内容一起推。</summary>
    private bool _watchPlayer;

    /// <summary>窗口打开或内容变化。<see cref="MccWindowInfo.Full"/> 为 true 表示全量快照。</summary>
    public event Action<MccWindowInfo>? WindowChanged;

    /// <summary>服务器关掉了这个窗口。</summary>
    public event Action<int>? WindowClosed;

    /// <summary>
    /// 进服后就把中文名表在后台准备好：等玩家点开箱子时表已经在内存里，
    /// 名字直接是中文，不会先闪一下英文。
    /// </summary>
    public override void AfterGameJoined()
    {
        EnsureChineseNames();
    }

    public override void OnInventoryOpen(int inventoryId)
    {
        if (inventoryId == 0)
            return;

        lock (_gate)
        {
            _activeWindowId = inventoryId;
            _sent.Remove(inventoryId);
        }

        EnsureChineseNames();
        string type = GetContainerTypeName(inventoryId);
        LogToConsole($"§7[容器] 服务器打开了窗口 #{inventoryId}（{type}），界面已弹出；点格子执行操作，面板右上角可以关掉。");
        Publish(inventoryId, full: true);
    }

    public override void OnInventoryUpdate(int inventoryId)
    {
        if (inventoryId == 0)
        {
            // 窗口 0 的更新有两层意思：① 光标那一摞变了（MCC 把光标记在窗口 0 上），
            // 正开着的服务器窗口要跟着刷；② 玩家背包自己变了（拾取/移动/丢弃），
            // 界面开着"背包"面板时一起刷。
            bool watch;
            int active;
            lock (_gate)
            {
                watch = _watchPlayer;
                active = _activeWindowId;
            }

            if (active != 0 && HasWindow(active))
                Publish(active, full: false);

            if (watch)
                Publish(0, full: false);

            return;
        }

        Publish(inventoryId, full: false);
    }

    public override void OnInventoryClose(int inventoryId)
    {
        if (inventoryId == 0)
            return;

        lock (_gate)
        {
            _sent.Remove(inventoryId);
            if (_activeWindowId == inventoryId)
                _activeWindowId = 0;
        }

        WindowClosed?.Invoke(inventoryId);
    }

    /// <summary>
    /// 界面开 / 收"背包"面板。
    ///
    /// <para>打开：立刻发一版玩家背包（窗口 0）的全量，之后背包一变就跟着发增量。</para>
    ///
    /// <para>收起：停止推送。<b>如果这时服务器窗口还开着</b>（界面切到背包面板前在看的那个），
    /// 顺手把它按全量重发一次：界面收的是"背包"面板，不是服务器那个窗口，得回到原样。</para>
    /// </summary>
    public void WatchPlayerInventory(bool watch)
    {
        int active;
        lock (_gate)
        {
            _watchPlayer = watch;
            // 重新打开时把那次的差异记录丢掉，确保界面上收到的第一份是全量（能重铺网格）
            _sent.Remove(0);
            active = _activeWindowId;
        }

        if (watch)
        {
            EnsureChineseNames();
            Publish(0, full: true);
            return;
        }

        if (active != 0 && HasWindow(active))
            Publish(active, full: true);
    }

    /// <summary>
    /// 顺手把中文名表在后台准备好（幂等）；好了以后把界面正看着的那份面板按全量重发一次，
    /// 先出来的英文名就换成中文了。
    /// </summary>
    private void EnsureChineseNames()
    {
        MccChineseNames.EnsureLoaded(ConnectedVersion(), RepublishChineseNames);
    }

    /// <summary>当前连接的 MC 版本（如 26.1）；拿不到就给 null，由中文名表按常识挑一个。</summary>
    private string? ConnectedVersion()
    {
        try
        {
            string version = ProtocolHandler.ProtocolVersion2MCVer(GetProtocolVersion());

            // ProtocolVersion2MCVer 认不出来时回 "0.0"
            return version.StartsWith("0.", StringComparison.Ordinal) ? null : version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 中文名表好了：把界面正在看的窗口按全量重发一次（格子签名里带名字，重发即刷新）。
    /// 走 MCC 的主线程：<see cref="Publish"/> 要读 MCC 的背包表，别和它自己的线程抢。
    /// </summary>
    private void RepublishChineseNames()
    {
        try
        {
            Handler.InvokeOnMainThread(() =>
            {
                bool watch;
                int active;
                lock (_gate)
                {
                    watch = _watchPlayer;
                    active = _activeWindowId;
                }

                if (!watch && (active == 0 || !HasWindow(active)))
                    return; // 没有面板在开着：不用刷

                LogToConsole($"§7[容器] 中文名表已就绪（{MccChineseNames.Count} 条），物品名已换成中文。");

                if (watch)
                    Publish(0, full: true);
                else
                    Publish(active, full: true);
            });
        }
        catch
        {
            // 会话已经拆了：没人在看面板
        }
    }

    /// <summary>
    /// 断开连接时清掉"背包面板开着"的状态：只清状态、<b>不</b>重发服务器窗口
    /// （界面这时候正因为断开在收面板，重发只会让它闪一下又收）。
    /// </summary>
    public void StopPlayerInventoryWatch()
    {
        lock (_gate)
        {
            _watchPlayer = false;
            _sent.Remove(0);
        }
    }

    /// <summary>点一个格子；<see cref="MccWindowAction.Close"/> 时关掉整个窗口。返回是否已投递。</summary>
    public bool Click(int windowId, int slot, string action)
    {
        if (string.Equals(action, MccWindowAction.Close, StringComparison.Ordinal))
            return CloseInventory(windowId);

        if (!Enum.TryParse(ToActionType(action), out WindowActionType type))
            return false;

        if (!WindowAction(windowId, slot, type))
            return false;

        // MCC 在 DoWindowAction 里已经本地预测了结果，这里立刻发一版，界面不用等服务端往返
        Publish(windowId, full: false);
        return true;
    }

    /// <summary>把窗口内容（或和上次的差异）发出去。</summary>
    private void Publish(int windowId, bool full)
    {
        if (!TryGetContainer(windowId, out Container container))
            return; // 还没进服 / 物品信息没到

        MccWindowInfo info = new()
        {
            Id = windowId,
            Full = full,
            Title = MccItemName.StripColor(container.Title),
            Type = container.Type.ToString(),
        };

        if (windowId == 0)
        {
            // 玩家背包（窗口 0 有 46 格：0-4 合成、5-8 装备、9-35 背包、36-44 快捷栏、45 副手）。
            // 只画装备 / 背包 / 快捷栏三组：合成格在 MCCX 里没法用（拖不进去也点不到配方），
            // 显示顺序又和格子号对不上，所以走分组（组里带格子号）。
            info.Player = true;
            info.Groups = PlayerInventoryGroups;
        }
        else
        {
            (int columns, int rows) = GetLayout(container.Type);
            info.Columns = columns;
            info.Rows = rows;

            // 服务器窗口是"整个菜单"（MCC 的容器里就带着玩家的 36 格）：界面把容器自己那块
            // 和玩家物品栏一起画出来（2026-10-16 需求：服务器窗口要和背包联动）。
            info.Groups = BuildWindowGroups(container.Type, columns);
        }

        Dictionary<int, string> previous;
        lock (_gate)
        {
            if (!_sent.TryGetValue(windowId, out Dictionary<int, string>? map))
                map = new Dictionary<int, string>();

            previous = map;
        }

        Dictionary<int, string> current = new();
        List<MccWindowSlot> changed = new();
        int itemCount = 0;

        foreach (int slot in info.DisplaySlots())
        {
            Item? item = container.Items.TryGetValue(slot, out Item? found) ? found : null;
            MccWindowSlot model = BuildSlot(slot, item);
            string signature = BuildSignature(model);
            current[slot] = signature;

            if (!model.IsEmpty)
                itemCount++;

            if (full || !previous.TryGetValue(slot, out string? old) || !string.Equals(old, signature, StringComparison.Ordinal))
                changed.Add(model);
        }

        // 光标那一摞：MCC 存在玩家窗口里，这里读出来一起发（Index = -1）
        MccWindowSlot cursor = BuildCursorSlot();
        string cursorSignature = BuildSignature(cursor);
        current[MccWindowSlot.CursorIndex] = cursorSignature;
        if (full || !previous.TryGetValue(MccWindowSlot.CursorIndex, out string? oldCursor)
                 || !string.Equals(oldCursor, cursorSignature, StringComparison.Ordinal))
        {
            changed.Add(cursor);
        }

        lock (_gate)
        {
            _sent[windowId] = current;
        }

        if (!full && changed.Count == 0)
            return; // 内容没变（比如只是别的窗口在动）

        info.Slots = changed;
        info.ItemCount = itemCount;
        WindowChanged?.Invoke(info);
    }

    /// <summary>
    /// 玩家背包面板的分组（格子号是 MCC 窗口 0 的槽位号）：装备（头盔 / 胸甲 / 护腿 / 靴子）、
    /// 副手、背包 27 格、快捷栏 9 格，各自标了在原版背包界面里的位置（<see cref="MccWindowGroup.Role"/>）。
    ///
    /// <para>合成格（0-4）不发：MCCX 里点不出配方，画出来只会让人以为能用。</para>
    ///
    /// <para><see cref="MccWindowGroup.Label"/> 一律留空：2026-10-18 用户要求界面按原版背包的样子摆，
    /// 原版那三块没有小标题（哪块是什么看位置就知道），界面的副标题里也写了。</para>
    ///
    /// <para>装备栏的 <see cref="MccWindowGroup.Columns"/> 给 4：界面把装备与副手摆成<b>一行</b>
    /// （2026-10-18 用户反馈竖排"竖直占比太高"），列数和实际摆法保持一致。</para>
    /// </summary>
    private static List<MccWindowGroup> PlayerInventoryGroups { get; } =
    [
        new MccWindowGroup
        {
            Role = MccWindowGroupRole.Armor,
            Columns = 4,
            Slots = [5, 6, 7, 8],
        },
        new MccWindowGroup
        {
            Role = MccWindowGroupRole.Offhand,
            Columns = 1,
            Slots = [45],
        },
        new MccWindowGroup
        {
            Role = MccWindowGroupRole.Main,
            Columns = 9,
            Slots = [.. Enumerable.Range(9, 27)],
        },
        new MccWindowGroup
        {
            Role = MccWindowGroupRole.Hotbar,
            Columns = 9,
            Slots = [.. Enumerable.Range(36, 9)],
        },
    ];

    /// <summary>
    /// 服务器窗口的两块：容器自己那块（不画小标题）+ 玩家物品栏那块（背包 27 + 快捷栏 9）。
    ///
    /// <para>槽位号直接用 MCC 窗口里的号（服务器发过来的整个菜单就是这么多格），所以"物品栏"里
    /// 点一格发出去的是这个窗口的真实槽位号，服务端认得：和游戏里开箱子点自己的背包是同一条路，
    /// 不需要往窗口 0 上绕（窗口 0 只在没开容器时才是玩家背包）。</para>
    ///
    /// <para>装备 / 副手不在里面：服务器开窗口时根本不会发那几个格子（它们在窗口 0 里）。
    /// 服务器窗口也不会是 <c>Player</c> 类型（那是 MCC 自己的窗口 0），这里不专门照顾。</para>
    /// </summary>
    private static List<MccWindowGroup> BuildWindowGroups(ContainerType type, int columns)
    {
        int total = Math.Max(0, type.SlotCount());
        int playerStart = Math.Max(0, total - PlayerInventorySlots);

        List<MccWindowGroup> groups =
        [
            new MccWindowGroup
            {
                // 容器自己的格子：0 到处，顺序就是格子号（铺出来和游戏里一样）
                Columns = columns,
                Slots = [.. Enumerable.Range(0, playerStart)],
            },
        ];

        if (total > playerStart)
        {
            groups.Add(new MccWindowGroup
            {
                // 玩家物品栏：27 格背包 + 9 格快捷栏，连排（游戏里开箱子时下面那一块）
                Label = "物品栏",
                Columns = 9,
                Slots = [.. Enumerable.Range(playerStart, total - playerStart)],
            });
        }

        return groups;
    }

    private bool HasWindow(int windowId) => TryGetContainer(windowId, out _);

    private string GetContainerTypeName(int windowId)
        => TryGetContainer(windowId, out Container container) ? container.Type.ToString() : "Unknown";

    /// <summary>取一个窗口（玩家窗口 0 / 服务器容器都走这里）。</summary>
    private bool TryGetContainer(int windowId, out Container container)
    {
        container = null!;
        try
        {
            Dictionary<int, Container> inventories = GetInventories();
            if (inventories.TryGetValue(windowId, out Container? found) && found is not null)
            {
                container = found;
                return true;
            }
        }
        catch
        {
            // 还没进服 / 会话正在拆：当作没有这个窗口
        }

        return false;
    }

    /// <summary>一个格子的内容；<paramref name="item"/> 为 null 或空物品 = 空格子。</summary>
    private static MccWindowSlot BuildSlot(int index, Item? item)
    {
        MccWindowSlot slot = new() { Index = index };
        if (item is null || item.IsEmpty)
            return slot;

        MccItemRef itemRef = MccItemName.Resolve(item.Type);
        slot.ItemId = itemRef.Name;
        slot.Block = itemRef.Block;
        slot.Count = item.Count;

        // 服务器改过名的（商店货、脚本改名）原样显示；MCC 的名字是英文，先查中文名表
        slot.Name = FirstLine(item.DisplayName) is { Length: > 0 } custom
            ? custom
            : MccChineseNames.ItemName(slot.ItemId) ?? item.GetTypeString();

        string[]? lores = item.Lores;
        if (lores is { Length: > 0 })
        {
            slot.Lore = new List<string>(lores.Length);
            foreach (string line in lores)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    slot.Lore.Add(line);
            }

            if (slot.Lore.Count == 0)
                slot.Lore = null;
        }

        slot.Enchantments = BuildEnchantments(item);

        int damage = item.Damage;
        int maxDamage = item.Components?.OfType<MaxDamageComponent>().FirstOrDefault()?.MaxDamage ?? 0;
        if (maxDamage > 0)
        {
            slot.Damage = damage;
            slot.MaxDamage = maxDamage;
        }

        return slot;
    }

    /// <summary>光标上正拿着的那一摞（MCC 把光标记在玩家窗口 0 的 -1 号格）。</summary>
    private MccWindowSlot BuildCursorSlot()
    {
        if (TryGetContainer(0, out Container player) &&
            player.Items.TryGetValue(MccWindowSlot.CursorIndex, out Item? item) &&
            item is not null)
        {
            return BuildSlot(MccWindowSlot.CursorIndex, item);
        }

        return new MccWindowSlot { Index = MccWindowSlot.CursorIndex };
    }

    /// <summary>附魔列表（1.20.6+ 走组件，老版本走 NBT），形如 "锋利 V"。</summary>
    private static List<string>? BuildEnchantments(Item item)
    {
        List<string>? result = null;
        try
        {
            var enchants = item.EnchantmentList;
            if (enchants is not null)
            {
                result = new List<string>(enchants.Count);
                foreach (Enchantment ench in enchants)
                {
                    // 附魔名和 MCC 一样按 enchantment.minecraft.&lt;注册名&gt; 查，只是先查中文名表
                    string name = MccChineseNames.EnchantmentName(
                                      MinecraftClient.ExtensionMethods.ToUnderscoreCase(ench.Type.ToString()))
                                  ?? EnchantmentMapping.GetEnchantmentName(ench.Type);
                    string level = EnchantmentMapping.ConvertLevelToRomanNumbers(ench.Level);
                    result.Add($"{name} {level}");
                }
            }
            else if (item.NBT is not null &&
                     (item.NBT.TryGetValue("Enchantments", out object? list) ||
                      item.NBT.TryGetValue("StoredEnchantments", out list)) &&
                     list is object[] array)
            {
                result = new List<string>(array.Length);
                foreach (Dictionary<string, object> ench in array)
                {
                    short level = (short)ench["lvl"];
                    string id = ((string)ench["id"]).Replace(':', '.');
                    string name = MccChineseNames.Lookup("enchantment." + id)
                                  ?? ChatParser.TranslateString("enchantment." + id) ?? id;
                    string levelText = ChatParser.TranslateString("enchantment.level." + level) ?? level.ToString();
                    result.Add($"{name} {levelText}");
                }
            }
        }
        catch
        {
            return result;
        }

        return result is { Count: > 0 } ? result : null;
    }

    /// <summary>只取第一行（自定义名里偶尔带换行）。</summary>
    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        int index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }

    /// <summary>格子内容签名（判断"这一格变了没有"）。</summary>
    private static string BuildSignature(MccWindowSlot slot)
    {
        if (slot.IsEmpty)
            return string.Empty;

        StringBuilder sb = new();
        sb.Append(slot.ItemId).Append('\u0001').Append(slot.Count).Append('\u0001').Append(slot.Name);
        sb.Append('\u0001').Append(slot.Damage).Append('/').Append(slot.MaxDamage);

        if (slot.Lore is { Count: > 0 })
        {
            foreach (string line in slot.Lore)
                sb.Append('\u0001').Append(line);
        }

        if (slot.Enchantments is { Count: > 0 })
        {
            foreach (string line in slot.Enchantments)
                sb.Append('\u0002').Append(line);
        }

        return sb.ToString();
    }

    private static string ToActionType(string action)
    {
        return action switch
        {
            MccWindowAction.Right => nameof(WindowActionType.RightClick),
            MccWindowAction.ShiftLeft => nameof(WindowActionType.ShiftClick),
            MccWindowAction.ShiftRight => nameof(WindowActionType.ShiftRightClick),
            MccWindowAction.DropOne => nameof(WindowActionType.DropItem),
            MccWindowAction.DropStack => nameof(WindowActionType.DropItemStack),
            _ => nameof(WindowActionType.LeftClick),
        };
    }

    /// <summary>
    /// 界面的网格尺寸。容器内容 = 总槽位数 - 36（玩家的背包/快捷栏）。
    /// 多数服务器 GUI 就是 9xN 的箱子（商店/背包/菜单），少数原版容器按各自的形状排。
    /// </summary>
    private static (int Columns, int Rows) GetLayout(ContainerType type)
    {
        int slots = Math.Max(0, type.SlotCount() - PlayerInventorySlots);

        int columns = type switch
        {
            ContainerType.Generic_9x1 or ContainerType.Generic_9x2 or ContainerType.Generic_9x3
                or ContainerType.Generic_9x4 or ContainerType.Generic_9x5 or ContainerType.Generic_9x6
                or ContainerType.ShulkerBox => 9,
            ContainerType.Generic_3x3 or ContainerType.Crafter => 3,
            ContainerType.Hopper or ContainerType.BrewingStand => 5,
            ContainerType.Crafting => 4,
            _ => Math.Clamp(slots, 1, 9),
        };

        int rows = Math.Max(1, (slots + columns - 1) / columns);
        return (columns, rows);
    }
}
