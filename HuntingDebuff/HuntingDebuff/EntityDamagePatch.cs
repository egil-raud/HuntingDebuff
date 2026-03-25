using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace Gnd.HuntingDebuffs
{
    [HarmonyPatch(typeof(Entity), "ReceiveDamage")]
    public class EntityDamagePatch
    {
        private static void Prefix(Entity __instance, DamageSource damageSource, float damage)
        {
            try
            {
                // Пропускаем, если цель - игрок
                if (__instance is EntityPlayer)
                    return;

                HuntingDebuffSystem instance = HuntingDebuffSystem.Instance;
                if (instance?.Sapi == null || __instance?.Alive != true)
                    return;

                IServerPlayer player = null;
                string weaponCode = "";
                bool isRangedAttack = false;

                if (damageSource?.SourceEntity is EntityPlayer directPlayer)
                {
                    player = instance.Sapi.World.PlayerByUid(directPlayer.PlayerUID) as IServerPlayer;
                    weaponCode = directPlayer.RightHandItemSlot?.Itemstack?.Collectible?.Code?.Path ?? "";

                    if (weaponCode.Contains("bow"))
                    {
                        return;
                    }
                }
                else if (damageSource?.CauseEntity is EntityPlayer indirectPlayer)
                {
                    player = instance.Sapi.World.PlayerByUid(indirectPlayer.PlayerUID) as IServerPlayer;
                    weaponCode = "arrow";
                    isRangedAttack = true;
                }

                if (player != null)
                {
                    instance.ApplyBleedFromWeapon(__instance, player, weaponCode, damage, isRangedAttack);
                }
            }
            catch (Exception ex)
            {
                __instance?.Api?.Logger?.Debug("[GND] Harmony prefix error: " + ex.Message);
            }
        }
    }
}