using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using ValheimPlus.Configurations;

namespace ValheimPlus.GameClasses
{
    /// <summary>
    /// Alters teleportation prevention
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "IsTeleportable")]
    public static class noItemTeleportPrevention
    {
        private static void Postfix(ref bool __result)
        {
            if (Configuration.Current.Items.IsEnabled)
            {
                if (Configuration.Current.Items.noTeleportPrevention)
                    __result = true;
            }
        }
    }

    /// <summary>
    /// Makes all items fill inventories top to bottom instead of just tools and weapons
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "TopFirst")]
    public static class Inventory_TopFirst_Patch
    {
        public static bool Prefix(ref bool __result)
        {
            if (Configurations.Configuration.Current.Inventory.IsEnabled &&
                Configurations.Configuration.Current.Inventory.inventoryFillTopToBottom)
            {
                __result = true;
                return false;
            }
            else return true;
        }
    }

    /// <summary>
    /// Configure player inventory size
    /// </summary>
    [HarmonyPatch(typeof(Inventory), MethodType.Constructor, new Type[] { typeof(string), typeof(Sprite), typeof(int), typeof(int) })]
    public static class Inventory_Constructor_Patch
    {
        private const int playerInventoryMaxRows = 20;
        private const int playerInventoryMinRows = 4;

        public static void Prefix(string name, ref int w, ref int h)
        {
            if (Configuration.Current.Inventory.IsEnabled)
            {
                // Player inventory
                if (name == "Grave" || name == "Inventory")
                {
                    h = Helper.Clamp(Configuration.Current.Inventory.playerInventoryRows, playerInventoryMinRows, playerInventoryMaxRows);
                }
            }
        }
    }


    public static class Inventory_NearbyChests_Cache
    {
        public static List<Container> chests = new List<Container>();
        public static readonly Stopwatch delta = new Stopwatch();
    }

    /// <summary>
    /// When merging another inventory, try to merge items with existing stacks.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "MoveAll")]
    public static class Inventory_MoveAll_Patch
    {
        private static void Prefix(ref Inventory __instance, ref Inventory fromInventory)
        {
            if (Configuration.Current.Inventory.IsEnabled && Configuration.Current.Inventory.mergeWithExistingStacks)
            {
                List<ItemDrop.ItemData> list = new List<ItemDrop.ItemData>(fromInventory.GetAllItems());
                foreach (ItemDrop.ItemData otherItem in list)
                {
                    if (otherItem.m_shared.m_maxStackSize > 1)
                    {
                        foreach (ItemDrop.ItemData myItem in __instance.m_inventory)
                        {
                            if (myItem.m_shared.m_name == otherItem.m_shared.m_name && myItem.m_quality == otherItem.m_quality)
                            {
                                int itemsToMove = Math.Min(myItem.m_shared.m_maxStackSize - myItem.m_stack, otherItem.m_stack);
                                myItem.m_stack += itemsToMove;
                                if (otherItem.m_stack == itemsToMove)
                                {
                                    fromInventory.RemoveItem(otherItem);
                                    break;
                                }
                                else
                                {
                                    otherItem.m_stack -= itemsToMove;
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public static class AutoStackAllStore
    {
        public static bool isStacking = false;
        public static Inventory currentInventory = null;
        public static int lastPlayerItemCount = 0;
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
    public static class Container_StackAll_Patch
    {

        /// <summary>
        /// Start the auto stack all loop and surpress stack feedback message
        /// </summary>
        static void Prefix(Inventory __instance, ref bool message)
        {
            if (!Configuration.Current.Inventory.autoStackAll) return;

            // disable message
            message = false;
            if (AutoStackAllStore.currentInventory == null)
            {
                // enable stack recursion bypass and reset count
                AutoStackAllStore.lastPlayerItemCount = Player.m_localPlayer.m_inventory.CountItems(null);
                AutoStackAllStore.currentInventory = __instance;
            }
        }

        /// <summary>
        /// Call StackAll on all chests in range.
        /// </summary>
        static void Postfix(Inventory __instance, ref int __result)
        {
            if (!Configuration.Current.Inventory.autoStackAll || (AutoStackAllStore.currentInventory != null && __instance != AutoStackAllStore.currentInventory)) return;

            // get chests in range
            GameObject pos = Player.m_localPlayer.gameObject;
            List<Container> chests = InventoryAssistant.GetNearbyChests(pos, Helper.Clamp(Configuration.Current.Inventory.autoStackAllRange, 1, 50), !Configuration.Current.Inventory.autoStackAllIgnorePrivateAreaCheck);

            // try to stack all items on found containers
            foreach (Container container in chests)
            {
                if (container.m_inventory == __instance) continue;
                container.StackAll();
            }

            // disable stack recursion bypass
            AutoStackAllStore.currentInventory = null;

            // Show stack message
            int itemCount = AutoStackAllStore.lastPlayerItemCount - Player.m_localPlayer.m_inventory.CountItems(null);
            if (itemCount > 0)
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_stackall " + itemCount + " in " + chests.Count + " Chests");
            }
            else
            {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_stackall_none" + " in " + chests.Count + " Chests");
            }
        }

        /// <summary>
        /// Start the auto stack all loop and surpress stack feedback message
        /// </summary>
        static void Prefix(Inventory __instance, ref bool message)
        {
            if (!Configuration.Current.Inventory.autoStackAll) return;

            // disable message
            message = false;
            if (!AutoStackAllStore.isStacking)
            {
                // enable stack recursion bypass and reset count
                AutoStackAllStore.lastPlayerItemCount = Player.m_localPlayer.m_inventory.CountItems(null);
                AutoStackAllStore.isStacking = true;
                AutoStackAllStore.currentInventory = __instance;
            }
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.ContainsItemByName), new System.Type[] { typeof(string) })]
    public static class Container_GetAllItems_Patch
    {
        /// <summary>
        /// If autoStackAllIgnoreEquipment is active, this will only return true if the searched item is not equipable
        /// </summary>
        static void Postfix(Inventory __instance, string name, ref bool __result)
        {
            // ony perform during auto stacking!
            if (!AutoStackAllStore.isStacking || !Configuration.Current.Inventory.autoStackAll || !Configuration.Current.Inventory.autoStackAllIgnoreEquipment) return;

            foreach (ItemDrop.ItemData item in __instance.m_inventory)
            {
                if (!item.IsEquipable() && item.m_shared.m_name == name)
                {
                    __result = true;
                    return;
                }
            }

            __result = false;
        }
    }



}
