using System;
using System.Globalization;
using System.Windows.Data;

namespace ExclusiveMusicPlayer;

/// <summary>
/// 把当前值（枚举）与 ConverterParameter 指定的枚举比较，相等返回 true。
/// 用于一组 RadioButton 绑定同一个枚举属性：每个 RadioButton 的
/// ConverterParameter 传自己的枚举值，IsChecked = (当前值 == 该枚举值)。
/// </summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return parameter is not null && Equals(value, parameter);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return parameter!;
    }
}
