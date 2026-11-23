using System;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;

namespace MidRunJoin;

/// <summary>
/// Vanilla Slay the Spire 2 UI styling helpers using native STS2 fonts, <see cref="StsColors"/>,
/// and character icon assets so mod dialogs blend seamlessly with the base game.
/// </summary>
internal static class VanillaUi
{
    internal static readonly Color PanelBg = new(0.086f, 0.118f, 0.153f, 0.97f);
    internal static readonly Color SectionBg = new(0.118f, 0.161f, 0.212f, 0.92f);
    internal static readonly Color RowBg = new(0.145f, 0.196f, 0.255f, 0.85f);
    internal static readonly Color RowHoverBg = new(0.196f, 0.267f, 0.345f, 0.95f);
    internal static readonly Color RowSelectedBg = new(0.220f, 0.286f, 0.220f, 0.96f);
    internal static readonly Color InputBg = new(0.067f, 0.094f, 0.125f, 0.96f);

    internal static readonly Color GoldBorder = new(0.784f, 0.635f, 0.290f, 0.95f);
    internal static readonly Color SubtleBorder = new(0.255f, 0.345f, 0.431f, 0.85f);
    internal static readonly Color TextOutline = new(0.102f, 0.082f, 0.063f, 0.95f);
    internal static readonly Color MutedText = new(0.678f, 0.741f, 0.800f, 1.0f);

    internal static readonly Color ConfirmBg = new(0.118f, 0.322f, 0.255f, 0.98f);
    internal static readonly Color ConfirmHoverBg = new(0.157f, 0.420f, 0.329f, 1.0f);
    internal static readonly Color ConfirmPressedBg = new(0.086f, 0.243f, 0.192f, 1.0f);

    internal static readonly Color CancelBg = new(0.267f, 0.137f, 0.157f, 0.96f);
    internal static readonly Color CancelHoverBg = new(0.365f, 0.184f, 0.208f, 1.0f);
    internal static readonly Color CancelPressedBg = new(0.200f, 0.098f, 0.114f, 1.0f);

    internal static readonly Color SecondaryBg = new(0.157f, 0.212f, 0.275f, 0.96f);
    internal static readonly Color SecondaryHoverBg = new(0.220f, 0.294f, 0.376f, 1.0f);
    internal static readonly Color SecondaryPressedBg = new(0.118f, 0.161f, 0.212f, 1.0f);

    private static Font? TryLoadGameFont(FontType type)
    {
        try
        {
            string? lang = LocManager.Instance?.Language;
            if (!string.IsNullOrWhiteSpace(lang))
            {
                var sub = FontManager.GetSubstituteFont(lang, type);
                if (sub != null)
                    return sub;
            }
            string path = type == FontType.Bold
                ? "res://themes/kreon_bold_shared.tres"
                : "res://themes/kreon_regular_shared.tres";
            if (ResourceLoader.Exists(path))
                return ResourceLoader.Load<Font>(path, null, ResourceLoader.CacheMode.Reuse);
        }
        catch
        {
            // Headless / test environments may not have all font resources loaded.
        }
        return null;
    }

    internal static void ApplyFont(Control control, FontType type, int fontSize, Color color, int outlineSize = 0)
    {
        var font = TryLoadGameFont(type);
        if (font != null)
            control.AddThemeFontOverride("font", font);
        control.AddThemeFontSizeOverride("font_size", fontSize);
        control.AddThemeColorOverride("font_color", color);
        if (outlineSize > 0)
        {
            control.AddThemeColorOverride("font_outline_color", TextOutline);
            control.AddThemeConstantOverride("outline_size", outlineSize);
        }
    }

    internal static StyleBoxFlat MakeBox(
        Color bg,
        Color border,
        int borderWidth = 1,
        int radius = 6,
        int padH = 10,
        int padV = 6,
        int shadowSize = 0)
    {
        var box = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            BorderWidthLeft = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = padH,
            ContentMarginRight = padH,
            ContentMarginTop = padV,
            ContentMarginBottom = padV
        };
        if (shadowSize > 0)
        {
            box.ShadowColor = new Color(0f, 0f, 0f, 0.65f);
            box.ShadowSize = shadowSize;
            box.ShadowOffset = new Vector2(0, 6);
        }
        return box;
    }

    internal static void StyleButton(
        Button btn,
        Color normalBg,
        Color hoverBg,
        Color pressedBg,
        Color normalBorder,
        Color hoverBorder,
        int fontSize = 15,
        FontType fontType = FontType.Bold,
        int radius = 6,
        int padH = 14,
        int padV = 6,
        int outlineSize = 2)
    {
        btn.AddThemeStyleboxOverride("normal", MakeBox(normalBg, normalBorder, 2, radius, padH, padV));
        btn.AddThemeStyleboxOverride("hover", MakeBox(hoverBg, hoverBorder, 2, radius, padH, padV));
        btn.AddThemeStyleboxOverride("pressed", MakeBox(pressedBg, normalBorder, 2, radius, padH, padV));
        btn.AddThemeStyleboxOverride("focus", MakeBox(hoverBg, hoverBorder, 2, radius, padH, padV));
        btn.AddThemeStyleboxOverride("disabled", MakeBox(new Color(0.15f, 0.17f, 0.20f, 0.7f), new Color(0.28f, 0.30f, 0.34f, 0.6f), 1, radius, padH, padV));
        ApplyFont(btn, fontType, fontSize, StsColors.cream, outlineSize);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        btn.AddThemeColorOverride("font_pressed_color", StsColors.gold);
        btn.AddThemeColorOverride("font_disabled_color", StsColors.gray);
        btn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    internal static void StylePrimaryButton(Button btn, int fontSize = 17) =>
        StyleButton(btn, ConfirmBg, ConfirmHoverBg, ConfirmPressedBg, GoldBorder, StsColors.gold, fontSize, FontType.Bold, 8, 22, 9, 3);

    internal static void StyleCancelButton(Button btn, int fontSize = 16) =>
        StyleButton(btn, CancelBg, CancelHoverBg, CancelPressedBg, new Color(0.68f, 0.33f, 0.33f, 0.9f), StsColors.red, fontSize, FontType.Bold, 8, 18, 9, 2);

    internal static void StyleSecondaryButton(Button btn, int fontSize = 14) =>
        StyleButton(btn, SecondaryBg, SecondaryHoverBg, SecondaryPressedBg, GoldBorder, StsColors.gold, fontSize, FontType.Bold, 6, 12, 5, 2);

    internal static void StyleDangerSmallButton(Button btn, int fontSize = 14) =>
        StyleButton(btn, CancelBg, CancelHoverBg, CancelPressedBg, new Color(0.68f, 0.33f, 0.33f, 0.85f), StsColors.red, fontSize, FontType.Bold, 6, 12, 4, 2);

    internal static void StyleLineEdit(LineEdit edit, int fontSize = 15)
    {
        edit.AddThemeStyleboxOverride("normal", MakeBox(InputBg, SubtleBorder, 1, 6, 10, 6));
        edit.AddThemeStyleboxOverride("focus", MakeBox(InputBg, StsColors.gold, 2, 6, 10, 6));
        ApplyFont(edit, FontType.Regular, fontSize, StsColors.cream);
        edit.AddThemeColorOverride("font_placeholder_color", MutedText);
        edit.AddThemeColorOverride("caret_color", StsColors.gold);
    }

    internal static void StyleOptionButton(OptionButton opt, int fontSize = 15)
    {
        StyleButton(opt, InputBg, RowHoverBg, RowBg, SubtleBorder, StsColors.gold, fontSize, FontType.Regular, 6, 12, 6, 1);
        var popup = opt.GetPopup();
        if (popup != null)
        {
            popup.AddThemeStyleboxOverride("panel", MakeBox(PanelBg, GoldBorder, 2, 6, 6, 6, 10));
            popup.AddThemeStyleboxOverride("hover", MakeBox(RowHoverBg, StsColors.gold, 1, 4, 8, 4));
            var font = TryLoadGameFont(FontType.Regular);
            if (font != null)
                popup.AddThemeFontOverride("font", font);
            popup.AddThemeFontSizeOverride("font_size", fontSize);
            popup.AddThemeColorOverride("font_color", StsColors.cream);
            popup.AddThemeColorOverride("font_hover_color", StsColors.gold);
        }
    }

    internal static void StyleCheckBox(CheckBox chk, int fontSize = 14)
    {
        ApplyFont(chk, FontType.Regular, fontSize, StsColors.cream);
        chk.AddThemeColorOverride("font_hover_color", StsColors.gold);
        chk.AddThemeColorOverride("font_pressed_color", StsColors.gold);
        chk.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    internal static void StyleFriendRowButton(Button btn, bool selected)
    {
        var normal = selected
            ? MakeBox(RowSelectedBg, StsColors.gold, 2, 5, 10, 5)
            : MakeBox(RowBg, SubtleBorder, 1, 5, 10, 5);
        var hover = selected
            ? MakeBox(RowSelectedBg, StsColors.gold, 2, 5, 10, 5)
            : MakeBox(RowHoverBg, GoldBorder, 1, 5, 10, 5);
        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", MakeBox(RowSelectedBg, StsColors.gold, 2, 5, 10, 5));
        btn.AddThemeStyleboxOverride("focus", hover);
        btn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    internal static PanelContainer CreateSectionCard(out VBoxContainer body, int padH = 12, int padV = 10)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        panel.AddThemeStyleboxOverride("panel", MakeBox(SectionBg, SubtleBorder, 1, 8, padH, padV));
        body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 6);
        panel.AddChild(body);
        return panel;
    }

    internal static PanelContainer CreateBadgePill(string text, Color bg, Color fg, int fontSize = 12)
    {
        var pill = new PanelContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        pill.AddThemeStyleboxOverride("panel", MakeBox(bg, new Color(fg.R, fg.G, fg.B, 0.55f), 1, 10, 8, 2));
        var lbl = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ApplyFont(lbl, FontType.Bold, fontSize, fg);
        pill.AddChild(lbl);
        return pill;
    }

    internal static TextureRect CreateCharacterIcon(CharacterModel? character, int size = 26)
    {
        var rect = new TextureRect
        {
            CustomMinimumSize = new Vector2(size, size),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        SetCharacterIconTexture(rect, character);
        return rect;
    }

    private const string RandomCharacterIconPath = "res://images/ui/top_panel/character_icon_random_character.png";

    internal static void SetCharacterIconTexture(TextureRect rect, CharacterModel? character)
    {
        if (character == null)
        {
            rect.Texture = null;
            return;
        }
        try
        {
            rect.Texture = character.IconTexture;
        }
        catch
        {
            rect.Texture = null;
        }
    }

    internal static void SetRandomCharacterIconTexture(TextureRect rect)
    {
        try
        {
            rect.Texture = ResourceLoader.Exists(RandomCharacterIconPath)
                ? ResourceLoader.Load<Texture2D>(RandomCharacterIconPath, null, ResourceLoader.CacheMode.Reuse)
                : null;
        }
        catch
        {
            rect.Texture = null;
        }
    }

    internal static ColorRect CreateGoldDivider() => new()
    {
        Color = new Color(GoldBorder.R, GoldBorder.G, GoldBorder.B, 0.45f),
        CustomMinimumSize = new Vector2(0, 2),
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        MouseFilter = Control.MouseFilterEnum.Ignore
    };
}
