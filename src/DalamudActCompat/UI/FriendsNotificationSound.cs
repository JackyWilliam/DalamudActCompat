using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DalamudActCompat.UI;

internal sealed class FriendsNotificationSound(string assemblyDirectory, Action<string> report) : IDisposable
{
    private readonly object gate = new();
    private WaveOutEvent? player;
    private Mp3FileReader? reader;
    private VolumeSampleProvider? gain;
    private int selection = -1, queued;
    private long generation;
    private bool disposed;

    // Linear, so 100% is the file as shipped and the slider never makes it louder than before.
    internal static float Gain(int volumePercent) => Math.Clamp(volumePercent, 0, 100) / 100f;

    internal void Play(int selected, int volumePercent = 100, bool replace = false)
    {
        // Decode/open the device off the render thread. One pending request and
        // one player prevent burst messages or repeated preview clicks from stacking.
        if (Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
        var expected = Interlocked.Read(ref generation);
        _ = Task.Run(() =>
        {
            try
            {
                lock (gate)
                {
                    if (disposed || expected != generation) return;
                    if (player?.PlaybackState == PlaybackState.Playing && !replace) return;
                    var next = Math.Clamp(selected, 0, 3);
                    if (next != selection || player is null)
                    {
                        ReleasePlayer();
                        reader = new Mp3FileReader(Path.Combine(assemblyDirectory, "Assets", "Sounds", $"friend-message-{next + 1}.mp3"));
                        // The volume is applied to the samples. WaveOutEvent.Volume goes through
                        // waveOutSetVolume, which moves this process's level in the Windows mixer,
                        // and this process is the game.
                        gain = new VolumeSampleProvider(reader.ToSampleProvider());
                        player = new WaveOutEvent(); player.Init(gain); selection = next;
                    }
                    else player.Stop();
                    gain!.Volume = Gain(volumePercent);
                    reader!.Position = 0; player.Play();
                }
            }
            catch (Exception error)
            {
                lock (gate) ReleasePlayer();
                report("好友提示音无法播放：" + error.Message);
            }
            finally { Volatile.Write(ref queued, 0); }
        });
    }

    internal void Stop()
    {
        // Cancel queued work as well as current playback on logout or mute.
        Interlocked.Increment(ref generation);
        try { lock (gate) player?.Stop(); }
        catch (Exception error) { report("好友提示音停止失败：" + error.Message); }
    }
    private void ReleasePlayer() { player?.Dispose(); player = null; reader?.Dispose(); reader = null; gain = null; selection = -1; }
    public void Dispose()
    {
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            disposed = true;
            try { ReleasePlayer(); }
            catch (Exception error) { report("好友提示音释放失败：" + error.Message); }
        }
    }
}
