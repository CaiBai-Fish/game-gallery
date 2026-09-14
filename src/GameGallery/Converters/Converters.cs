using Microsoft.UI.Xaml;

namespace GameGallery.Converters;

/// <summary>
/// x:Bind 函数绑定用的静态辅助方法。
///
/// 注意：这里刻意不用 IValueConverter。因为主窗口的根是 Window（不是 FrameworkElement），
/// XAML 编译器为 DataTemplate 里的 {x:Bind ... Converter=...} 生成的代码需要
/// “converter lookup root”，而它在 Window 上无法生成；函数绑定没有这个限制。
/// </summary>
public static class Bind
{
    /// <summary>true → Visible。</summary>
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}