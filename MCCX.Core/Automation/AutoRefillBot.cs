using MinecraftClient.Inventory;
using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 自动补充（用户 2026-10-03 第五批需求 2）：选中快捷栏格发生「非空 → 空」的跳变
/// （物品用完、放完、工具损坏消失）时，从背包找同款物品补到手上。
///
/// 搜索范围（用户确认）：主背包 9-35 优先，找不到再看其他快捷栏格 36-44（跳过当前手持格）；
/// 副手 45 不参与（盾牌/弩被抽走会改变玩家的防御用法，不做自动干预）。
///
/// 补货动作 = 两次左键（从源格拿起 → 放进必空的手持格），做完光标回到空，
/// 所以开始前要求光标（窗口 0 的 slot -1）必须是空的。
///
/// 触发方式是 <see cref="Update"/> 里每 tick 轮询选中格，而不是挂 OnInventoryUpdate：
/// 本地预测的改动（如 MCC 自己的 /inventory drop 命令）只改客户端库存、服务端不回包，
/// 事件根本不会来（2026-10-04 g_refill 实测），轮询两条路径（服务端推送 / 本地预测）都能看见。
///
/// 守卫与防误触：
///   1. 只处理玩家窗口 0；有别的容器开着（箱子/村民等，GetInventories 数量 &gt; 1）时不动手，
///      因为那时槽位语义属于那个窗口。
///   2. 基线带槽位号：换了快捷栏格就重建基线，绝不把「换到空格」误判成「用完」。
///   3. 连续观察 <see cref="DebounceTicks"/> 个 tick 都是空才动手，避开一批更新的中间态；
///      触发前先消费掉这次跳变，补货结果如何都不会对同一个空格反复喊。
///
/// 需要的消息日志走 LogToConsole，会进 MCCX 日志区（[自动补充] 前缀）。
/// </summary>
public sealed class AutoRefillBot : ChatBot
{
    /// <summary>玩家窗口 0：快捷栏槽位基址，当前手持 = 36 + 当前选中格。</summary>
    private const int HotbarBase = 36;

    /// <summary>主背包（快捷栏上方三行）在窗口 0 的槽位范围。</summary>
    private const int MainInvFirst = 9;
    private const int MainInvLast = 35;

    /// <summary>连续观察到「空」多少个 tick 才认定用完（服务端一批更新的落定窗口）。</summary>
    private const int DebounceTicks = 3;

    /// <summary>上一次看到的选中格内容；<see cref="ItemType.Air"/> 表示空。</summary>
    private ItemType _prevType = ItemType.Air;

    /// <summary>基线对应的选中格号（用于识别"换了格子"而非"用完"）。</summary>
    private int _prevSlot = -1;

    private bool _prevKnown;

    /// <summary>「非空 → 空」之后连续观察到空的 tick 数。</summary>
    private int _settle;

    public override void Initialize()
    {
        // 挂载即就绪：给用户/测试一个明确锚点（进服、开关打开都会走到这里）
        LogToConsole("§7[自动补充] 已就绪：手持物品用完后自动从背包补同款。");
    }

    public override void AfterGameJoined()
    {
        // 进服就建基线：不能等第一次变化，那次可能正好就是「用完」的那次
        ResetBaseline();
    }

    public override void Update()
    {
        Container inv;
        byte slot;
        try
        {
            inv = GetPlayerInventory();
            slot = GetCurrentSlot();
        }
        catch
        {
            return; // 还没进服 / 物品信息没到，等下一 tick
        }

        int target = HotbarBase + slot;
        ItemType cur = ReadType(inv, target);

        if (!_prevKnown || _prevSlot != slot)
        {
            // 首次观察或刚换格：只记账，不触发（换到空格不是"用完"）
            _prevType = cur;
            _prevSlot = slot;
            _prevKnown = true;
            _settle = 0;
            return;
        }

        if (cur == _prevType)
        {
            _settle = 0; // 状态稳定（含持续为空）
            return;
        }

        if (cur == ItemType.Air)
        {
            // 非空 → 空的候选：连着观察满 DebounceTicks 才算"用完"，
            // 期间一旦变回来（服务端纠正 / 中间态）就清零重来
            if (++_settle < DebounceTicks)
                return;

            ItemType wanted = _prevType;
            _prevType = ItemType.Air; // 先消费这次跳变：补货结果如何都不再对同一空格重入
            _settle = 0;
            TryRefill(inv, target, wanted);
            return;
        }

        // 变成别的非空物品：只记账
        _prevType = cur;
        _settle = 0;
    }

    /// <summary>重建基线：记下当前选中格号与内容；拿不到物品信息就标记待定，等下次轮询。</summary>
    private void ResetBaseline()
    {
        _settle = 0;

        try
        {
            Container inv = GetPlayerInventory();
            _prevSlot = GetCurrentSlot();
            _prevType = ReadType(inv, HotbarBase + _prevSlot);
            _prevKnown = true;
        }
        catch
        {
            _prevKnown = false;
        }
    }

    private void TryRefill(Container inv, int target, ItemType wanted)
    {
        // 容器开着时槽位语义是那个窗口的，直接跳过最稳
        if (GetInventories().Count > 1)
        {
            LogToConsole("§7[自动补充] 有容器窗口开着，这次先不补。");
            return;
        }

        // 光标必须是空的：光标上有东西时左键会把它一起搅进去
        if (inv.Items.TryGetValue(-1, out Item? cursor) && cursor is not null && !cursor.IsEmpty)
        {
            LogToConsole("§7[自动补充] 光标上有物品，这次先不补。");
            return;
        }

        int source = FindSource(inv, wanted, target);
        string name = Item.GetTypeString(wanted);

        if (source < 0)
        {
            LogToConsole($"§7[自动补充] {name} 用完了，背包里没有同款，暂不补充。");
            return;
        }

        // 两次左键：拿起 → 放进必空的手持格。目标格为空，落下后光标也回到空
        if (!WindowAction(0, source, WindowActionType.LeftClick))
            return;

        if (!WindowAction(0, target, WindowActionType.LeftClick))
        {
            // 第二下没成功：把光标上的东西还回原格，别悬在光标上
            WindowAction(0, source, WindowActionType.LeftClick);
            return;
        }

        LogToConsole($"§a[自动补充] {name} 用完了，已从{DescribeSlot(source)}补到手上。");
    }

    /// <summary>找同款物品：主背包 9-35 优先，其次其他快捷栏格（用户确认的范围，副手不参与）。</summary>
    private static int FindSource(Container inv, ItemType wanted, int target)
    {
        for (int s = MainInvFirst; s <= MainInvLast; s++)
        {
            if (ReadType(inv, s) == wanted)
                return s;
        }

        for (int s = HotbarBase; s < HotbarBase + 9; s++)
        {
            if (s != target && ReadType(inv, s) == wanted)
                return s;
        }

        return -1;
    }

    /// <summary>读槽位物品类型；空槽/缺槽都算 <see cref="ItemType.Air"/>。</summary>
    private static ItemType ReadType(Container inv, int slot)
    {
        return inv.Items.TryGetValue(slot, out Item? item) && item is not null && !item.IsEmpty
            ? item.Type
            : ItemType.Air;
    }

    /// <summary>槽位号转成人话：背包格按界面 1-27 数，快捷栏按 1-9 数。</summary>
    private static string DescribeSlot(int slot)
    {
        return slot >= HotbarBase
            ? $"快捷栏第 {slot - HotbarBase + 1} 格"
            : $"背包第 {slot - MainInvFirst + 1} 格";
    }
}
