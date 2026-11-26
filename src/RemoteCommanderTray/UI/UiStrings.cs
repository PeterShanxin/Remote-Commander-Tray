using System.Globalization;
using RemoteCommanderTray.Core;
using RemoteCommanderTray.Windows;

namespace RemoteCommanderTray.UI;

internal enum TextId
{
    StartingEllipsis,
    LaunchTitle,
    LaunchConnecting,
    LaunchAgentStopped,
    LastConnectedNever,
    OpenSignInPage,
    SignInAgain,
    CopySignInCode,
    CopyCode,
    RestartConnection,
    ReauthenticateMenu,
    StopAgent,
    StartAgent,
    OpenRemoteMcp,
    OpenLogs,
    CopyDiagnostics,
    LaunchAtSignIn,
    LaunchDisabled,
    LaunchUnknown,
    About,
    Exit,
    LanguageMenu,
    FollowWindows,
    Online,
    Starting,
    Connecting,
    Reconnecting,
    Stopped,
    Stopping,
    Offline,
    Error,
    AuthenticationRequired,
    LastConnected,
    Retrying,
    AboutTitle,
    Version,
    ApplicationName,
    ProjectPage,
    Upstream,
    DataFolder,
    Close,
    AboutDescription,
    ReauthenticateTitle,
    ReauthenticatePrompt,
    NoSignInPage,
    SignInCodeCopied,
    LogFolderUnavailable,
    DiagnosticsCopied,
    StartupSettingsPrompt,
    StartupSettingFailed,
    CommandFailed,
    ClipboardUnavailable,
    SaveLanguageFailed,
    StatusTitle,
    SignInNeededTitle,
    SignInNeededBody,
    CleanupFailedTitle,
    CleanupFailedBody,
    NotConnectedTitle,
    NotConnectedBody,
    SignInRequiredTitle,
    SignInRequiredBody,
    RepeatedFailureTitle,
    RepeatedFailureBody,
    UnavailableTitle,
    UnavailableBody,
}

/// <summary>One tray's language choice and a shared catalog of authored UI text.</summary>
internal sealed class UiStrings
{
    private readonly Func<IReadOnlyList<string>> _getSystemUiLanguages;
    private bool _useChinese;

    public UiStrings(string language, Func<IReadOnlyList<string>>? getSystemUiLanguages = null)
    {
        _getSystemUiLanguages = getSystemUiLanguages ?? WindowsUiLanguages.GetPreferred;
        Configure(language);
    }

    public void Configure(string language) => _useChinese = language switch
    {
        "zh-CN" => true,
        "en" => false,
        _ => UseSimplifiedChinese(_getSystemUiLanguages()),
    };

    internal static bool UseSimplifiedChinese(IEnumerable<string> preferred)
    {
        foreach (var language in preferred)
        {
            if (IsLanguage(language, "zh-Hans") || IsLanguage(language, "zh-CN") ||
                IsLanguage(language, "zh-SG"))
            {
                return true;
            }
            if (IsLanguage(language, "en"))
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsLanguage(string label, string prefix) =>
        label.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
        label.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);

    public string Get(TextId id)
    {
        var pair = Catalog[id];
        return _useChinese ? pair.Zh : pair.En;
    }

    public string Format(TextId id, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(id), args);

    public string State(AgentSnapshot snapshot) => snapshot.State switch
    {
        AgentState.Stopped => Get(TextId.Stopped),
        AgentState.Stopping => Get(TextId.Stopping),
        AgentState.Starting => Get(TextId.Starting),
        AgentState.Connecting when snapshot.RestartCount > 0 || snapshot.LastConnectedUtc is not null =>
            Get(TextId.Reconnecting),
        AgentState.Connecting => Get(TextId.Connecting),
        AgentState.AuthenticationRequired => Get(TextId.AuthenticationRequired),
        AgentState.Online => Get(TextId.Online),
        AgentState.Error when snapshot.ProcessRunning => Get(TextId.Error),
        AgentState.Error => Get(TextId.Offline),
        _ => snapshot.StateLabel,
    };

    public (string Title, string Message) Notification(TrayNotification item)
    {
        if (!_useChinese)
        {
            return (item.Title, item.Message);
        }
        return item.MessageId switch
        {
            NotificationMessageId.ReauthenticationStarted => (Get(TextId.SignInNeededTitle), Get(TextId.SignInNeededBody)),
            NotificationMessageId.CleanupFailed => (Get(TextId.CleanupFailedTitle), Get(TextId.CleanupFailedBody)),
            NotificationMessageId.ConnectionStalled => (Get(TextId.NotConnectedTitle), Get(TextId.NotConnectedBody)),
            NotificationMessageId.SignInRequired => (Get(TextId.SignInRequiredTitle), Get(TextId.SignInRequiredBody)),
            NotificationMessageId.RepeatedFailure when item.FailureCount is { } count =>
                (Get(TextId.RepeatedFailureTitle), Format(TextId.RepeatedFailureBody, count)),
            NotificationMessageId.AgentUnavailable => (Get(TextId.UnavailableTitle), Get(TextId.UnavailableBody)),
            _ => (item.Title, item.Message),
        };
    }

    private static readonly IReadOnlyDictionary<TextId, (string En, string Zh)> Catalog =
        new Dictionary<TextId, (string, string)>
        {
            [TextId.LaunchTitle] = ("Remote Commander Tray is running", "Remote Commander Tray 已启动"),
            [TextId.LaunchConnecting] = ("Connecting to Desktop Commander…", "正在连接 Desktop Commander…"),
            [TextId.LaunchAgentStopped] = ("Open the tray menu to start the agent.", "打开托盘菜单即可启动 Agent。"),
            [TextId.StartingEllipsis] = ("Starting...", "正在启动…"),
            [TextId.LastConnectedNever] = ("Last connected: never", "上次连接：从未"),
            [TextId.OpenSignInPage] = ("Open sign-in page", "打开登录页面"),
            [TextId.SignInAgain] = ("Sign in again...", "重新登录…"),
            [TextId.CopySignInCode] = ("Copy sign-in code", "复制登录验证码"),
            [TextId.CopyCode] = ("Copy code: {0}", "复制验证码：{0}"),
            [TextId.RestartConnection] = ("Restart connection", "重新连接"),
            [TextId.ReauthenticateMenu] = ("Re-authenticate...", "重新认证…"),
            [TextId.StopAgent] = ("Stop agent", "停止 Agent"),
            [TextId.StartAgent] = ("Start agent", "启动 Agent"),
            [TextId.OpenRemoteMcp] = ("Open Remote MCP", "打开 Remote MCP"),
            [TextId.OpenLogs] = ("Open logs", "查看日志"),
            [TextId.CopyDiagnostics] = ("Copy diagnostics", "复制诊断信息"),
            [TextId.LaunchAtSignIn] = ("Launch at sign-in", "登录 Windows 时启动"),
            [TextId.LaunchDisabled] = ("Launch at sign-in (turned off in Windows)", "登录 Windows 时启动（已被 Windows 禁用）"),
            [TextId.LaunchUnknown] = ("Launch at sign-in (check Windows settings)", "登录 Windows 时启动（请检查系统设置）"),
            [TextId.About] = ("About", "关于"),
            [TextId.Exit] = ("Exit", "退出"),
            [TextId.LanguageMenu] = ("Language / 语言", "Language / 语言"),
            [TextId.FollowWindows] = ("Follow Windows / 跟随系统", "Follow Windows / 跟随系统"),
            [TextId.Online] = ("Online", "已连接"),
            [TextId.Starting] = ("Starting", "正在启动"),
            [TextId.Stopping] = ("Stopping", "正在停止"),
            [TextId.Connecting] = ("Connecting", "正在连接"),
            [TextId.Reconnecting] = ("Reconnecting", "正在重连"),
            [TextId.Stopped] = ("Stopped", "已停止"),
            [TextId.Offline] = ("Offline", "离线"),
            [TextId.Error] = ("Error", "错误"),
            [TextId.AuthenticationRequired] = ("Authentication required", "需要登录"),
            [TextId.LastConnected] = ("Last connected: {0}", "上次连接：{0}"),
            [TextId.Retrying] = ("Retrying in {0}s (attempt {1})", "{0} 秒后重试（第 {1} 次）"),
            [TextId.AboutTitle] = ("About Remote Commander Tray", "关于 Remote Commander Tray"),
            [TextId.Version] = ("Version {0}   -   MIT licensed", "版本 {0}  ·  MIT 许可证"),
            [TextId.ApplicationName] = ("Remote Commander Tray", "Remote Commander Tray"),
            [TextId.ProjectPage] = ("Project page", "项目主页"),
            [TextId.Upstream] = ("Desktop Commander (upstream)", "Desktop Commander（上游项目）"),
            [TextId.DataFolder] = ("Data folder: {0}", "数据目录：{0}"),
            [TextId.Close] = ("Close", "关闭"),
            [TextId.AboutDescription] = ("A tray companion that runs and supervises the official Desktop Commander Remote Device. It never handles sign-in tokens: authentication, device identity and the Remote MCP connection all stay with the official CLI.", "本托盘程序负责启动并监控官方 Desktop Commander Remote Device。它不处理登录令牌；认证、设备身份和远程 MCP 连接均由官方命令行程序管理。"),
            [TextId.ReauthenticateTitle] = ("Re-authenticate", "重新认证"),
            [TextId.ReauthenticatePrompt] = ("Sign out of Desktop Commander on this device and sign in again?\n\nThe official Desktop Commander CLI clears its own saved credentials and opens your browser. Remote Commander Tray never reads or stores them.", "要在此设备上退出 Desktop Commander 并重新登录吗？\n\n官方命令行程序将自行清除保存的凭据并打开浏览器；托盘程序不会读取或保存凭据。"),
            [TextId.NoSignInPage] = ("No sign-in page has been reported yet. Open logs to see what the agent is doing.", "Agent 尚未提供登录页面。请查看日志以了解当前状态。"),
            [TextId.SignInCodeCopied] = ("Sign-in code copied.", "登录验证码已复制。"),
            [TextId.LogFolderUnavailable] = ("Could not open the log folder:\n{0}", "无法打开日志文件夹：\n{0}"),
            [TextId.DiagnosticsCopied] = ("Diagnostics copied to the clipboard.", "诊断信息已复制到剪贴板。"),
            [TextId.StartupSettingsPrompt] = ("Windows startup is disabled or its state could not be verified.\n\nCheck or re-enable it in Windows Startup Apps settings now?", "Windows 已禁用开机启动，或无法验证其状态。\n\n是否现在打开 Windows 启动应用设置进行检查？"),
            [TextId.StartupSettingFailed] = ("Could not change the startup setting:\n{0}", "无法更改开机启动设置：\n{0}"),
            [TextId.CommandFailed] = ("That did not work:\n{0}", "操作失败：\n{0}"),
            [TextId.ClipboardUnavailable] = ("Windows would not let the clipboard be written just now. Try again.", "暂时无法写入 Windows 剪贴板，请重试。"),
            [TextId.SaveLanguageFailed] = ("Unable to save language selection.", "无法保存语言设置。"),
            [TextId.StatusTitle] = ("Remote Commander - {0}", "Remote Commander · {0}"),
            [TextId.SignInNeededTitle] = ("Remote Commander - sign-in needed", "Remote Commander · 需要登录"),
            [TextId.SignInNeededBody] = ("Finish the official Desktop Commander sign-in in your browser. Use Open sign-in page in the tray if no browser opens.", "请在浏览器中完成 Desktop Commander 登录。如果浏览器未打开，请在托盘菜单中选择「打开登录页面」。"),
            [TextId.CleanupFailedTitle] = ("Remote Commander - cleanup failed", "Remote Commander · 清理失败"),
            [TextId.CleanupFailedBody] = ("The prior agent could not be fully cleaned up. No replacement will be started. Exit the tray.", "未能完全清理上一代 Agent。托盘不会启动替代进程。请退出托盘。"),
            [TextId.NotConnectedTitle] = ("Remote Commander - not connected", "Remote Commander · 未连接"),
            [TextId.NotConnectedBody] = ("The device has not come online. Try Restart connection, or Re-authenticate if sign-in expired.", "设备尚未上线。请选择「重新连接」。如果登录已过期，请选择「重新认证」。"),
            [TextId.SignInRequiredTitle] = ("Remote Commander - sign-in required", "Remote Commander · 登录后才能上线"),
            [TextId.SignInRequiredBody] = ("Desktop Commander needs you to sign in. Use Open sign-in page, or Re-authenticate if the session expired.", "Desktop Commander 需要登录。请选择「打开登录页面」。如果会话已过期，请选择「重新认证」。"),
            [TextId.RepeatedFailureTitle] = ("Remote Commander - agent keeps failing", "Remote Commander · Agent 反复启动失败"),
            [TextId.RepeatedFailureBody] = ("The Desktop Commander agent failed to stay running {0} times in a row. Open logs for details.", "Desktop Commander Agent 已连续 {0} 次未能持续运行。请选择「查看日志」了解详情。"),
            [TextId.UnavailableTitle] = ("Remote Commander - agent unavailable", "Remote Commander · Agent 不可用"),
            [TextId.UnavailableBody] = ("Check the Desktop Commander installation and tray settings.", "请检查 Desktop Commander 安装和托盘设置。"),
        };
}
