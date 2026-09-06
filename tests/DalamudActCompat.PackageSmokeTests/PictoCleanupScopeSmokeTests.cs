using System.Xml;
using System.Xml.Linq;
using DalamudActCompat.Overlay;

internal static class PictoCleanupScopeSmokeTests
{
    public static void Run()
    {
        using var reader = XmlReader.Create(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PictoCleanupScopes.xml"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        foreach (var trigger in document.Descendants("Trigger"))
        {
            var isUntagged = ((string)trigger.Attribute("Name")!).StartsWith("22 ", StringComparison.Ordinal);
            using var overlay = new PictoActOverlayService(null!);
            foreach (var tag in new[] { "ACTCOMPAT_EXA", "ACTCOMPAT_POLYGON_OTHER", "UNRELATED" })
                overlay.Apply($"Omen: Circle\nTag: {tag}\nPos: 0, 0, 0\nScale: 3\nt: 60");
            var startedAt = DateTimeOffset.UtcNow;
            foreach (var action in trigger.Descendants("Action").Where(a => (string?)a.Attribute("NamedCallbackName") == "PictoACT"))
            {
                // Literal substitutions only: never evaluate user expressions, execute scripts, speak, or draw to the game.
                var payload = ((string)action.Attribute("NamedCallbackParam")!)
                    .Replace("${_me.Pos}", "0, 0, 0", StringComparison.Ordinal)
                    .Replace("${_me.h}", "0", StringComparison.Ordinal);
                var commands = PictoActOverlayService.Parse(payload);
                Check(!commands.Any(c => c.Remove && string.IsNullOrWhiteSpace(c.Tag) && c.Regex is null),
                    "A test still issues unscoped global Remove.");
                overlay.Apply(payload);
            }
            overlay.ProcessPending(DateTimeOffset.UtcNow.AddSeconds(30));
            if (isUntagged)
            {
                Check(overlay.ShapeCount == 7, "Untagged compatibility test removed unrelated shapes or lost a delayed shape.");
                Check(overlay.ShapeSnapshot.Count(shape => shape.ExpiresAt <= startedAt.AddSeconds(7)) == 4,
                    "Untagged test shapes do not all have bounded self-expiry.");
            }
            else
            {
                Check(overlay.ShapeCount == 3, "Scoped cleanup removed unrelated shapes or left its delayed shapes queued.");
            }
        }
        Console.WriteLine("PASS PictoACT XML: untagged shapes self-expire; test 24 removes only its own active/delayed shapes");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
