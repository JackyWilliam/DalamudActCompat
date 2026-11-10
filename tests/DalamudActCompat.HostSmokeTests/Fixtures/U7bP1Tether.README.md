# U7b P1 tether → knockback call regression

`U7bP1Tether.xml` contains thirteen original triggers from the author's U7b pack, preserving
their IDs, conditions, actions, folder scope and inherited environment values:

- `P1/杂项/转录：连线` (tether transcription), and
- every trigger of `P1/P1-1a 前半`.

Source: `https://1824544011.cdn.123clouddisk.com/1824544011/Remote_Triggers/U7b.xml`,
v1.0.6 as cached by Triggernometry on 2026-11-07, SHA-256
`94d51dde618130e992be73b200e6f2730c2c61213229fbd71eb18b46f98b3f14`. No player data,
configuration scripts or network actions are included. The one direct speech action is
replaced by a log message at load time, and the pack's `tts:` log lines are observed as text,
so the test never plays audio.

## What it reproduces

Player report (1.0.0.3): "绝妖星宝宝椅机制报反，如 P1 一神击退".

`转录：连线` runs when the tether line (type 23, id 002D) arrives and reads the tether
source's height from `_entity[sid].z`; 18.5 identifies the first statue. Only then does
`1. 连线 1` announce "点名击退" and mark the player as the knockback target, and `4.1 播报魔法`
later says "击退到 <corner>" for the target or "直接去 <opposite corner>" for everyone else.

When the Host hands the tether line to Triggernometry before it has applied the entity
capture that carries the source's current state, the lookup misses, the player is never
marked, and the target hears the other group's corner — the diagonal opposite. The probe
shows this with a source that had just moved, one that had just spawned, and one that left
and came back; `moved-unordered` keeps the failing behaviour visible as a control.

Run the HostSmokeTests executable with `--probe-triggernometry-u7b-tether` followed by the
Host executable, the bundled plugin root, and an isolated scratch config root. It uses the
real bundled Triggernometry engine and its rewritten Host integration; nothing but log lines
and entity snapshots is injected.

The probe covers the Host's ordering of lines against captures. Whether the real client has
applied a packet by the capture after its line can only be confirmed in the game.
