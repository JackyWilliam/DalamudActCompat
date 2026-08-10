# Third-party notices

DalamudActCompat embeds compatibility components derived from
[IINACT](https://github.com/marzent/IINACT) and its dependency tree.
IINACT is licensed under GPL-3.0. The patched source used by this project is
available from `vendor/IINACT` and
<https://github.com/JackyWilliam/IINACT/tree/compat/dalamud-act-compat-net10>.

The release archive includes the applicable upstream license texts under
`LICENSES/`. Copyright remains with the respective upstream authors.

The release archive also includes the official OverlayPlugin/cactbot release
package. On first load, DalamudActCompat installs it into the current user's
own plugin configuration directory; on a later DalamudActCompat update, it
upgrades older bundled Cactbot versions without overwriting files under
`cactbot/user/`.

- OverlayPlugin/cactbot 0.37.5
  - Project/source: <https://github.com/OverlayPlugin/cactbot>
  - Release: <https://github.com/OverlayPlugin/cactbot/releases/tag/v0.37.5>
  - Package: <https://github.com/OverlayPlugin/cactbot/releases/download/v0.37.5/cactbot-0.37.5.zip>
  - License: Apache-2.0; the license text is included with the bundled package.

`FFXIV_ACT_Plugin` and its SDK assemblies are official binary dependencies.
They are not authored by the DalamudActCompat project. Their upstream project
and releases are maintained at
<https://github.com/ravahn/FFXIV_ACT_Plugin>.

The release archive also bundles optional ACT extensions. DalamudActCompat
displays their author, version, license/permission status, project URL, source
URL, download URL, and SHA-256 details in game on first install and again after
every DalamudActCompat update. The extensions are not loaded until that notice
is acknowledged. Triggernometry, ACT.FoxTTS, and PostNamazu use registered
author sources for update checks. SilverDasher and Cafe.Matcha remain on their
disclosed, hash-pinned complete packages. URLs are displayed for attribution
and verification; the notice does not open them. SilverDasher is installed
disabled and is loaded only after the user explicitly enables it. Cafe.Matcha
is enabled after installation and starts last in its own dedicated Host process.

- Triggernometry CN Maintained Edition 2.1.2.2
  - Original author/copyright holder: Paissa Heavy Industries
  - Current CN maintainer and distributor: MnFeN
  - Project/source: <https://github.com/MnFeN/Triggernometry>
  - DLL: <https://1824544011.v.123pan.cn/1824544011/Triggernometry_Release_CN/Triggernometry.dll>
  - License: MIT; the license text is included with the bundled files.
- ACT.FoxTTS 3.3.1.189
  - Author/maintainer: Noisyfox
  - Project/source: <https://github.com/Noisyfox/ACT.FoxTTS>
  - DLL release asset: <https://github.com/Noisyfox/ACT.FoxTTS/releases/download/3.3.1/ACT.FoxTTS-3.3.1.189-Release.7z>
  - License: GPL-3.0; the license text is included with the bundled files.
- PostNamazu 1.3.6.6
  - Author/maintainer: Natsukage
  - Project/source: <https://github.com/Natsukage/PostNamazu>
  - DLL release asset: <https://github.com/Natsukage/PostNamazu/releases/download/1.3.6.6/PostNamazu.zip>
  - Redistribution is authorized by the author; the upstream repository does
    not declare an SPDX license.
- SilverDasher 0.6.0.4
  - Author/copyright: The Players / SilverDasher
  - Support: QQ group `582145824`
  - Project: <https://www.ffcafe.cn/act/>
  - Complete package SHA-256: `8D73B14AF27CC4781DDF09B7926C5D99A11CD5B8A02B94FD90430ACF38371866`
  - Entry DLL SHA-256: `A3F356743A438B49CC0796858CA3B127E79DE90E4D7E960177EBDA50A8568CF8`
  - The supplied package contains no license file. Redistribution of this
    bundled version was authorized by the upstream maintainer through direct
    communication on 2026-08-10.
- Cafe.Matcha 26.8.10.829
  - Author/copyright: FFCafe and Cafe.Matcha contributors
  - Project: <https://github.com/thewakingsands/matcha>
  - Exact source commit: <https://github.com/thewakingsands/matcha/tree/6cf242b59475aa77e4c2deee61e1b9191be5ba13>
  - Upstream Actions run: <https://github.com/thewakingsands/matcha/actions/runs/31370163458>
  - DACT compatibility patch/build instructions:
    `vendor/BundledActPlugins/matcha/dact-compat.patch` and `BUILD.md`
  - Complete package SHA-256: `9737B120C795EA207A651FE15D7A390F732AAB377CEEECAD959AD88BB621AC1C`
  - Entry DLL SHA-256: `D55D7D8BEDFA90665422C42B86B1CA102896D360C7D077E4DFB2248A1CB2E8B5`
  - Hash-pinned upstream Actions companion SHA-256:
    `EF485B027FE84150768A8498331BEFCE5C997047FADF7B38B766EC9703818ED6`
  - License: AGPL-3.0; the license text is included with the bundled package.
  - The bundled DLL is a source-built DACT compatibility variant of the exact
    commit above. Its small published patch routes configuration writes,
    external links, network permission checks, and notifications through the
    dedicated Matcha Host. The original Actions DLL is retained as a non-entry
    companion so the compatibility build can preserve the upstream-injected
    Universalis/runtime constants through a sealed, authenticated data file
    without publishing them in source or logs.
    The Host rejects either binary if its registered hash changes.

SharpCompress 0.50.1 is used to read the ACT.FoxTTS 7z release during the
runtime author-source update check. SharpCompress is distributed under the MIT
license: <https://github.com/adamhathcock/sharpcompress>.

dnlib 4.5.0 is used inside the isolated Compatibility Host to preserve
PostNamazu's mixed-mode GreyMagic native image while replacing one obsolete
.NET Framework method reference. dnlib is distributed under the MIT license:
<https://github.com/0xd4d/dnlib>. Its license text is included under
`LICENSES/dnlib-MIT.txt`.

The release archive also contains 96 FINAL FANTASY XIV class/job icon PNGs,
used non-commercially as combat-meter UI indicators.

- Source/reference and attribution:
  <https://ffxiv.gamerescape.com/wiki/Dictionary_of_Icons#Disciple_of_War.2FMagic_Class_Icons>
- FINAL FANTASY XIV Materials Usage License:
  <https://support.na.square-enix.com/rule.php?id=5382&la=1&tag=authc>
- Gamer Escape site terms:
  <https://gamerescape.com/tos/>
- Copyright: © SQUARE ENIX

These icon assets remain Square Enix material and are not licensed under this
project's GPL-3.0 license. Their use is subject to the current FINAL FANTASY XIV
Materials Usage License, including its non-commercial-use, attribution,
no-material-alteration, and removal-request conditions. Gamer Escape's wiki
license does not grant rights to third-party Square Enix material.
