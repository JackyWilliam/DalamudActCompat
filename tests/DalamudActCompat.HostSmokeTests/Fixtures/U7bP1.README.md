# U7b P1 unnamed scene-object regression

`U7bP1.xml` contains ten original triggers, preserving their IDs, conditions,
actions, folder scope and inherited environment values. Captured 2026-09-07:

- `https://1824544011.v.123pan.cn/1824544011/Remote_Triggers/U7b.xml`, v1.0.4,
  SHA-256 `80c52d6e69cd82bc3d12ed7a062478fd37837c16b797bec8496cfc717b65d20f`.
- `Utils.xml` under the same URL prefix, audit snapshot from 2026-09-06,
  SHA-256 `a0c24f8ab499d5c3918cd56c6cc8443d893947fd616764ff61a161a7048fa68d`.

No player data, configuration scripts, network actions or unrelated triggers are
included. The test replaces only UseTTS output with diagnostic LogMessage output
to avoid playing audio. Synthetic setup supplies party index 1 and an empty
persistent U7b configuration dictionary, allowing the author's setting defaults.

Run the HostSmokeTests executable with `--probe-triggernometry-u7b` followed by
the Host executable, bundled plugin root, and an **isolated scratch config root**.
The fixture also runs during the normal three-path Host smoke invocation. It uses
the real bundled Triggernometry engine and its rewritten Host integration.

The test sends ActorControl `111/019D` lines into the original Utils converter,
which looks up the scene object's base ID and emits `AAA:204`. With blank-name
objects omitted, both half-room triggers and P1 initialization remain silent.
With objects retained, both half-room directions work, and subsequent ice/fire
events produce eight spread circles or two stack circles plus one personal arrow.
Each circle arrives in a separate callback. All matches and variable evaluations
are performed by Triggernometry; no final AAA line or draw payload is injected.

The Package smoke test exercises the actual game-side snapshot builder using
mocked Dalamud interfaces. It fails against the old name filter (one valid named
object instead of five valid objects) and covers identity deduplication, invalid
IDs, movement and removal. This joins the data-production regression with the
Host-consumption regression without a running game.

`ACTCOMPAT_U7B_PROBE_OUTPUT` optionally captures the synthetic callback results.
`../../DalamudActCompat.PackageSmokeTests/Fixtures/U7bP1Drawings.json` is a capture
from this test, used to check the actual managed PictoACT parser/storage, distinct
circle positions and left/right angles. Native VFX rendering and live packet
capture still require in-game validation; offline success does not assert those.
