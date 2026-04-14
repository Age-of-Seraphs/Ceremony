using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
#nullable disable

namespace circuits
{
    public interface ICircuitInteractable
    {
        bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling);
    }

    public class BBDeferInteract(Block block) : BlockBehavior(block)
    {
        HashSet<AssetLocation> bypassCodes;

        public override void Initialize(JsonObject properties)
        {
            base.Initialize(properties);

            bypassCodes = [ new("circuits:circuitwand") ];

            var extra = properties?["bypassItems"]?.AsArray<string>(null);
            if (extra != null)
            {
                foreach (var s in extra)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    bypassCodes.Add(new AssetLocation(s.Trim()));
                }
            }
        }

        public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            var heldCode = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code;
            if (heldCode != null && bypassCodes.Contains(heldCode))
            {
                handling = EnumHandling.PreventDefault;
                return true;
            }

            if (blockSel?.Position == null)
            {
                handling = EnumHandling.PassThrough;
                return false;
            }

            var be = world.BlockAccessor.GetBlockEntity(blockSel.Position);
            var behs = be?.Behaviors;
            if (behs == null)
            {
                handling = EnumHandling.PassThrough;
                return false;
            }

            foreach (var b in behs)
            {
                if (b is not ICircuitInteractable ia) continue;

                EnumHandling h = EnumHandling.PassThrough;
                bool handled = ia.OnBlockInteractStart(world, byPlayer, blockSel, ref h);

                if (!handled && h == EnumHandling.PassThrough)
                {
                    handling = EnumHandling.PassThrough;
                    return false;
                }

                if (handled)
                {
                    handling = h;
                    return true;
                }
            }

            handling = EnumHandling.PassThrough;
            return false;
        }

        public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
        {
            var be = world.BlockAccessor.GetBlockEntity(pos);
            var cb = FindCircuitBehavior(be);
            if (cb == null)
            {
                handling = EnumHandling.PassThrough;
                return null;
            }

            handling = EnumHandling.PreventSubsequent;
            var stack = new ItemStack(block);
            SaveSettingsToStack(cb, stack);
            return stack;
        }

        public override ItemStack[] GetDrops(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, ref float dropQuantityMultiplier, ref EnumHandling handling)
        {
            var be = world.BlockAccessor.GetBlockEntity(pos);
            var cb = FindCircuitBehavior(be);
            if (cb == null)
            {
                handling = EnumHandling.PassThrough;
                return null;
            }

            handling = EnumHandling.PreventSubsequent;
            var stack = new ItemStack(block);
            SaveSettingsToStack(cb, stack);
            return [stack];
        }

        private static CircuitBehavior FindCircuitBehavior(BlockEntity be)
        {
            if (be?.Behaviors == null) return null;
            foreach (var b in be.Behaviors)
                if (b is CircuitBehavior cb) return cb;
            return null;
        }

        private static void SaveSettingsToStack(CircuitBehavior cb, ItemStack stack)
        {
            var tree = new TreeAttribute();
            cb.WriteSettingsToTree(tree);
            if (tree.Count > 0)
                stack.Attributes["circuits:bedata"] = tree;
        }
    }
}
