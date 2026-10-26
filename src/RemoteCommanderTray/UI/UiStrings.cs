using System.Globalization;

namespace RemoteCommanderTray.UI;

/// <summary>English source strings with a lightweight Chinese UI translation.</summary>
/// <remarks>No CLI output, credentials, logs, or protocol messages are translated.</remarks>
internal static class UiStrings
{
    private static string _language = "auto";
    public static void Configure(string? language)
        => _language = language is "zh-CN" or "en" ? language : "auto";

    private static bool UseChinese =>
        _language == "zh-CN"
        || (_language == "auto" && (CultureInfo.CurrentUICulture.Name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
            || CultureInfo.CurrentUICulture.Name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || CultureInfo.CurrentUICulture.Name.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase)));

    private static readonly Dictionary<string, string> Zh = new(StringComparer.Ordinal)
    {
        ["Starting..."] = "正在启动…",
        ["Last connected: never"] = "上次连接：从未",
        ["Open sign-in page"] = "打开登录页面",
        ["Sign in again..."] = "重新登录…",
        ["Copy sign-in code"] = "复制登录验证码",
        ["Copy code: {0}"] = "复制验证码：{0}",
        ["Restart connection"] = "重新连接",
        ["Re-authenticate..."] = "重新认证…",
        ["Stop agent"] = "停止 Agent",
        ["Start agent"] = "启动 Agent",
        ["Open Remote MCP"] = "打开 Remote MCP",
        ["Open logs"] = "查看日志",
        ["Copy diagnostics"] = "复制诊断信息",
        ["Launch at sign-in"] = "登录 Windows 时启动",
        ["Launch at sign-in (turned off in Windows)"] = "登录 Windows 时启动（已被 Windows 禁用）",
        ["Launch at sign-in (check Windows settings)"] = "登录 Windows 时启动（请检查系统设置）",
        ["About"] = "关于",
        ["Exit"] = "退出",
        ["Language / 语言"] = "语言 / Language",
        ["Follow Windows / 跟随系统"] = "跟随系统 / Follow Windows",
        ["Online"] = "已连接",
        ["Starting"] = "正在启动",
        ["Connecting"] = "正在连接",
        ["Reconnecting"] = "正在重连",
        ["Stopped"] = "已停止",
        ["Offline"] = "离线",
        ["Error"] = "错误",
        ["Authentication required"] = "需要登录",
        ["Last connected: {0}"] = "上次连接：{0}",
        ["Retrying in {0}s (attempt {1})"] = "{0} 秒后重试（第 {1} 次）",
        ["About Remote Commander Tray"] = "关于 Remote Commander Tray",
        ["Version {0}   -   MIT licensed"] = "版本 {0}  ·  MIT 许可证",
        ["Remote Commander Tray"] = "Remote Commander Tray",
        ["Project page"] = "项目主页",
        ["Desktop Commander (upstream)"] = "Desktop Commander（上游项目）",
        ["Data folder: {0}"] = "数据目录：{0}",
        ["Close"] = "关闭",
        ["A tray companion that runs and supervises the official Desktop Commander Remote Device. It never handles sign-in tokens: authentication, device identity and the Remote MCP connection all stay with the official CLI."] = "本托盘程序负责启动并监控官方 Desktop Commander Remote Device。它不处理登录令牌；认证、设备身份和远程 MCP 连接均由官方命令行程序管理。",
        ["Re-authenticate"] = "重新认证",
        ["Sign out of Desktop Commander on this device and sign in again?\n\nThe official Desktop Commander CLI clears its own saved credentials and opens your browser. Remote Commander Tray never reads or stores them."] = "要在此设备上退出 Desktop Commander 并重新登录吗？\n\n官方命令行程序将自行清除保存的凭据并打开浏览器；托盘程序不会读取或保存凭据。",
        ["No sign-in page has been reported yet. Open logs to see what the agent is doing."] = "Agent 尚未提供登录页面。请查看日志以了解当前状态。",
        ["Sign-in code copied."] = "登录验证码已复制。",
        ["Could not open the log folder:\n{0}"] = "无法打开日志文件夹：\n{0}",
        ["Diagnostics copied to the clipboard."] = "诊断信息已复制到剪贴板。",
        ["Windows startup is disabled or its state could not be verified.\n\nCheck or re-enable it in Windows Startup Apps settings now?"] = "Windows 已禁用开机启动，或无法验证其状态。\n\n是否现在打开 Windows 启动应用设置进行检查？",
        ["Could not change the startup setting:\n{0}"] = "无法更改开机启动设置：\n{0}",
        ["That did not work:\n{0}"] = "操作失败：\n{0}",
        ["Windows would not let the clipboard be written just now. Try again."] = "暂时无法写入 Windows 剪贴板，请重试。",
    };

    public static string Get(string english) => UseChinese && Zh.TryGetValue(english, out var chinese) ? chinese : english;
    public static string Format(string english, params object?[] args) => string.Format(Get(english), args);
}
