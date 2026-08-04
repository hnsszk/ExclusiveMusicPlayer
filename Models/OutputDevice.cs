using System;

namespace ExclusiveMusicPlayer.Models;

/// <summary>
/// 输出设备选项：显示名 + 用于 NAudio MMDeviceEnumerator 匹配的设备 id。
/// Id 为空表示「跟随系统默认输出设备」（不做任何选择，由 Windows 决定当前输出）。
/// </summary>
public sealed record OutputDevice(string Id, string DisplayName)
{
    /// <summary>固定不变的「跟随系统默认」项，始终排在设备列表最前。</summary>
    public static OutputDevice SystemDefault { get; } = new(string.Empty, "系统默认");

    /// <summary>是否是「跟随系统默认」项。</summary>
    public bool IsSystemDefault => string.IsNullOrEmpty(Id);
}
