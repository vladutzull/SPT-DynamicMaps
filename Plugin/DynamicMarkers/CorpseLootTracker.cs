using System;
using System.Collections.Generic;
using DynamicMaps.Patches;
using EFT.InventoryLogic;
using EFT.Interactive;

namespace DynamicMaps.DynamicMarkers
{
    internal enum CorpseLootState
    {
        None,
        LootedByOther,
        LootedByPlayer,
    }

    // Tracks corpse loot state. This is kept independent of the map UI so corpses looted while the
    // map is closed still show up correctly when it's opened.
    internal static class CorpseLootTracker
    {
        private static readonly Dictionary<ItemController, Action<RemoveItemEventArgs>> _lootHandlers = [];

        // Corpse root item id -> corpse owner profile id, used to identify the corpse being looted
        private static readonly Dictionary<string, string> _profileIdsByItemId = [];

        private static readonly HashSet<string> _playerLootedProfileIds = [];
        private static readonly HashSet<string> _otherLootedProfileIds = [];

        private static bool _started;

        public static event Action<string, CorpseLootState> OnCorpseLootStateChanged;

        // Called when a raid starts, corpses created from then on are tracked
        public static void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            LootItemInitPatch.OnRegisterLoot += OnRegisterLoot;
        }

        // Called when a raid ends, drops every subscription and remembered state
        public static void Stop()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            LootItemInitPatch.OnRegisterLoot -= OnRegisterLoot;

            foreach (var pair in _lootHandlers)
            {
                if (pair.Key is not null)
                {
                    pair.Key.RemoveItemEvent -= pair.Value;
                }
            }

            _lootHandlers.Clear();
            _profileIdsByItemId.Clear();
            _playerLootedProfileIds.Clear();
            _otherLootedProfileIds.Clear();
        }

        public static CorpseLootState GetState(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
            {
                return CorpseLootState.None;
            }

            if (_playerLootedProfileIds.Contains(profileId))
            {
                return CorpseLootState.LootedByPlayer;
            }

            return _otherLootedProfileIds.Contains(profileId)
                ? CorpseLootState.LootedByOther
                : CorpseLootState.None;
        }

        // Called when the main player opens a loot window, which is what counts as "the player
        // looted this corpse". Taking items out is not required.
        public static void NotifyLootWindowOpened(Item lootedItem)
        {
            if (lootedItem is null)
            {
                return;
            }

            if (!_profileIdsByItemId.TryGetValue(lootedItem.Id, out var profileId))
            {
                return;
            }

            if (!_playerLootedProfileIds.Add(profileId))
            {
                return;
            }

            OnCorpseLootStateChanged?.Invoke(profileId, CorpseLootState.LootedByPlayer);
        }

        private static void OnRegisterLoot(LootItem lootItem)
        {
            if (lootItem is not Corpse corpse || corpse.ItemOwner is null)
            {
                return;
            }

            var profileId = corpse.PlayerProfileID;
            if (!string.IsNullOrEmpty(profileId))
            {
                // The loot window is opened with the root item of the corpse's item controller,
                // but accept the corpse item too in case it differs
                if (corpse.Item is not null)
                {
                    _profileIdsByItemId[corpse.Item.Id] = profileId;
                }

                if (corpse.ItemOwner.RootItem is not null)
                {
                    _profileIdsByItemId[corpse.ItemOwner.RootItem.Id] = profileId;
                }
            }

            if (_lootHandlers.ContainsKey(corpse.ItemOwner))
            {
                return;
            }

            // Every successful removal from the corpse means somebody else took something out of
            // it. The local player's own loot counts are handled by NotifyLootWindowOpened instead.
            Action<RemoveItemEventArgs> handler = args => HandleItemRemoved(corpse, args);

            _lootHandlers[corpse.ItemOwner] = handler;
            corpse.ItemOwner.RemoveItemEvent += handler;
        }

        private static void HandleItemRemoved(Corpse corpse, RemoveItemEventArgs args)
        {
            if (args.Status != CommandStatus.Succeed || corpse == null || string.IsNullOrEmpty(corpse.PlayerProfileID))
            {
                return;
            }

            if (!_otherLootedProfileIds.Add(corpse.PlayerProfileID))
            {
                return;
            }

            OnCorpseLootStateChanged?.Invoke(corpse.PlayerProfileID, GetState(corpse.PlayerProfileID));
        }
    }
}
