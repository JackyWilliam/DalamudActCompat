# Cafe.Matcha DACT compatibility build

The bundled `Cafe.Matcha.dll` is built from upstream commit
`6cf242b59475aa77e4c2deee61e1b9191be5ba13` under AGPL-3.0, with only the
changes recorded in `dact-compat.patch`.

The patch adds a small reflection bridge used only when the DLL runs in the
dedicated DalamudActCompat Host. Configuration and bundled data stay confined
to their assigned roots; import/export may access only a JSON file explicitly
selected in Matcha's own file dialog. Real-time alerts use the Host Windows
shell first and then a typed game-side IPC fallback; delivery failures are
logged and never enter Matcha's blocking `MessageBox`. Outside that Host, the
original ACT behavior is retained. The patch also disables Confuser for this
reproducible compatibility build; the upstream release build remains unchanged.

Build prerequisites and dependency versions are the same as upstream
`.github/workflows/build.yml`:

- ACT 3.6.0.275
- FFXIV ACT Plugin SDK 2.0.7.0
- .NET Framework 4.8 targeting pack
- a current .NET SDK/MSBuild installation

From a clean checkout at the commit above:

```powershell
git apply dact-compat.patch
dotnet restore Cafe.Matcha.sln
dotnet msbuild Cafe.Matcha.sln -p:Configuration=Release -p:DactCompatBuild=true -m
```

The expected entry DLL SHA-256 is
`13564DF8F69C6C983C8C57F1A711CE128AFF879EB2C32DECBA09EDE9C906EA25`.

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
