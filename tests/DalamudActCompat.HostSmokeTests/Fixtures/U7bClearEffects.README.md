# U7b clear-effect callback probe

`U7bClearEffects.xml` retains the eleven original clear-effect triggers and their
actual ancestor chain from U7b v1.0.6, including zone 1363, regexes, action delays
and `${_entity[${sid}].Address}`. Source:
https://1824544011.cdn.123clouddisk.com/1824544011/Remote_Triggers/U7b.xml
(SHA256 `378e32f02e66150146d492563292d9c41ab9a143266ce6d4a7dac536b432aebf`).

Run HostSmokeTests with `--probe-u7b-clear-effects <Host.exe> <plugin-root>
<isolated-config-root>`. The configuration root must be disposable; the probe
replaces its TN root. Use the bundled TN DLL and the corresponding Host.

The probe replaces only the Redraw sink with a capture callback, and grants no
native-memory or game-command permission. It verifies all eleven original node
IDs, NPC address resolution, configured delays, zone rejection, and an NPC
snapshot arriving 100 ms after its event. It never executes native Redraw.

Optional `ACTCOMPAT_CLEAR_EVENT_INPUT` points to a JSON string array of original
ACT `108:caster:ability:...` payloads, without timestamps. Receipt times are
rebased and synthetic NPC addresses are supplied. The probe checks every
callback and actor/address count. This tests payload compatibility, not the
original fight's real-time ordering, object lifetime or rendering result.

Optional `ACTCOMPAT_CLEAR_PROBE_OUTPUT` receives the isolated Host output. The
direct capture bypasses diagnostic rate limits for larger replay batches;
normal drawing diagnostics are also checked for the first eleven nodes.
