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
