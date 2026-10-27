# Online PictoACT regression fixtures

Captured 2026-09-06 from the 14 configured 洛 online repositories under
https://1824544011.v.123pan.cn/1824544011/Remote_Triggers/ . No player configuration,
entity snapshots, account data, or executable repository scripts are included.

`PictoOnlineExpressions.json` records 1,934 distinct expression samples from the
resource fields plus 9 semantic boundary samples. `Template` retains the original
resource expression. For syntax coverage, balanced `${...}` placeholders were
replaced with fixed scalar samples 0, 1, 2, 3 and 100; `_d` was sampled as 10.
Top-level vector components were then split without touching inner parentheses.
This is a parameter grammar audit, not evaluation of a live trigger or its variables.

Expected results were captured by reflection from the **real bundled**
TriggernometryPlugin 2.1.2.2 `MathParser.Parse`, after the normal embedded-resource
compatibility loader unpacked the proxy. Proxy SHA-256:
`ce417ec8c4a225d88ffb71de1d578e3e21bc405369c54445daabc3947c07e8af`.
No expected result comes from DACT's parser. Of 1,961 unique samples, 18 were
rejected by upstream: 15 scalar substitutions for multi-component θ arguments,
one unresolved regex replacement token, and two scientific-notation boundary
inputs upstream does not accept. These are excluded, not counted as passes.
Against DACT 3715592, 303 accepted samples failed or differed. All 1,943 match
after this change. This does not assert support for every upstream math feature.

`PictoOnlineCommands.json` keeps S7c payloads and fixed substitutions separately:
M11 moon/cross in four directions, M12 wind Fan45/Fan30 with both group and side
values, and M12 blue initial safe areas (20 cases). Shared-tag lines and VFX asset
paths are asserted. In particular, `!00`/`!01` reach DACT unchanged. Tests do not
normalize these to boolean literals. The blue fixture has not been confirmed to
be the user's spoken “小宇宙” mechanic.

The managed service receives no game services; tests never execute scripts or
render native VFX. Additional cases cover bool aliases, Change, nested vector
expressions, nonfinite input, and atomic batch rejection. Existing Package tests
cover delayed execution, ExaFlare, triangulation, entity transforms, and cleanup.

Run the normal PackageSmokeTests executable after a Release build; no network
access is required. The source hashes below identify the audit snapshot, not
future automatic updates.

| Repository | Bytes | SHA-256 |
| --- | ---: | --- |
| SelfTest.xml | 66,563 | `588ceecacb0690d6c1803fe151f64bb6ad8b8050a4f9a93b55fb7865bf07bc58` |
| Utils.xml | 257,688 | `a0c24f8ab499d5c3918cd56c6cc8443d893947fd616764ff61a161a7048fa68d` |
| S7a.xml | 289,851 | `a2c89690499a60ac0a2d55f02181d59eb01868dbf5fac3636152a1a54d5ba24e` |
| S7b.xml | 417,864 | `e2b022fa5e8994b75667f2da78b459fb79190e05d59429feece7d62e93f90075` |
| S7c.xml | 449,180 | `ae84411b4cd4a21ea5d9e8bbc20287d02171564a4f5d162263eedef7374613a9` |
| Ex7.xml | 438,986 | `cff7914a85ca421839369e123365f4770c62a5ff20425da938501bffdd06db30` |
| temp.xml | 46,251 | `e748c96d86269820ce365098aeadd930eabec011ffdc6d100f25fdf43827828f` |
| U6b.xml | 591,393 | `82158beab99f7d418a3815fe152fdde7c3224809852f4db97f93cd18b2df8424` |
| U7a.xml | 528,197 | `4425c47edb4726fbaa7ef41b50288451dbceb64f1bbb3f00ceb389e082a68470` |
| U7b.xml | 536,595 | `80c52d6e69cd82bc3d12ed7a062478fd37837c16b797bec8496cfc717b65d20f` |
| field.xml | 836,234 | `ee2c154f082932e2131753f67a5d64d10b552508328c464d667b5be7de89cb25` |
| dungeon.xml | 228,848 | `44d86d1018dfca09f6fa38a7fbc8dd4b401ae823f66b9aa69cd6023722d08e7e` |
| vc.xml | 967,822 | `eea754aa82ffa801e40b662c3a4b7f036b9083dd422d282d8e99a973694907b5` |
| U6a.xml | 518,444 | `f892d1ceb1ae78c38b25a589e8acbf8290cd3d1615495591227cfeb9d2ccdee6` |

## 2026-10-24 snapshot (Triggernometry 2.2.1.0)

Captured 2026-10-24 from the 15 repositories configured in the local Triggernometry
(the 14 above plus `U5a.xml`), now served from
https://1824544011.cdn.123clouddisk.com/1824544011/Remote_Triggers/ . All 15 returned 200;
only `SelfTest.xml` is byte-identical to the 2026-09-06 snapshot. 941 PictoACT callbacks,
1,382 commands. `U5a.xml` already uses the 2.2.1.0 syntax: `<id>.Target` in Pos/θ,
`<x>, <y>.FarPlayer1`, `<id>, z=0`, and `polarDir8` / `polarDirN4` / `polardirN8` /
`<x>, <y> polardir4`.

`PictoOnline221Samples.json` holds every sample the real TN 2.2.1.0 accepts that is
not already pinned by `PictoOnlineExpressions.json` (the 1,217 overlapping scalars agree
with the 2.1.2.2 expectations exactly): 195 scalars, 737 coordinates and 369 θ values.
Expectations come from reflection over the user's installed 2.2.1.0 proxy
(SHA-256 `9a3c430c00a82ca99f83b2a373adbe1ba0c365a7f06bbf517bc6b76cbd5bb37c`):
`MathParser.Parse` for scalars, `DynamicCoordArg.Parse` for Pos/Target/O/dPos, and
`StaticVfxArgsParser`'s θ precedence (entity ID, then angle, then dynamic coordinate).
Fixed coordinates are stored in Dalamud axes (x, height, ground y); entity and selector
references are stored only as "dynamic", because no actor snapshot is involved.

Placeholders were replaced by type: names ending in `pos`/`xy` became `100, 100`; a
placeholder that is the whole coordinate reference (alone, before `, z=` or before a
selector) became an entity ID or `100, 100, 0`; all others took 0, 1, 2, 3 and 100.
Of 2,534 unique samples, 15 were rejected by upstream too: multi-line `${?d:`/`${?l:`
values, coordinate samples in entity-only `.Target` slots, and similar substitution
artifacts. They are excluded, not counted as passes.

Against released 0.4.5.13 (origin/main 064f167), 34 accepted samples failed or differed:
29 U5a samples using 2.2.1.0 syntax, vc.xml's empty `delay:` (1), and its
`-${l} / 2√2` box offset (4), which DACT evaluated as (-l / 2)√2. After the 2.2.1.0
syntax support and the two parser fixes, 2,519/2,519 match. `PictoOnlineCommands.json` gained 9 of those original U5a/vc payloads
with fixed substitutions; the `2√2` case also asserts the upstream position.

| Repository | Bytes | SHA-256 |
| --- | ---: | --- |
| SelfTest.xml | 66,563 | `588ceecacb0690d6c1803fe151f64bb6ad8b8050a4f9a93b55fb7865bf07bc58` |
| Utils.xml | 247,628 | `7f339207d2517b475bee50ca6e60a47d9e7c75eac41eef93e204e81252ac8881` |
| S7a.xml | 288,214 | `aa97159c59c0881a66756f062dbefce5a109f4fea7c23642bd793ab7ff8292d7` |
| S7b.xml | 417,754 | `b1a4cd440243c71cde37d924004579abc270624196b9f5aaaa1d14b60152c37d` |
| S7c.xml | 438,867 | `b51731bf78636f1a854dad24291b69403b0438694c11978db3632701f5a2308e` |
| Ex7.xml | 438,995 | `e9c9fadf7a01aa281e2225111f318cb7ed25c52fe132524942354d3b4096a749` |
| temp.xml | 67,641 | `762b2b29bf8ac9bd469a43cfdded74c516bee8408a4164705f6390ba6a3b8b50` |
| U6a.xml | 512,112 | `1eb3de5a183e64368b08540c130f26db7eef03fa122737f3f2dff9497ed92ab1` |
| U6b.xml | 585,575 | `36a57e71523d9a9dacdd002525e534a10cee64c4e4b1c271b7bde5ba9e47ed6b` |
| U7a.xml | 528,259 | `f5dc6ec870039aba0125673f4abd4f2a8257f5d837c5f343cc71d339fc9572ab` |
| U7b.xml | 533,992 | `378e32f02e66150146d492563292d9c41ab9a143266ce6d4a7dac536b432aebf` |
| field.xml | 837,477 | `7d9b51d23f7757e85dc6009c4320c9f5754585ae6a1dc4e9b41e030f3ddbbdb3` |
| dungeon.xml | 228,108 | `f47ffb4ac386337b23fc8395ef528bd8161916644b0b7094ea2bd1aea68e218a` |
| vc.xml | 1,016,370 | `361ead67e92fe680004346a4c102716abef22e64c668783446f0118e773b0d94` |
| U5a.xml | 433,454 | `9d656d6bcea899d3281daf37f818397ebcdae67571e9f424df39cf09ee929630` |

Known differences outside every online resource: upstream also lets its implicit " *"
outrank `^` (`2(3)^2` = 36, DACT 18), applies `√` before `^` when chained after a
coefficient (`2√2^2` = 8, DACT 4), and treats `- a %% b` as `-(a %% b)`. None occurs in
either snapshot, so DACT keeps its existing grammar there.

## 2026-10-27 parity check (Triggernometry 2.3.0.0)

The 15 configured repositories were downloaded again on 2026-10-27; every file hash matched the
2026-10-24 snapshot, so the 2.2.1.0 fixtures stay valid. The same reflection harness was run against
the real bundled Triggernometry 2.3.0.0: all 2,519 samples still match DACT (15 rejected by both).
Twenty-four synthetic samples cover the 2.3.0.0 changes (`minuspolar` height negation, hexadecimal
`E0000000` as an entity in θ, hex and binary scalar fast paths); after the matching DACT change all
match. Polygon triangulation with repeated, near-repeated and collinear points is covered by the
package tests rather than this fixture.
