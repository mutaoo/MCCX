using System.Globalization;
using MCCX.Core;
using Microsoft.UI.Xaml;

namespace MCCX_App.ViewModels;

/// <summary>
/// 自动指令列表里的一条（2026-10-22 用户需求）：界面直接编辑，
/// 改一个字 / 改间隔 / 改执行方式与次数 / 拨这条自己的执行开关、"进服立即执行"或"日志"
/// 都会回调外层重新下发并落盘。
/// <para>
/// 间隔在界面上是字符串（输入框），空/非法时按默认 60 秒算；
/// 真正的夹取（1 秒 ~ 24 小时、次数 0 ~ 100 万）在 <see cref="AutoCommandBot.SetCommands"/> 里再做一次，
/// 界面与子进程的规则保持一致。
/// </para>
/// </summary>
public sealed class AutoCommandItem : ObservableObject
{
    private readonly Action _changed;

    private string _text;
    private string _intervalSeconds;
    private bool _runOnJoin;
    private bool _loop;
    private string _repeatCount;
    private bool _enabled;
    private bool _logEnabled;

    public AutoCommandItem(AutoCommandDefinition definition, Action changed)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));

        _text = definition.Text ?? string.Empty;
        _intervalSeconds = definition.IntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        _runOnJoin = definition.RunOnJoin;
        _loop = definition.Loop;
        // 0 = 无限：界面上留空（占位提示"无限"），比让人看到 0 更好懂
        _repeatCount = definition.RepeatCount > 0
            ? definition.RepeatCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        _enabled = definition.Enabled;
        _logEnabled = definition.LogEnabled;
    }

    /// <summary>指令原文（以 / 开头先试 MCC 内部命令，不是内部命令才当服务器指令；其余当聊天发）。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? string.Empty))
                _changed();
        }
    }

    /// <summary>间隔（秒）的输入框文本。</summary>
    public string IntervalSeconds
    {
        get => _intervalSeconds;
        set
        {
            if (SetProperty(ref _intervalSeconds, value ?? string.Empty))
                _changed();
        }
    }

    /// <summary>进服（真正进入游戏）后立刻执行一次。</summary>
    public bool RunOnJoin
    {
        get => _runOnJoin;
        set
        {
            if (SetProperty(ref _runOnJoin, value))
                _changed();
        }
    }

    /// <summary>执行方式下拉的选中项：0 = 循环执行，1 = 只执行一次。</summary>
    public int ModeIndex
    {
        get => _loop ? 0 : 1;
        set
        {
            bool loop = value != 1;
            if (_loop == loop)
                return;

            _loop = loop;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RepeatVisibility));
            _changed();
        }
    }

    /// <summary>循环执行的总次数（输入框文本；空 = 无限）。只执行一次时不用它。</summary>
    public string RepeatCount
    {
        get => _repeatCount;
        set
        {
            if (SetProperty(ref _repeatCount, value ?? string.Empty))
                _changed();
        }
    }

    /// <summary>"次数"输入框只在循环执行时显示（只执行一次固定 1 次，没有可填的）。</summary>
    public Visibility RepeatVisibility => _loop ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>这条指令自己的执行开关（关掉只停这一条，总开关和其它指令不受影响）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
                _changed();
        }
    }

    /// <summary>这条指令自己的过程日志开关（每条独立）。</summary>
    public bool LogEnabled
    {
        get => _logEnabled;
        set
        {
            if (SetProperty(ref _logEnabled, value))
                _changed();
        }
    }

    /// <summary>转成下发给子进程 / 落盘的模型。</summary>
    public AutoCommandDefinition ToDefinition() => new()
    {
        Text = Text.Trim(),
        IntervalSeconds = ParseInterval(IntervalSeconds),
        RunOnJoin = RunOnJoin,
        Loop = _loop,
        RepeatCount = _loop ? ParseRepeatCount(RepeatCount) : 0,
        Enabled = Enabled,
        LogEnabled = LogEnabled,
    };

    /// <summary>秒数文本 → 数值（空/非法 = 60 秒；范围在子进程侧再夹一遍）。</summary>
    private static double ParseInterval(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? seconds
            : 60.0;

    /// <summary>次数文本 → 数值（空/非法/≤0 = 0 = 无限；上限在子进程侧再夹一遍）。</summary>
    private static int ParseRepeatCount(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0
            ? count
            : 0;
}
