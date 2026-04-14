using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
#nullable disable


namespace circuits
{
    public class GuiDialogLocatorEditor : GuiDialogGeneric
    {
        private readonly Action<CustomLocatorProps, string> onConfirm;
        private readonly CustomLocatorProps props;

        private int[] colors;
        private string[] icons;

        private int selectedColor;
        private string selectedIcon;
        private string selectedVariantType;

        private readonly string[] variantTypes =
        [
            "map-blank",
            "map1",
            "map2",
            "map-cavetobias",
            "map-devastationarea",
            "map-treasures"
        ];

        public override string ToggleKeyCombinationCode => null;

        public GuiDialogLocatorEditor(ICoreClientAPI capi, WaypointMapLayer wml, CustomLocatorProps initial, string currentVariantType, Action<CustomLocatorProps, string> onConfirm) : base("", capi)
        {
            this.onConfirm = onConfirm;

            props = new CustomLocatorProps
            {
                WaypointText = initial.WaypointText,
                WaypointIcon = initial.WaypointIcon,
                WaypointColorSwatch = initial.WaypointColorSwatch,
                WaypointPos = initial.WaypointPos,
                IsWritten = initial.IsWritten
            };

            icons = [.. wml.WaypointIcons.Keys];
            colors = [.. wml.WaypointColors];
            selectedIcon = icons.Contains(props.WaypointIcon) ? props.WaypointIcon : icons.FirstOrDefault() ?? "x";
            selectedColor = props.WaypointColorSwatch;
            selectedVariantType = "map-treasures";

            int colorIndex = colors.IndexOf(props.WaypointColorSwatch);
            if (colorIndex < 0)
            {
                colors = [.. colors, props.WaypointColorSwatch];
                colorIndex = colors.Length - 1;
            }

            ComposeDialog(colorIndex, icons.IndexOf(selectedIcon));
        }

        public override bool TryOpen()
        {
            return base.TryOpen();
        }

        private void ComposeDialog(int colorIndex, int iconIndex)
        {
            ElementBounds left = ElementBounds.Fixed(0, 28, 120, 25);
            ElementBounds right = left.RightCopy();
            ElementBounds buttonRow = ElementBounds.Fixed(0, 28, 360, 25);

            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            SingleComposer?.Dispose();

            int iconSize = 22;
            int variantIndex = Math.Max(0, variantTypes.IndexOf(selectedVariantType));
            if (variantIndex < 0) variantIndex = 0;

            SingleComposer = capi.Gui.CreateCompo("customlocator-wp", dialogBounds)
                .AddShadedDialogBG(bg, withTitleBar: false)
                .AddDialogTitleBar(Lang.Get("Create Locator Map"), () => { TryClose(); })
                .BeginChildElements(bg)

                .AddStaticText(Lang.Get("Waypoint name"), CairoFont.WhiteSmallText(), left = left.FlatCopy())
                .AddTextInput(right = right.FlatCopy().WithFixedWidth(200), OnNameChanged, CairoFont.TextInput(), "nameInput")

                .AddStaticText("Map Variant", CairoFont.WhiteSmallText(), left = left.BelowCopy(0, 9))
                .AddDropDown(
                    variantTypes,               // values
                    variantTypes,               // labels
                    variantIndex,
                    OnVariantSelected,
                    right = right.BelowCopy(0, 5).WithFixedWidth(200),
                    "variantDropdown"
                )

                .AddRichtext(Lang.Get("waypoint-color"), CairoFont.WhiteSmallText(), left = left.BelowCopy(0, 5))
                .AddColorListPicker(colors, OnColorSelected,
                    left = left.BelowCopy(0, 5).WithFixedSize(iconSize, iconSize), 270, "colorpicker")

                .AddStaticText(Lang.Get("Icon"), CairoFont.WhiteSmallText(),
                    left = left.WithFixedPosition(0, left.fixedY + left.fixedHeight).WithFixedWidth(100).BelowCopy())
                .AddIconListPicker(icons, OnIconSelected,
                    left = left.BelowCopy(0, 5).WithFixedSize(iconSize + 5, iconSize + 5), 270, "iconpicker")

                .AddSmallButton(Lang.Get("Cancel"), OnCancel, buttonRow.FlatCopy().FixedUnder(left).WithFixedWidth(100))
                .AddSmallButton(Lang.Get("Save"), OnSave,
                    buttonRow.FlatCopy().FixedUnder(left).WithFixedWidth(100).WithAlignment(EnumDialogArea.RightFixed),
                    EnumButtonStyle.Normal, "saveButton")

                .EndChildElements()
                .Compose();

            SingleComposer.ColorListPickerSetValue("colorpicker", Math.Max(0, colorIndex));
            SingleComposer.IconListPickerSetValue("iconpicker", Math.Max(0, iconIndex));

            SingleComposer.GetTextInput("nameInput").SetValue(props.WaypointText);
            SingleComposer.GetButton("saveButton").Enabled = props.WaypointText.Trim() != "";

            SingleComposer.GetDropDown("variantDropdown")?.SetSelectedValue(selectedVariantType);

        }

        private void OnColorSelected(int index)
        {
            selectedColor = colors[index];
            selectedColor |= unchecked((int)0xFF000000);

        }

        private void OnIconSelected(int index)
        {
            selectedIcon = icons[index];

        }

        private bool OnSave()
        {
            string name = SingleComposer.GetTextInput("nameInput").GetText() ?? "";

            props.WaypointText = name;
            props.WaypointIcon = selectedIcon;
            props.WaypointColorSwatch = selectedColor;

            onConfirm(props, selectedVariantType);
            TryClose();
            return true;
        }

        private bool OnCancel()
        {
            TryClose();
            return true;
        }
        private void OnVariantSelected(string code, bool selected)
        {
            if (!selected) return;
            selectedVariantType = code;
        }

        private void OnNameChanged(string text)
        {
            SingleComposer.GetButton("saveButton").Enabled = text.Trim() != "";
        }
    }
}
