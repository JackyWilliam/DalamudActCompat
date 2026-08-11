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
└── releases/download/v0.1.0/DalamudActCompat.zip
```

## Release Steps

1. Run `.github/workflows/release.yml` with a numeric tag such as `v0.3.9.0`.
2. Confirm the Windows self-hosted runner published `DalamudActCompat.zip` to `https://github.com/JackyWilliam/DalamudActCompat/releases`.
3. Wait for `DalamudActCompatRepo` to detect the latest stable Release and update its own `pluginmaster.json`.
4. Confirm the raw URL returns the new version in a browser.
5. Add the raw URL to Dalamud custom plugin repositories and test install/update.

## Automated Flow

The distribution repository owns the synchronization process:

1. The source repository's manually triggered `.github/workflows/release.yml` creates the requested stable Release.
2. `DalamudActCompatRepo/.github/workflows/sync-latest-release.yml` checks the latest stable Release every 15 minutes.
3. The workflow validates the version, ZIP metadata, download URL, and release notes before changing `pluginmaster.json`.
4. The distribution repository commits the result to its own `main` branch with its scoped `GITHUB_TOKEN`.
5. Verify `https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json`.

No personal access token or cross-repository secret is required. To verify or repair the current metadata immediately, manually run the distribution repository's `Sync latest release` workflow with `force` enabled.

The source-side `repo/pluginmaster.json` remains a local validation template. The user-facing metadata is the file on the distribution repository's `main` branch.
