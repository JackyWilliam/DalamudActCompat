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
└── pluginmaster.json
```

Use the source repository for code and release artifacts:

```text
DalamudActCompat/
├── src/
├── repo/pluginmaster.json
└── releases/download/v0.1.0/DalamudActCompat.zip
```

## Release Steps

1. Build the plugin on a machine with .NET 10 SDK and Dalamud v14 development dependencies.
2. Publish `DalamudActCompat.zip` to `https://github.com/JackyWilliam/DalamudActCompat/releases`.
3. Update `repo/pluginmaster.json` with the new `AssemblyVersion`, download links, changelog, and Unix `LastUpdate`.
4. Copy `repo/pluginmaster.json` to the separate `DalamudActCompatRepo` repository root.
5. Confirm the raw URL returns JSON in a browser.
6. Add the raw URL to Dalamud custom plugin repositories and test install/update.

## Automated Flow

After pushing the source repository to GitHub:

1. Create a public repository named `JackyWilliam/DalamudActCompatRepo`.
2. Add a repository secret named `DALAMUD_REPO_TOKEN` to the source repository. The token needs write access to `JackyWilliam/DalamudActCompatRepo`.
3. Push a tag such as `v0.1.0` to create the release ZIP from `.github/workflows/release.yml`.
4. Run `.github/workflows/sync-custom-repo.yml` manually with `version = 0.1.0` and a changelog.
5. Verify `https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json`.

## Current Safety Note

This project provides a distribution template only. The parser is not live yet, FFXIV_ACT_Plugin is not loaded yet, and actual game behavior is unverified.
