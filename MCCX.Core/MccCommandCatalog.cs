using MinecraftClient;

namespace MCCX.Core;

/// <summary>
/// MCC 内置命令目录：通过反射枚举 MinecraftClient.Commands 下的 Command 子类，
/// 用于 MCCX 命令输入框的本地静态建议。
/// </summary>
public static class MccCommandCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> _commands = new(Enumerate);

    /// <summary>MCC 内部命令名（不含前导 '/'），按字母排序、无重复。</summary>
    public static IReadOnlyList<string> Commands => _commands.Value;

    private static IReadOnlyList<string> Enumerate()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Type type in Program.GetTypesInNamespace("MinecraftClient.Commands"))
        {
            if (!type.IsSubclassOf(typeof(Command)))
                continue;

            try
            {
                if (Activator.CreateInstance(type) is not Command cmd)
                    continue;

                // help 命令的 CmdName 会抛 NotImplementedException（MCC 上游如此），固定登记为 help。
                string name = type.Name.Equals("help", StringComparison.OrdinalIgnoreCase)
                    ? "help"
                    : cmd.CmdName;

                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name.TrimStart('/'));
            }
            catch
            {
                // 忽略无法实例化的命令类型
            }
        }

        var list = new List<string>(names);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
