# Custom Repository Publishing

Dalamud custom repositories are HTTP-accessible JSON files. Users add the raw JSON URL to Dalamud, not the GitHub repository homepage and not the release ZIP URL.

## User-Facing URL

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

## Repository Layout

Use a small public repository for the custom repository file:

```text
DalamudActCompatRepo/
├── pluginmaster.json
├── tools/sync-latest-release.ps1
└── .github/workflows/sync-latest-release.yml
```

Use the source repository for code and release artifacts:

```text
DalamudActCompat/
├── src/
├── repo/pluginmaster.json
└── releases/download/v0.3.10.0/DalamudActCompat-core.zip
```

## Release Steps

1. Run `.github/workflows/release.yml` from the source ref to publish, with a tag matching its assembly version such as `v0.4.2.0`. Leave `dry_run` disabled for publication. A new tag targets the verified workflow commit; an existing tag must already point to that commit.
2. Confirm the Windows self-hosted runner published `DalamudActCompat-core.zip`, `resource-packs.json`, and exactly three resource-pack ZIPs to `https://github.com/JackyWilliam/DalamudActCompat/releases`.
3. Wait for `DalamudActCompatRepo` to detect the latest stable Release and update its own `pluginmaster.json`.
4. Confirm the raw URL returns the new version in a browser.
5. Add the raw URL to Dalamud custom plugin repositories and test install/update.

## Windows Release Runner

The Release job requires a repository runner with the labels `self-hosted`, `Windows`, `X64`, and `dact-release`. Ordinary pull-request and branch Build jobs continue to use GitHub-hosted Windows runners.

The runner account needs PowerShell 7 (`pwsh`), Git, GitHub CLI (`gh`), a writable .NET 10 SDK installation directory, and the real CN or Global launcher's `addon/Hooks/dev` reference assemblies under its own `%APPDATA%`. A service using another account will not automatically see the interactive user's launcher installation. The release workflow resolves those references before restore, checks the bundled dependencies, builds the full solution, runs all four offline smoke suites, and validates the five release assets.

After installing or changing the runner, dispatch Release with `dry_run=true` and the current assembly version. This follows the same build, tests, and packaging path, saves the five files as a workflow artifact for seven days, and skips GitHub Release/tag publication. Verify the job succeeded on the intended runner before using it for a new version.

On the restored workstation, the runner is installed at `E:\DACT\runner`, with job checkouts at `E:\DACT\w`. `E:\DACT\Start-DACT-Runner.ps1` prepares its tool environment and starts it in the background through the current user's Windows Startup shortcut. Keep that Windows session logged in and the computer awake while publishing; signing out, sleeping, or shutting down takes the runner offline. The launcher script also handles listener restarts without starting duplicate listeners.

## Automated Flow

The distribution repository owns the synchronization process:

1. The source repository's manually triggered `.github/workflows/release.yml` creates the requested stable Release.
2. `DalamudActCompatRepo/.github/workflows/sync-latest-release.yml` checks the latest stable Release every 15 minutes.
3. The workflow validates the version, ZIP metadata, download URL, and release notes before changing `pluginmaster.json`.
4. The distribution repository commits the result to its own `main` branch with its scoped `GITHUB_TOKEN`.
5. Verify `https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json`.

No personal access token or cross-repository secret is required. To verify or repair the current metadata immediately, manually run the distribution repository's `Sync latest release` workflow with `force` enabled.

The source-side `repo/pluginmaster.json` remains a local validation template. The user-facing metadata is the file on the distribution repository's `main` branch.
