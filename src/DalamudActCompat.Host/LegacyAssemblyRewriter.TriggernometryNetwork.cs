using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    private static void UseFallbackDnsForTriggernometry(ModuleDefinition module)
    {
        // TN (CN) fetches its update manifest, translations, PostNamazu and every trigger
        // repository through `new HttpClient()` from 123pan's CDN, which some resolvers outside
        // China cannot look up (user report: DNS errors abroad). Those clients are created by
        // the Host instead, with a connection that retries the lookup through Cloudflare /
        // Google DNS. Clients TN builds around its own handler, WebClient and the HTTP trigger
        // action keep the system resolver.
        var create = module.ImportReference(
            typeof(HostPluginBridge).GetMethod(nameof(HostPluginBridge.CreateTriggernometryHttpClient))
            ?? throw new MissingMethodException(typeof(HostPluginBridge).FullName, nameof(HostPluginBridge.CreateTriggernometryHttpClient)));
        var replaced = 0;
        foreach (var method in module.Types.SelectMany(EnumerateTypes).SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Newobj || instruction.Operand is not MethodReference
                    {
                        Name: ".ctor",
                        HasParameters: false,
                        DeclaringType.FullName: "System.Net.Http.HttpClient",
                    })
                {
                    continue;
                }

                // Same stack effect: no arguments in, one HttpClient out.
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = create;
                replaced++;
            }
        }

        Console.WriteLine($"Triggernometry HTTP clients retry failed DNS lookups through Cloudflare / Google ({replaced}).");
    }
}
