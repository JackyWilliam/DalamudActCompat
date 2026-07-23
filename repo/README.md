# DalamudActCompat Custom Repository

Add this URL in Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json
```

This directory contains the `pluginmaster.json` file that should be copied or mirrored into the public distribution repository `JackyWilliam/DalamudActCompatRepo`.

The plugin ZIP should be published from the source repository release:

```text
https://github.com/JackyWilliam/DalamudActCompat/releases/download/v0.1.0/DalamudActCompat.zip
```

Keep the raw JSON URL and release ZIP URL separate. Dalamud users add the raw JSON URL; the JSON points Dalamud to the ZIP artifact.
