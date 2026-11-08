using Dalamud.Bindings.ImGui;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

internal static class UiLanguageSelector
{
    // 1.0 stacked field. Autonyms keep the list readable whatever language is active.
    internal static bool DrawField(PluginConfiguration configuration, UiText text)
    {
        var languages = UiLanguages.All.ToArray();
        DactUi.BeginField(text.Get("界面语言", "UI language"));
        var index = Array.FindIndex(languages, language => language.Id == text.Language);
        var changed = DactUi.Select("ui-language", languages.Select(language => language.Name).ToArray(), ref index) &&
                      index >= 0 && languages[index].Id != configuration.UiLanguage;
        if (changed) configuration.UiLanguage = languages[index].Id;
        DactUi.EndField();
        return changed;
    }

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
