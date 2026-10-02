using System.Globalization;
using MinecraftClient.Mapping;

namespace MCCX.Core;

/// <summary>攻击生物过滤的候选分类（需求 3.2）：敌对 / 中立 / 友好。</summary>
public enum MobCategory
{
    /// <summary>敌对：会主动攻击玩家（含条件敌对与 Boss）。</summary>
    Hostile = 0,

    /// <summary>中立：平时不主动打人，被招惹才反击。</summary>
    Neutral = 1,

    /// <summary>友好：不会攻击玩家。</summary>
    Friendly = 2,
}

/// <summary>攻击生物过滤模式（需求 3.2：白名单 / 黑名单）。</summary>
public enum MobFilterMode
{
    /// <summary>不过滤：只打敌对生物（默认，等于没加过滤时的行为）。</summary>
    Off = 0,

    /// <summary>白名单：只打勾选的生物。</summary>
    Whitelist = 1,

    /// <summary>黑名单：候选生物全部可打，勾选的跳过。</summary>
    Blacklist = 2,
}

/// <summary>
/// 一个候选生物。<see cref="Key"/> 是 <see cref="EntityType"/> 的名字（如 "Zombie"）：
/// 跨版本稳定，用于加密账号库落盘与父子进程管道传输；<see cref="Name"/> 是界面上显示的中文名。
/// </summary>
public sealed record MobCandidate(string Key, string Name, MobCategory Category);

/// <summary>
/// 候选生物目录（按敌对 / 中立 / 友好分类，供攻击过滤勾选）。
///
/// 只收“活的生物”：玩家、掉落物、矿车、船、箭、盔甲架、末地水晶这类一律不进目录，
/// 它们在砍怪逻辑里永远成不了目标。
///
/// 分类基准是原版行为，并保证 MCC 的 <c>IsHostile()</c> 列表整体落在敌对桶里：
/// 这样“不过滤（仅敌对）”模式的行为与加过滤之前完全一致，不会出现“以前打蜘蛛、现在不打了”。
/// </summary>
public static class MobCatalog
{
    private static readonly Dictionary<EntityType, MobCategory> Categories = [];

    private static readonly List<MobCandidate> HostileList = [];
    private static readonly List<MobCandidate> NeutralList = [];
    private static readonly List<MobCandidate> FriendlyList = [];

    static MobCatalog()
    {
        Add(MobCategory.Hostile,
        [
            (EntityType.Blaze, "烈焰人"),
            (EntityType.Bogged, "沼骸"),
            (EntityType.Breeze, "旋风人"),
            (EntityType.CaveSpider, "洞穴蜘蛛"),
            (EntityType.Creeper, "苦力怕"),
            (EntityType.Creaking, "嘎枝"),
            (EntityType.Drowned, "溺尸"),
            (EntityType.ElderGuardian, "远古守卫者"),
            (EntityType.EnderDragon, "末影龙"),
            (EntityType.Enderman, "末影人"),
            (EntityType.Endermite, "末影螨"),
            (EntityType.Evoker, "唤魔者"),
            (EntityType.Ghast, "恶魂"),
            (EntityType.Giant, "巨人"),
            (EntityType.Guardian, "守卫者"),
            (EntityType.Hoglin, "疣猪兽"),
            (EntityType.Husk, "尸壳"),
            (EntityType.Illusioner, "幻术师"),
            (EntityType.MagmaCube, "岩浆怪"),
            (EntityType.Parched, "焦骸"),
            (EntityType.Phantom, "幻翼"),
            (EntityType.Piglin, "猪灵"),
            (EntityType.PiglinBrute, "猪灵蛮兵"),
            (EntityType.Pillager, "掠夺者"),
            (EntityType.Ravager, "劫掠兽"),
            (EntityType.Shulker, "潜影贝"),
            (EntityType.Silverfish, "蠹虫"),
            (EntityType.Skeleton, "骷髅"),
            (EntityType.Slime, "史莱姆"),
            (EntityType.Spider, "蜘蛛"),
            (EntityType.Stray, "流浪者"),
            (EntityType.Vex, "恼鬼"),
            (EntityType.Vindicator, "卫道士"),
            (EntityType.Warden, "监守者"),
            (EntityType.Witch, "女巫"),
            (EntityType.Wither, "凋灵"),
            (EntityType.WitherSkeleton, "凋灵骷髅"),
            (EntityType.Zombie, "僵尸"),
            (EntityType.ZombieVillager, "僵尸村民"),
            (EntityType.Zoglin, "僵尸疣猪兽"),
            (EntityType.ZombifiedPiglin, "僵尸猪灵"),
        ]);

        Add(MobCategory.Neutral,
        [
            (EntityType.Bee, "蜜蜂"),
            (EntityType.Dolphin, "海豚"),
            (EntityType.Fox, "狐狸"),
            (EntityType.Goat, "山羊"),
            (EntityType.IronGolem, "铁傀儡"),
            (EntityType.Llama, "羊驼"),
            (EntityType.Nautilus, "鹦鹉螺"),
            (EntityType.Panda, "熊猫"),
            (EntityType.PolarBear, "北极熊"),
            (EntityType.Pufferfish, "河豚"),
            (EntityType.TraderLlama, "行商羊驼"),
            (EntityType.Wolf, "狼"),
            (EntityType.ZombieNautilus, "僵尸鹦鹉螺"),
        ]);

        Add(MobCategory.Friendly,
        [
            (EntityType.Allay, "悦灵"),
            (EntityType.Armadillo, "犰狳"),
            (EntityType.Axolotl, "美西螈"),
            (EntityType.Bat, "蝙蝠"),
            (EntityType.Camel, "骆驼"),
            (EntityType.CamelHusk, "骆驼尸壳"),
            (EntityType.Cat, "猫"),
            (EntityType.Chicken, "鸡"),
            (EntityType.Cod, "鳕鱼"),
            (EntityType.CopperGolem, "铜傀儡"),
            (EntityType.Cow, "牛"),
            (EntityType.Donkey, "驴"),
            (EntityType.Frog, "青蛙"),
            (EntityType.GlowSquid, "发光鱿鱼"),
            (EntityType.HappyGhast, "快乐恶魂"),
            (EntityType.Horse, "马"),
            (EntityType.Mannequin, "玩家模型"),
            (EntityType.Mooshroom, "哞菇"),
            (EntityType.Mule, "骡"),
            (EntityType.Ocelot, "豹猫"),
            (EntityType.Parrot, "鹦鹉"),
            (EntityType.Pig, "猪"),
            (EntityType.Rabbit, "兔子"),
            (EntityType.Salmon, "鲑鱼"),
            (EntityType.Sheep, "绵羊"),
            (EntityType.SkeletonHorse, "骷髅马"),
            (EntityType.Sniffer, "嗅探兽"),
            (EntityType.SnowGolem, "雪傀儡"),
            (EntityType.Squid, "鱿鱼"),
            (EntityType.Strider, "炽足兽"),
            (EntityType.SulfurCube, "硫方怪"),
            (EntityType.Tadpole, "蝌蚪"),
            (EntityType.TropicalFish, "热带鱼"),
            (EntityType.Turtle, "海龟"),
            (EntityType.Villager, "村民"),
            (EntityType.WanderingTrader, "流浪商人"),
            (EntityType.ZombieHorse, "僵尸马"),
        ]);

        SortByName(HostileList);
        SortByName(NeutralList);
        SortByName(FriendlyList);

        Hostile = HostileList.AsReadOnly();
        Neutral = NeutralList.AsReadOnly();
        Friendly = FriendlyList.AsReadOnly();
        All = [.. HostileList, .. NeutralList, .. FriendlyList];
    }

    /// <summary>敌对生物候选（“不过滤”模式打的就是这一桶）。</summary>
    public static IReadOnlyList<MobCandidate> Hostile { get; }

    /// <summary>中立生物候选。</summary>
    public static IReadOnlyList<MobCandidate> Neutral { get; }

    /// <summary>友好生物候选。</summary>
    public static IReadOnlyList<MobCandidate> Friendly { get; }

    /// <summary>全部候选（敌对 + 中立 + 友好，按分类分段）。</summary>
    public static IReadOnlyList<MobCandidate> All { get; }

    /// <summary>查这个实体类型属于哪一类；不在目录里返回 false。</summary>
    public static bool TryGetCategory(EntityType type, out MobCategory category) =>
        Categories.TryGetValue(type, out category);

    /// <summary>
    /// 能不能作为攻击候选：目录里的生物，或者 MCC 认识的敌对生物（目录没收录的新怪物兜底）。
    /// 玩家、掉落物、矿车、船、箭都不是候选。
    /// </summary>
    public static bool IsAttackable(EntityType type) => Categories.ContainsKey(type) || type.IsHostile();

    /// <summary>
    /// 是不是敌对生物（“不过滤”模式只打这一类）：目录里标敌对的，
    /// 或者目录没收录、但 MCC 的敌对列表里有。
    /// </summary>
    public static bool IsHostile(EntityType type) =>
        Categories.TryGetValue(type, out MobCategory category)
            ? category == MobCategory.Hostile
            : type.IsHostile();

    private static void Add(MobCategory category, IReadOnlyList<(EntityType Type, string Name)> items)
    {
        List<MobCandidate> target = category switch
        {
            MobCategory.Hostile => HostileList,
            MobCategory.Neutral => NeutralList,
            _ => FriendlyList,
        };

        foreach ((EntityType type, string name) in items)
        {
            // 同一个 EntityType 重复登记说明表写错了，直接抛出来，别把错数据带到线上
            if (Categories.ContainsKey(type))
                throw new InvalidOperationException($"生物目录重复登记：{type}");

            Categories[type] = category;
            target.Add(new MobCandidate(type.ToString(), name, category));
        }
    }

    /// <summary>按中文名排序（简体中文按拼音），拿不到中文排序规则就退回按 Key 排。</summary>
    private static void SortByName(List<MobCandidate> list)
    {
        try
        {
            CultureInfo zh = CultureInfo.GetCultureInfo("zh-Hans-CN");
            list.Sort((a, b) => string.Compare(a.Name, b.Name, zh, CompareOptions.None));
        }
        catch (CultureNotFoundException)
        {
            list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        }
    }
}
