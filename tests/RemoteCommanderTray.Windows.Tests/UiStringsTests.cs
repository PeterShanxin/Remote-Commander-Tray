using RemoteCommanderTray.Core;
using RemoteCommanderTray.UI;
using Xunit;

namespace RemoteCommanderTray.Windows.Tests;

public sealed class UiStringsTests
{
    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh-Hans", true)]
    [InlineData("ZH-hans-CN", true)]
    [InlineData("zh-SG", true)]
    [InlineData("zh-TW", false)]
    [InlineData("zh-Hant", false)]
    [InlineData("zh-HK", false)]
    [InlineData("zh-X", false)]
    [InlineData("en-US", false)]
    public void Follow_Windows_distinguishes_simplified_from_other_locales(string label, bool expected)
        => Assert.Equal(expected, UiStrings.UseSimplifiedChinese([label]));

    [Fact]
    public void Follow_Windows_uses_the_first_supported_language_or_English()
    {
        Assert.False(UiStrings.UseSimplifiedChinese([]));
        Assert.True(UiStrings.UseSimplifiedChinese(["fr-FR", "zh-CN", "en-US"]));
        Assert.False(UiStrings.UseSimplifiedChinese(["en-GB", "zh-CN"]));
    }

    [Fact]
    public void Explicit_language_choices_override_Windows_and_stay_isolated()
    {
        var followingChinese = new UiStrings("auto", () => ["zh-Hans"]);
        var english = new UiStrings("en", () => throw new Xunit.Sdk.XunitException("Explicit mode must not query Windows."));
        var chinese = new UiStrings("zh-CN", () => throw new Xunit.Sdk.XunitException("Explicit mode must not query Windows."));

        Assert.Equal("启动 Agent", followingChinese.Get(TextId.StartAgent));
        Assert.Equal("Start agent", english.Get(TextId.StartAgent));
        Assert.Equal("启动 Agent", chinese.Get(TextId.StartAgent));

        followingChinese.Configure("en");
        Assert.Equal(english.Get(TextId.StartAgent), followingChinese.Get(TextId.StartAgent));
        Assert.Equal("启动 Agent", chinese.Get(TextId.StartAgent));
    }

    [Fact]
    public void Native_status_and_notification_meaning_is_preserved_in_Chinese()
    {
        var strings = new UiStrings("zh-CN");
        Assert.Equal("正在连接", strings.State(new AgentSnapshot { State = AgentState.Connecting }));
        Assert.Equal("正在重连", strings.State(new AgentSnapshot { State = AgentState.Connecting, RestartCount = 1 }));
        Assert.Equal("错误", strings.State(new AgentSnapshot { State = AgentState.Error, ProcessRunning = true }));
        Assert.Equal("离线", strings.State(new AgentSnapshot { State = AgentState.Error }));

        var failure = new TrayNotification(NotificationKind.StartFailure, "Original title", "Original body")
        {
            MessageId = NotificationMessageId.RepeatedFailure,
            FailureCount = 4,
        };
        Assert.Contains("4 次", strings.Notification(failure).Message);
        Assert.Contains("清理失败", strings.Notification(failure with { MessageId = NotificationMessageId.CleanupFailed }).Title);
        Assert.Equal(("Original title", "Original body"), new UiStrings("en").Notification(failure));
    }

    [Fact]
    public void Every_stable_id_has_text_in_both_languages()
    {
        foreach (var language in new[] { "en", "zh-CN" })
        {
            var strings = new UiStrings(language);
            foreach (var id in Enum.GetValues<TextId>())
            {
                Assert.False(string.IsNullOrWhiteSpace(strings.Get(id)));
            }
        }
    }
}
