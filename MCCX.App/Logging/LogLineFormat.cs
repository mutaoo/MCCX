using System.Text;
using MCCX_App.Containers;
using Windows.UI;

namespace MCCX_App.Logging;

/// <summary>
/// 日志行里一段"同色同 Weight"的文字。索引/长度不存，按顺序拼起来就是整行纯文本，
/// 所以字符下标天然与 <c>LogEntry.Text</c> 对齐（链接下划线、选区、搜索命中都按这个文本算）。
/// </summary>
public readonly record struct LogRun(string Text, Color? Foreground, bool Bold);

/// <summary>
/// 把 MCC 输出的一行原始文本（带 § 颜色码）拆成"纯文本 + 每段颜色"。
///
/// <para>
/// 2026-10-31 用户需求：服务器发来的消息本来是彩色的，日志区也要按推送的颜色显示。
/// 解析规则必须与 MCC 的 <c>ChatBot.GetVerbatim</c> <b>逐字符一致</b>（那是日志纯文本的来源），
/// 否则彩色层和 TextBox 的字符位置会错开，选区/链接下划线/搜索高亮全都会歪：
/// </para>
/// <list type="bullet">
///   <item>§ + 1 个字符：颜色 <c>0-9a-f</c> / 格式 <c>k-o</c> / 重置 <c>r</c>，一律吃掉两个字符；</item>
///   <item>§#rrggbb（8 个字符）：颜色，同样整段吃掉；</item>
///   <item>行尾孤零零的 §：丢掉（与 GetVerbatim 一样）；</item>
///   <item>颜色码会重置格式（MC 的规矩：颜色码之后粗体等格式回到默认）。</item>
/// </list>
/// <para>
/// 代价：每条日志只在第一次真正显示时解析一次（见 <c>LogEntry.ColoredRuns</c>）——虚拟化列表
/// 同时最多实体化几十行，滚动/刷新都不会重复解析，实测对帧时间没有可测影响。
/// </para>
/// </summary>
public static class LogLineFormat
{
    /// <summary>把带 § 码的原始文本拆成若干段；没有颜色码时只有一段、颜色为 null。</summary>
    public static IReadOnlyList<LogRun> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        List<LogRun> runs = [];
        StringBuilder text = new(raw.Length);
        Color? color = null;
        bool bold = false;

        void Flush()
        {
            if (text.Length == 0)
                return;

            runs.Add(new LogRun(text.ToString(), color, bold));
            text.Clear();
        }

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '§')
            {
                text.Append(c);
                continue;
            }

            // §#rrggbb：与 GetVerbatim 一样要求 § 后面还有 7 个字符，否则只当普通"§#"
            if (i + 1 < raw.Length && raw[i + 1] == '#' && i + 7 < raw.Length)
            {
                Color? hex = TryParseHexColor(raw, i + 2);
                if (hex is not null)
                {
                    Flush();
                    color = hex;
                    bold = false;
                }

                i += 7;
                continue;
            }

            if (i + 1 >= raw.Length)
                break; // 行尾孤立的 §：GetVerbatim 也是直接丢掉

            char code = char.ToLowerInvariant(raw[++i]);
            switch (code)
            {
                case 'r':
                    Flush();
                    color = null;
                    bold = false;
                    break;

                case 'l':
                    Flush();
                    bold = true;
                    break;

                // k=乱码 / m=删除线 / n=下划线 / o=斜体：日志区不做这些效果，但码必须吃掉，
                // 否则纯文本会和 TextBox 对不上。o 顺便给斜体？不做（Consolas 中文回退字体斜体很丑）。
                case 'k':
                case 'm':
                case 'n':
                case 'o':
                    Flush();
                    break;

                default:
                    if (MccTextStyle.FromCode(code) is { } parsed)
                    {
                        Flush();
                        color = parsed;
                        bold = false; // MC：颜色码会把格式重置回默认
                    }

                    // 其它未知码：与 GetVerbatim 一样只丢掉"§+1 字符"
                    break;
            }
        }

        Flush();
        return runs;
    }

    /// <summary>解析 §#rrggbb 里的 6 位十六进制；坏数据返回 null。</summary>
    private static Color? TryParseHexColor(string raw, int start)
    {
        int value = 0;
        for (int i = 0; i < 6; i++)
        {
            int digit = HexValue(raw[start + i]);
            if (digit < 0)
                return null;

            value = (value << 4) | digit;
        }

        return Color.FromArgb(0xFF, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
