using Dalamud.Interface.ManagedFontAtlas;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PassportCheckerReborn.UI;

/// <summary>
/// Supplies the fonts behind the Material type scale (<see cref="M3.TitleMedium"/> and friends) and the
/// matching icon font, at the user's text size.
/// <para>
/// Text uses the user's Dalamud default font rather than the Axis game font, because the global client's
/// Axis has no Hangul glyphs; the default font carries the extra glyphs for Dalamud's UI language.
/// </para>
/// <para>
/// Each text size gets its own isolated atlas. Building the Hangul glyph set takes seconds, and adding fonts
/// to an atlas that is in use would rebuild the fonts already on screen too. With one atlas per size, the
/// current size keeps drawing untouched while a newly picked one builds, and the switch happens only once
/// the new size is complete.
/// </para>
/// </summary>
internal static class FontManager
{
    /// <summary>How long a replaced atlas is kept before disposal, so no queued draw data still points at it.</summary>
    private static readonly TimeSpan RetireDelay = TimeSpan.FromSeconds(3);

    private static readonly Dictionary<int, FontSet> Sets = [];
    private static FontSet? active;
    private static FontSet? target;

    /// <summary>True while a newly picked text size is still building and the previous one is shown instead.</summary>
    public static bool IsPending => target is not null && target != active;

    /// <summary>
    /// Makes <paramref name="textScale"/> the wanted size, starting its build if needed, and switches to it
    /// once every font in it is ready. Call once per frame before drawing.
    /// </summary>
    public static void Update(float textScale, IReadOnlyList<float> ratios)
    {
        var key = KeyOf(textScale);
        if (!Sets.TryGetValue(key, out var wanted))
        {
            wanted = new FontSet(textScale, ratios);
            Sets[key] = wanted;
        }

        target = wanted;

        // Nothing is on screen yet (plugin load), so there is nothing to keep: use the wanted set directly
        // and fall back to the current font until it finishes.
        active ??= wanted;

        if (active != wanted && wanted.IsReady)
        {
            active.RetiredAt = DateTime.UtcNow;
            active = wanted;
        }

        RetireUnused();
    }

    /// <summary>The type-scale font at <paramref name="ratio"/> times the default font size, at the shown text size.</summary>
    public static ImFontPtr GetFont(float ratio)
    {
        return active?.GetText(ratio) ?? ImGui.GetFont();
    }

    /// <summary>The Font Awesome icon font sized to match body text at the shown text size.</summary>
    public static ImFontPtr GetIconFont()
    {
        return active?.GetIcon() ?? UiBuilder.IconFont;
    }

    public static void DisposeAll()
    {
        foreach (var set in Sets.Values)
        {
            set.Dispose();
        }

        Sets.Clear();
        active = null;
        target = null;
    }

    /// <summary>Rounds to a whole percent so near-identical scales share one entry.</summary>
    private static int KeyOf(float scale)
    {
        return Math.Max(1, (int)MathF.Round(scale * 100f));
    }

    /// <summary>
    /// Disposes sets that are neither shown nor wanted, once they have been out of use for a moment. That
    /// covers a size that was replaced, and one that was picked and then abandoned before it finished.
    /// </summary>
    private static void RetireUnused()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, set) in Sets.ToArray())
        {
            if (set == active || set == target)
            {
                set.RetiredAt = null;
                continue;
            }

            set.RetiredAt ??= now;
            if (now - set.RetiredAt.Value >= RetireDelay)
            {
                set.Dispose();
                Sets.Remove(key);
            }
        }
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

    /// <summary>Every font of the type scale plus the icon font at one text size, on an atlas of their own.</summary>
    private sealed class FontSet : IDisposable
    {
        private readonly IFontAtlas atlas;
        private readonly Dictionary<int, IFontHandle> text = [];
        private readonly IFontHandle icon;

        public FontSet(float textScale, IReadOnlyList<float> ratios)
        {
            var ui = PassportCheckerReborn.PluginInterface.UiBuilder;
            atlas = ui.CreateFontAtlas(FontAtlasAutoRebuildMode.Async, true, $"PassportCheckerReborn text {textScale:F2}x");

            // One build for the whole set instead of one per handle.
            using (atlas.SuppressAutoRebuild())
            {
                foreach (var ratio in ratios)
                {
                    var key = KeyOf(ratio);
                    if (!text.ContainsKey(key))
                    {
                        // A negative size is read as a multiple of the default font's size.
                        var relativeSize = -(ratio * textScale);
                        text[key] = atlas.NewDelegateFontHandle(
                            e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(relativeSize)));
                    }
                }

                icon = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                {
                    var config = new SafeFontConfig { SizePx = ui.DefaultFontSpec.SizePx * textScale };
                    tk.AddFontAwesomeIconFont(config);
                }));
            }
        }

        public DateTime? RetiredAt { get; set; }

        public bool IsReady => icon.Available && text.Values.All(handle => handle.Available);

        public ImFontPtr? GetText(float ratio)
        {
            return text.TryGetValue(KeyOf(ratio), out var handle) ? TryGetLoaded(handle) : null;
        }

        public ImFontPtr? GetIcon()
        {
            return TryGetLoaded(icon);
        }

        public void Dispose()
        {
            foreach (var handle in text.Values)
            {
                handle.Dispose();
            }

            icon.Dispose();
            atlas.Dispose();
        }
    }
}
