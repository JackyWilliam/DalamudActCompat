# Cafe.Matcha DACT compatibility build

The bundled `Cafe.Matcha.dll` is built from upstream commit
`d4fffc60399549b9e9b8af9d6ca9bfec7378c7ff` under AGPL-3.0, with only the
changes recorded in `dact-compat.patch`.

Package revision **DACT5**, DLL version `26.9.26.1753`, includes the upstream
2026-09-26 fishing lifecycle events. The Host preserves all 28 verified CN and
Global opcode mappings, including EventPlay4 `0x01C6`, SystemLogMessage `0x00A8`,
FishCaught `0x0110`, StatusEffectList `0x0083`, and client-direction ClientTrigger
`0x8187`. The legacy FishBite event remains available. Parser timestamps pass
through the existing Host network bridge unchanged.

`Cafe.Matcha-26.9.26.1753-dact5.zip` SHA-256:
`aac2fbaf31e37e932bc04e210e2a73c8824662ce30b90295875d14389b14e613`.
To reproduce its contents, take DACT4 and replace only
`Plugins/Cafe.Matcha/Cafe.Matcha.dll` with the build below. The data, manifest,
upstream companion, and sealed runtime constants remain byte-for-byte unchanged.
This source revision has no changes to the runtime constants or resource files.

The patch adds a small reflection bridge used only when the DLL runs in the
dedicated DalamudActCompat Host. Configuration and bundled data stay confined
to their assigned roots; import/export may access only a JSON file explicitly
selected in Matcha's own file dialog. Real-time alerts use the Host Windows
shell first and then a typed game-side IPC fallback; the fallback also carries
Matcha's event type so Dalamud can select an icon without parsing localized or
user-formatted text. Delivery failures are logged and never enter Matcha's
blocking `MessageBox`. Outside that Host, the original ACT behavior is retained.
The patch also disables Confuser for this reproducible compatibility build; the
upstream release build remains unchanged.

Build prerequisites and dependency versions are the same as upstream
`.github/workflows/build.yml`:

- ACT 3.6.0.275
- FFXIV ACT Plugin SDK 2.0.7.0
- .NET Framework 4.8 targeting pack
- a current .NET SDK/MSBuild installation
- Windows SDK UniversalApiContract metadata (an installed SDK or the NuGet
  `Microsoft.Windows.SDK.NET.Ref` 10.0.17763.57 `winmd` directory)

From a clean checkout at the commit above:

```powershell
git apply --unidiff-zero dact-compat.patch
dotnet restore Cafe.Matcha/Cafe.Matcha.csproj
dotnet build Cafe.Matcha/Cafe.Matcha.csproj -c Release -t:Rebuild -p:DactCompatBuild=true -p:DebugType=none -p:DebugSymbols=false
```

Normalize source and patch line endings to LF before applying if the checkout
uses CRLF. On a machine without a Windows SDK at the upstream hardcoded paths,
build `Cafe.Matcha/Cafe.Matcha.csproj` and pass
`-p:DactWindowsContractPath=<absolute-path-to-Windows.Foundation.UniversalApiContract.winmd>`.
DACT5 was built with .NET SDK 10.0.401. Source/style analyzer warnings are present;
the build completed with no errors. The declared version is fixed to the source
commit time instead of changing on every local build.

The expected entry DLL SHA-256 is
`73606BAABD1E8386E5900BEC074DB7D5FE898A26109F79C921A5187C222CA9BD`.

The complete package also keeps the unmodified upstream Actions DLL as
`Plugins/Cafe.Matcha/upstream/Cafe.Matcha.Upstream.dll` (SHA-256
`EF485B027FE84150768A8498331BEFCE5C997047FADF7B38B766EC9703818ED6`).
It is never used as the ACT entry assembly. On Windows PowerShell 5.1,
`GenerateRuntimeData.ps1` reads its four upstream-injected runtime constants
and creates `Cafe.Matcha.Runtime.bin` (SHA-256
`D8D134DDBBE60E82C6C3C28C8058446380F5C6BABD73A2666E9575E1E0C44200`).
The file is authenticated and sealed with a key derived from the fixed
upstream binary. The dedicated Host validates both files, opens the data only
in memory, copies the constants into the compatibility assembly, and clears
the temporary byte buffers without writing or logging their values.
