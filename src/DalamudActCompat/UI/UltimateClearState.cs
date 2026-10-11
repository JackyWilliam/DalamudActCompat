using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DalamudActCompat.UI;

// Observe actual game progress on the framework thread; account persistence lives in
// CloudClientService. A character change invalidates the cache before any reward is recorded.
internal sealed class UltimateClearState
{
    private static readonly uint[] InstanceContentIds = [30057, 30067, 30086, 30106, 30124, 30143];
    private readonly Func<bool> loggedIn;
    private readonly Func<ulong> contentId;
    private readonly Func<uint, bool> completed;
    private readonly Func<long> clock;
    private long checkedAt = long.MinValue;
    private ulong checkedCharacter;
    private uint cleared;

    public UltimateClearState(IClientState clientState, IPlayerState playerState)
        : this(() => clientState.IsLoggedIn, () => playerState.ContentId, UIState.IsInstanceContentCompleted) { }

    internal UltimateClearState(Func<bool> loggedIn, Func<ulong> contentId, Func<uint, bool> completed, Func<long>? clock = null)
    {
        this.loggedIn = loggedIn; this.contentId = contentId; this.completed = completed;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    public uint CompletedDuty()
    {
        var character = loggedIn() ? contentId() : 0;
        if (character == 0)
        {
            checkedAt = long.MinValue; checkedCharacter = 0; cleared = 0;
            return 0;
        }
        var now = clock();
        if (character == checkedCharacter && checkedAt != long.MinValue && now - checkedAt < 1000) return cleared;
        checkedAt = now;
        checkedCharacter = character;
        cleared = InstanceContentIds.FirstOrDefault(id => completed(id));
        return cleared;
    }
}
