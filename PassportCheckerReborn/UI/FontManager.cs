using Dalamud.Interface.ManagedFontAtlas;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.UI;

/// <summary>
/// Supplies the fonts behind the Material type scale (<see cref="M3.TitleMedium"/> and friends).
/// Each size gets its own handle on the plugin's atlas. The handle is the user's Dalamud default font at
/// that size rather than the Axis game font, because the global client's Axis has no Hangul glyphs; the
/// default font carries the extra glyphs for Dalamud's UI language.
/// </summary>
internal static class FontManager
{
    private static readonly Dictionary<int, IFontHandle> Handles = [];

    /// <summary>
    /// The default font at <paramref name="scale"/> times the user's default font size. Until its handle has
    /// been built, the current font is returned instead.
    /// </summary>
    public static ImFontPtr GetFont(float scale)
    {
        return TryGetLoaded(GetHandle(KeyOf(scale))) ?? ImGui.GetFont();
    }

    /// <summary>
    /// Creates the handles for <paramref name="scales"/> up front and together. Building the Hangul glyph set
    /// takes seconds, so doing it at load (in one atlas build rather than one per size as each is first drawn)
    /// keeps windows from opening in the fallback font and visibly resizing once the real fonts arrive.
    /// </summary>
    public static void Preload(IEnumerable<float> scales)
    {
        foreach (var scale in scales)
        {
            GetHandle(KeyOf(scale));
        }
    }

    public static void DisposeAll()
    {
        foreach (var handle in Handles.Values)
        {
            handle.Dispose();
        }

        Handles.Clear();
    }

    /// <summary>Rounds to a whole percent so near-identical scales share one handle.</summary>
    private static int KeyOf(float scale)
    {
        return Math.Max(1, (int)MathF.Round(scale * 100f));
    }

    private static IFontHandle GetHandle(int key)
    {
        if (!Handles.TryGetValue(key, out var handle))
        {
            // A negative size is read as a multiple of the default font's size.
            var relativeSize = -key / 100f;
            handle = PassportCheckerReborn.PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(
                e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(relativeSize)));
            Handles[key] = handle;
        }

        return handle;
    }

    private static ImFontPtr? TryGetLoaded(IFontHandle handle)
    {
        // The atlas builds asynchronously.
        if (!handle.Available)
        {
            return null;
        }

        try
        {
            using var locked = handle.Lock();
            var font = locked.ImFont;
            return font.IsLoaded() ? font : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
