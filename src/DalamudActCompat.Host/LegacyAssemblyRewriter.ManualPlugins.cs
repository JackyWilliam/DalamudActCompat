using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    public static Assembly LoadManualPlugin(string id, string path, AssemblyLoadContext context)
    {
        var expectedHash = id switch
        {
            "actlogsuploader" => "A2364FCCE5C2CAA9F48134E9D7E5D402A660DD1B393D120B464B77D50E4AA1E7",
            "act.jsb_5" => "0DCFD7F79FA62CB6DAB985FCA44DF93A414D607FFF255CBB2B4D991F37486FE6",
            _ => throw new ArgumentException("No manual compatibility profile exists.", nameof(id)),
        };
        // These adapters are for the supplied upstream builds only. Never edit the
        // installed DLL or apply an x86/IL transformation to an unknown native image.
        var bytes = File.ReadAllBytes(path);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != expectedHash)
            throw new InvalidDataException("This plugin version has not been validated for DACT compatibility.");
        using var input = new MemoryStream(bytes);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        var module = definition.MainModule;
        if (id == "actlogsuploader")
        {
            // Bind the modern managed assemblies before Bootstrap can load its
            // Framework copies (which reference the removed IExpando interface).
            _ = typeof(Microsoft.ClearScript.ScriptEngine).Assembly;
            _ = typeof(Microsoft.ClearScript.V8.V8ScriptEngine).Assembly;
            RewriteUploader(module, Path.GetDirectoryName(path)!);
        }
        else RewriteTitan(module);
        using var output = new MemoryStream();
        definition.Write(output);
        output.Position = 0;
        return context.LoadFromStream(output);
    }

    private static void RewriteUploader(ModuleDefinition module, string directory)
    {
        var settings = module.GetType("ACTLogsUploader.Config.PluginSettings");
        ReplaceWithBridge(settings.Properties.Single(p => p.Name == "DefaultConfigPath").GetMethod,
            nameof(HostPluginBridge.GetUploaderConfigPath));
        ReplaceWithBridge(module.GetType("ACTLogsUploader.Upload.ParserEngine").Properties.Single(p => p.Name == "CacheDir").GetMethod,
            nameof(HostPluginBridge.GetUploaderParserCachePath));
        var bootstrap = module.GetType("ACTLogsUploader.Bootstrap").Methods.Single(m => m.Name == "GetPluginDirectory");
        bootstrap.Body = new Mono.Cecil.Cil.MethodBody(bootstrap);
        bootstrap.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, directory));
        bootstrap.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

        var plugin = module.GetType("ACTLogsUploader.Plugin");
        var resolve = plugin.Methods.Single(m => m.Name == "ResolveLogDirectory");
        resolve.Body = new Mono.Cecil.Cil.MethodBody(resolve);
        var il = resolve.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, plugin.Fields.Single(f => f.Name == "_settings"));
        il.Emit(OpCodes.Callvirt, settings.Properties.Single(p => p.Name == "LogDirectory").GetMethod);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(HostPluginBridge).GetMethod(nameof(HostPluginBridge.ResolveUploaderLogDirectory))!));
        il.Emit(OpCodes.Ret);
        var start = module.Types.SelectMany(EnumerateTypes).SelectMany(t => t.Methods)
            .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.Diagnostics.Process" && m.Name == "Start");
        start.Operand = module.ImportReference(typeof(HostPluginBridge).GetMethod(nameof(HostPluginBridge.OpenUploaderUrl))!);
    }

    private static void RewriteTitan(ModuleDefinition module)
    {
        if (module.Architecture != TargetArchitecture.I386 || (module.Attributes & ModuleAttributes.ILOnly) == 0)
            throw new InvalidDataException("The Titan compatibility profile requires the verified managed-only build.");
        module.Attributes &= ~(ModuleAttributes.Required32Bit | ModuleAttributes.Preferred32Bit);
        var natives = module.GetType("FanHuaACTTools.JJJSBTT.Natives");
        ReplaceWithBridge(natives.Methods.Single(m => m.Name == "Find"), nameof(HostPluginBridge.FindTitanGameWindow));
        ReplaceWithBridge(natives.Methods.Single(m => m.Name == "SendKey"), nameof(HostPluginBridge.SendTitanKey));
        ReplaceWithBridge(module.GetType("JJJSBTT").Methods.Single(m => m.Name == "TTS"), nameof(HostPluginBridge.SendTitanTts));
    }

    private static void ReplaceWithBridge(MethodDefinition method, string bridgeName)
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        var il = method.Body.GetILProcessor();
        foreach (var parameter in method.Parameters) il.Emit(OpCodes.Ldarg, parameter);
        il.Emit(OpCodes.Call, method.Module.ImportReference(typeof(HostPluginBridge).GetMethod(bridgeName)!));
        il.Emit(OpCodes.Ret);
    }
}
