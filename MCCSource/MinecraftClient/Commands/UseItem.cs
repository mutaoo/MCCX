using Brigadier.NET;
using Brigadier.NET.Builder;
using MinecraftClient.CommandHandler;
using MinecraftClient.Inventory;
using MinecraftClient.Mapping;
using static MinecraftClient.CommandHandler.CmdResult;

namespace MinecraftClient.Commands
{
    class UseItem : Command
    {
        public override string CmdName { get { return "useitem"; } }
        public override string CmdUsage { get { return "useitem [mainhand|offhand] | useitem [x] [y] [z] [mainhand|offhand]"; } }
        public override string CmdDesc { get { return Translations.cmd_useitem_desc; } }

        public override void RegisterCommand(CommandDispatcher<CmdResult> dispatcher)
        {
            dispatcher.Register(l => l.Literal("help")
                .Then(l => l.Literal(CmdName)
                    .Executes(r => GetUsage(r.Source, string.Empty))
                )
            );

            dispatcher.Register(l => l.Literal(CmdName)
                .Executes(r => DoUseItem(r.Source))
                .Then(l => l.Literal("mainhand")
                    .Executes(r => DoUseItem(r.Source, Hand.MainHand)))
                .Then(l => l.Literal("offhand")
                    .Executes(r => DoUseItem(r.Source, Hand.OffHand)))
                .Then(l => l.Argument("Location", MccArguments.Location())
                    .Executes(r => DoUseItemAtLocation(r.Source, MccArguments.GetLocation(r, "Location"), Hand.MainHand))
                    .Then(l => l.Literal("mainhand")
                        .Executes(r => DoUseItemAtLocation(r.Source, MccArguments.GetLocation(r, "Location"), Hand.MainHand)))
                    .Then(l => l.Literal("offhand")
                        .Executes(r => DoUseItemAtLocation(r.Source, MccArguments.GetLocation(r, "Location"), Hand.OffHand))))
                .Then(l => l.Literal("_help")
                    .Executes(r => GetUsage(r.Source, string.Empty))
                    .Redirect(dispatcher.GetRoot().GetChild("help").GetChild(CmdName)))
            );
        }

        private int GetUsage(CmdResult r, string? cmd)
        {
            return r.SetAndReturn(cmd switch
            {
#pragma warning disable format // @formatter:off
                _           =>  GetCmdDescTranslated(),
#pragma warning restore format // @formatter:on
            });
        }

        private static bool ShouldUseOffhandFood(McClient handler)
        {
            Container? inventory = handler.GetInventory(0);
            if (inventory is null)
                return false;

            if (!inventory.Items.TryGetValue(45, out Item? offhandItem)
                || offhandItem.IsEmpty
                || !offhandItem.Type.IsFood())
                return false;

            int mainHandSlot = 36 + handler.GetCurrentSlot();
            return !inventory.Items.TryGetValue(mainHandSlot, out Item? mainHandItem)
                || mainHandItem.IsEmpty
                || !mainHandItem.Type.IsFood();
        }

        private int DoUseItem(CmdResult r, Hand? requestedHand = null)
        {
            McClient handler = CmdResult.currentHandler!;
            if (!handler.GetInventoryEnabled())
                return r.SetAndReturn(Status.FailNeedInventory);

            Hand hand = requestedHand ?? (ShouldUseOffhandFood(handler) ? Hand.OffHand : Hand.MainHand);

            // 2026-10-24（MCCX）：去掉"先射线找方块 → PlaceBlock"这一步。
            // 那个分支会把手上的任何东西都当方块放：药水 / 末影珍珠 / 食物这类服务端不收，
            // 日志却照样回"使用了一个物品"，而且有没有反应取决于当时视线里有没有方块 ——
            // 用户反馈"同样的 /useitem，有的账号窗口能用、有的不能"。
            // UseItemOnHand 就是原版右键（PlayerInteractItem）：放方块还是用物品由服务端按视线判断。
            // 想明确对着某块方块用，走 /useblock <x> <y> <z>。
            if (IsHandEmpty(handler, hand))
                return r.SetAndReturn("手上没有物品：/useitem 用的是当前选中格里的物品（副手请用 /useitem offhand）。", false);

            if (hand == Hand.OffHand)
                handler.UseItemOnLeftHand();
            else
                handler.UseItemOnHand();

            handler.DoAnimation((int)hand);
            return r.SetAndReturn(Status.Done, Translations.cmd_useitem_use);
        }

        /// <summary>当前这只手是不是空的（背包还没同步时不做判断，免得误报）。</summary>
        private static bool IsHandEmpty(McClient handler, Hand hand)
        {
            Container? inventory = handler.GetInventory(0);
            if (inventory is null)
                return false;

            int slot = hand == Hand.OffHand ? 45 : 36 + handler.GetCurrentSlot();
            return !inventory.Items.TryGetValue(slot, out Item? item) || item.IsEmpty;
        }

        private int DoUseItemAtLocation(CmdResult r, Location block, Hand hand)
        {
            McClient handler = CmdResult.currentHandler!;
            if (!handler.GetTerrainEnabled())
                return r.SetAndReturn(Status.FailNeedTerrain);

            Location current = handler.GetCurrentLocation();
            block = block.ToAbsolute(current).ToFloor();
            handler.PlaceBlock(block, Direction.Up, hand, lookAtBlock: true);
            handler.DoAnimation((int)hand);
            return r.SetAndReturn(Status.Done, Translations.cmd_useitem_use);
        }

    }
}
