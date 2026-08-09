using System.Diagnostics;
using FetchDependencies;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine(
        "Usage: ParserDependencySync <external-dependencies-directory> <iinact-version> [--check]");
    return 2;
}

var dependencyDirectory = Path.GetFullPath(args[0]);
var iinactVersion = Version.Parse(args[1]);
var checkOnly = args.Length == 3 &&
                string.Equals(args[2], "--check", StringComparison.OrdinalIgnoreCase);
Directory.CreateDirectory(dependencyDirectory);

using var httpClient = new HttpClient();
var remoteVersionText = (await httpClient.GetStringAsync(
    "https://www.iinact.com/updater/version")).Trim();
var remoteVersion = Version.Parse(remoteVersionText);
var pluginPath = Path.Combine(dependencyDirectory, "FFXIV_ACT_Plugin.dll");
var localVersion = File.Exists(pluginPath)
    ? Version.Parse(FileVersionInfo.GetVersionInfo(pluginPath).FileVersion!)
    : new Version();

if (checkOnly)
{
    if (localVersion != remoteVersion)
    {
        Console.Error.WriteLine(
            $"FFXIV_ACT_Plugin is stale. Local {localVersion}, current {remoteVersion}.");
        return 1;
    }

    var logfilePath = Path.Combine(dependencyDirectory, "FFXIV_ACT_Plugin.Logfile.dll");
    if (!LogFormatIdentity.Matches(logfilePath, iinactVersion))
    {
        Console.Error.WriteLine(
            $"FFXIV_ACT_Plugin.Logfile has a stale IINACT identity. Expected {LogFormatIdentity.ExpectedTemplate(iinactVersion)}.");
        return 1;
    }

    Console.WriteLine(
        $"FFXIV_ACT_Plugin {localVersion} is current and identifies IINACT {iinactVersion}.");
    return 0;
}

var fetcher = new FetchDependencies.FetchDependencies(
    iinactVersion,
    dependencyDirectory,
    isChinese: false,
    httpClient);
fetcher.GetFfxivPlugin(forceUpdate: true);

localVersion = Version.Parse(FileVersionInfo.GetVersionInfo(pluginPath).FileVersion!);
if (localVersion != remoteVersion)
{
    Console.Error.WriteLine(
        $"FFXIV_ACT_Plugin update did not reach current version. Local {localVersion}, current {remoteVersion}.");
    return 1;
}

var synchronizedLogfilePath = Path.Combine(dependencyDirectory, "FFXIV_ACT_Plugin.Logfile.dll");
if (!LogFormatIdentity.Matches(synchronizedLogfilePath, iinactVersion))
{
    Console.Error.WriteLine(
        $"FFXIV_ACT_Plugin.Logfile identity patch failed. Expected {LogFormatIdentity.ExpectedTemplate(iinactVersion)}.");
    return 1;
}

var sdkDirectory = Path.Combine(dependencyDirectory, "SDK");
Directory.CreateDirectory(sdkDirectory);
foreach (var assembly in Directory.EnumerateFiles(
             dependencyDirectory,
             "FFXIV_ACT_Plugin.*.dll",
             SearchOption.TopDirectoryOnly))
{
    File.Copy(assembly, Path.Combine(sdkDirectory, Path.GetFileName(assembly)), overwrite: true);
}

Console.WriteLine($"Synchronized FFXIV_ACT_Plugin {localVersion} and SDK assemblies.");
return 0;
