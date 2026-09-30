using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

/// <summary>
/// Material-styled pieces shared by the member info and party list overlays. Every cell lines its
/// text up with the frame height, so rows read evenly next to job icons and badges.
/// </summary>
internal static class OverlayWidgets
{
    public const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.BordersInnerH |
        ImGuiTableFlags.SizingFixedFit |
        ImGuiTableFlags.NoHostExtendX;

    public static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.85f);

    /// <summary>The overlay title: an icon in a tonal circle, the title, and an optional muted second line.</summary>
    public static void Header(FontAwesomeIcon icon, string title, string? subtitle)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var start = ImGui.GetCursorScreenPos();
        var iconBox = 28f * scale;
        var lineGap = 1f * scale;

        Vector2 titleSize;
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            titleSize = ImGui.CalcTextSize(title);
        }

        var subtitleSize = string.IsNullOrEmpty(subtitle) ? Vector2.Zero : ImGui.CalcTextSize(subtitle);
        var textHeight = titleSize.Y + (subtitleSize.Y > 0f ? lineGap + subtitleSize.Y : 0f);
        var height = MathF.Max(iconBox, textHeight);
        var textX = start.X + iconBox + M3.Space2;

        ImGui.Dummy(new Vector2(textX - start.X + MathF.Max(titleSize.X, subtitleSize.X), height));

        var drawList = ImGui.GetWindowDrawList();
        var center = new Vector2(start.X + (iconBox * 0.5f), start.Y + (height * 0.5f));
        var half = new Vector2(iconBox * 0.5f);
        drawList.AddCircleFilled(center, iconBox * 0.5f, M3.U32(s.PrimaryContainer), 24);
        M3Draw.IconCentered(drawList, icon, center - half, center + half, s.OnPrimaryContainer);

        var textY = start.Y + ((height - textHeight) * 0.5f);
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            drawList.AddText(new Vector2(textX, textY), M3.U32(s.OnSurface), title);
        }

        if (subtitleSize.Y > 0f)
        {
            drawList.AddText(new Vector2(textX, textY + titleSize.Y + lineGap), M3.U32(Muted), subtitle);
        }
    }

    /// <summary>A muted icon and message, for when there is nothing to list.</summary>
    public static void EmptyState(FontAwesomeIcon icon, string message, string? detail = null)
    {
        var start = ImGui.GetCursorScreenPos();
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), Muted);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        using var group = ImRaii.Group();
        ImGui.TextUnformatted(message);

        if (!string.IsNullOrEmpty(detail))
        {
            using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
            ImGui.TextUnformatted(detail);
        }
    }

    /// <summary>A muted status line with a leading icon, such as a loading notice under the table.</summary>
    public static void StatusLine(FontAwesomeIcon icon, string message)
    {
        var start = ImGui.GetCursorScreenPos();
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), Muted);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        ImGui.TextColored(Muted, message);
    }

    // ── Table ────────────────────────────────────────────────────────────────

    public static void BeginHeaderRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
    }

    /// <summary>A column label in the Material data-table style: small, uppercase, muted.</summary>
    public static void HeaderCell(string label)
    {
        ImGui.TableNextColumn();
        using var font = ImRaii.PushFont(M3.LabelSmall);
        using var color = ImRaii.PushColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(label.ToUpperInvariant());
    }

    /// <summary>Draws the job's icon at frame height, falling back to its abbreviation.</summary>
    public static void JobIcon(string jobAbbreviation)
    {
        var texture = GetJobIconTexture(jobAbbreviation);
        if (texture is not null)
        {
            ImGui.Image(texture.Handle, new Vector2(ImGui.GetFrameHeight()));
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(M3.Scheme.Tertiary, string.IsNullOrWhiteSpace(jobAbbreviation) ? "?" : jobAbbreviation);
    }

    /// <summary>
    /// Text that opens <paramref name="url"/> when clicked, underlined on hover like a link.
    /// <paramref name="drawTooltip"/>, when given, replaces the plain <paramref name="tooltip"/> text.
    /// </summary>
    public static void LinkText(string text, string url, Vector4? color = null, string? tooltip = null, Action? drawTooltip = null)
    {
        var tone = color ?? M3.Scheme.OnSurface;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(tone, text);

        if (ImGui.IsItemHovered())
        {
            var scale = M3.Scale;
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(
                new Vector2(min.X, max.Y - (1f * scale)),
                new Vector2(max.X, max.Y - (1f * scale)),
                M3.U32(tone, 0.8f), 1f * scale);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (drawTooltip is not null)
            {
                drawTooltip();
            }
            else
            {
                ImguiTooltips.ShowTooltip(tooltip);
            }
        }

        if (ImGui.IsItemClicked())
        {
            Util.OpenLink(url);
        }
    }

    /// <summary>Placeholder for a cell whose lookup is still running.</summary>
    public static void Pending()
    {
        ImGui.AlignTextToFramePadding();
        MutedText("…");
    }

    // ── Tomestone ────────────────────────────────────────────────────────────

    public static void TomestoneCell(TomestoneCharacterInfo? info)
    {
        var s = M3.Scheme;
        ImGui.AlignTextToFramePadding();

        if (info == null)
        {
            MutedText(Loc.T("Hidden Profile"));
            return;
        }

        if (info.NoLogs)
        {
            MutedText(Loc.T("No Logs"));
            return;
        }

        var hasClears = info.TotalClears.HasValue && info.TotalClears.Value > 0;
        var hasProgPoint = !string.IsNullOrWhiteSpace(info.ProgPoint);
        var hasBestPercent = info.BestPercent.HasValue;

        if (hasClears)
        {
            var clearsText = "Cleared";
            if (!string.IsNullOrWhiteSpace(info.CompletionWeek))
            {
                clearsText += $" ({info.CompletionWeek})";
            }

            ImGui.TextColored(s.Success, clearsText);

            if (hasBestPercent)
            {
                ImGui.SameLine();
                ImGui.TextColored(s.Success, $"Best: {info.BestPercent:F1}%");
            }
        }
        else if (hasProgPoint)
        {
            var progText = info.ProgPoint!;
            if (!string.IsNullOrWhiteSpace(info.DisplayPercent))
            {
                progText += $" ({info.DisplayPercent})";
            }

            ImGui.TextColored(s.Warning, progText);
        }
        else if (hasBestPercent)
        {
            ImGui.TextColored(s.Warning, $"Best: {info.BestPercent:F1}%");
        }
        else
        {
            MutedText(Loc.T("Hidden Profile"));
        }
    }

    // ── FFLogs ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the FFLogs cell for a member — the single source of truth shared by both overlays. Handles
    /// lookup failure, the unmapped zone-average fallback, multi-phase (P1/P2), single clears, progression,
    /// and no-logs. The caller is responsible for the "still loading" and private-slot cases.
    /// </summary>
    public static void FFLogsCell(EncounterParseResult? result, PartyMemberInfo member)
    {
        ImGui.AlignTextToFramePadding();

        if (result is null || !result.HasData)
        {
            if (result?.FetchFailed == true)
            {
                LookupFailed();
            }
            else
            {
                MutedText(Loc.T("No logs"));
            }

            return;
        }

        if (!result.IsEncounterSpecific)
        {
            // Unmapped duty: the only signal we have is the character's current-tier average.
            if (result.BestParse.HasValue)
            {
                ParseTemplate(Loc.T("Average overall parse {0}%"), result.BestParse.Value);
            }
            else
            {
                MutedText(Loc.T("N/A"));
            }

            return;
        }

        var hasMultiPhaseData = result.Phase1TotalKills.HasValue ||
                                result.Phase2TotalKills.HasValue ||
                                result.Phase1BestParse.HasValue ||
                                result.Phase2BestParse.HasValue ||
                                result.Phase2LowestBossHpPct.HasValue;

        if (hasMultiPhaseData)
        {
            // Both phases on ONE line, compact: "P1 30킬 95.0%   P2 14킬 90.0%". Each phase keeps its own kill
            // count and parse/progression (P1 and P2 usually differ), but the per-job icon + best-on-a-different-
            // job are dropped here — showing them for both phases would make the line too wide.
            PhaseCompact("P1", result.Phase1Result);
            ImGui.SameLine(0f, M3.Space3);
            PhaseCompact("P2", result.Phase2Result);
        }
        else if (result.TotalKills > 0)
        {
            ClearedCell(result, member);
        }
        else if (result.LowestBossHpPct.HasValue)
        {
            Progression(result);
        }
        else
        {
            MutedText(Loc.T("No logs"));
        }
    }

    /// <summary>
    /// FFLogs' own percentile colours. These stay fixed rather than following the theme, so parses
    /// read the way players already know them.
    /// </summary>
    public static Vector4 ParseColor(double percentile) => percentile switch
    {
        >= 99 => new Vector4(0.898f, 0.800f, 0.502f, 1.0f),  // Gold (99+)
        >= 95 => new Vector4(0.894f, 0.510f, 0.200f, 1.0f),  // Orange (95-98)
        >= 75 => new Vector4(0.635f, 0.282f, 0.808f, 1.0f),  // Purple (75-94)
        >= 50 => new Vector4(0.118f, 0.392f, 1.000f, 1.0f),  // Blue (50-74)
        >= 25 => new Vector4(0.118f, 0.784f, 0.118f, 1.0f),  // Green (25-49)
        _ => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),  // Grey (<25)
    };

    /// <summary>One phase of a two-phase fight, compact: "P1 &lt;kills&gt;킬 &lt;parse&gt;%", "P1 &lt;pct&gt;% 전멸", or "P1 기록 없음".</summary>
    private static void PhaseCompact(string label, EncounterParseResult? phase)
    {
        MutedText(label);
        ImGui.SameLine();

        if (phase is null || !phase.HasData)
        {
            MutedText(Loc.T("No logs"));
            return;
        }

        if (phase.TotalKills > 0)
        {
            ImGui.TextColored(M3.Scheme.Success, string.Format(Loc.T("Cleared {0}X"), phase.TotalKills));
            if (phase.BestParse.HasValue)
            {
                ImGui.SameLine();
                ImGui.TextColored(ParseColor(phase.BestParse.Value), $"{phase.BestParse.Value:F1}%");
            }
        }
        else if (phase.LowestBossHpPct.HasValue)
        {
            ImGui.TextColored(M3.Scheme.Warning, string.Format(Loc.T("{0}% wipe"), phase.LowestBossHpPct.Value.ToString("F1")));
        }
        else
        {
            MutedText(Loc.T("No logs"));
        }
    }

    /// <summary>Single-encounter clear: "Cleared NX" + current-job parse (icon), "-%", or nothing for a non-combat job.</summary>
    private static void ClearedCell(EncounterParseResult result, PartyMemberInfo member)
    {
        var s = M3.Scheme;
        var clearedText = string.Format(Loc.T("Cleared {0}X"), result.TotalKills);
        var spec = FFLogsService.GetSpecForJob(member.JobAbbreviation);

        if (result.CurrentJobBestParse.HasValue)
        {
            // "46킬 · [current job icon] 1%" — the icon makes clear the % is for the job they're bringing.
            ImGui.TextColored(s.Success, clearedText + " ·");
            ImGui.SameLine();
            JobIconInline(member.JobAbbreviation, FFLogsService.GetJobIconIdForSpec(spec));
            ImGui.SameLine();
            ImGui.TextColored(ParseColor(result.CurrentJobBestParse.Value), $"{result.CurrentJobBestParse.Value:F1}%");
        }
        else if (spec is not null)
        {
            // Combat job that cleared but has no ranked parse for this fight: clear + [icon] -%.
            ImGui.TextColored(s.Success, clearedText + " ·");
            ImGui.SameLine();
            JobIconInline(member.JobAbbreviation, FFLogsService.GetJobIconIdForSpec(spec));
            ImGui.SameLine();
            MutedText("-%");
        }
        else
        {
            // Non-combat job (crafter/gatherer): no combat parse to show — just the clear, plus the best
            // parse on a real job (via BestParseOnDifferentJob below).
            ImGui.TextColored(s.Success, clearedText);
        }

        BestParseOnDifferentJob(result, member);
    }

    /// <summary>
    /// Draws a no-kill progression pull, always tagged as a wipe so it can't be mistaken for a parse
    /// percentile. Phased fights read "P3 8.8% 전멸"; phase-less fights read "8.8% 전멸".
    /// </summary>
    private static void Progression(EncounterParseResult result)
    {
        if (!result.LowestBossHpPct.HasValue)
        {
            return;
        }

        var wipe = string.Format(Loc.T("{0}% wipe"), result.LowestBossHpPct.Value.ToString("F1"));
        var text = result.ProgLastPhase is int ph && ph >= 2 ? $"P{ph} {wipe}" : wipe;
        ImGui.TextColored(M3.Scheme.Warning, text);
    }

    /// <summary>A "Lookup failed" marker (distinct from muted "No logs") with a retry hint tooltip.</summary>
    private static void LookupFailed()
    {
        ImGui.TextColored(M3.Scheme.Error, Loc.T("Lookup failed"));
        if (ImGui.IsItemHovered())
        {
            ImguiTooltips.ShowTooltip(Loc.T("FFLogs lookup failed (network or rate limit) — refresh to retry."));
        }
    }

    /// <summary>
    /// Renders a localized "…{0}%" parse template with only the percentage (and its trailing "%") tinted by
    /// parse grade; the surrounding descriptive label stays muted. Falls back to a fully-coloured string if
    /// the template has no <c>{0}</c> placeholder.
    /// </summary>
    private static void ParseTemplate(string template, double pct)
    {
        var placeholder = template.IndexOf("{0}", StringComparison.Ordinal);
        var pctText = pct.ToString("F1");

        if (placeholder < 0)
        {
            ImGui.TextColored(ParseColor(pct), string.Format(template, pctText));
            return;
        }

        MutedText(template[..placeholder]);
        ImGui.SameLine(0f, 0f);
        ImGui.TextColored(ParseColor(pct), pctText + template[(placeholder + 3)..]);
    }

    /// <summary>
    /// If the overall best parse is on a different job from the member's current job, draws it after
    /// the current result with that job's icon. Draws nothing when the current job is the best job.
    /// </summary>
    private static void BestParseOnDifferentJob(EncounterParseResult result, PartyMemberInfo member)
    {
        if (!result.BestParse.HasValue || result.BestParseJobAbbreviation == null)
        {
            return;
        }

        if (string.Equals(result.BestParseJobAbbreviation, member.JobAbbreviation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (result.CurrentJobBestParse.HasValue && result.BestParse.Value <= result.CurrentJobBestParse.Value)
        {
            return;
        }

        ImGui.SameLine();
        MutedText("·");
        ImGui.SameLine();
        JobIconInline(result.BestParseJobAbbreviation, result.BestParseJobIconId);
        ImGui.SameLine();
        ImGui.TextColored(ParseColor(result.BestParse.Value), $"{result.BestParse.Value:F1}%");
    }

    /// <summary>A text-sized job icon on the current line, or a "[ABBR]" text fallback. Caller handles SameLine.</summary>
    private static void JobIconInline(string jobAbbreviation, uint? iconId)
    {
        var texture = iconId.HasValue ? GetIconTexture(iconId.Value) : null;
        if (texture is not null)
        {
            InlineIcon(texture);
        }
        else
        {
            ImGui.TextColored(M3.Scheme.Tertiary, $"[{jobAbbreviation}]");
        }
    }

    /// <summary>
    /// A text-sized icon on a frame-aligned line. It reserves the frame height and centres the image,
    /// because an image placed directly would sit at the top of the line, above the text.
    /// </summary>
    private static void InlineIcon(IDalamudTextureWrap texture)
    {
        var frameHeight = ImGui.GetFrameHeight();
        var lineHeight = ImGui.GetTextLineHeight();
        ImGui.Dummy(new Vector2(lineHeight, frameHeight));

        var min = ImGui.GetItemRectMin() + new Vector2(0f, (frameHeight - lineHeight) * 0.5f);
        ImGui.GetWindowDrawList().AddImage(texture.Handle, min, min + new Vector2(lineHeight));
    }

    private static void MutedText(string text)
    {
        ImGui.TextColored(Muted, text);
    }

    private static IDalamudTextureWrap? GetJobIconTexture(string jobAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(jobAbbreviation))
        {
            return null;
        }

        var iconId = FFLogsService.GetJobIconIdForSpec(FFLogsService.GetSpecForJob(jobAbbreviation));
        return iconId.HasValue ? GetIconTexture(iconId.Value) : null;
    }

    private static IDalamudTextureWrap? GetIconTexture(uint iconId)
    {
        try
        {
            return PassportCheckerReborn.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
