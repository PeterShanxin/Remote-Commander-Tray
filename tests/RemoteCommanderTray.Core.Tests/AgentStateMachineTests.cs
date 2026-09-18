using RemoteCommanderTray.Core;
using Xunit;

namespace RemoteCommanderTray.Core.Tests;

public class AgentStateMachineTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private AgentStateMachine NewMachine() => new(() => _now);

    [Fact]
    public void Starts_stopped()
        => Assert.Equal(AgentState.Stopped, NewMachine().Snapshot.State);

    [Fact]
    public void Walks_a_successful_startup_to_online()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        Assert.Equal(AgentState.Starting, machine.Snapshot.State);

        machine.Apply(new AgentSignal(AgentSignalKind.ConnectingToRemote));
        Assert.Equal(AgentState.Connecting, machine.Snapshot.State);

        machine.Apply(new AgentSignal(AgentSignalKind.SessionRestored));
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceName, "SHANXINMEOWPEOW"));
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));

        var snapshot = machine.Snapshot;
        Assert.Equal(AgentState.Online, snapshot.State);
        Assert.Equal("SHANXINMEOWPEOW", snapshot.DeviceName);
        Assert.Equal(_now, snapshot.LastConnectedUtc);
        Assert.Equal("Online", snapshot.StateLabel);
    }

    [Fact]
    public void Authentication_prompt_surfaces_the_url_and_code()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.AuthenticationStarted));
        machine.Apply(new AgentSignal(AgentSignalKind.VerificationUri, "https://example.com/device"));
        machine.Apply(new AgentSignal(AgentSignalKind.UserCode, "WDJB-MJHT"));

        var snapshot = machine.Snapshot;
        Assert.Equal(AgentState.AuthenticationRequired, snapshot.State);
        Assert.Equal("https://example.com/device", snapshot.VerificationUri);
        Assert.Equal("WDJB-MJHT", snapshot.UserCode);
    }

    [Fact]
    public void Coming_online_clears_the_sign_in_prompt()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.AuthenticationStarted));
        machine.Apply(new AgentSignal(AgentSignalKind.VerificationUri, "https://example.com/device"));
        machine.Apply(new AgentSignal(AgentSignalKind.UserCode, "WDJB-MJHT"));
        machine.Apply(new AgentSignal(AgentSignalKind.AuthorizationSucceeded));
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));

        Assert.Null(machine.Snapshot.VerificationUri);
        Assert.Null(machine.Snapshot.UserCode);
    }

    [Fact]
    public void A_channel_blip_reconnects_without_losing_the_last_connected_time()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));
        var connectedAt = machine.Snapshot.LastConnectedUtc;

        _now = _now.AddSeconds(30);
        machine.Apply(new AgentSignal(AgentSignalKind.ChannelDisrupted, "Channel closed"));

        Assert.Equal(AgentState.Connecting, machine.Snapshot.State);
        Assert.Equal("Reconnecting", machine.Snapshot.StateLabel);
        Assert.Equal(connectedAt, machine.Snapshot.LastConnectedUtc);
        Assert.True(machine.Snapshot.ProcessRunning);
    }

    [Fact]
    public void Channel_recovery_returns_to_online_once_the_device_has_registered()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));
        machine.Apply(new AgentSignal(AgentSignalKind.ChannelDisrupted, "Channel error"));
        machine.Apply(new AgentSignal(AgentSignalKind.ChannelSubscribed));

        Assert.Equal(AgentState.Online, machine.Snapshot.State);
    }

    [Fact]
    public void Channel_subscribed_before_the_device_is_ready_does_not_claim_online()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.ConnectingToRemote));
        machine.Apply(new AgentSignal(AgentSignalKind.ChannelSubscribed));

        Assert.Equal(AgentState.Connecting, machine.Snapshot.State);
    }

    [Fact]
    public void Unexpected_exit_is_an_error_but_a_user_stop_is_not()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.OnProcessExited(1, userRequested: false);
        Assert.Equal(AgentState.Error, machine.Snapshot.State);
        Assert.Equal("Offline", machine.Snapshot.StateLabel);
        Assert.Contains("exited with code 1", machine.Snapshot.LastError);

        machine.OnProcessStarted();
        machine.OnProcessExited(0, userRequested: true);
        Assert.Equal(AgentState.Stopped, machine.Snapshot.State);
        Assert.Null(machine.Snapshot.LastError);
    }

    [Fact]
    public void Exit_while_the_agent_is_not_wanted_is_stopped_not_error()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.SetAgentWanted(false);
        machine.OnProcessExited(143, userRequested: false);

        Assert.Equal(AgentState.Stopped, machine.Snapshot.State);
    }

    [Fact]
    public void Coming_online_clears_the_restart_counter_but_keeps_the_total()
    {
        var machine = NewMachine();
        machine.OnRestartPerformed();
        machine.OnRestartPerformed();
        machine.OnRestartScheduled(2, _now.AddSeconds(15));
        Assert.Equal(2, machine.Snapshot.RestartCount);

        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));

        Assert.Equal(0, machine.Snapshot.RestartCount);
        Assert.Equal(2, machine.Snapshot.TotalRestartCount);
        Assert.Null(machine.Snapshot.NextRestartUtc);
    }

    [Fact]
    public void A_stalled_connection_becomes_an_error()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.ConnectingToRemote));
        machine.OnConnectionStalled(TimeSpan.FromMinutes(6));

        Assert.Equal(AgentState.Error, machine.Snapshot.State);
        Assert.Contains("6 min", machine.Snapshot.LastError);
    }

    [Fact]
    public void A_stalled_check_does_nothing_when_the_device_is_online()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));
        machine.OnConnectionStalled(TimeSpan.FromMinutes(6));

        Assert.Equal(AgentState.Online, machine.Snapshot.State);
    }

    [Fact]
    public void An_expired_session_requires_user_authentication()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.DeviceReady));
        machine.Apply(new AgentSignal(AgentSignalKind.SessionExpired, "Remote session expired"));

        Assert.Equal(AgentState.AuthenticationRequired, machine.Snapshot.State);
    }

    [Fact]
    public void Only_real_changes_raise_the_changed_event()
    {
        var machine = NewMachine();
        var raised = 0;
        machine.Changed += (_, _) => raised++;

        machine.Apply(new AgentSignal(AgentSignalKind.ConnectingToRemote));
        machine.Apply(new AgentSignal(AgentSignalKind.ConnectingToRemote));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void State_since_only_moves_on_a_real_transition()
    {
        var machine = NewMachine();
        machine.OnProcessStarted();
        machine.Apply(new AgentSignal(AgentSignalKind.ConnectingToRemote));
        var enteredAt = machine.Snapshot.StateSinceUtc;

        _now = _now.AddMinutes(3);
        machine.Apply(new AgentSignal(AgentSignalKind.ConnectedToRemote));

        Assert.Equal(enteredAt, machine.Snapshot.StateSinceUtc);
    }
}
