# Remote Commander Tray privacy policy

Effective date: 16 November 2026. Publisher: Shanxin Li.

Remote Commander Tray is an independent, open-source companion for the
official Desktop Commander Remote Device command-line application. It is not
affiliated with or endorsed by the Desktop Commander service.

## Windows local information

The tray stores preferences and rotating diagnostic logs on your computer. Ordinary
logs record lifecycle events and sanitized connection states; tool arguments,
tool results, authentication codes, and unrecognized raw output are omitted.
The tray reads the official client's output to display connection status and
sign-in instructions. It does not read or copy the client's credential file.

Verbose logging is off by default. If you explicitly enable it, the separate
verbose log can contain raw client output and private information returned by
remote tools. Review it before sharing. Copy diagnostics does not copy log files.

The Store version stores its own data in its package LocalState folder. On first
launch it can import preferences from an existing portable tray installation.
It does not import old logs or authentication credentials.

## Linux Beta local information

The Linux companion saves the paths to the installed CLI and Node.js under the
current user account. The user systemd manager keeps its startup setting and
ordinary service metadata. The companion discards the CLI's output rather than
recording it in the journal. It does not read or copy the CLI's credentials.

## Network and third-party services

The tray itself has no analytics collection, advertising SDK, or telemetry endpoint.
It starts the installed official Desktop Commander client at your request or when
your saved startup preference calls for it. That client connects to the Desktop
Commander service, handles sign-in, and executes remote requests authorized through
that service. Depending on your requests and client settings, those requests can
read or transmit files, command output, and other information from your computer.
The tray does not provide an additional access-control boundary for the client.

Desktop Commander's processing and retention are described in its
[privacy documentation](https://desktopcommander.app/help/security-privacy/data-privacy)
and [legal policies](https://legal.desktopcommander.app/).
Node.js and the Desktop Commander CLI are separate prerequisites, not bundled with
or automatically downloaded by the Store version of this app.

On Windows, the tray opens websites only for sign-in or when you use a menu link. On Linux, follow the official CLI for sign-in. These websites
and Microsoft Store handle information under their respective privacy policies.

## Control and removal

On Windows, use Stop agent or Exit to stop the managed remote process. Turn off Launch at sign-in
to stop automatic tray startup. The Windows Startup Apps setting can also disable it.
Uninstalling the Store app removes its package-local data. Existing portable data,
Node.js, the official CLI, and the client's account/credentials are separate and
are not deleted by uninstalling this companion. Use the official client's account
controls to manage those credentials or request account deletion.

On Linux, select **Stop** to stop the service and **Disable startup** to prevent
its next automatic start. Closing the desktop window leaves the service running.
The [Linux uninstall command](LINUX_BETA.md#headless-controls) removes the
companion without signing you out of the official CLI.

For questions, use the [project issue tracker](https://github.com/PeterShanxin/Remote-Commander-Tray/issues).
Do not include credentials, authentication codes, private files, or unredacted logs
in public issues.
