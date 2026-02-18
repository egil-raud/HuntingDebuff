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
        private static bool onceLogged;

        private static void Prefix(Entity __instance, DamageSource damageSource, float damage)
        {
            try
            {
                HuntingDebuffSystem instance = HuntingDebuffSystem.Instance;
                if (instance?.Sapi == null || __instance?.Alive != true)
                    return;

                if (!onceLogged)
                {
                    instance.Sapi.Logger.Notification("[GND] Harmony prefix for Entity.ReceiveDamage is firing.");
                    onceLogged = true;
                }

                IServerPlayer player = null;
                string weaponCode = "";

                if (damageSource?.SourceEntity is EntityPlayer directPlayer)
                {
                    player = instance.Sapi.World.PlayerByUid(directPlayer.PlayerUID) as IServerPlayer;
                    weaponCode = directPlayer.RightHandItemSlot?.Itemstack?.Collectible?.Code?.Path ?? "";
                }
                else if (damageSource?.CauseEntity is EntityPlayer indirectPlayer)
                {
                    player = instance.Sapi.World.PlayerByUid(indirectPlayer.PlayerUID) as IServerPlayer;
                    weaponCode = "arrow";
                }

                if (player != null)
                {
                    instance.ApplyBleedFromWeapon(__instance, player, weaponCode);
                }
            }
            catch (Exception ex)
            {
                __instance?.Api?.Logger?.Debug("[GND] Harmony prefix error: " + ex.Message);
            }
        }
    }
}