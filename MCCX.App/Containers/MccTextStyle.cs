using Windows.UI;

namespace MCCX_App.Containers;

/// <summary>
/// 服务器发来的名字 / lore 里带的是 Minecraft 的 <c>§</c> 颜色码（MCC 的 ChatParser 解析后就是这个形状），
/// 这里把它拆成"纯文本 + 首色"，供容器面板的格子提示着色。
///
/// <para>MC 的规矩是"名字/一行 lore 用第一个颜色码决定整行颜色"，后面的颜色码只影响它之后的部分；
/// MCCX 的提示不做行内多色（一个 TextBlock 一个颜色够了），所以只取第一个，其余去掉。</para>
///
/// <para>返回 null 颜色 = 这行没写颜色码，调用方用自己的默认前景色（暗色主题用主题文字色）。</para>
/// </summary>
public static class MccTextStyle
{
    /// <summary>去掉颜色码 + 取第一个颜色码对应的颜色。</summary>
    public static (string Text, Color? Color) Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return (string.Empty, null);

        Color? color = null;
        System.Text.StringBuilder text = new(raw.Length);

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '§' || i + 1 >= raw.Length)
            {
                text.Append(c);
                continue;
            }

            char code = char.ToLowerInvariant(raw[++i]);
            Color? parsed = FromCode(code);
            if (parsed is not null && color is null)
                color = parsed;
        }

        return (text.ToString(), color);
    }

    /// <summary>颜色码 → 颜色；<c>k</c>~<c>o</c>（乱码/加粗那类）与 <c>r</c>（重置）返回 null。</summary>
    public static Color? FromCode(char code)
    {
        return char.ToLowerInvariant(code) switch
        {
            '0' => Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
            '1' => Color.FromArgb(0xFF, 0x00, 0x00, 0xAA),
            '2' => Color.FromArgb(0xFF, 0x00, 0xAA, 0x00),
            '3' => Color.FromArgb(0xFF, 0x00, 0xAA, 0xAA),
            '4' => Color.FromArgb(0xFF, 0xAA, 0x00, 0x00),
            '5' => Color.FromArgb(0xFF, 0xAA, 0x00, 0xAA),
            '6' => Color.FromArgb(0xFF, 0xFF, 0xAA, 0x00),
            '7' => Color.FromArgb(0xFF, 0xAA, 0xAA, 0xAA),
            '8' => Color.FromArgb(0xFF, 0x55, 0x55, 0x55),
            '9' => Color.FromArgb(0xFF, 0x55, 0x55, 0xFF),
            'a' => Color.FromArgb(0xFF, 0x55, 0xFF, 0x55),
            'b' => Color.FromArgb(0xFF, 0x55, 0xFF, 0xFF),
            'c' => Color.FromArgb(0xFF, 0xFF, 0x55, 0x55),
            'd' => Color.FromArgb(0xFF, 0xFF, 0x55, 0xFF),
            'e' => Color.FromArgb(0xFF, 0xFF, 0xFF, 0x55),
            'f' => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            _ => null,
        };
    }

    /// <summary>
    /// 这个颜色在浅色底上还能不能看清（浮在日志区正中的面板是浅色卡片）。
    /// 亮度按 sRGB 近似值加权，白 / 黄 / 亮绿这类浅色在浅色主题下退回主题文字色。
    /// </summary>
    public static bool IsReadableOnLightBackground(Color color)
    {
        double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance < 0.7;
    }
}
