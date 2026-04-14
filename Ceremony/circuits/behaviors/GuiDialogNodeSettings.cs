using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

#nullable disable

namespace circuits
{
    public class GuiDialogNodeSettings : GuiDialogBlockEntity
    {
        private readonly NodeSettingsDescriptor descriptor;
        private readonly Action<Dictionary<string, string>> onSaved;

        public override string ToggleKeyCombinationCode => null;
        private readonly Dictionary<string, DummyInventory> itemInvs = new();

        public GuiDialogNodeSettings(
            BlockPos pos,
            NodeSettingsDescriptor descriptor,
            Dictionary<string, object> currentValues,
            ICoreClientAPI capi,
            Action<Dictionary<string, string>> onSaved = null)
            : base(descriptor.Title, pos, capi)
        {
            this.descriptor = descriptor;
            this.onSaved = onSaved;
            ComposeDialog(currentValues);
        }

        private void ComposeDialog(Dictionary<string, object> currentValues)
        {
            double dialogWidth = 400;

            ElementBounds lastBounds = null;

            var fieldBounds = new List<(NodeSettingField field, ElementBounds label, ElementBounds input)>();

            foreach (var field in descriptor.Fields)
            {
                ElementBounds labelBounds;
                ElementBounds inputBounds;

                double inputW = (field.Type == SettingType.Select || field.Type == SettingType.Slider) ? 200 : (field.Type == SettingType.ItemSlot) ? 36 : 100;
                if (lastBounds == null)
                {
                    labelBounds = ElementBounds.Fixed(0, 34, dialogWidth, 25);
                    inputBounds = ElementBounds.Fixed(130, 27, inputW, 30);
                }
                else
                {
                    double yOffset = field.Type switch
                    {
                        SettingType.Toggle => 14,
                        SettingType.ItemSlot => 44,
                        _ => 10
                    };

                    double inputYAdj = field.Type switch
                    {
                        SettingType.Toggle => 2,
                        SettingType.ItemSlot => 15,
                        _ => -7
                    };
                    labelBounds = lastBounds.BelowCopy(0, yOffset);
                    inputBounds = ElementBounds.Fixed(130, labelBounds.fixedY + inputYAdj, inputW, 30);
                }

                fieldBounds.Add((field, labelBounds, inputBounds));
                lastBounds = labelBounds;
            }

            ElementBounds belowLast = lastBounds ?? ElementBounds.Fixed(0, 34, dialogWidth, 25);

            ElementBounds cancelBounds = ElementBounds.FixedSize(0, 0)
                .FixedUnder(belowLast, 25)
                .WithFixedPadding(10, 2);

            ElementBounds saveBounds = ElementBounds.FixedSize(0, 0)
                .FixedUnder(belowLast, 25)
                .WithAlignment(EnumDialogArea.RightFixed)
                .WithFixedPadding(10, 2);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            SingleComposer?.Dispose();

            var pos = BlockEntityPosition;
            SingleComposer = capi.Gui.CreateCompo($"nodesettings-{pos.X}-{pos.Y}-{pos.Z}", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(descriptor.Title, OnTitleBarClose)
                .BeginChildElements(bgBounds);

            foreach (var (field, labelBounds, inputBounds) in fieldBounds)
            {
                SingleComposer.AddStaticText(field.Label, CairoFont.WhiteSmallText(), labelBounds);

                switch (field.Type)
                {
                    case SettingType.Number:
                        SingleComposer.AddNumberInput(inputBounds, null, CairoFont.WhiteDetailText(), "field_" + field.Key);
                        break;
                    case SettingType.Toggle:
                        SingleComposer.AddSwitch(null, inputBounds, "field_" + field.Key);
                        break;
                    case SettingType.Text:
                        SingleComposer.AddTextInput(inputBounds, null, CairoFont.WhiteDetailText(), "field_" + field.Key);
                        break;
                    case SettingType.Select:
                        SingleComposer.AddDropDown(
                            field.OptionKeys, field.OptionLabels, 0, null,
                            inputBounds, CairoFont.WhiteDetailText(), "field_" + field.Key);
                        break;
                    case SettingType.Slider:
                        SingleComposer.AddSlider(_ => true, inputBounds, "field_" + field.Key);
                        break;
                    case SettingType.ItemSlot:
                        {
                            var inv = new GhostInventory(capi);
                            inv.Open(capi.World.Player);
                            itemInvs[field.Key] = inv;

                            SingleComposer.AddItemSlotGrid(
                                inv,
                                _ => OnSlotChanged(field.Key),
                                1,
                                new int[] { 0 },
                                inputBounds.FlatCopy().WithFixedSize(36, 36),
                                "field_" + field.Key
                            );
                            break;
                        }
                }
            }

            SingleComposer
                .AddSmallButton("Cancel", OnCancel, cancelBounds)
                .AddSmallButton("Save", OnSave, saveBounds)
                .EndChildElements()
                .Compose();

            // Set current values
            foreach (var field in descriptor.Fields)
            {
                string elKey = "field_" + field.Key;
                object curVal = currentValues != null && currentValues.TryGetValue(field.Key, out var v) ? v : field.Default;

                switch (field.Type)
                {
                    case SettingType.Number:
                        float numVal = curVal switch
                        {
                            int i => i,
                            float f => f,
                            double d => (float)d,
                            _ => Convert.ToSingle(field.Default ?? 0f)
                        };
                        SingleComposer.GetNumberInput(elKey)?.SetValue(numVal);
                        break;
                    case SettingType.Toggle:
                        bool boolVal = curVal is bool b ? b : Convert.ToBoolean(field.Default ?? false);
                        var sw = SingleComposer.GetSwitch(elKey);
                        if (sw != null) sw.On = boolVal;
                        break;
                    case SettingType.Text:
                        string strVal = curVal?.ToString() ?? field.Default?.ToString() ?? "";
                        SingleComposer.GetTextInput(elKey)?.SetValue(strVal);
                        break;
                    case SettingType.Select:
                        string selVal = curVal?.ToString() ?? field.Default?.ToString() ?? "";
                        SingleComposer.GetDropDown(elKey)?.SetSelectedValue(selVal);
                        break;
                    case SettingType.Slider:
                        int sliderVal = curVal switch
                        {
                            int i => i,
                            float f => (int)f,
                            double d => (int)d,
                            _ => Convert.ToInt32(field.Default ?? 0)
                        };
                        SingleComposer.GetSlider(elKey)?.SetValues(sliderVal, (int)field.Min, (int)field.Max, 1);
                        break;
                    case SettingType.ItemSlot:
                        {
                            if (!itemInvs.TryGetValue(field.Key, out var inv)) break;
                            if (curVal is ItemStack stack)
                            {
                                inv[0].Itemstack = stack.Clone();
                                inv[0].Itemstack.StackSize = 1;
                            }
                            inv[0].MarkDirty();
                            break;
                        }
                }
            }

            SingleComposer.UnfocusOwnElements();
        }

        private void OnSlotChanged(string fieldKey)
        {
            if (!itemInvs.TryGetValue(fieldKey, out var inv)) return;
            if (inv[0].Itemstack != null)
                inv[0].Itemstack.StackSize = 1;
            inv[0].MarkDirty();
        }

        private bool OnCancel()
        {
            TryClose();
            return true;
        }

        private bool OnSave()
        {
            var values = new Dictionary<string, string>();

            foreach (var field in descriptor.Fields)
            {
                string elKey = "field_" + field.Key;

                switch (field.Type)
                {
                    case SettingType.Number:
                        var numInput = SingleComposer.GetNumberInput(elKey);
                        values[field.Key] = numInput != null ? ((int)numInput.GetValue()).ToString() : "0";
                        break;
                    case SettingType.Toggle:
                        var sw = SingleComposer.GetSwitch(elKey);
                        values[field.Key] = sw != null ? sw.On.ToString() : "false";
                        break;
                    case SettingType.Text:
                        var textInput = SingleComposer.GetTextInput(elKey);
                        values[field.Key] = textInput?.GetText() ?? "";
                        break;
                    case SettingType.Select:
                        var dd = SingleComposer.GetDropDown(elKey);
                        values[field.Key] = dd?.SelectedValue ?? field.Default?.ToString() ?? "";
                        break;
                    case SettingType.Slider:
                        var slider = SingleComposer.GetSlider(elKey);
                        values[field.Key] = slider?.GetValue().ToString() ?? "0";
                        break;
                    case SettingType.ItemSlot:
                        if (itemInvs.TryGetValue(field.Key, out var slotInv))
                            values[field.Key] = CircuitBehavior.SerializeItemStack(slotInv[0].Itemstack);
                        else
                            values[field.Key] = "";
                        break;
                }
            }

            onSaved?.Invoke(values);
            var packet = new EditNodeSettingsPacket { Values = values };
            capi.Network.SendBlockEntityPacket(BlockEntityPosition, CircuitBehavior.PacketIdNodeSettings, SerializerUtil.Serialize(packet));
            TryClose();
            return true;
        }

        private void OnTitleBarClose()
        {
            TryClose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();
            CloseItemInventories();
        }

        public override void Dispose()
        {
            CloseItemInventories();
            base.Dispose();
        }

        private void CloseItemInventories()
        {
            var player = capi.World.Player;
            foreach (var inv in itemInvs.Values)
            {
                try { inv.Close(player); } catch { }
            }
            itemInvs.Clear();
        }

        /// <summary>
        /// A client-only inventory whose slots copy the cursor item on click
        /// instead of transferring it, avoiding mouse-slot desync.
        /// </summary>
        private class GhostInventory : DummyInventory
        {
            public GhostInventory(ICoreClientAPI capi) : base(capi) { }

            public override object ActivateSlot(int slotId, ItemSlot sourceSlot, ref ItemStackMoveOperation op)
            {
                if (sourceSlot?.Itemstack != null)
                {
                    this[slotId].Itemstack = sourceSlot.Itemstack.Clone();
                    this[slotId].Itemstack.StackSize = 1;
                }
                else
                {
                    this[slotId].Itemstack = null;
                }

                this[slotId].MarkDirty();
                op.MovedQuantity = 0;
                return null;
            }
        }
    }
}
