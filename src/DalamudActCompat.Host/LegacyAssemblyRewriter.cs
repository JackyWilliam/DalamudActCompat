using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Formats.Nrbf;
using System.Globalization;
using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Resources;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using DalamudActCompat.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Resources.Extensions;

namespace DalamudActCompat.Host;

public static class LegacyAssemblyRewriter
{
    private const string MatchaUpstreamFileName = "Cafe.Matcha.Upstream.dll";
    private const string MatchaUpstreamSha256 =
        "EF485B027FE84150768A8498331BEFCE5C997047FADF7B38B766EC9703818ED6";
    private const string MatchaRuntimeDataFileName = "Cafe.Matcha.Runtime.bin";
    private const string MatchaRuntimeDataSha256 =
        "D8D134DDBBE60E82C6C3C28C8058446380F5C6BABD73A2666E9575E1E0C44200";
    private const string SilverDasherWeaverFileName = "SilverDasher.Weaver.dll";
    private const string SilverDasherWeaverSha256 =
        "20FE1491A9B35BB5096F25D1115A3E51F2C8BBAE2B741C204C2069ADDA507ECC";
    private const string SilverDasherCompatibleWeaverSha256 =
        "CD77EC62F7802C50BE02EC99AA83DFD5DE6CED7A41A4A866F80FB8A7509E26E1";
    private const int SilverDasherWeaverProcessAttachJumpOffset = 0x3254;

    public static Assembly LoadMatcha(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        if (!string.Equals(definition.Name.Name, "Cafe.Matcha", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected Matcha assembly identity: {definition.Name.FullName}.");
        }

        var module = definition.MainModule;
        var bridgeType = module.GetType("Cafe.Matcha.Utils.DactBridge")
                         ?? throw new TypeLoadException(
                             "Matcha DACT compatibility bridge is missing.");
        var contractVersion = bridgeType.Fields.SingleOrDefault(field =>
            field.Name == "ContractVersion" &&
            field.IsLiteral &&
            field.FieldType.MetadataType == MetadataType.String);
        if (!string.Equals(contractVersion?.Constant as string, "3", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Matcha DACT compatibility contract is missing or unsupported.");
        }

        var methods = module.Types
            .SelectMany(EnumerateTypes)
            .Where(type => !type.FullName.StartsWith("<Module>", StringComparison.Ordinal))
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .ToArray();

        var callsOutsideBridge = methods
            .Where(method => method.DeclaringType != bridgeType)
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .ToArray();
        var expectedBridgeCalls = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ReadAllText"] = 2,
            ["WriteAllText"] = 1,
            ["ReadUserTextFile"] = 1,
            ["WriteUserTextFile"] = 1,
            ["StartProcess"] = 4,
            ["Demand"] = 1,
            ["SendNotification"] = 1,
        };
        foreach (var (methodName, expectedCount) in expectedBridgeCalls)
        {
            var actualCount = callsOutsideBridge.Count(call =>
                call.DeclaringType.FullName == bridgeType.FullName &&
                call.Name == methodName);
            if (actualCount != expectedCount)
            {
                throw new InvalidOperationException(
                    $"Matcha DACT bridge surface changed for {methodName}; " +
                    $"expected {expectedCount}, found {actualCount}.");
            }
        }

        var notificationCall = methods
            .SelectMany(method => method.Body.Instructions.Select(instruction => (method, instruction)))
            .Single(candidate =>
                candidate.instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == bridgeType.FullName &&
                called.Name == "SendNotification");
        var instructions = notificationCall.method.Body.Instructions;
        var notificationIndex = instructions.IndexOf(notificationCall.instruction);
        var notificationTail = instructions
            .Skip(notificationIndex + 1)
            .Where(instruction => instruction.OpCode.Code != Code.Nop)
            .Take(2)
            .Select(instruction => instruction.OpCode.Code)
            .ToArray();
        // DACT owns the Windows and typed IPC routes. Ignoring the bool result here
        // prevents Matcha's outer catch from reaching its blocking MessageBox fallback.
        if (!notificationTail.SequenceEqual([Code.Pop, Code.Ret]))
        {
            throw new InvalidOperationException(
                "Matcha DACT notification path can still enter the blocking dialog fallback.");
        }

        var directFileIo = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == typeof(File).FullName &&
            call.Name is nameof(File.ReadAllText) or nameof(File.WriteAllText));
        var directProcessStarts = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == typeof(Process).FullName &&
            call.Name == nameof(Process.Start));
        if (directFileIo != 0 || directProcessStarts != 0)
        {
            throw new InvalidOperationException(
                "Matcha bypasses its DACT permission bridge: " +
                $"file={directFileIo}, process={directProcessStarts}.");
        }

        var upstreamPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!,
            "upstream",
            MatchaUpstreamFileName);
        var runtimeDataPath = Path.Combine(
            Path.GetDirectoryName(upstreamPath)!,
            MatchaRuntimeDataFileName);
        var secrets = ExtractMatchaUpstreamSecrets(upstreamPath, runtimeDataPath);
        using var assemblyImage = new MemoryStream(
            File.ReadAllBytes(assemblyPath),
            writable: false);
        var assembly = loadContext.LoadFromStream(assemblyImage);
        ApplyMatchaUpstreamSecrets(assembly, secrets);
        ApplyMatchaGlobal755Hotfix2Opcodes(assembly);
        return assembly;
    }

    private static void ApplyMatchaGlobal755Hotfix2Opcodes(Assembly assembly)
    {
        var storageType = assembly.GetType(
                              "Cafe.Matcha.Constant.OpcodeStorage",
                              throwOnError: true)!
                          ?? throw new TypeLoadException(
                              "Matcha opcode storage is missing.");
        var opcodeType = assembly.GetType(
                             "Cafe.Matcha.Constant.MatchaOpcode",
                             throwOnError: true)!
                         ?? throw new TypeLoadException(
                             "Matcha opcode enum is missing.");
        var globalField = storageType.GetField(
                              "Global",
                              BindingFlags.Public | BindingFlags.Static)
                          ?? throw new MissingFieldException(storageType.FullName, "Global");
        var global = globalField.GetValue(null) as IDictionary
                     ?? throw new InvalidDataException(
                         "Matcha Global opcode storage has an unexpected shape.");
        var verified = new Dictionary<ushort, string>
        {
            [0x0096] = "ActorControl",
            [0x037C] = "ActorControlSelf",
            [0x027D] = "CEDirector",
            [0x012E] = "CompanyAirshipStatus",
            [0x03AF] = "CompanySubmersibleStatus",
            [0x0197] = "ContentFinderNotifyPop",
            [0x02C7] = "ResumeEventScene32",
            [0x01A5] = "EventPlay",
            [0x0278] = "EventStart",
            [0x0097] = "Examine",
            [0x0161] = "InitZone",
            [0x0104] = "InventoryTransaction",
            [0x0204] = "ItemInfo",
            [0x0190] = "MarketBoardItemListing",
            [0x022F] = "MarketBoardItemListingCount",
            [0x017B] = "MarketBoardItemListingHistory",
            [0x835B] = "MarketBoardRequestItemListingInfo",
            [0x00E9] = "NpcSpawn",
            [0x00A6] = "PlayerSetup",
            [0x032D] = "PlayerSpawn",
            [0x01A2] = "SubmarineStatusList",
        };

        // FateInfo and WorldVisitQueue are not published in the verified 7.55h2 table.
        // Omitting them is safer than retaining stale keys that now identify other packets.
        global.Clear();
        foreach (var (opcode, name) in verified)
        {
            global.Add(opcode, Enum.Parse(opcodeType, name));
        }
    }

    private static MatchaUpstreamSecrets ExtractMatchaUpstreamSecrets(
        string assemblyPath,
        string runtimeDataPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                "The hash-pinned upstream Matcha companion is missing.",
                assemblyPath);
        }

        var upstreamImage = File.ReadAllBytes(assemblyPath);
        var upstreamHash = SHA256.HashData(upstreamImage);
        var actualHash = Convert.ToHexString(upstreamHash);
        if (!string.Equals(actualHash, MatchaUpstreamSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Upstream Matcha companion hash changed; expected " +
                $"{MatchaUpstreamSha256}, got {actualHash}.");
        }

        if (!File.Exists(runtimeDataPath))
        {
            throw new FileNotFoundException(
                "The sealed upstream Matcha runtime data is missing.",
                runtimeDataPath);
        }

        var payload = File.ReadAllBytes(runtimeDataPath);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(
                payloadHash,
                MatchaRuntimeDataSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Matcha runtime data hash changed; expected " +
                $"{MatchaRuntimeDataSha256}, got {payloadHash}.");
        }

        const int magicLength = 4;
        const int tagLength = 32;
        if (payload.Length <= magicLength + tagLength ||
            !payload.AsSpan(0, magicLength).SequenceEqual("DMR1"u8))
        {
            throw new InvalidDataException("Matcha runtime data header is invalid.");
        }

        var plainText = new byte[payload.Length - magicLength - tagLength];
        try
        {
            var cipherText = payload.AsSpan(magicLength + tagLength);
            for (var index = 0; index < cipherText.Length; index++)
            {
                plainText[index] = (byte)(cipherText[index] ^ upstreamHash[index % upstreamHash.Length]);
            }

            using var hmac = new HMACSHA256(upstreamHash);
            var expectedTag = hmac.ComputeHash(plainText);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedTag,
                    payload.AsSpan(magicLength, tagLength)))
            {
                throw new InvalidDataException(
                    "Matcha runtime data authentication failed.");
            }

            var values = JsonSerializer.Deserialize<string[]>(plainText)
                         ?? throw new InvalidDataException(
                             "Matcha runtime data is empty.");
            if (values.Length != 4 ||
                !Uri.TryCreate(values[0], UriKind.Absolute, out var telemetryRoot) ||
                telemetryRoot.Scheme != Uri.UriSchemeHttps ||
                string.IsNullOrWhiteSpace(values[1]) ||
                !Guid.TryParse(values[2], out _) ||
                !Guid.TryParse(values[3], out _))
            {
                throw new InvalidDataException(
                    "The hash-pinned upstream Matcha runtime constants are incomplete.");
            }

            return new MatchaUpstreamSecrets(
                values[0],
                values[1],
                values[2],
                values[3]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainText);
            CryptographicOperations.ZeroMemory(upstreamHash);
            CryptographicOperations.ZeroMemory(upstreamImage);
        }
    }

    private static void ApplyMatchaUpstreamSecrets(
        Assembly assembly,
        MatchaUpstreamSecrets secrets)
    {
        var secretType = assembly.GetType(
                             "Cafe.Matcha.Constant.Secret",
                             throwOnError: true)!
                         ?? throw new TypeLoadException(
                             "Matcha runtime constant holder is missing.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TelemetryRoot"] = secrets.TelemetryRoot,
            ["UniversalisKey"] = secrets.UniversalisKey,
            ["TelemetryFate"] = secrets.TelemetryFate,
            ["TelemetryNpc"] = secrets.TelemetryNpc,
        };
        foreach (var (fieldName, value) in values)
        {
            var field = secretType.GetField(
                            fieldName,
                            BindingFlags.Public | BindingFlags.Static)
                        ?? throw new MissingFieldException(secretType.FullName, fieldName);
            field.SetValue(null, value);
        }
    }

    public static Assembly LoadSilverDasher(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        _ = loadContext;
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        if (!string.Equals(
                definition.Name.Name,
                "SilverDasher",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected SilverDasher loader identity: {definition.Name.FullName}.");
        }

        var loaderType = definition.MainModule.GetType("SilverDasher.Loader.Loader")
                         ?? throw new TypeLoadException(
                             "SilverDasher loader type SilverDasher.Loader.Loader is missing.");
        var loadMethod = loaderType.Methods.SingleOrDefault(method =>
            method.Name == "Load" &&
            !method.IsStatic &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.ReturnType.FullName == typeof(Assembly).FullName)
                         ?? throw new MissingMethodException(
                             loaderType.FullName,
                             "Load(string)");
        var bridge = definition.MainModule.ImportReference(
            typeof(HostPluginBridge).GetMethod(
                nameof(HostPluginBridge.LoadSilverDasherAssembly),
                BindingFlags.Public | BindingFlags.Static)!
            );
        ReplaceWithBridge(
            loadMethod,
            bridge,
            loadInstance: false,
            loadParameters: true);

        using var output = new MemoryStream();
        definition.Write(output);
        return LoadContentAddressedAssembly(
            "silverdasher",
            "SilverDasher.Loader",
            output.ToArray());
    }

    public static Assembly LoadSilverDasherCore(string assemblyPath)
    {
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        if (!string.Equals(
                definition.Name.Name,
                "SilverDasher.Core",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected SilverDasher core identity: {definition.Name.FullName}.");
        }

        var module = definition.MainModule;
        var processCalls = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "FFXIV_ACT_Plugin.Common.IDataRepository" &&
                called.Name == "GetCurrentFFXIVProcess" &&
                called.Parameters.Count == 0)
            .ToArray();
        if (processCalls.Length != 1)
        {
            throw new InvalidOperationException(
                $"SilverDasher process-access surface changed; expected 1 call, found {processCalls.Length}.");
        }

        var primal = module.GetType("SilverDasher.ACT.Doppelgangers.Primal")
                     ?? throw new TypeLoadException(
                         "SilverDasher Primal compatibility target is missing.");
        var init = primal.Methods.SingleOrDefault(method =>
            method.Name == "Init" &&
            !method.IsStatic &&
            method.Parameters.Count == 0 &&
            method.ReturnType.MetadataType == MetadataType.Void)
                   ?? throw new MissingMethodException(primal.FullName, "Init()");
        if (!init.Body.Instructions.Contains(processCalls[0]))
        {
            throw new InvalidOperationException(
                "SilverDasher process access moved outside Primal.Init; refusing a broad rewrite.");
        }

        var changeProcess = primal.Methods.SingleOrDefault(method =>
            method.Name == "ChangeProcess" &&
            !method.IsStatic &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == typeof(Process).FullName &&
            method.ReturnType.MetadataType == MetadataType.Void)
                            ?? throw new MissingMethodException(
                                primal.FullName,
                                "ChangeProcess(Process)");
        var getProcess = module.ImportReference(
            typeof(HostPluginBridge).GetMethod(
                nameof(HostPluginBridge.GetSilverDasherGameProcess),
                BindingFlags.Public | BindingFlags.Static)!);
        var changeProcessCalls = init.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.FullName == changeProcess.FullName);
        if (changeProcessCalls != 1)
        {
            throw new InvalidOperationException(
                $"SilverDasher process-change surface changed; expected 1 call, found {changeProcessCalls}.");
        }

        var processCall = processCalls[0];
        processCall.OpCode = OpCodes.Pop;
        processCall.Operand = null;
        init.Body.GetILProcessor().InsertAfter(
            processCall,
            Instruction.Create(OpCodes.Call, getProcess));

        var ttsCalls = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => (method, instruction)))
            .Where(pair =>
                pair.instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "Advanced_Combat_Tracker.FormActMain" &&
                called.Name == "TTS" &&
                called.Parameters.Count == 1 &&
                called.Parameters[0].ParameterType.MetadataType == MetadataType.String)
            .ToArray();
        if (ttsCalls.Length != 2 ||
            ttsCalls.Select(pair => pair.method.FullName).ToHashSet(StringComparer.Ordinal).Count != 2)
        {
            throw new InvalidOperationException(
                $"SilverDasher TTS surface changed; expected 2 exact calls, found {ttsCalls.Length}.");
        }

        var sendTts = module.ImportReference(
            typeof(HostPluginBridge).GetMethod(
                nameof(HostPluginBridge.SendSilverDasherTts),
                BindingFlags.Public | BindingFlags.Static)!);
        foreach (var (method, instruction) in ttsCalls)
        {
            var instructions = method.Body.Instructions;
            var callIndex = instructions.IndexOf(instruction);
            var formLoad = instructions
                .Take(callIndex)
                .LastOrDefault(candidate =>
                    candidate.Operand is FieldReference field &&
                    field.DeclaringType.FullName == "Advanced_Combat_Tracker.ActGlobals" &&
                    field.Name == "oFormActMain");
            if (formLoad is null)
            {
                throw new InvalidOperationException(
                    $"SilverDasher TTS call in {method.FullName} has no exact ACT form receiver load.");
            }

            formLoad.OpCode = OpCodes.Nop;
            formLoad.Operand = null;
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = sendTts;
        }

        RewriteSilverDasherNotifications(module);
        RewriteSilverDasherCombatantLookup(module);
        RewriteSilverDasherMqttPayload(module);
        RewriteSilverDasherOpcodeData(module);
        RewriteSilverDasherUnknownOpcodeGuard(module);
        RewriteSilverDasherWpfDispatch(module);

        using var output = new MemoryStream();
        definition.Write(output);
        var assembly = LoadContentAddressedAssembly(
            "silverdasher",
            "SilverDasher.Core",
            output.ToArray());
        RegisterSilverDasherWeaverResolver(assembly, assemblyPath);
        return assembly;
    }

    private static void RegisterSilverDasherWeaverResolver(
        Assembly coreAssembly,
        string coreAssemblyPath)
    {
        var coreDirectory = Path.GetDirectoryName(Path.GetFullPath(coreAssemblyPath))
                            ?? throw new InvalidDataException(
                                "SilverDasher core assembly has no parent directory.");
        var weaverPath = Path.Combine(coreDirectory, SilverDasherWeaverFileName);
        NativeLibrary.SetDllImportResolver(
            coreAssembly,
            (libraryName, _, _) =>
            {
                if (!string.Equals(
                        libraryName,
                        SilverDasherWeaverFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                HostPluginBridge.DemandSilverDasherCapability("NativeGameMemory");
                var compatiblePath = PrepareSilverDasherWeaverCompatibilityCopy(weaverPath);
                var handle = NativeLibrary.Load(compatiblePath);
                Console.WriteLine(
                    "SilverDasher Weaver loaded through its hash-pinned DACT Host compatibility copy.");
                return handle;
            });
    }

    private static string PrepareSilverDasherWeaverCompatibilityCopy(string weaverPath)
    {
        if (!File.Exists(weaverPath))
        {
            throw new FileNotFoundException(
                "SilverDasher Weaver native library is missing.",
                weaverPath);
        }

        var image = File.ReadAllBytes(weaverPath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(image));
        if (!string.Equals(sourceHash, SilverDasherWeaverSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SilverDasher Weaver hash changed; expected {SilverDasherWeaverSha256}, got {sourceHash}.");
        }

        ReadOnlySpan<byte> expectedCode =
        [
            0x83, 0xEA, 0x01, 0x74, 0x0B, 0x83, 0xFA, 0x01,
            0x74, 0x06, 0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3,
            0xE9, 0x4B, 0xED, 0xFF, 0xFF,
        ];
        const int codeStart = SilverDasherWeaverProcessAttachJumpOffset - 4;
        if (image.Length < codeStart + expectedCode.Length ||
            !image.AsSpan(codeStart, expectedCode.Length).SequenceEqual(expectedCode))
        {
            throw new InvalidDataException(
                "SilverDasher Weaver process-identity guard changed; refusing a broad native patch.");
        }

        // The original native DLL accepts only CafeACT/ACT executable names in DllMain.
        // Retarget its process-attach branch to the existing success return. This changes
        // one displacement byte and leaves its exports, hardware seal, and user package intact.
        image[SilverDasherWeaverProcessAttachJumpOffset] = 0x05;
        var compatibleHash = Convert.ToHexString(SHA256.HashData(image));
        if (!string.Equals(
                compatibleHash,
                SilverDasherCompatibleWeaverSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SilverDasher Weaver compatibility image is unexpected: {compatibleHash}.");
        }

        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "DalamudActCompat",
            "silverdasher");
        Directory.CreateDirectory(cacheDirectory);
        var compatiblePath = Path.Combine(
            cacheDirectory,
            $"SilverDasher.Weaver-{compatibleHash}.dll");
        if (!File.Exists(compatiblePath))
        {
            var stagingPath = Path.Combine(
                cacheDirectory,
                $".{Environment.ProcessId}-{Guid.NewGuid():N}.native.tmp");
            try
            {
                File.WriteAllBytes(stagingPath, image);
                try
                {
                    File.Move(stagingPath, compatiblePath);
                }
                catch (IOException) when (File.Exists(compatiblePath))
                {
                }
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
        }

        var cachedHash = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(compatiblePath)));
        if (!string.Equals(
                cachedHash,
                SilverDasherCompatibleWeaverSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cached SilverDasher Weaver compatibility image is invalid: {cachedHash}.");
        }

        return compatiblePath;
    }

    public static Assembly LoadSilverDasherManagedZodiark(string assemblyPath)
    {
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        if (!string.Equals(
                definition.Name.Name,
                "SilverDasher.ManagedZodiark",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unexpected SilverDasher Zodiark identity: {definition.Name.FullName}.");
        }

        var zodiarkProcess = definition.MainModule.GetType("Zodiark.ZodiarkProcess")
                             ?? throw new TypeLoadException(
                                 "SilverDasher Zodiark process type is missing.");
        var openProcess = zodiarkProcess.Methods.SingleOrDefault(method =>
            method.Name == "OpenProcess" &&
            !method.IsStatic &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.FullName == typeof(Process).FullName &&
            method.ReturnType.MetadataType == MetadataType.Void &&
            method.HasBody)
                          ?? throw new MissingMethodException(
                              zodiarkProcess.FullName,
                              "OpenProcess(Process)");
        var enterDebugModeCalls = openProcess.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == typeof(Process).FullName &&
                called.Name == nameof(Process.EnterDebugMode) &&
                called.Parameters.Count == 0)
            .ToArray();
        var allAccessConstants = openProcess.Body.Instructions
            .Where(instruction =>
                instruction.OpCode.Code == Code.Ldc_I4 &&
                instruction.Operand is int value &&
                value == 0x001F0FFF)
            .ToArray();
        if (enterDebugModeCalls.Length != 1 || allAccessConstants.Length != 1)
        {
            throw new InvalidOperationException(
                "SilverDasher Zodiark privilege surface changed; refusing a broad process-access rewrite.");
        }

        enterDebugModeCalls[0].OpCode = OpCodes.Nop;
        enterDebugModeCalls[0].Operand = null;
        // SilverDasher only scans the game process. Avoid PROCESS_ALL_ACCESS and
        // request exactly PROCESS_QUERY_INFORMATION | PROCESS_VM_READ.
        allAccessConstants[0].Operand = 0x0410;

        var checkPrivilege = zodiarkProcess.Methods.SingleOrDefault(method =>
            method.Name == "CheckSeDebugPrivilege" &&
            !method.IsStatic &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType is ByReferenceType byReference &&
            byReference.ElementType.MetadataType == MetadataType.Boolean &&
            method.ReturnType.MetadataType == MetadataType.Int32 &&
            method.HasBody)
                             ?? throw new MissingMethodException(
                                 zodiarkProcess.FullName,
                                 "CheckSeDebugPrivilege(bool&)");
        checkPrivilege.Body.ExceptionHandlers.Clear();
        checkPrivilege.Body.Variables.Clear();
        checkPrivilege.Body.InitLocals = false;
        checkPrivilege.Body.Instructions.Clear();
        var checkIl = checkPrivilege.Body.GetILProcessor();
        checkIl.Append(checkIl.Create(OpCodes.Ldarg_1));
        checkIl.Append(checkIl.Create(OpCodes.Ldc_I4_1));
        checkIl.Append(checkIl.Create(OpCodes.Stind_I1));
        checkIl.Append(checkIl.Create(OpCodes.Ldc_I4_0));
        checkIl.Append(checkIl.Create(OpCodes.Ret));

        using var output = new MemoryStream();
        definition.Write(output);
        return LoadContentAddressedAssembly(
            "silverdasher",
            "SilverDasher.ManagedZodiark",
            output.ToArray());
    }

    private static void RewriteSilverDasherNotifications(ModuleDefinition module)
    {
        var notifier = module.GetType("SilverDasher.ACT.Doppelgangers.Notifier")
                       ?? throw new TypeLoadException(
                           "SilverDasher notification compatibility target is missing.");
        var sendToast = notifier.Methods.SingleOrDefault(method =>
            method.Name == "SendToast" &&
            !method.IsStatic &&
            method.Parameters.Count == 4 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[1].ParameterType.FullName == "SilverDasher.ACT.Models.HuntState" &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[3].ParameterType.MetadataType == MetadataType.Boolean &&
            method.ReturnType.MetadataType == MetadataType.Boolean &&
            method.HasBody)
                        ?? throw new MissingMethodException(
                            notifier.FullName,
                            "SendToast(string,HuntState,string,bool)");
        var calls = sendToast.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .ToArray();
        var uniqueCalls = calls
            .DistinctBy(called => called.FullName)
            .ToArray();
        var configField = sendToast.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<FieldReference>()
            .DistinctBy(field => field.FullName)
            .SingleOrDefault(field =>
                field.DeclaringType.FullName == "SilverDasher.ACT.Doppelgangers.Keeper" &&
                field.Name == "Config")
                          ?? throw new MissingFieldException(
                              "SilverDasher.ACT.Doppelgangers.Keeper",
                              "Config");
        var mobsField = sendToast.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<FieldReference>()
            .DistinctBy(field => field.FullName)
            .SingleOrDefault(field =>
                field.DeclaringType.FullName == "SilverDasher.ACT.Doppelgangers.Keeper" &&
                field.Name == "Mobs")
                        ?? throw new MissingFieldException(
                            "SilverDasher.ACT.Doppelgangers.Keeper",
                            "Mobs");
        var getSystemToast = uniqueCalls.SingleOrDefault(called =>
                                 called.DeclaringType.FullName == "SilverDasher.ACT.Models.Config" &&
                                 called.Name == "get_SystemToast" &&
                                 called.Parameters.Count == 0)
                             ?? throw new MissingMethodException(
                                 "SilverDasher.ACT.Models.Config",
                                 "get_SystemToast");
        var statusPushable = uniqueCalls.SingleOrDefault(called =>
                                 called.DeclaringType.FullName == "SilverDasher.ACT.Models.Config" &&
                                 called.Name == "StatusPushable" &&
                                 called.Parameters.Count == 2)
                             ?? throw new MissingMethodException(
                                 "SilverDasher.ACT.Models.Config",
                                 "StatusPushable");
        var getKeeper = uniqueCalls.SingleOrDefault(called =>
                            called.DeclaringType.FullName == "SilverDasher.ACT.Doppelgangers.Doppelganger" &&
                            called.Name == "get_Keeper" &&
                            called.Parameters.Count == 0)
                        ?? throw new MissingMethodException(
                            "SilverDasher.ACT.Doppelgangers.Doppelganger",
                            "get_Keeper");
        var getStateName = uniqueCalls.SingleOrDefault(called =>
                               called.DeclaringType.FullName == "SilverDasher.ACT.Storages.MobStorage" &&
                               called.Name == "GetStateName" &&
                               called.Parameters.Count == 1)
                           ?? throw new MissingMethodException(
                               "SilverDasher.ACT.Storages.MobStorage",
                               "GetStateName");
        var concat = uniqueCalls.SingleOrDefault(called =>
                         called.DeclaringType.FullName == typeof(string).FullName &&
                         called.Name == nameof(string.Concat) &&
                         called.Parameters.Count == 2 &&
                         called.Parameters.All(parameter =>
                             parameter.ParameterType.MetadataType == MetadataType.String))
                     ?? throw new MissingMethodException(typeof(string).FullName, "Concat(string,string)");
        var nativeNotificationCalls = calls.Count(called =>
            called.DeclaringType.FullName is
                "Windows.UI.Notifications.ToastNotificationManager" or
                "Windows.UI.Notifications.ToastNotifier" or
                "Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder");
        if (nativeNotificationCalls != 7)
        {
            throw new InvalidOperationException(
                $"SilverDasher toast surface changed; expected 7 native calls, found {nativeNotificationCalls}.");
        }

        var bridge = module.ImportReference(
            typeof(HostPluginBridge).GetMethod(
                nameof(HostPluginBridge.SendSilverDasherNotification),
                BindingFlags.Public | BindingFlags.Static)!);
        sendToast.Body = new Mono.Cecil.Cil.MethodBody(sendToast)
        {
            InitLocals = false,
        };
        var il = sendToast.Body.GetILProcessor();
        var checkStatus = il.Create(OpCodes.Ldsfld, configField);
        var dispatch = il.Create(OpCodes.Ldarg_1);
        il.Append(il.Create(OpCodes.Ldarg, sendToast.Parameters[3]));
        il.Append(il.Create(OpCodes.Brfalse_S, dispatch));
        il.Append(il.Create(OpCodes.Ldsfld, configField));
        il.Append(il.Create(OpCodes.Callvirt, getSystemToast));
        il.Append(il.Create(OpCodes.Brtrue_S, checkStatus));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(checkStatus);
        il.Append(il.Create(OpCodes.Ldstr, "Toast"));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Callvirt, statusPushable));
        il.Append(il.Create(OpCodes.Brtrue_S, dispatch));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ret));
        il.Append(dispatch);
        il.Append(il.Create(OpCodes.Ldarg_3));
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Call, getKeeper));
        il.Append(il.Create(OpCodes.Ldfld, mobsField));
        il.Append(il.Create(OpCodes.Ldarg_2));
        il.Append(il.Create(OpCodes.Callvirt, getStateName));
        il.Append(il.Create(OpCodes.Call, concat));
        il.Append(il.Create(OpCodes.Call, bridge));
        il.Append(il.Create(OpCodes.Ret));
    }

    private static void RewriteSilverDasherCombatantLookup(ModuleDefinition module)
    {
        var negotiator = module.GetType("SilverDasher.ACT.Doppelgangers.Negotiator")
                         ?? throw new TypeLoadException(
                             "SilverDasher combatant compatibility target is missing.");
        var scanMobs = negotiator.Methods.SingleOrDefault(method =>
            method.Name == "ScanMobs" &&
            !method.IsStatic &&
            method.Parameters.Count == 0 &&
            method.HasBody)
                       ?? throw new MissingMethodException(negotiator.FullName, "ScanMobs()");
        var ffdata = negotiator.Fields.SingleOrDefault(field =>
                         field.Name == "ffdata" &&
                         field.FieldType.FullName == "FFXIV_ACT_Plugin.Common.IDataRepository")
                     ?? throw new MissingFieldException(negotiator.FullName, "ffdata");
        var dynamicNames = scanMobs.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand as string)
            .Where(name => name is "DataRepository" or "GetCombatantList")
            .ToArray();
        var storeCombatants = scanMobs.Body.Instructions.SingleOrDefault(instruction =>
            instruction.Offset == 0x00F4 && instruction.OpCode.Code == Code.Stloc_3);
        var dynamicStart = scanMobs.Body.Instructions.SingleOrDefault(instruction =>
            instruction.Offset == 0x001F &&
            instruction.OpCode.Code == Code.Ldsfld &&
            instruction.Operand is FieldReference field &&
            field.DeclaringType.FullName == "SilverDasher.ACT.Doppelgangers.Negotiator/<>o__9");
        if (dynamicNames.Length != 2 ||
            !dynamicNames.Contains("DataRepository", StringComparer.Ordinal) ||
            !dynamicNames.Contains("GetCombatantList", StringComparer.Ordinal) ||
            dynamicStart is null ||
            storeCombatants is null)
        {
            throw new InvalidOperationException(
                "SilverDasher dynamic combatant lookup surface changed; refusing a broad rewrite.");
        }

        var block = scanMobs.Body.Instructions
            .SkipWhile(instruction => instruction != dynamicStart)
            .TakeWhile(instruction => instruction != storeCombatants)
            .ToArray();
        if (block.Length < 3 || scanMobs.Body.ExceptionHandlers.Any(handler =>
                block.Contains(handler.TryStart) ||
                block.Contains(handler.TryEnd) ||
                block.Contains(handler.HandlerStart) ||
                block.Contains(handler.HandlerEnd) ||
                (handler.FilterStart is not null && block.Contains(handler.FilterStart))))
        {
            throw new InvalidOperationException(
                "SilverDasher dynamic combatant lookup control flow changed; refusing a broad rewrite.");
        }

        var getCombatants = module.ImportReference(
            typeof(FFXIV_ACT_Plugin.Common.IDataRepository).GetMethod(
                nameof(FFXIV_ACT_Plugin.Common.IDataRepository.GetCombatantList),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null)!);
        foreach (var instruction in block)
        {
            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }

        block[0].OpCode = OpCodes.Ldarg_0;
        block[1].OpCode = OpCodes.Ldfld;
        block[1].Operand = ffdata;
        block[2].OpCode = OpCodes.Callvirt;
        block[2].Operand = getCombatants;
    }

    private static void RewriteSilverDasherMqttPayload(ModuleDefinition module)
    {
        var notifier = module.GetType("SilverDasher.ACT.Doppelgangers.Notifier")
                       ?? throw new TypeLoadException(
                           "SilverDasher MQTT compatibility target is missing.");
        var unpack = notifier.Methods.SingleOrDefault(method =>
            method.Name == "Unpack" &&
            !method.IsStatic &&
            method.Parameters.Count == 2 &&
            method.Parameters.All(parameter =>
                parameter.ParameterType.MetadataType == MetadataType.String) &&
            method.ReturnType.MetadataType == MetadataType.Void &&
            method.HasBody)
                     ?? throw new MissingMethodException(
                         notifier.FullName,
                         "Unpack(string,string)");
        var deserializeCalls = unpack.Body.Instructions.Count(instruction =>
            instruction.Operand is GenericInstanceMethod called &&
            called.DeclaringType.FullName == "Newtonsoft.Json.JsonConvert" &&
            called.Name == "DeserializeObject" &&
            called.GenericArguments.Count == 1 &&
            called.GenericArguments[0].FullName == "Newtonsoft.Json.Linq.JObject");
        if (deserializeCalls != 1)
        {
            throw new InvalidOperationException(
                $"SilverDasher MQTT payload surface changed; expected 1 JSON parse, found {deserializeCalls}.");
        }

        var normalize = module.ImportReference(
            typeof(HostPluginBridge).GetMethod(
                nameof(HostPluginBridge.NormalizeSilverDasherMqttPayload),
                BindingFlags.Public | BindingFlags.Static)!);
        var first = unpack.Body.Instructions[0];
        var il = unpack.Body.GetILProcessor();
        il.InsertBefore(first, il.Create(OpCodes.Ldarg_2));
        il.InsertBefore(first, il.Create(OpCodes.Call, normalize));
        il.InsertBefore(first, il.Create(OpCodes.Starg, unpack.Parameters[1]));
    }

    private static void RewriteSilverDasherOpcodeData(ModuleDefinition module)
    {
        var storage = module.GetType("SilverDasher.ACT.Storages.BaseStorage")
                      ?? throw new TypeLoadException(
                          "SilverDasher base storage is missing.");
        var loadData = storage.Methods.SingleOrDefault(method =>
            method.Name == "LoadData" &&
            method.HasGenericParameters &&
            method.GenericParameters.Count == 1 &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.HasBody)
                       ?? throw new MissingMethodException(storage.FullName, "LoadData<T>");
        var readCalls = loadData.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == typeof(File).FullName &&
                called.Name == nameof(File.ReadAllText) &&
                called.Parameters.Count == 1)
            .ToArray();
        if (readCalls.Length != 1)
        {
            throw new InvalidOperationException(
                $"SilverDasher data-file surface changed; expected 1 read, found {readCalls.Length}.");
        }

        // Route the exact data-file read through the scoped bridge so a downloaded
        // opcode file cannot restore stale Global values after the bundled seed loads.
        readCalls[0].OpCode = OpCodes.Call;
        readCalls[0].Operand = module.ImportReference(
            typeof(HostPluginBridge).GetMethod(
                nameof(HostPluginBridge.ReadSilverDasherDataFile),
                BindingFlags.Public | BindingFlags.Static)!);
    }

    private static void RewriteSilverDasherUnknownOpcodeGuard(ModuleDefinition module)
    {
        var opcodeType = module.GetType("SilverDasher.ACT.Enums.OpcodeType")
                         ?? throw new TypeLoadException(
                             "SilverDasher opcode type enum is missing.");
        var unknown = opcodeType.Fields.SingleOrDefault(field =>
            field.Name == "Unknown" &&
            field.HasConstant &&
            Convert.ToInt32(field.Constant, CultureInfo.InvariantCulture) == 0)
                      ?? throw new InvalidOperationException(
                          "SilverDasher Unknown opcode value changed.");
        _ = unknown;

        var overseer = module.GetType("SilverDasher.ACT.Doppelgangers.Overseer")
                       ?? throw new TypeLoadException(
                           "SilverDasher network overseer is missing.");
        var receive = overseer.Methods.SingleOrDefault(method =>
            method.Name == "OnNetworkReceive" &&
            !method.IsStatic &&
            method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Int64 &&
            method.Parameters[2].ParameterType is ArrayType array &&
            array.ElementType.MetadataType == MetadataType.Byte &&
            method.ReturnType.MetadataType == MetadataType.Void &&
            method.HasBody)
                      ?? throw new MissingMethodException(
                          overseer.FullName,
                          "OnNetworkReceive(string,long,byte[])");
        var getOpcodeCalls = receive.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "SilverDasher.ACT.Storages.OpcodeStorage" &&
                called.Name == "GetOpcode" &&
                called.Parameters.Count == 1)
            .ToArray();
        var storeOpcodeType = receive.Body.Instructions.FirstOrDefault(instruction =>
            instruction.OpCode.Code == Code.Stloc_0);
        if (getOpcodeCalls.Length != 1 ||
            storeOpcodeType is null ||
            receive.Body.Variables.Count == 0 ||
            receive.Body.Variables[0].VariableType.FullName != opcodeType.FullName)
        {
            throw new InvalidOperationException(
                "SilverDasher network opcode surface changed; refusing a broad callback rewrite.");
        }

        var continueInstruction = storeOpcodeType.Next
                                  ?? throw new InvalidOperationException(
                                      "SilverDasher network callback has no opcode continuation.");
        var il = receive.Body.GetILProcessor();
        var loadOpcodeType = il.Create(OpCodes.Ldloc, receive.Body.Variables[0]);
        var continueWhenKnown = il.Create(OpCodes.Brtrue_S, continueInstruction);
        var returnUnknown = il.Create(OpCodes.Ret);
        il.InsertAfter(storeOpcodeType, loadOpcodeType);
        il.InsertAfter(loadOpcodeType, continueWhenKnown);
        il.InsertAfter(continueWhenKnown, returnUnknown);
    }

    private static void RewriteSilverDasherWpfDispatch(ModuleDefinition module)
    {
        var pluginControl = module.GetType("SilverDasher.ACT.Views.PluginControl")
                            ?? throw new TypeLoadException(
                                "SilverDasher WPF plugin control is missing.");
        var dispatcherTemplate = pluginControl.Methods.Single(method =>
            method.Name == "ButtonRestartToggle" && method.HasBody);
        var templateCalls = dispatcherTemplate.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .ToArray();
        var getDispatcher = templateCalls.Single(called =>
            called.DeclaringType.FullName == "System.Windows.Threading.DispatcherObject" &&
            called.Name == "get_Dispatcher");
        var invoke = templateCalls.Single(called =>
            called.DeclaringType.FullName == "System.Windows.Threading.Dispatcher" &&
            called.Name == "Invoke" &&
            called.Parameters.Count == 1 &&
            called.Parameters[0].ParameterType.FullName == typeof(Action).FullName);
        var actionConstructor = templateCalls.Single(called =>
            called.DeclaringType.FullName == typeof(Action).FullName &&
            called.Name == ".ctor" &&
            called.Parameters.Count == 2);

        foreach (var methodName in new[] { "SetPluginStatus", "Log" })
        {
            var method = pluginControl.Methods.SingleOrDefault(candidate =>
                candidate.Name == methodName &&
                !candidate.IsStatic &&
                candidate.Parameters.Count == 1 &&
                candidate.ReturnType.MetadataType == MetadataType.Void)
                         ?? throw new MissingMethodException(pluginControl.FullName, methodName);
            var winFormsDispatchCalls = method.Body.Instructions.Count(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "System.Windows.Forms.Control" &&
                called.Name is "get_InvokeRequired" or "Invoke");
            if (winFormsDispatchCalls != 2 || method.Body.Variables.Count != 1)
            {
                throw new InvalidOperationException(
                    $"SilverDasher {methodName} UI dispatch surface changed; refusing a broad WPF patch.");
            }

            var closureType = method.Body.Variables[0].VariableType.Resolve()
                              ?? throw new TypeLoadException(
                                  $"SilverDasher {methodName} closure type could not be resolved.");
            var closureConstructor = closureType.Methods.Single(candidate =>
                candidate.IsConstructor &&
                !candidate.IsStatic &&
                candidate.Parameters.Count == 0);
            var callback = closureType.Methods.Single(candidate =>
                candidate.Name.EndsWith(">b__0", StringComparison.Ordinal) &&
                !candidate.IsStatic &&
                candidate.Parameters.Count == 0 &&
                candidate.ReturnType.MetadataType == MetadataType.Void);
            var ownerField = closureType.Fields.Single(field =>
                field.FieldType.FullName == pluginControl.FullName);
            var valueField = closureType.Fields.Single(field =>
                field.FieldType.FullName == method.Parameters[0].ParameterType.FullName);

            method.Body = new Mono.Cecil.Cil.MethodBody(method)
            {
                InitLocals = true,
            };
            var closure = new VariableDefinition(closureType);
            method.Body.Variables.Add(closure);
            var il = method.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Newobj, closureConstructor));
            il.Append(il.Create(OpCodes.Stloc, closure));
            il.Append(il.Create(OpCodes.Ldloc, closure));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Stfld, ownerField));
            il.Append(il.Create(OpCodes.Ldloc, closure));
            il.Append(il.Create(OpCodes.Ldarg_1));
            il.Append(il.Create(OpCodes.Stfld, valueField));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Call, getDispatcher));
            il.Append(il.Create(OpCodes.Ldloc, closure));
            il.Append(il.Create(OpCodes.Ldftn, callback));
            il.Append(il.Create(OpCodes.Newobj, actionConstructor));
            il.Append(il.Create(OpCodes.Callvirt, invoke));
            il.Append(il.Create(OpCodes.Ret));
        }
    }

    private static Assembly LoadContentAddressedAssembly(
        string cacheName,
        string fileName,
        byte[] image)
    {
        var hash = Convert.ToHexString(SHA256.HashData(image));
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "DalamudActCompat",
            cacheName);
        Directory.CreateDirectory(cacheDirectory);
        var assemblyPath = Path.Combine(cacheDirectory, $"{fileName}-{hash}.dll");
        if (!File.Exists(assemblyPath))
        {
            var stagingPath = Path.Combine(
                cacheDirectory,
                $".{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(stagingPath, image);
                try
                {
                    File.Move(stagingPath, assemblyPath);
                }
                catch (IOException) when (File.Exists(assemblyPath))
                {
                }
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
        }

        return Assembly.Load(File.ReadAllBytes(assemblyPath));
    }

    public static Assembly LoadTriggernometry(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        var outer = loadContext.LoadFromAssemblyPath(assemblyPath);
        PreloadTriggernometryScriptingAssemblies(outer, loadContext);
        const string implementationResource = "costura.triggernometryplugin.dll.compressed";
        using var compressed = outer.GetManifestResourceStream(implementationResource)
                               ?? throw new MissingManifestResourceException(
                                   $"Triggernometry implementation resource {implementationResource} is missing.");
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var implementation = new MemoryStream();
        deflate.CopyTo(implementation);
        implementation.Position = 0;
        using var patched = RewriteTriggernometryImplementation(implementation);
        _ = LoadPatchedTriggernometryImplementation(patched, loadContext);
        return outer;
    }

    public static void RegisterTriggernometryPictoAct(AssemblyLoadContext loadContext)
    {
        const string moduleTypeName =
            "Triggernometry.PluginBridges.BridgeNamazu.Modules.PictoACTModule";
        var implementation = loadContext.Assemblies.FirstOrDefault(
            assembly => assembly.GetType(moduleTypeName, throwOnError: false) is not null)
            ?? throw new TypeLoadException(
                $"Loaded Triggernometry implementation does not contain {moduleTypeName}.");
        var moduleType = implementation.GetType(moduleTypeName, throwOnError: true)!;
        var module = Activator.CreateInstance(moduleType)
                     ?? throw new InvalidOperationException(
                         $"Could not create Triggernometry module {moduleTypeName}.");
        var pictoActCallback = moduleType.GetMethod(
                                   "CbPictoACT",
                                   BindingFlags.Instance | BindingFlags.Public |
                                   BindingFlags.NonPublic,
                                   null,
                                   [typeof(string)],
                                   null)
                               ?? throw new MissingMethodException(moduleTypeName, "CbPictoACT");
        var callback = (Action<string>)pictoActCallback.CreateDelegate(
            typeof(Action<string>),
            module);
        var registerCallback = moduleType.BaseType?.GetMethod(
                                   "RegisterCallback",
                                   BindingFlags.Instance | BindingFlags.Public,
                                   null,
                                   [typeof(string), typeof(Action<string>)],
                                   null)
                               ?? throw new MissingMethodException(
                                   moduleType.BaseType?.FullName,
                                   "RegisterCallback");
        registerCallback.Invoke(
            module,
            // Registration replaces callbacks with the same name. Reuse the rewritten
            // callback so this final startup step cannot discard ActorVfx removal routing.
            ["PictoACT", callback]);
        Console.WriteLine(
            "Triggernometry PictoACT callback registered through the game-side drawing broker.");
    }

    private static Assembly LoadPatchedTriggernometryImplementation(
        MemoryStream patched,
        AssemblyLoadContext loadContext)
    {
        var image = patched.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(image));
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "DalamudActCompat",
            "triggernometry");
        Directory.CreateDirectory(cacheDirectory);
        var assemblyPath = Path.Combine(
            cacheDirectory,
            $"TriggernometryPlugin-{hash}.dll");

        if (!File.Exists(assemblyPath))
        {
            var stagingPath = Path.Combine(
                cacheDirectory,
                $".{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(stagingPath, image);
                try
                {
                    File.Move(stagingPath, assemblyPath);
                }
                catch (IOException) when (File.Exists(assemblyPath))
                {
                    // Another Host finished writing the same content-addressed image.
                }
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
        }

        return loadContext.LoadFromAssemblyPath(assemblyPath);
    }

    public static Assembly LoadPostNamazu(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        PreloadPostNamazuGreyMagicCompatibility(definition, loadContext);
        var module = definition.MainModule;
        var bridgeType = typeof(HostPluginBridge);
        var setClipboard = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SetClipboardText))!);
        var getClipboard = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.GetClipboardText))!);
        var copyPostNamazuLog = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.CopyPostNamazuLog))!);
        var overlayAdapter = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.UsePostNamazuOverlayAdapter))!);
        var command = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuCommand))!);
        var nativeRuntimeAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsPostNamazuNativeRuntimeAllowed))!);
        var normalizeMarkPayload = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.NormalizePostNamazuMarkPayload))!);
        var mark = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuMark))!);
        var waymark = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuWaymark))!);
        var preset = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuPreset))!);
        var sendKey = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuKey))!);
        var queue = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuQueue))!);
        var breakQueue = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.BreakPostNamazuQueue))!);
        var networkAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsPostNamazuNetworkAllowed))!);
        var startHttpListener = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.StartPostNamazuHttpListener))!);
        var skipHttpThreadAbort = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SkipPostNamazuThreadAbort))!);
        var copyLogPatched = false;
        var overlayAdapterPatched = false;
        var httpListenerPatched = false;
        var httpThreadAbortPatched = false;
        var markPatched = false;
        var waymarkPatched = false;
        var presetPatched = false;
        var sendKeyPatched = false;
        var queuePatched = false;
        var breakQueuePatched = false;

        foreach (var type in module.Types.SelectMany(EnumerateTypes))
        {
            foreach (var method in type.Methods.Where(method => method.HasBody).ToArray())
            {
                if (type.FullName == "PostNamazu.Common.PluginIntegrationManager" &&
                    method.Name == "InitializeOverlayIntegration" &&
                    method.Parameters.Count == 0)
                {
                    ReplaceWithBridge(
                        method,
                        overlayAdapter,
                        loadInstance: true,
                        loadParameters: false);
                    overlayAdapterPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.PostNamazuUi" &&
                    method.Name == "CopyLog" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean)
                {
                    var listField = type.Fields.Single(field => field.Name == "lstMessages");
                    method.Body.ExceptionHandlers.Clear();
                    method.Body.Variables.Clear();
                    method.Body.Instructions.Clear();
                    method.Body.InitLocals = false;
                    var il = method.Body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, listField));
                    il.Append(il.Create(OpCodes.Ldarg_1));
                    il.Append(il.Create(OpCodes.Call, copyPostNamazuLog));
                    il.Append(il.Create(OpCodes.Ret));
                    copyLogPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.PostNamazu" &&
                    method.Name == "ServerStart")
                {
                    InsertBooleanGuard(method, networkAllowed, returnCompletedTask: false);
                }

                if (type.FullName == "PostNamazu.Common.HttpServer" &&
                    method.Name is "Listen" or "Stop")
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference called &&
                            called.DeclaringType.FullName == typeof(System.Net.HttpListener).FullName &&
                            called.Name == nameof(System.Net.HttpListener.Start) &&
                            called.Parameters.Count == 0)
                        {
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = startHttpListener;
                            httpListenerPatched = true;
                        }
                        else if (instruction.Operand is MethodReference abort &&
                                 abort.DeclaringType.FullName == typeof(Thread).FullName &&
                                 abort.Name == nameof(Thread.Abort) &&
                                 abort.Parameters.Count == 0)
                        {
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = skipHttpThreadAbort;
                            httpThreadAbortPatched = true;
                        }
                    }
                }

                if (type.FullName == "PostNamazu.Actions.Command" &&
                    method.Name == "DoTextCommand" &&
                    method.Parameters.Count == 1)
                {
                    ReplaceWithBridge(method, command, loadInstance: false, loadParameters: true);
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.Mark" &&
                    method.Name == "DoMarking" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                {
                    WrapWithNativeRuntimeFallback(
                        method,
                        nativeRuntimeAllowed,
                        normalizeMarkPayload,
                        mark);
                    markPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.WayMark" &&
                    method.Name == "DoWaymarks" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                {
                    ReplaceWithBridge(method, waymark, loadInstance: false, loadParameters: true);
                    waymarkPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.Preset" &&
                    method.Name == "DoInsertPreset" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                {
                    ReplaceWithBridge(method, preset, loadInstance: false, loadParameters: true);
                    presetPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.SendKey" &&
                    method.Name == "DoSendKey" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                {
                    ReplaceWithBridge(method, sendKey, loadInstance: false, loadParameters: true);
                    sendKeyPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.Queue" &&
                    method.Name == "DoQueue" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                {
                    ReplaceWithBridge(method, queue, loadInstance: true, loadParameters: true);
                    queuePatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.Queue" &&
                    method.Name == "BreakQueue" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.String)
                {
                    ReplaceWithBridge(method, breakQueue, loadInstance: false, loadParameters: true);
                    breakQueuePatched = true;
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (called.DeclaringType.FullName == typeof(Clipboard).FullName)
                    {
                        if (called.Name == nameof(Clipboard.SetText) && called.Parameters.Count == 1)
                        {
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = setClipboard;
                        }
                        else if (called.Name == nameof(Clipboard.GetText) &&
                                 called.Parameters.Count == 0)
                        {
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = getClipboard;
                        }
                    }
                }
            }
        }

        if (!copyLogPatched || !overlayAdapterPatched || !httpListenerPatched ||
            !httpThreadAbortPatched || !markPatched || !waymarkPatched ||
            !presetPatched || !sendKeyPatched || !queuePatched || !breakQueuePatched)
        {
            throw new InvalidOperationException(
                "PostNamazu compatibility shape changed; " +
                $"copyLog={copyLogPatched}, overlayAdapter={overlayAdapterPatched}, " +
                $"httpListener={httpListenerPatched}, httpStop={httpThreadAbortPatched}, " +
                $"mark={markPatched}, waymark={waymarkPatched}, preset={presetPatched}, " +
                $"sendKey={sendKeyPatched}, queue={queuePatched}, breakQueue={breakQueuePatched}.");
        }

        ValidatePostNamazuPublicSurface(module);

        using var output = new MemoryStream();
        definition.Write(output);
        output.Position = 0;
        return loadContext.LoadFromStream(output);
    }

    private static void PreloadPostNamazuGreyMagicCompatibility(
        AssemblyDefinition postNamazu,
        AssemblyLoadContext loadContext)
    {
        const string resourceName = "costura64.greymagic.dll";
        if (loadContext.Assemblies.Any(assembly => string.Equals(
                assembly.GetName().Name,
                "GreyMagic",
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "GreyMagic was loaded before the PostNamazu .NET compatibility shim.");
        }

        var resource = postNamazu.MainModule.Resources
                           .OfType<EmbeddedResource>()
                           .SingleOrDefault(candidate => string.Equals(
                               candidate.Name,
                               resourceName,
                               StringComparison.OrdinalIgnoreCase))
                       ?? throw new MissingManifestResourceException(resourceName);
        using var resourceInput = resource.GetResourceStream();
        using var originalImage = new MemoryStream();
        resourceInput.CopyTo(originalImage);
        var originalBytes = originalImage.ToArray();
        using var originalDefinition = AssemblyDefinition.ReadAssembly(
            new MemoryStream(originalBytes, writable: false));
        var originalAttributes = originalDefinition.MainModule.Attributes;
        var originalNativeMethods = CountNativeMethods(originalDefinition.MainModule);
        if ((originalAttributes & ModuleAttributes.ILOnly) != 0 ||
            originalNativeMethods == 0)
        {
            throw new InvalidOperationException(
                "PostNamazu GreyMagic is no longer the expected mixed-mode native image.");
        }

        using var definition = dnlib.DotNet.ModuleDefMD.Load(originalBytes);
        var replacement = typeof(EventWaitHandleAcl).GetMethod(
                              nameof(EventWaitHandleAcl.Create),
                              BindingFlags.Static | BindingFlags.Public,
                              binder: null,
                              [
                                  typeof(bool),
                                  typeof(EventResetMode),
                                  typeof(string),
                                  typeof(bool).MakeByRefType(),
                                  typeof(EventWaitHandleSecurity),
                              ],
                              modifiers: null)
                          ?? throw new MissingMethodException(
                              typeof(EventWaitHandleAcl).FullName,
                              nameof(EventWaitHandleAcl.Create));
        var replacementReference = new dnlib.DotNet.Importer(definition).Import(replacement);
        var patched = 0;
        foreach (var instruction in definition.GetTypes()
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is not dnlib.DotNet.IMethod called ||
                called.Name.String != ".ctor" ||
                called.DeclaringType.FullName != typeof(EventWaitHandle).FullName ||
                called.MethodSig?.Params.Count != 5 ||
                called.MethodSig.Params[0].FullName != typeof(bool).FullName ||
                called.MethodSig.Params[1].FullName != typeof(EventResetMode).FullName ||
                called.MethodSig.Params[2].FullName != typeof(string).FullName ||
                called.MethodSig.Params[3].FullName != "System.Boolean&" ||
                called.MethodSig.Params[4].FullName != typeof(EventWaitHandleSecurity).FullName)
            {
                continue;
            }

            instruction.OpCode = dnlib.DotNet.Emit.OpCodes.Call;
            instruction.Operand = replacementReference;
            patched++;
        }

        if (patched != 1 || definition.GetTypes()
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Any(instruction =>
                    instruction.Operand is dnlib.DotNet.IMethod called &&
                    called.Name.String == ".ctor" &&
                    called.DeclaringType.FullName == typeof(EventWaitHandle).FullName &&
                    called.MethodSig?.Params.Count == 5))
        {
            throw new InvalidOperationException(
                $"Unexpected PostNamazu GreyMagic EventWaitHandle shape: patched={patched}.");
        }

        using var output = new MemoryStream();
        definition.NativeWrite(
            output,
            new dnlib.DotNet.Writer.NativeModuleWriterOptions(
                definition,
                optimizeImageSize: true));
        var image = output.ToArray();
        using (var validation = AssemblyDefinition.ReadAssembly(
                   new MemoryStream(image, writable: false)))
        {
            var validationCalls = validation.MainModule.Types
                .SelectMany(EnumerateTypes)
                .SelectMany(type => type.Methods)
                .Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions)
                .Select(instruction => instruction.Operand)
                .OfType<MethodReference>()
                .ToArray();
            var legacyCalls = validationCalls.Count(called =>
                called.Name == ".ctor" &&
                called.DeclaringType.FullName == typeof(EventWaitHandle).FullName &&
                called.Parameters.Count == 5);
            var replacementCalls = validationCalls.Count(called =>
                called.Name == nameof(EventWaitHandleAcl.Create) &&
                called.DeclaringType.FullName == typeof(EventWaitHandleAcl).FullName &&
                called.Parameters.Count == 5);
            var rewrittenNativeMethods = CountNativeMethods(validation.MainModule);
            if (legacyCalls != 0 || replacementCalls != 1 ||
                validation.MainModule.Attributes != originalAttributes ||
                rewrittenNativeMethods != originalNativeMethods)
            {
                throw new InvalidOperationException(
                    "PostNamazu GreyMagic native-image validation failed: " +
                    $"legacy={legacyCalls}, replacement={replacementCalls}, " +
                    $"attributes={validation.MainModule.Attributes}/{originalAttributes}, " +
                    $"native={rewrittenNativeMethods}/{originalNativeMethods}.");
            }
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "DalamudActCompat",
            "postnamazu-greymagic");
        Directory.CreateDirectory(cacheDirectory);
        var assemblyPath = Path.Combine(cacheDirectory, $"GreyMagic-{hash}.dll");
        if (!File.Exists(assemblyPath))
        {
            var stagingPath = Path.Combine(
                cacheDirectory,
                $".{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(stagingPath, image);
                try
                {
                    File.Move(stagingPath, assemblyPath);
                }
                catch (IOException) when (File.Exists(assemblyPath))
                {
                    // Another Host finished writing the same content-addressed image.
                }
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
        }

        _ = loadContext.LoadFromAssemblyPath(assemblyPath);
        Console.WriteLine(
            "PostNamazu GreyMagic EventWaitHandle ACL compatibility shim loaded.");
    }

    private static int CountNativeMethods(ModuleDefinition module)
        => module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Count(method =>
                method.RVA != 0 &&
                (!method.HasBody ||
                 (method.ImplAttributes & Mono.Cecil.MethodImplAttributes.CodeTypeMask) !=
                 Mono.Cecil.MethodImplAttributes.IL));

    private static MemoryStream RewriteTriggernometryImplementation(Stream implementation)
    {
        using var definition = AssemblyDefinition.ReadAssembly(implementation);
        var module = definition.MainModule;
        var resources = module.Resources
            .OfType<EmbeddedResource>()
            .Where(resource => resource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var resource in resources)
        {
            using var input = resource.GetResourceStream();
            using var reader = new ResourceReader(input);
            using var output = new MemoryStream();
            using (var writer = new PreserializedResourceWriter(output))
            {
                IDictionaryEnumerator entries = reader.GetEnumerator();
                while (entries.MoveNext())
                {
                    var key = (string)entries.Key;
                    reader.GetResourceData(key, out var typeName, out var payload);
                    if (typeName.StartsWith("ResourceTypeCode.", StringComparison.Ordinal))
                    {
                        writer.AddResource(key, entries.Value);
                    }
                    else
                    {
                        WriteConvertedResource(writer, key, DeserializeLegacyPayload(payload));
                    }
                }
            }

            var index = module.Resources.IndexOf(resource);
            module.Resources[index] = new EmbeddedResource(
                resource.Name,
                resource.Attributes,
                output.ToArray());
        }

        RedirectResourceManagerCalls(module);
        PatchLegacyJavaScriptSerializer(module);
        PatchTriggernometryCompatibility(module);
        ValidateTriggernometryPublicSurface(module);
        var patched = new MemoryStream();
        definition.Write(patched);
        patched.Position = 0;
        return patched;
    }

    private static void PatchLegacyJavaScriptSerializer(ModuleDefinition module)
    {
        const string legacyTypeName =
            "System.Web.Script.Serialization.JavaScriptSerializer";
        var bridgeType = typeof(LegacyJavaScriptSerializer);
        var bridgeTypeReference = module.ImportReference(bridgeType);
        var constructor = module.ImportReference(bridgeType.GetConstructor(Type.EmptyTypes)!);
        var serialize = module.ImportReference(bridgeType.GetMethod(
            nameof(LegacyJavaScriptSerializer.Serialize),
            [typeof(object)])!);
        var deserialize = module.ImportReference(bridgeType.GetMethods()
            .Single(method =>
                method.Name == nameof(LegacyJavaScriptSerializer.Deserialize) &&
                method.IsGenericMethodDefinition));
        var deserializeObject = module.ImportReference(bridgeType.GetMethod(
            nameof(LegacyJavaScriptSerializer.DeserializeObject),
            [typeof(string)])!);
        var patched = 0;

        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is not MethodReference called ||
                called.DeclaringType.FullName != legacyTypeName)
            {
                continue;
            }

            MethodReference replacement = called.Name switch
            {
                ".ctor" when called.Parameters.Count == 0 => constructor,
                nameof(LegacyJavaScriptSerializer.Serialize)
                    when called.Parameters.Count == 1 => serialize,
                nameof(LegacyJavaScriptSerializer.Deserialize)
                    when called is GenericInstanceMethod generic &&
                         generic.GenericArguments.Count == 1 =>
                    MakeGenericMethod(deserialize, generic.GenericArguments),
                nameof(LegacyJavaScriptSerializer.DeserializeObject)
                    when called.Parameters.Count == 1 => deserializeObject,
                _ => throw new InvalidOperationException(
                    $"Unsupported JavaScriptSerializer call {called.FullName}."),
            };
            instruction.Operand = replacement;
            patched++;
        }

        if (patched != 24)
        {
            throw new InvalidOperationException(
                $"Expected 24 Triggernometry JavaScriptSerializer calls, patched {patched}.");
        }

        var legacyTypeReferences = module.GetTypeReferences()
            .Where(reference => reference.FullName == legacyTypeName)
            .ToArray();
        if (legacyTypeReferences.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one Triggernometry JavaScriptSerializer type reference, found {legacyTypeReferences.Length}.");
        }

        foreach (var reference in legacyTypeReferences)
        {
            reference.Namespace = bridgeTypeReference.Namespace;
            reference.Name = bridgeTypeReference.Name;
            reference.Scope = bridgeTypeReference.Scope;
        }

        if (module.GetTypeReferences().Any(reference => reference.FullName == legacyTypeName))
        {
            throw new InvalidOperationException(
                "Triggernometry still contains a JavaScriptSerializer type reference after patching.");
        }

        var systemWeb = module.AssemblyReferences.SingleOrDefault(reference =>
            reference.Name == "System.Web.Extensions");
        if (systemWeb is not null)
        {
            module.AssemblyReferences.Remove(systemWeb);
        }
    }

    private static void PatchTriggernometryCompatibility(ModuleDefinition module)
    {
        var bridgeType = typeof(HostPluginBridge);
        var admin = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.CheckTriggernometryAdministratorCapability))!);
        var enqueueGeneric = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.EnqueueTriggerEventBounded))!);
        var unstoppable = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.ReportUnstoppableTriggernometryThread))!);
        var networkAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsTriggernometryNetworkAllowed))!);
        var scriptAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsTriggernometryHighRiskScriptAllowed))!);
        var pictoAct = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuPictoAct))!);
        var extractPictoActActorRemovals = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.ExtractPictoActActorRemovalCommands))!);
        var setHeading = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuSetHeading))!);
        var patchExportXml = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.PatchTriggernometryExportXml))!);
        var subscribeZoneChanges = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SubscribeTriggernometryZoneChanges))!);
        var unsubscribeZoneChanges = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.UnsubscribeTriggernometryZoneChanges))!);
        var startProcessByName = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.StartTriggernometryProcess),
                [typeof(string)])!);
        var startProcessByNameAndArguments = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.StartTriggernometryProcess),
                [typeof(string), typeof(string)])!);
        var startProcessByInfo = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.StartTriggernometryProcess),
                [typeof(System.Diagnostics.ProcessStartInfo)])!);
        var skipStartupUpdateCheck = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.SkipTriggernometryStartupUpdateCheck),
                [typeof(object), typeof(bool)])!);
        var checkPostNamazuAdministratorRequirement = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.CheckTriggernometryPostNamazuAdministratorRequirement),
                Type.EmptyTypes)!);
        var callOverlayHandler = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.CallTriggernometryOverlayHandler),
                [typeof(object)])!);
        var adminMethod = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Single(method =>
                method.Name == "CheckIfAdministrator" &&
                method.ReturnType.MetadataType == MetadataType.Boolean &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
        ReplaceWithBridge(adminMethod, admin, loadInstance: false, loadParameters: true);

        var ffxivBridge = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.PluginBridges.BridgeFFXIV");
        var subscribeZoneMethod = ffxivBridge.Methods.Single(method =>
            method.Name == "SubscribeToZoneChanged" && method.Parameters.Count == 1);
        var unsubscribeNetworkMethod = ffxivBridge.Methods.Single(method =>
            method.Name == "UnsubscribeFromNetworkEvents" && method.Parameters.Count == 1);
        ReplaceWithBridge(
            subscribeZoneMethod,
            subscribeZoneChanges,
            loadInstance: false,
            loadParameters: true);
        ReplaceWithBridge(
            unsubscribeNetworkMethod,
            unsubscribeZoneChanges,
            loadInstance: false,
            loadParameters: true);

        var repositoryUpdate = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Single(method =>
                method.Name == "UpdateAllRepositoriesAsync" &&
                method.Parameters.Count == 1 &&
                method.ReturnType.FullName == typeof(Task).FullName);
        InsertBooleanGuard(repositoryUpdate, networkAllowed, returnCompletedTask: true);
        var endpointStart = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.Core.Endpoint")
            .Methods
            .Single(method => method.Name == "Start" && method.Parameters.Count == 0);
        InsertBooleanGuard(endpointStart, networkAllowed, returnCompletedTask: false);
        var scriptSecurity = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type =>
                type.FullName == "Triggernometry.Core.Scripting.ScriptSecurity")
            .Methods
            .Single(method =>
                method.Name == "IsFeatureAllowedByConfig" &&
                method.ReturnType.MetadataType == MetadataType.Boolean);
        InsertBooleanGuard(scriptSecurity, scriptAllowed, returnCompletedTask: false);
        var pictoActCallback = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName ==
                "Triggernometry.PluginBridges.BridgeNamazu.Modules.PictoACTModule")
            .Methods
            .Single(method =>
                method.Name == "CbPictoACT" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        WrapPictoActWithActorRemoval(
            pictoActCallback,
            pictoAct,
            extractPictoActActorRemovals);

        var entitySetHeading = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName ==
                "Triggernometry.PluginBridges.BridgeNamazu.Modules.EntityModule")
            .Methods
            .Single(method =>
                method.Name == "SetHeading" &&
                method.Parameters.Count == 2 &&
                method.Parameters[0].ParameterType.FullName == typeof(IntPtr).FullName &&
                method.Parameters[1].ParameterType.MetadataType == MetadataType.Single);
        // The original BridgeNamazu path attaches GreyMagic from the isolated Host. Route only
        // local-player heading through the permission-gated game-side bridge; other native
        // PostNamazu operations keep their existing compatibility behavior.
        ReplaceWithBridge(
            entitySetHeading,
            setHeading,
            loadInstance: false,
            loadParameters: true);

        var exportUnserialize = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.Core.TriggernometryExport")
            .Methods
            .Single(method =>
                method.Name == "Unserialize" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        var exportFirstInstruction = exportUnserialize.Body.Instructions[0];
        var exportIl = exportUnserialize.Body.GetILProcessor();
        // Patch the imported repository text before Triggernometry materializes trigger objects,
        // so refreshed resources cannot silently restore the two incompatible U6b assumptions.
        exportIl.InsertBefore(
            exportFirstInstruction,
            exportIl.Create(OpCodes.Ldarg, exportUnserialize.Parameters[0]));
        exportIl.InsertBefore(exportFirstInstruction, exportIl.Create(OpCodes.Call, patchExportXml));
        exportIl.InsertBefore(
            exportFirstInstruction,
            exportIl.Create(OpCodes.Starg, exportUnserialize.Parameters[0]));

        var bridgeNamazu = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type =>
                type.FullName ==
                "Triggernometry.PluginBridges.BridgeNamazu.BridgeNamazu");
        var wrappedPluginGetter = bridgeNamazu.Methods.Single(method =>
            method.Name == "get_WrappedPlugin" && method.Parameters.Count == 0);
        var wrappedPluginField = bridgeNamazu.Fields.Single(field =>
            field.Name == "_wrappedPlugin" && field.IsStatic);
        var wrappedPluginType = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.Core.RealPlugin/PluginWrapper");
        var pluginObjectGetter = wrappedPluginType.Methods.Single(method =>
            method.Name == "get_pluginObj" && method.Parameters.Count == 0);
        var instanceHookGetter = wrappedPluginGetter.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Single(method =>
                method.DeclaringType.FullName == "Triggernometry.Core.RealPlugin" &&
                method.Name == "get_InstanceHook");
        var instanceHookInvoke = wrappedPluginGetter.Body.Instructions
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Single(method =>
                method.DeclaringType.FullName ==
                    "Triggernometry.Core.RealPlugin/InstanceDelegate" &&
                method.Name == "Invoke");

        // Triggernometry starts before PostNamazu so PostNamazu can discover it in ACT's plugin
        // list. The upstream getter permanently caches the first empty wrapper from that order.
        // Retry only while the cached wrapper has no plugin object, preserving normal caching once
        // PostNamazu is available and correcting the upstream filename typo at the same boundary.
        wrappedPluginGetter.Body = new Mono.Cecil.Cil.MethodBody(wrappedPluginGetter);
        var wrappedPluginIl = wrappedPluginGetter.Body.GetILProcessor();
        var resolveWrappedPlugin = wrappedPluginIl.Create(OpCodes.Call, instanceHookGetter);
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ldsfld, wrappedPluginField));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Brfalse, resolveWrappedPlugin));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ldsfld, wrappedPluginField));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Callvirt, pluginObjectGetter));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Brfalse, resolveWrappedPlugin));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ldsfld, wrappedPluginField));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ret));
        wrappedPluginIl.Append(resolveWrappedPlugin);
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ldstr, "PostNamazu.dll"));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ldstr, "PostNamazu.PostNamazu"));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Callvirt, instanceHookInvoke));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Dup));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Stsfld, wrappedPluginField));
        wrappedPluginIl.Append(wrappedPluginIl.Create(OpCodes.Ret));

        var bridgeNamazuInitializer = bridgeNamazu
            .Methods
            .Single(method => method.IsConstructor && method.IsStatic);
        var postNamazuAdminChecks = bridgeNamazuInitializer.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "Triggernometry.Core.RealPlugin" &&
                called.Name == "IsAdmin" &&
                called.Parameters.Count == 0 &&
                called.ReturnType.MetadataType == MetadataType.Boolean)
            .ToArray();
        var postNamazuAdministratorWarnings = bridgeNamazuInitializer.Body.Instructions
            .Where(instruction =>
                instruction.OpCode.Code == Code.Ldstr &&
                instruction.Operand is string message &&
                message.Contains("鲶鱼精邮差扩展", StringComparison.Ordinal) &&
                message.Contains("ACT 未以管理员权限运行", StringComparison.Ordinal))
            .ToArray();
        if (postNamazuAdminChecks.Length != 1 ||
            postNamazuAdministratorWarnings.Length != 1)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry/PostNamazu administrator notice shape: " +
                $"checks={postNamazuAdminChecks.Length}, " +
                $"warnings={postNamazuAdministratorWarnings.Length}.");
        }

        postNamazuAdminChecks[0].OpCode = OpCodes.Call;
        postNamazuAdminChecks[0].Operand = checkPostNamazuAdministratorRequirement;

        var triggernometryInitializer = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.Core.RealPlugin")
            .Methods
            .Single(method =>
                method.Name == "InitPlugin" &&
                method.Parameters.Count == 2);
        var startupUpdateChecks = triggernometryInitializer.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "Triggernometry.Core.RealPlugin" &&
                called.Name == "CheckForUpdates" &&
                called.Parameters.Count == 1 &&
                called.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean)
            .ToArray();
        if (startupUpdateChecks.Length != 1)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry startup update-check shape: " +
                $"calls={startupUpdateChecks.Length}.");
        }

        startupUpdateChecks[0].OpCode = OpCodes.Call;
        startupUpdateChecks[0].Operand = skipStartupUpdateCheck;

        var realPlugin = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.Core.RealPlugin");
        var handleVersionUpdate = realPlugin.Methods.Single(method =>
            method.Name == "HandleVersionUpdate" &&
            method.Parameters.Count == 0);
        var saveCurrentConfig = realPlugin.Methods.Single(method =>
            method.Name == "SaveCurrentConfig" &&
            method.Parameters.Count == 0);
        var pluginVersionWrites = handleVersionUpdate.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == "Triggernometry.Core.Configuration" &&
                called.Name == "set_PluginVersion" &&
                called.Parameters.Count == 1)
            .ToArray();
        if (pluginVersionWrites.Length != 1)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry version-state shape: " +
                $"writes={pluginVersionWrites.Length}.");
        }

        var versionIl = handleVersionUpdate.Body.GetILProcessor();
        var loadThisForSave = versionIl.Create(OpCodes.Ldarg_0);
        versionIl.InsertAfter(pluginVersionWrites[0], loadThisForSave);
        versionIl.InsertAfter(
            loadThisForSave,
            versionIl.Create(OpCodes.Call, saveCurrentConfig));

        var launchProcess = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type =>
                type.FullName == "Triggernometry.Core.Actions.ActionLaunchProcess")
            .Methods
            .Single(method =>
                method.Name == "ExecuteImplementation" &&
                method.Parameters.Count == 1);
        var instanceProcessStarts = launchProcess.Body.Instructions
            .Where(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == typeof(System.Diagnostics.Process).FullName &&
                called.Name == nameof(System.Diagnostics.Process.Start) &&
                called.HasThis &&
                called.Parameters.Count == 0)
            .ToArray();
        if (instanceProcessStarts.Length != 1 ||
            launchProcess.Body.Variables.Count < 3 ||
            launchProcess.Body.Variables[1].VariableType.FullName !=
                typeof(System.Diagnostics.Process).FullName ||
            launchProcess.Body.Variables[2].VariableType.FullName !=
                typeof(System.Diagnostics.ProcessStartInfo).FullName)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry LaunchProcess shape: " +
                $"instanceStarts={instanceProcessStarts.Length}, " +
                $"variables={launchProcess.Body.Variables.Count}.");
        }

        var instanceStart = instanceProcessStarts[0];
        var processLoadForStart = instanceStart.Previous;
        var setStartInfo = processLoadForStart?.Previous;
        var startInfoLoad = setStartInfo?.Previous;
        var processLoadForSet = startInfoLoad?.Previous;
        var discardStartResult = instanceStart.Next;
        var returnInstruction = launchProcess.Body.Instructions.Single(instruction =>
            instruction.OpCode.Code == Code.Ret);
        if (processLoadForSet?.OpCode.Code != Code.Ldloc_1 ||
            startInfoLoad?.OpCode.Code != Code.Ldloc_2 ||
            setStartInfo?.Operand is not MethodReference setStartInfoCall ||
            setStartInfoCall.DeclaringType.FullName !=
                typeof(System.Diagnostics.Process).FullName ||
            setStartInfoCall.Name != "set_StartInfo" ||
            processLoadForStart?.OpCode.Code != Code.Ldloc_1 ||
            discardStartResult?.OpCode.Code != Code.Pop)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry LaunchProcess instruction sequence.");
        }

        processLoadForSet.OpCode = OpCodes.Nop;
        processLoadForSet.Operand = null;
        setStartInfo.OpCode = OpCodes.Call;
        setStartInfo.Operand = startProcessByInfo;
        processLoadForStart.OpCode = OpCodes.Stloc_1;
        processLoadForStart.Operand = null;
        instanceStart.OpCode = OpCodes.Ldloc_1;
        instanceStart.Operand = null;
        discardStartResult.OpCode = OpCodes.Brfalse;
        discardStartResult.Operand = returnInstruction;

        // Triggernometry first probes OverlayPlugin's private combatant-memory shape before
        // falling back to FFXIV_ACT_Plugin. Current OverlayPlugin no longer exposes the old
        // reflection contract to the external Host, while the Host already supplies a complete
        // read-only FFXIV_ACT_Plugin repository from the game-side entity snapshot. Route every
        // combatant lookup directly to that stable repository and avoid the obsolete probe.
        var bridgeFfxiv = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.PluginBridges.BridgeFFXIV");
        var combatants = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.PluginBridges.ModuleCombatants");
        var combatantsInitializer = combatants.Methods.Single(method =>
            method.Name == "Initialize" && method.Parameters.Count == 0);
        ReplaceWithReturn(combatantsInitializer);
        ReplaceWithBridge(
            combatants.Methods.Single(method =>
                method.Name == "InternalGetEntities" && method.Parameters.Count == 0),
            bridgeFfxiv.Methods.Single(method =>
                method.Name == "InternalGetEntities" && method.Parameters.Count == 0),
            loadInstance: false,
            loadParameters: false);
        ReplaceWithBridge(
            combatants.Methods.Single(method =>
                method.Name == "InternalGetEntityByID" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.UInt32),
            bridgeFfxiv.Methods.Single(method =>
                method.Name == "InternalGetEntityByID" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.UInt32),
            loadInstance: false,
            loadParameters: true);
        ReplaceWithBridge(
            combatants.Methods.Single(method =>
                method.Name == "InternalGetMyself" && method.Parameters.Count == 0),
            bridgeFfxiv.Methods.Single(method =>
                method.Name == "InternalGetMyself" && method.Parameters.Count == 0),
            loadInstance: false,
            loadParameters: false);

        // Triggernometry's legacy reflection contract expects OverlayPlugin.Core to run in
        // the same ACT process. In this architecture the real dispatcher intentionally stays
        // in FFXIV, so keep the public Triggernometry API and broker each handler call over the
        // bounded, permission-checked Host IPC channel.
        var moduleEvents = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.PluginBridges.ModuleEvents");
        var moduleEventsInitializer = moduleEvents.Methods.Single(method =>
            method.Name == "Initialize" && method.Parameters.Count == 0);
        var moduleEventsReady = moduleEvents.Fields.Single(field =>
            field.Name == "Ready" &&
            field.FieldType.MetadataType == MetadataType.Boolean &&
            field.IsStatic);
        moduleEventsInitializer.Body.ExceptionHandlers.Clear();
        moduleEventsInitializer.Body.Variables.Clear();
        moduleEventsInitializer.Body.Instructions.Clear();
        moduleEventsInitializer.Body.InitLocals = false;
        var moduleEventsIl = moduleEventsInitializer.Body.GetILProcessor();
        moduleEventsIl.Append(moduleEventsIl.Create(OpCodes.Ldc_I4_1));
        moduleEventsIl.Append(moduleEventsIl.Create(OpCodes.Stsfld, moduleEventsReady));
        moduleEventsIl.Append(moduleEventsIl.Create(OpCodes.Ret));
        ReplaceWithBridge(
            moduleEvents.Methods.Single(method =>
                method.Name == "CallOverlayHandler" &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.Object),
            callOverlayHandler,
            loadInstance: false,
            loadParameters: true);

        var enqueueCount = 0;
        var abortCount = 0;
        var processStartCount = 0;
        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is MethodReference
                {
                    Name: "Enqueue",
                    DeclaringType: GenericInstanceType queueType,
                } &&
                queueType.ElementType.FullName == "System.Collections.Generic.Queue`1" &&
                queueType.GenericArguments is [{ FullName: "Triggernometry.Core.LogEvent" } argument])
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = MakeGenericMethod(enqueueGeneric, [argument]);
                enqueueCount++;
                continue;
            }

            if (instruction.Operand is MethodReference
                {
                    Name: nameof(Thread.Abort),
                    DeclaringType.FullName: "System.Threading.Thread",
                })
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = unstoppable;
                abortCount++;
                continue;
            }

            if (instruction.Operand is MethodReference
                {
                    Name: nameof(System.Diagnostics.Process.Start),
                    DeclaringType.FullName: "System.Diagnostics.Process",
                } processStart)
            {
                MethodReference? replacement = processStart.Parameters.Count switch
                {
                    1 when processStart.Parameters[0].ParameterType.FullName ==
                               typeof(string).FullName
                        => startProcessByName,
                    2 when processStart.Parameters[0].ParameterType.FullName ==
                               typeof(string).FullName &&
                           processStart.Parameters[1].ParameterType.FullName ==
                               typeof(string).FullName
                        => startProcessByNameAndArguments,
                    1 when processStart.Parameters[0].ParameterType.FullName ==
                               typeof(System.Diagnostics.ProcessStartInfo).FullName
                        => startProcessByInfo,
                    _ => null,
                };
                if (replacement is not null)
                {
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = replacement;
                    processStartCount++;
                }
            }
        }

        if (enqueueCount != 2 || abortCount != 3 || processStartCount == 0)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry patch shape: " +
                $"enqueue={enqueueCount}, abort={abortCount}, process={processStartCount}.");
        }
    }

    private static void PreloadTriggernometryScriptingAssemblies(
        Assembly outer,
        AssemblyLoadContext loadContext)
    {
        string[] resources =
        [
            "costura.microsoft.codeanalysis.dll.compressed",
            "costura.microsoft.codeanalysis.scripting.dll.compressed",
            "costura.microsoft.codeanalysis.csharp.dll.compressed",
            "costura.microsoft.codeanalysis.csharp.scripting.dll.compressed",
        ];
        foreach (var resourceName in resources)
        {
            using var compressed = outer.GetManifestResourceStream(resourceName)
                                   ?? throw new MissingManifestResourceException(resourceName);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var dependency = new MemoryStream();
            deflate.CopyTo(dependency);
            dependency.Position = 0;
            using var definition = AssemblyDefinition.ReadAssembly(dependency);
            if (loadContext.Assemblies.Any(candidate => string.Equals(
                    candidate.GetName().Name,
                    definition.Name.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            dependency.Position = 0;
            loadContext.LoadFromStream(dependency);
        }
    }

    private static void ValidatePostNamazuPublicSurface(ModuleDefinition module)
    {
        var types = module.Types.SelectMany(EnumerateTypes).ToArray();
        var actionModules = types
            .Where(type => type.BaseType?.FullName == "PostNamazu.Actions.NamazuModule")
            .Select(type => type.FullName)
            .ToArray();

        var commands = types
            .SelectMany(type => type.Methods.Select(method => (type, method)))
            .SelectMany(pair => pair.method.CustomAttributes
                .Where(attribute =>
                    attribute.AttributeType.FullName == "PostNamazu.Attributes.CommandAttribute")
                .Select(attribute =>
                    $"{pair.type.FullName}.{pair.method.Name}:" +
                    attribute.ConstructorArguments.Single().Value))
            .ToArray();
        ValidatePostNamazuActionSurface(actionModules, commands);

        var attach = types.Single(type => type.FullName == "PostNamazu.PostNamazu")
            .Methods.Single(method => method.Name == "Attach" && method.Parameters.Count == 0);
        var startsOriginalMemory = attach.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == "GreyMagic.ExternalProcessMemory" &&
            called.Name == ".ctor");
        var processMonitor = types
            .Single(type => type.FullName == "PostNamazu.Common.ProcessManager")
            .Methods.Single(method =>
                method.Name == "StartProcessMonitoring" && method.Parameters.Count == 0);
        var startsOriginalMonitor = processMonitor.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == typeof(BackgroundWorker).FullName &&
            called.Name == nameof(BackgroundWorker.RunWorkerAsync));
        var rawEntryPoints = types.Single(type => type.FullName == "PostNamazu.PostNamazu")
            .Methods.Where(method =>
                method.Name is "Call" or "DirectCall" or "ExecuteInFrameLock")
            .ToArray();
        var rawEntryPointWasRejected = rawEntryPoints.Any(method =>
            method.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
                called.Name.Contains("Unsupported", StringComparison.Ordinal)));
        if (!startsOriginalMemory || !startsOriginalMonitor || rawEntryPointWasRejected)
        {
            throw new InvalidOperationException(
                "PostNamazu original native runtime was truncated: " +
                $"memory={startsOriginalMemory}, monitor={startsOriginalMonitor}, " +
                $"rawRejected={rawEntryPointWasRejected}.");
        }
    }

    private static void ValidatePostNamazuActionSurface(
        IEnumerable<string> actionModules,
        IEnumerable<string> commands)
    {
        string[] current =
        [
            "PostNamazu.Actions.Command",
            "PostNamazu.Actions.Mark",
            "PostNamazu.Actions.Preset",
            "PostNamazu.Actions.Queue",
            "PostNamazu.Actions.SendKey",
            "PostNamazu.Actions.WayMark",
        ];
        var legacy = current
            .Append("PostNamazu.Actions.NormalCommand")
            .ToArray();
        var actualModules = actionModules.ToHashSet(StringComparer.Ordinal);
        string normalCommandOwner;
        if (actualModules.SetEquals(current))
        {
            normalCommandOwner = "PostNamazu.Actions.Command";
        }
        else if (actualModules.SetEquals(legacy))
        {
            normalCommandOwner = "PostNamazu.Actions.NormalCommand";
        }
        else
        {
            throw new InvalidOperationException(
                "PostNamazu action modules changed; " +
                $"actual=[{string.Join(",", actualModules.Order(StringComparer.Ordinal))}], " +
                $"accepted=[{string.Join(",", current.Order(StringComparer.Ordinal))}] or " +
                $"[{string.Join(",", legacy.Order(StringComparer.Ordinal))}].");
        }

        string[] expectedCommands =
        [
            "PostNamazu.Actions.Command.DoTextCommand:command",
            "PostNamazu.Actions.Command.DoTextCommand:DoTextCommand",
            "PostNamazu.Actions.Mark.DoMarking:mark",
            $"{normalCommandOwner}.DoNormalTextCommand:normalcommand",
            $"{normalCommandOwner}.DoNormalTextCommand:DoNormalTextCommand",
            "PostNamazu.Actions.Preset.DoInsertPreset:preset",
            "PostNamazu.Actions.Preset.DoInsertPreset:DoInsertPreset",
            "PostNamazu.Actions.Queue.DoQueue:queue",
            "PostNamazu.Actions.Queue.DoQueue:DoQueueActions",
            "PostNamazu.Actions.Queue.BreakQueue:stop",
            "PostNamazu.Actions.Queue.BreakQueue:break",
            "PostNamazu.Actions.Queue.BreakQueue:BreakQueueActions",
            "PostNamazu.Actions.SendKey.DoSendKey:sendkey",
            "PostNamazu.Actions.WayMark.DoWaymarks:place",
            "PostNamazu.Actions.WayMark.DoWaymarks:DoWaymarks",
        ];
        AssertExactSurface("PostNamazu command aliases", commands, expectedCommands);
    }

    private static void ValidateTriggernometryPublicSurface(ModuleDefinition module)
    {
        var types = module.Types.SelectMany(EnumerateTypes).ToArray();
        string[] expectedActions =
        [
            "ActionActInteraction", "ActionBeep", "ActionDiscordWebhook",
            "ActionDiskOperation", "ActionExecuteScript", "ActionFolderOperation",
            "ActionJsonRequest", "ActionKeypress", "ActionLaunchProcess",
            "ActionLiveSplitControl", "ActionLogMessage", "ActionLoop",
            "ActionMessageBox", "ActionMouse", "ActionMutex", "ActionNamedCallback",
            "ActionObsControl", "ActionOverlayImage", "ActionOverlayText",
            "ActionPlaceholder", "ActionPlaySound", "ActionPlaySpeech",
            "ActionRepository", "ActionTriggerOperation", "ActionVariableDict",
            "ActionVariableList", "ActionVariableScalar", "ActionVariableTable",
            "ActionWindowMessage",
        ];
        var actionClasses = types
            .Where(type =>
                type.Namespace == "Triggernometry.Core.Actions" &&
                !type.IsNested &&
                type.BaseType?.FullName == "Triggernometry.Core.ActionBase")
            .Select(type => type.Name)
            .ToArray();
        AssertExactSurface("Triggernometry action classes", actionClasses, expectedActions);

        string[] expectedModules =
        [
            "AbilityRangeCheckModule", "CameraModule", "EntityModule",
            "EnvironmentEffectModule", "ExecuteCommandModule", "InstanceAfkTimerModule",
            "MovementModule", "PictoACTModule", "QuitInstanceModule",
            "ShowTextGimmickHintModule", "UseActionModule", "VfxModule",
        ];
        var moduleClasses = types
            .Where(type =>
                type.Namespace == "Triggernometry.PluginBridges.BridgeNamazu.Modules" &&
                !type.IsNested &&
                type.BaseType?.FullName ==
                "Triggernometry.PluginBridges.BridgeNamazu.Modules.ModuleBase")
            .Select(type => type.Name)
            .ToArray();
        AssertExactSurface("Triggernometry BridgeNamazu modules", moduleClasses, expectedModules);

        string[] expectedCallbacks =
        [
            "DisableAbilityRangeCheck", "SetCameraParams", "InvokeOnMultipleEntities",
            "SetDefaultPos", "SetPos", "SetModelRelPos", "Teleport",
            "SetDefaultHeading", "SetHeading", "Target", "SetModelStatus",
            "SetObjectScale", "ObjectScaling", "SetOpacity", "SetStatusLoopVfx",
            "Redraw", "SetHighlightColor", "RemoveStatus", "EObjAnimation",
            "PlayActionTimeline", "MapEffect", "ChangeWeather", "Exec", "StatusOff",
            "ExecTgt", "ExecPos", "TeleportDive", "DisableInstanceAfkTimer",
            "SetMoveSpeedMultiplier", "SetJumpHeightMultiplier", "PictoACT",
            "QuitInstance", "Hint", "Warn", "UseAction", "UseActionLocation",
            "LockOn", "Channeling", "CastVfx", "ActorVfx", "Omen", "StaticVfx",
        ];
        var callbacks = moduleClasses
            .Select(name => types.Single(type =>
                type.FullName ==
                $"Triggernometry.PluginBridges.BridgeNamazu.Modules.{name}"))
            .SelectMany(type => type.Methods)
            .SelectMany(method => method.CustomAttributes
                .Where(attribute =>
                    attribute.AttributeType.FullName ==
                    "Triggernometry.PluginBridges.BridgeNamazu.Modules.CallbackMethodAttribute")
                .Select(attribute => attribute.ConstructorArguments[0].Value?.ToString() ?? string.Empty))
            .ToArray();
        AssertExactSurface("Triggernometry BridgeNamazu callbacks", callbacks, expectedCallbacks);

        string[] expectedScriptingMethods = ["MouseToWorld", "IsMouseInSight"];
        var scriptingMethods = moduleClasses
            .Select(name => types.Single(type =>
                type.FullName ==
                $"Triggernometry.PluginBridges.BridgeNamazu.Modules.{name}"))
            .SelectMany(type => type.Methods)
            .SelectMany(method => method.CustomAttributes
                .Where(attribute =>
                    attribute.AttributeType.FullName ==
                    "Triggernometry.PluginBridges.BridgeNamazu.Modules.ScriptingMethodAttribute")
                .Select(_ => method.Name))
            .ToArray();
        AssertExactSurface(
            "Triggernometry BridgeNamazu scripting methods",
            scriptingMethods,
            expectedScriptingMethods);
    }

    private static void AssertExactSurface(
        string label,
        IEnumerable<string> actual,
        IEnumerable<string> expected)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet).Order(StringComparer.Ordinal).ToArray();
        var extra = actualSet.Except(expectedSet).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length == 0 && extra.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{label} changed; missing=[{string.Join(",", missing)}], " +
            $"extra=[{string.Join(",", extra)}].");
    }

    private static object DeserializeLegacyPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        var record = NrbfDecoder.DecodeClassRecord(stream, leaveOpen: true);
        if (record.TypeNameMatches(typeof(ImageListStreamer)))
        {
            return ReadByteArray(record, "Data");
        }

        if (record.TypeNameMatches(typeof(Bitmap)))
        {
            using var imageStream = new MemoryStream(ReadByteArray(record, "Data"), writable: false);
            using var bitmap = new Bitmap(imageStream);
            return new Bitmap(bitmap);
        }

        if (record.TypeNameMatches(typeof(Point)))
        {
            return new Point(record.GetInt32("x"), record.GetInt32("y"));
        }

        throw new NotSupportedException($"Legacy resource type {record.TypeName} is not allowed.");
    }

    private static byte[] ReadByteArray(ClassRecord record, string memberName)
    {
        var array = record.GetArrayRecord(memberName)
                    ?? throw new InvalidDataException($"{record.TypeName}/{memberName} has no array.");
        if (array.Lengths.Length != 1 || array.Lengths[0] > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Legacy resource byte array length is invalid.");
        }

        return (byte[])array.GetArray(typeof(byte[]), allowNulls: false);
    }

    private static void WriteConvertedResource(
        PreserializedResourceWriter writer,
        string key,
        object value)
    {
        var type = value.GetType();
        var typeName = type.AssemblyQualifiedName
                       ?? throw new InvalidOperationException($"{type.FullName} has no qualified name.");
        var converter = TypeDescriptor.GetConverter(type);
        if (value is byte[] imageListData &&
            key.EndsWith(".ImageStream", StringComparison.Ordinal))
        {
            writer.AddResource(key, imageListData);
            return;
        }

        if (converter.CanConvertTo(typeof(byte[])) && converter.CanConvertFrom(typeof(byte[])))
        {
            var bytes = converter.ConvertTo(
                            null,
                            CultureInfo.InvariantCulture,
                            value,
                            typeof(byte[])) as byte[]
                        ?? throw new InvalidOperationException($"Converter for {typeName} returned null.");
            writer.AddTypeConverterResource(key, bytes, typeName);
            return;
        }

        if (converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)))
        {
            writer.AddResource(
                key,
                converter.ConvertToInvariantString(value)
                ?? throw new InvalidOperationException($"Converter for {typeName} returned null."),
                typeName);
            return;
        }

        throw new NotSupportedException($"Resource {key}/{typeName} cannot be converted safely.");
    }

    public static object? GetResourceObject(ResourceManager manager, string key)
    {
        var value = manager.GetObject(key);
        if (value is not byte[] data || !key.EndsWith(".ImageStream", StringComparison.Ordinal))
        {
            return value;
        }

        var type = typeof(ImageListStreamer);
#pragma warning disable SYSLIB0050
        var info = new SerializationInfo(type, new FormatterConverter());
#pragma warning restore SYSLIB0050
        info.AddValue("Data", data, typeof(byte[]));
        var constructor = type.GetConstructor(
                              BindingFlags.Instance | BindingFlags.NonPublic,
                              null,
                              [typeof(SerializationInfo), typeof(StreamingContext)],
                              null)
                          ?? throw new MissingMethodException(type.FullName, ".ctor");
#pragma warning disable SYSLIB0050
        return constructor.Invoke([info, new StreamingContext(StreamingContextStates.All)]);
#pragma warning restore SYSLIB0050
    }

    private static void RedirectResourceManagerCalls(ModuleDefinition module)
    {
        var replacement = module.ImportReference(
            typeof(LegacyAssemblyRewriter).GetMethod(
                nameof(GetResourceObject),
                BindingFlags.Public | BindingFlags.Static)!);
        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.OpCode == OpCodes.Callvirt &&
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == typeof(ResourceManager).FullName &&
                called.Name == nameof(ResourceManager.GetObject) &&
                called.Parameters.Count == 1)
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = replacement;
            }
        }
    }

    private static void ReplaceWithBridge(
        MethodDefinition method,
        MethodReference bridge,
        bool loadInstance,
        bool loadParameters)
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        var processor = method.Body.GetILProcessor();
        if (loadInstance)
        {
            processor.Append(processor.Create(OpCodes.Ldarg_0));
        }
        if (loadParameters)
        {
            foreach (var parameter in method.Parameters)
            {
                processor.Append(processor.Create(OpCodes.Ldarg, parameter));
            }
        }

        processor.Append(processor.Create(OpCodes.Call, bridge));
        processor.Append(processor.Create(OpCodes.Ret));
    }

    private static void WrapWithNativeRuntimeFallback(
        MethodDefinition original,
        MethodReference nativeRuntimeAllowed,
        MethodReference nativePayloadNormalizer,
        MethodReference fallbackBridge)
    {
        if (original.IsAbstract || original.HasGenericParameters ||
            original.ReturnType.MetadataType != MetadataType.Void)
        {
            throw new InvalidOperationException(
                $"Unsupported PostNamazu native action shape: {original.FullName}.");
        }

        var type = original.DeclaringType;
        var module = original.Module;
        var originalName = original.Name;
        var originalAttributes = original.Attributes;
        var originalImplAttributes = original.ImplAttributes;
        var originalSemanticsAttributes = original.SemanticsAttributes;

        original.Name = $"{originalName}__DalamudActCompatNative";
        original.Attributes =
            (original.Attributes & ~Mono.Cecil.MethodAttributes.MemberAccessMask &
             ~Mono.Cecil.MethodAttributes.Abstract & ~Mono.Cecil.MethodAttributes.Virtual &
             ~Mono.Cecil.MethodAttributes.NewSlot) |
            Mono.Cecil.MethodAttributes.Private;
        original.SemanticsAttributes = Mono.Cecil.MethodSemanticsAttributes.None;

        var wrapper = new MethodDefinition(
            originalName,
            originalAttributes,
            module.ImportReference(original.ReturnType))
        {
            ImplAttributes = originalImplAttributes,
            SemanticsAttributes = originalSemanticsAttributes,
            CallingConvention = original.CallingConvention,
        };
        foreach (var parameter in original.Parameters)
        {
            var wrapperParameter = new ParameterDefinition(
                parameter.Name,
                parameter.Attributes,
                module.ImportReference(parameter.ParameterType));
            if (parameter.HasConstant)
            {
                wrapperParameter.Constant = parameter.Constant;
            }
            wrapper.Parameters.Add(wrapperParameter);
        }
        foreach (var attribute in original.CustomAttributes.ToArray())
        {
            original.CustomAttributes.Remove(attribute);
            wrapper.CustomAttributes.Add(attribute);
        }

        type.Methods.Add(wrapper);
        foreach (var caller in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(candidate => candidate.Methods)
                     .Where(candidate => candidate.HasBody && candidate != wrapper))
        {
            foreach (var instruction in caller.Body.Instructions)
            {
                if (instruction.Operand is MethodReference called &&
                    called.Module == module &&
                    called.MetadataToken == original.MetadataToken)
                {
                    instruction.Operand = wrapper;
                }
            }
        }

        wrapper.Body = new Mono.Cecil.Cil.MethodBody(wrapper);
        var processor = wrapper.Body.GetILProcessor();
        var fallback = processor.Create(OpCodes.Nop);
        processor.Append(processor.Create(OpCodes.Call, nativeRuntimeAllowed));
        processor.Append(processor.Create(OpCodes.Brfalse, fallback));
        if (!original.IsStatic)
        {
            processor.Append(processor.Create(OpCodes.Ldarg_0));
        }
        foreach (var parameter in wrapper.Parameters)
        {
            processor.Append(processor.Create(OpCodes.Ldarg, parameter));
            processor.Append(processor.Create(OpCodes.Call, nativePayloadNormalizer));
        }
        processor.Append(processor.Create(OpCodes.Call, original));
        processor.Append(processor.Create(OpCodes.Ret));
        processor.Append(fallback);
        foreach (var parameter in wrapper.Parameters)
        {
            processor.Append(processor.Create(OpCodes.Ldarg, parameter));
        }
        processor.Append(processor.Create(OpCodes.Call, fallbackBridge));
        processor.Append(processor.Create(OpCodes.Ret));

        var nativeGateCount = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.FullName == nativeRuntimeAllowed.FullName);
        var originalCallCount = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called && called == original);
        var normalizerCallCount = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.FullName == nativePayloadNormalizer.FullName);
        var fallbackCallCount = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.FullName == fallbackBridge.FullName);
        if (nativeGateCount != 1 || originalCallCount != 1 || normalizerCallCount != 1 ||
            fallbackCallCount != 1 ||
            original.CustomAttributes.Count != 0 || wrapper.CustomAttributes.Count == 0)
        {
            throw new InvalidOperationException(
                $"PostNamazu native action wrapper validation failed for {wrapper.FullName}: " +
                $"gate={nativeGateCount}, original={originalCallCount}, " +
                $"normalizer={normalizerCallCount}, " +
                $"fallback={fallbackCallCount}, attributes={wrapper.CustomAttributes.Count}.");
        }
    }

    private static void WrapPictoActWithActorRemoval(
        MethodDefinition original,
        MethodReference staticVfxBridge,
        MethodReference extractActorRemovals)
    {
        if (original.IsAbstract || original.HasGenericParameters || original.IsStatic ||
            original.ReturnType.MetadataType != MetadataType.Void ||
            original.Parameters.Count != 1 ||
            original.Parameters[0].ParameterType.MetadataType != MetadataType.String)
        {
            throw new InvalidOperationException(
                $"Unsupported Triggernometry PictoACT callback shape: {original.FullName}.");
        }

        var type = original.DeclaringType;
        var module = original.Module;
        var originalName = original.Name;
        var originalAttributes = original.Attributes;
        var originalImplAttributes = original.ImplAttributes;
        var originalSemanticsAttributes = original.SemanticsAttributes;
        original.Name = $"{originalName}__DalamudActCompatActorRemoval";
        original.Attributes =
            (original.Attributes & ~Mono.Cecil.MethodAttributes.MemberAccessMask &
             ~Mono.Cecil.MethodAttributes.Abstract & ~Mono.Cecil.MethodAttributes.Virtual &
             ~Mono.Cecil.MethodAttributes.NewSlot) |
            Mono.Cecil.MethodAttributes.Private;
        original.SemanticsAttributes = Mono.Cecil.MethodSemanticsAttributes.None;

        var wrapper = new MethodDefinition(
            originalName,
            originalAttributes,
            module.ImportReference(original.ReturnType))
        {
            ImplAttributes = originalImplAttributes,
            SemanticsAttributes = originalSemanticsAttributes,
            CallingConvention = original.CallingConvention,
        };
        foreach (var parameter in original.Parameters)
        {
            wrapper.Parameters.Add(new ParameterDefinition(
                parameter.Name,
                parameter.Attributes,
                module.ImportReference(parameter.ParameterType)));
        }
        foreach (var attribute in original.CustomAttributes.ToArray())
        {
            original.CustomAttributes.Remove(attribute);
            wrapper.CustomAttributes.Add(attribute);
        }

        type.Methods.Add(wrapper);
        foreach (var caller in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(candidate => candidate.Methods)
                     .Where(candidate => candidate.HasBody && candidate != wrapper))
        {
            foreach (var instruction in caller.Body.Instructions)
            {
                if (instruction.Operand is MethodReference called &&
                    called.Module == module &&
                    called.MetadataToken == original.MetadataToken)
                {
                    instruction.Operand = wrapper;
                }
            }
        }

        wrapper.Body = new Mono.Cecil.Cil.MethodBody(wrapper) { InitLocals = true };
        var actorCommands = new VariableDefinition(module.TypeSystem.String);
        wrapper.Body.Variables.Add(actorCommands);
        var processor = wrapper.Body.GetILProcessor();
        var returnInstruction = processor.Create(OpCodes.Ret);
        var isNullOrWhiteSpace = module.ImportReference(
            typeof(string).GetMethod(
                nameof(string.IsNullOrWhiteSpace),
                [typeof(string)])!);

        // Every command reaches the game-side static VFX broker. Only actor/all removals are
        // additionally sent through the original manager that owns ActorVfx handles.
        processor.Append(processor.Create(OpCodes.Ldarg_1));
        processor.Append(processor.Create(OpCodes.Call, staticVfxBridge));
        processor.Append(processor.Create(OpCodes.Ldarg_1));
        processor.Append(processor.Create(OpCodes.Call, extractActorRemovals));
        processor.Append(processor.Create(OpCodes.Stloc, actorCommands));
        processor.Append(processor.Create(OpCodes.Ldloc, actorCommands));
        processor.Append(processor.Create(OpCodes.Call, isNullOrWhiteSpace));
        processor.Append(processor.Create(OpCodes.Brtrue, returnInstruction));
        processor.Append(processor.Create(OpCodes.Ldarg_0));
        processor.Append(processor.Create(OpCodes.Ldloc, actorCommands));
        processor.Append(processor.Create(OpCodes.Call, original));
        processor.Append(returnInstruction);

        var originalCalls = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called && called == original);
        var bridgeCalls = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.FullName == staticVfxBridge.FullName);
        var extractorCalls = wrapper.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.FullName == extractActorRemovals.FullName);
        if (originalCalls != 1 || bridgeCalls != 1 || extractorCalls != 1 ||
            original.CustomAttributes.Count != 0 || wrapper.CustomAttributes.Count == 0)
        {
            throw new InvalidOperationException(
                "Triggernometry PictoACT actor-removal wrapper validation failed: " +
                $"original={originalCalls}, bridge={bridgeCalls}, extractor={extractorCalls}, " +
                $"attributes={wrapper.CustomAttributes.Count}.");
        }
    }

    private static void ReplaceWithReturn(MethodDefinition method)
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
    }

    private static void InsertBooleanGuard(
        MethodDefinition method,
        MethodReference isAllowed,
        bool returnCompletedTask)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot add a permission guard to {method.FullName}.");
        }

        var processor = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        processor.InsertBefore(first, processor.Create(OpCodes.Call, isAllowed));
        processor.InsertBefore(first, processor.Create(OpCodes.Brtrue, first));
        if (returnCompletedTask)
        {
            var completedTask = method.Module.ImportReference(
                typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!);
            processor.InsertBefore(first, processor.Create(OpCodes.Call, completedTask));
        }
        else if (method.ReturnType.MetadataType == MetadataType.Boolean)
        {
            processor.InsertBefore(first, processor.Create(OpCodes.Ldc_I4_0));
        }
        else if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            throw new InvalidOperationException(
                $"Permission guard has no denied return for {method.FullName}.");
        }
        processor.InsertBefore(first, processor.Create(OpCodes.Ret));
    }

    private static GenericInstanceMethod MakeGenericMethod(
        MethodReference method,
        IEnumerable<TypeReference> arguments)
    {
        var generic = new GenericInstanceMethod(method);
        foreach (var argument in arguments)
        {
            generic.GenericArguments.Add(argument);
        }

        return generic;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(EnumerateTypes))
        {
            yield return nested;
        }
    }

    private sealed record MatchaUpstreamSecrets(
        string TelemetryRoot,
        string UniversalisKey,
        string TelemetryFate,
        string TelemetryNpc);
}
