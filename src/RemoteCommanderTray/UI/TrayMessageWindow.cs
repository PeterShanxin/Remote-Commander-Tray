using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.UI;

/// <summary>The tray's hidden top-level window receives Windows power broadcasts and UI callbacks.</summary>
internal sealed class TrayMessageWindow(AgentStateMachine machine) : Form
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeAutomatic = 0x0012;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmPowerBroadcast)
        {
            switch ((int)message.WParam)
            {
                case PbtApmSuspend:
                    machine.OnSystemSuspending();
                    break;
                case PbtApmResumeAutomatic:
                    machine.OnSystemResumed();
                    break;
            }
        }
        base.WndProc(ref message);
    }
}
