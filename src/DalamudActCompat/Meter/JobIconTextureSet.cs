using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.Meter;

public sealed class JobIconTextureSet
{
    private readonly ITextureProvider textureProvider;
    private readonly string rootDirectory;
    private readonly Dictionary<string, ISharedImmediateTexture?> textures =
        new(StringComparer.OrdinalIgnoreCase);

    public JobIconTextureSet(ITextureProvider textureProvider, string rootDirectory)
    {
        this.textureProvider = textureProvider;
        this.rootDirectory = rootDirectory;
    }

    public ISharedImmediateTexture? Get(JobDisplayStyle style, string job)
    {
        var folder = JobDisplayFormatter.IconFolder(style);
        if (folder is null)
        {
            return null;
        }

        var code = JobDisplayFormatter.NormalizeJobCode(job);
        if (!JobDisplayFormatter.IsSupportedJobCode(code))
        {
            return null;
        }

        var path = Path.Combine(rootDirectory, folder, $"{code}.png");
        if (textures.TryGetValue(path, out var texture))
        {
            return texture;
        }

        if (!File.Exists(path))
        {
            textures[path] = null;
            return null;
        }

        texture = textureProvider.GetFromFile(path);
        textures[path] = texture;
        return texture;
    }
}
