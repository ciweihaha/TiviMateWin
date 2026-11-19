using System.Reflection;

namespace TiviMateWin.Services;

/// <summary>应用版本号显示（v1.4.35：用户不知道自己在用哪个版本）。
/// 唯一来源是 csproj 的 &lt;Version&gt;（tag 构建时 CI 以 tag 名注入），
/// 程序集版本形如 1.4.35.0，界面上只显示前三段“1.4.35”。</summary>
public static class AppVersion
{
    /// <summary>当前运行的程序集版本（标题栏、设置页共用）。</summary>
    public static Version? Current => Assembly.GetExecutingAssembly().GetName().Version;

    /// <summary>“1.4.35.0”→“1.4.35”；拿不到版本时返回“?”。</summary>
    public static string Display(Version? version) =>
        version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
}
