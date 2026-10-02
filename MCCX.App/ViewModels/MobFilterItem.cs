using MCCX.Core;

namespace MCCX_App.ViewModels;

/// <summary>
/// 攻击生物过滤列表里的一项：界面上显示中文名，悬停提示英文的 EntityType 名（<see cref="Key"/>）。
/// 勾选状态属于所属账号（每个账号一份列表实例），变化时通知账号 VM 下发参数并写回账号库。
/// </summary>
public sealed class MobFilterItem : ObservableObject
{
    private bool _isChecked;

    public MobFilterItem(MobCandidate candidate, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        Key = candidate.Key;
        Name = candidate.Name;
        Category = candidate.Category;
        _isChecked = isChecked;
    }

    /// <summary>EntityType 名（如 "Zombie"）：落盘、走管道、悬停提示都用它。</summary>
    public string Key { get; }

    /// <summary>界面上显示的中文名。</summary>
    public string Name { get; }

    /// <summary>分组：敌对 / 中立 / 友好。</summary>
    public MobCategory Category { get; }

    /// <summary>勾选状态变了（账号 VM 用它重新下发攻击参数并写回账号库）。</summary>
    public event Action? CheckedChanged;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                CheckedChanged?.Invoke();
        }
    }
}
