using Vintagestory.API.Common;

namespace circuits
{
    public class BBWandPassThrough : BlockBehavior
    {
        public BBWandPassThrough(Block block) : base(block) { }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            var held = byPlayer.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code;
            if (held == "circuits:circuitwand")
            {
                handling = EnumHandling.PassThrough;
                return false;
            }

            return base.OnBlockInteractStart(world, byPlayer, blockSel, ref handling);
        }
    }
}
