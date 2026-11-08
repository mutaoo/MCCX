namespace MCCX.Core.Dialogs;

/// <summary>
/// 「自动登录要提交什么」的纯数据规则（2026-11-01）。
///
/// 界面上那条服务器对话框（账号正开着）和后台那条（账号没打开、不建 ContentDialog）共用这一份，
/// 免得两边条件漂移：登录类动作怎么挑、密码项填什么、其它项按初始状态填什么。
/// </summary>
public static class DialogAutoLogin
{
    /// <summary>
    /// "登录"类动作 = 第一个非取消动作（MCC 的动作从 1 开始编号）；没有就返回 -1。
    /// </summary>
    public static int FindLoginActionIndex(MccDialogInfo info)
        => info.Actions.FirstOrDefault(action => !action.IsCancel)?.Index ?? -1;

    /// <summary>
    /// 生成"填入记住的密码后点登录"的取值：密码项用 <paramref name="remembered"/>，
    /// 其它项按界面上的初始状态（布尔取初值、下拉取初值或第一项、文本取初值），
    /// 与用户打开窗直接点登录时提交的内容一致。
    ///
    /// 除密码外还有**空着的文本类输入项**（验证码之类）时返回 <c>null</c>：
    /// 后台看不见也改不了，盲填空串只会白失败一次，这种应该留给用户自己填。
    /// </summary>
    public static Dictionary<string, string>? TryBuildValues(MccDialogInfo info, string remembered)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (MccDialogField field in info.Inputs)
        {
            if (field.IsSecret)
            {
                values[field.Key] = remembered;
                continue;
            }

            switch (field.Kind)
            {
                case MccDialogKind.Boolean:
                    values[field.Key] = field.InitialValue.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                    break;

                case MccDialogKind.Option when field.Options is { Count: > 0 } options:
                    values[field.Key] = options.Contains(field.InitialValue) ? field.InitialValue : options[0];
                    break;

                default:
                    if (string.IsNullOrEmpty(field.InitialValue))
                        return null;

                    values[field.Key] = field.InitialValue;
                    break;
            }
        }

        return values;
    }
}
