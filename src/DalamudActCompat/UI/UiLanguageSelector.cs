using Dalamud.Bindings.ImGui;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal static class UiLanguageSelector
{
    internal static bool Draw(PluginConfiguration configuration, UiText text)
    {
        var changed = false;
        if (!DactTheme.BeginCombo(text.Get("界面语言", "UI language"), UiLanguages.Name(text.Language))) return false;
        foreach (var language in UiLanguages.All)
        {
            // Autonyms make the selector usable even when the current language
            // was chosen accidentally; hidden IDs remain stable across switches.
            if (!ImGui.Selectable(language.Name + "###ui-language-" + language.Id, text.Language == language.Id)) continue;
            configuration.UiLanguage = language.Id;
            changed = true;
        }
        ImGui.EndCombo();
        return changed;
    }
}
