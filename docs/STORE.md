# Microsoft Store distribution

Store ID: `9PK9DMKPH68Q`. The reserved identity is in
`packaging/store-identity.json`; it contains no signing secrets. A reserved product
is a draft, not a published or certified app.

## Build

When a Windows release should go to the Store, bump `StorePackageVersion` in
`Directory.Build.props` above the version already published in Partner Center.
Merging the version change to `main` runs **Store package** for x64 and ARM64.
Rebuild the same revision via **Run workflow** if needed. Download `Store-x64`
and `Store-arm64` from the successful run, check that both `build-info.json` files
match the intended commit and version, then upload the unsigned MSIXs to Partner
Center. The **Store package** workflow builds and validates packages; it
does not submit them. The separate release sync below automates submission
after it is enabled.

For a local build, install .NET SDK 8 and Windows SDK (MakeAppx), then run:

```powershell
[xml]$props = Get-Content Directory.Build.props -Raw
$version = $props.Project.PropertyGroup.StorePackageVersion
./tools/build-store.ps1 -Architecture x64 -PackageVersion $version -OutputDirectory artifacts/store-x64
./tools/build-store.ps1 -Architecture arm64 -PackageVersion $version -OutputDirectory artifacts/store-arm64
```

Each output directory must be new. The script publishes a self-contained, untrimmed,
multi-file WinForms payload, checks the executable architecture and required runtime
files, runs MakeAppx validation, and records the source revision and SHA-256 hash.
The fourth Store version component must be zero; use the same version for both
architectures. Store package versions are independent of the portable product version.

The output MSIX is unsigned and intended for upload to Partner Center. It cannot
be installed as a trusted Store release until Microsoft signs/distributes it.
Do not disable Smart App Control or install a local root certificate to use it.
The CI installation test signs only a disposable runner copy, after uploading the
unsigned build artifact. Its certificate is never a release signing credential.

## Automatic submission

The [release sync](../.github/workflows/store-release-sync.yml) builds x64 and
ARM64 from the exact tag after the owner publishes a stable GitHub release.
The tag must match the app version and point to a commit on `main`. The
workflow checks the build receipts, SHA-256, MSIX identities and executable
architectures before contacting Partner Center. It replaces only packages,
preserves existing listing metadata, and requests publication after
certification. Linux release assets are not sent to Microsoft.

Activation is intentionally separate from merging:

1. In Partner Center, confirm the free MSIX product `9PK9DMKPH68Q` already
   has a published submission and the associated Microsoft Entra application
   has the Manager role for the [MSIX submission API](https://learn.microsoft.com/en-us/windows/uwp/monetize/manage-app-submissions).
2. In GitHub, create or inspect the `store-submission` environment. Store
   `AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID` and
   `AZURE_AD_APPLICATION_SECRET` only in that environment. Allow deployments
   from `main` and stable release tags (`v*`); do not grant other workflows the
   same credentials. Keep trusted tag creation restricted to the maintainers.
   Required reviewers are optional but would stop unattended submission.
3. On `main`, run **Sync GitHub release to Microsoft Store** manually for
   read-only inventory. Verify the published versions and clear any pending
   submission through its current owner. Manual dispatch never submits.
4. Set `STORE_RELEASES_AFTER` to the UTC activation time, using
   `YYYY-MM-DDTHH:MM:SSZ`, and then set
   `STORE_RELEASE_AUTOMATION_ENABLED=true`. Both are environment variables.
   Older release events cannot submit, even when rerun. Leave the toggle
   off until setup and inventory are verified. Turn it off to pause future
   submissions; this does not cancel certification already underway.

Bump the independent Store package version above all published versions
before a release. Create a GitHub draft, attach and verify the release assets,
then publish under the owner account. GitHub will not deliver the
`release.published` trigger when a workflow publishes with its
`GITHUB_TOKEN`; owner publication is the supported path.

The sync never edits, cancels or commits someone else's draft. A matching
already-submitted or published pair is a no-op. A failed upload leaves its
draft in place: use the printed submission ID and inspect it deliberately;
do not rerun expecting the workflow to reuse it. A GitHub job that reports
certification started is not proof Microsoft has certified or published it.
Use the same read-only inventory dispatch or Partner Center to check status.

## Store-specific behavior

- Startup uses the declared `RemoteCommanderTrayStartup` Windows StartupTask and is
  initially off. Windows user/policy disables are respected. No legacy Run key is
  created or refreshed by the Store build.
- Preferences and logs use package LocalState. Only preferences are imported on the
  first run from an existing portable installation. The official CLI still owns
  its existing credentials and device identity.
- Node.js 22.12+ and the official Desktop Commander CLI must already be installed.
  The Store build never falls back to an automatic `npx` download.
- Portable builds retain the existing startup and download behavior.
- Store packages require Windows 10 2004 (build 19041) or Windows 11. Their startup
  extension passes `--autostart`, so a duplicate logon activation exits silently.

## Move from the portable or NUC recovery launcher

First exit the old tray or stop its recovery supervisor, then disable that old
login entry in Windows Startup Apps. Do not run two supervisors for the same device.
The Store and portable tray share a single-instance mutex, but standalone CLI
launchers outside this app may not. Keep credentials in place. Launch the Store
app, confirm the same device comes online, and opt into its Launch at sign-in.
Do not delete the old program or logs until this is verified.

## Verification before certification

CI runs the existing portable tests and native lifecycle checks. The Store workflow
builds both architectures and installs/launches an x64 test package on a disposable
Windows runner, checking package-local logs, disabled-by-default startup, missing
dependency handling, and absence of a legacy Run value.

Also verify on an authorized test device: actual client sign-in and reconnect,
Start/Stop/Exit, startup toggle and external Windows disable, sign-out/sign-in,
upgrade preserving preferences, and uninstall. Run Windows App Certification Kit.
Capture real screenshots of the installed app; do not substitute mock screenshots.

The NUC used to prepare this change has Smart App Control enabled. It blocks newly
compiled unsigned executables and test assemblies, so compilation/MakeAppx success
on that machine does not establish packaged runtime correctness.

## Submission material

See `store-listing/en-US.md` and `store-listing/zh-CN.md` for listing copy and
`store-listing/reviewer-notes.md` for the full-trust explanation and test steps.
Publish `PRIVACY.md` at a stable public URL before entering its link in Partner Center.
Upload the unsigned x64/ARM64 MSIX files. Complete properties, price/availability,
age ratings, actual screenshots and reviewer notes. Do not claim Store certification
or publish the public Store link as available before approval.
