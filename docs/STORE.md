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
Center. The workflow builds and validates packages; it does not submit them.

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
