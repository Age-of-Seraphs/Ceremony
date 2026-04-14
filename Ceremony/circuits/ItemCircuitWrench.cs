using Vintagestory.API.Common;

namespace circuits
{
    public class ItemCircuitWrench : Item
    {
        public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandHandling handling) { handling = EnumHandHandling.PreventDefault; }
        public override bool OnHeldAttackStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel) { return false; }
        public override void OnHeldAttackStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel) { }

        public override void OnHeldInteractStart(
            ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel,
            bool firstEvent, ref EnumHandHandling handling)
        {
            handling = EnumHandHandling.PreventDefault;
        }
    }
}
