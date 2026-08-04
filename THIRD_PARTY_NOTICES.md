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

- OverlayPlugin/cactbot 0.37.4
  - Project/source: <https://github.com/OverlayPlugin/cactbot>
  - Release: <https://github.com/OverlayPlugin/cactbot/releases/tag/v0.37.4>
  - Package: <https://github.com/OverlayPlugin/cactbot/releases/download/v0.37.4/cactbot-0.37.4.zip>
  - License: Apache-2.0; the license text is included with the bundled package.

`FFXIV_ACT_Plugin` and its SDK assemblies are official binary dependencies.
They are not authored by the DalamudActCompat project. Their upstream project
and releases are maintained at
<https://github.com/ravahn/FFXIV_ACT_Plugin>.

The release archive also bundles the following optional ACT plugin DLLs with
permission from their authors/maintainers. DalamudActCompat displays the same
author, version, license/permission, project URL, source URL, download URL, and
SHA-256 details in game on first install and again after every
DalamudActCompat update. The DLLs are not loaded until that notice is
acknowledged. DalamudActCompat also checks the author sources at startup and
uses the same notice and load gate before installing an upstream update. URLs
are displayed for attribution and verification; the notice does not open them.

- Triggernometry CN Maintained Edition 2.1.1.2
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

SharpCompress 0.50.1 is used to read the ACT.FoxTTS 7z release during the
runtime author-source update check. SharpCompress is distributed under the MIT
license: <https://github.com/adamhathcock/sharpcompress>.

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
