using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using Photon.Pun;

namespace BagExpander
{
    [BepInPlugin("com.hiarlyscripter.bagexpander", "Bag Expander", "1.0.0")]
    public sealed class BagExpanderPlugin : BaseUnityPlugin
    {
        internal static BagExpanderPlugin Instance { get; private set; }
        internal static ManualLogSource Log        { get; private set; }

        // ── Seção: Slots ──────────────────────────────────────────────────────
        internal static ConfigEntry<int>  SlotCount;
        internal static ConfigEntry<bool> LeaveInBase;

        // ── Seção: Multiplayer ────────────────────────────────────────────────
        internal static ConfigEntry<bool> HostControls;

        private void Awake()
        {
            Instance = this;
            Log      = Logger;

            // ── Slots ──
            SlotCount = Config.Bind(
                "Slots", "SlotCount", 3,
                new ConfigDescription(
                    "Número de slots de inventário disponíveis. " +
                    "Mínimo: 0 (sem inventário), máximo: 10, padrão do jogo: 3. " +
                    "Requer reinício da rodada para aplicar. Editável pelo REPOConfig.",
                    new AcceptableValueRange<int>(0, 10)));

            LeaveInBase = Config.Bind(
                "Slots", "LeaveInBase", false,
                "true  = ao trocar de rodada, itens nos slots extras (acima de 3) ficam na base.\n" +
                "false = itens retornam equipados ao jogador na próxima rodada (padrão).");

            // ── Multiplayer ──
            HostControls = Config.Bind(
                "Multiplayer", "HostControls", true,
                "true  = o HOST define o número máximo de slots para todos os jogadores. " +
                "Clientes não conseguem equipar itens além do SlotCount do host, " +
                "mesmo que tenham o mod com um valor maior configurado.\n" +
                "false = cada jogador usa livremente seu próprio SlotCount. " +
                "Todos os jogadores precisam ter o mod instalado para se beneficiar.\n" +
                "IMPORTANTE: este mod deve ser instalado por cada jogador individualmente. " +
                "O host não consegue forçar slots extras em clientes que não têm o mod.");

            new Harmony("com.hiarlyscripter.bagexpander").PatchAll(typeof(SlotPatches));
            Log.LogInfo("Bag Expander v1.0.0 carregado.");
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  EquipTracker — rastreia itens nos slots extras entre rodadas
    // ──────────────────────────────────────────────────────────────────────────
    internal static class EquipTracker
    {
        private static readonly Dictionary<string, Dictionary<int, int>> _registry
            = new Dictionary<string, Dictionary<int, int>>();

        internal static void Record(string steamID, int slot, int nameHash)
        {
            if (!_registry.TryGetValue(steamID, out var slots))
            {
                slots = new Dictionary<int, int>();
                _registry[steamID] = slots;
            }
            slots[slot] = nameHash;
        }

        internal static void Erase(string steamID, int slot)
        {
            if (!_registry.TryGetValue(steamID, out var slots)) return;
            slots.Remove(slot);
            if (slots.Count == 0) _registry.Remove(steamID);
        }

        internal static void Reset() => _registry.Clear();

        internal static bool Lookup(int nameHash, out string steamID, out int slot, List<PlayerAvatar> players)
        {
            foreach (var player in players)
            {
                string sid = SemiFunc.PlayerGetSteamID(player);
                if (!_registry.TryGetValue(sid, out var slots)) continue;
                foreach (var kv in slots)
                {
                    if (kv.Value != nameHash) continue;
                    steamID = sid;
                    slot    = kv.Key;
                    return true;
                }
            }
            steamID = null;
            slot    = -1;
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  SlotPatches — patches Harmony
    // ──────────────────────────────────────────────────────────────────────────
    [HarmonyPatch]
    internal static class SlotPatches
    {
        private static readonly FieldInfo _spotsListField =
            AccessTools.Field(typeof(Inventory), "inventorySpots");

        private static readonly FieldInfo _uiChildrenField =
            AccessTools.Field(typeof(InventoryUI), "allChildren");

        private static readonly MethodInfo _handleInputMethod =
            AccessTools.Method(typeof(InventorySpot), "HandleInput");

        // ── 1. Expande a lista interna de slots ───────────────────────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Inventory), "Awake")]
        static void ExpandSlotList(Inventory __instance)
        {
            var list   = (List<InventorySpot>)_spotsListField.GetValue(__instance);
            int needed = Math.Max(BagExpanderPlugin.SlotCount.Value, 3);
            while (list.Count < needed) list.Add(null);
        }

        // ── 2. Ajusta a UI de slots ───────────────────────────────────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryUI), "Start")]
        static void AdjustSlotVisuals(InventoryUI __instance)
        {
            int target = BagExpanderPlugin.SlotCount.Value;
            if (target == 3) return;

            Transform root     = ((Component)__instance).transform;
            var uiChildren     = _uiChildrenField?.GetValue(__instance) as List<GameObject>;
            float spacing      = 40f;
            float originX      = -(target * spacing) / 2f + spacing / 2f;

            if (target < 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    Transform node = root.Find($"Inventory Spot {i + 1}");
                    if (node == null) continue;
                    if (i < target)
                        node.localPosition = new Vector3(originX + i * spacing, node.localPosition.y, 0f);
                    else
                    {
                        node.gameObject.SetActive(false);
                        uiChildren?.Remove(node.gameObject);
                    }
                }
                return;
            }

            Transform tpl = root.Find("Inventory Spot 1") ?? (root.childCount > 0 ? root.GetChild(0) : null);
            if (tpl == null) return;

            for (int i = 0; i < target; i++)
            {
                if (i < 3)
                {
                    Transform existing = root.Find($"Inventory Spot {i + 1}");
                    if (existing != null)
                        existing.localPosition = new Vector3(originX + i * spacing, existing.localPosition.y, 0f);
                    continue;
                }

                Transform clone = UnityEngine.Object.Instantiate(tpl, tpl.parent);
                clone.name = $"Inventory Spot {i + 1}";

                var spotComp = clone.GetComponent<InventorySpot>();
                spotComp.inventorySpotIndex = i;

                var numLabel = clone.Find("Numbers")?.GetComponent<TextMeshProUGUI>();
                if (numLabel != null) numLabel.text = (i + 1).ToString();
                if (spotComp.noItem != null) spotComp.noItem.text = (i + 1).ToString();

                clone.localPosition = new Vector3(originX + i * spacing, tpl.localPosition.y, 0f);
                uiChildren?.Add(clone.gameObject);
            }
        }

        // ── 3. Teclas de atalho para slots 4–10 ──────────────────────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventorySpot), "Update")]
        static void ExtraSlotKeyInput(InventorySpot __instance)
        {
            int idx = __instance.inventorySpotIndex;
            if (idx < 3 || idx >= BagExpanderPlugin.SlotCount.Value) return;

            Key key = (idx == 9)
                ? Key.Digit0
                : (Key)Enum.Parse(typeof(Key), $"Digit{idx + 1}");

            if (((ButtonControl)Keyboard.current[key]).wasPressedThisFrame)
                _handleInputMethod.Invoke(__instance, null);
        }

        // ── 4. Registra itens nos slots extras (host/singleplayer) ────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(StatsManager), "PlayerInventoryUpdate")]
        static void TrackExtraEquip(string _steamID, string itemName, int spot)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer() || spot < 3) return;

            if (string.IsNullOrEmpty(itemName))
                EquipTracker.Erase(_steamID, spot);
            else
                EquipTracker.Record(_steamID, spot, itemName.GetHashCode());
        }

        // ── 5. Controla quem pode equipar além do limite ───────────────────────
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemEquippable), "RPC_RequestEquip")]
        static bool CheckSlotLimit(int spotIndex)
        {
            if (!SemiFunc.IsMultiplayer()) return true;
            if (!BagExpanderPlugin.HostControls.Value) return true;

            // HostControls=true: bloqueia qualquer equip além do SlotCount do host
            return spotIndex < BagExpanderPlugin.SlotCount.Value;
        }

        // ── 6. Limpa estado ao voltar ao menu ─────────────────────────────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuOpen), "Start")]
        static void ClearStateOnMenu() => EquipTracker.Reset();

        // ── 7. Restaura itens dos slots extras após troca de rodada ───────────
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PunManager), "SetItemNameLOGIC",
            new Type[] { typeof(string), typeof(int), typeof(ItemAttributes) })]
        static void RestoreExtraSlotItems(
            PunManager __instance, string _name, int photonViewID, ItemAttributes _itemAttributes)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

            ItemAttributes attr = _itemAttributes;
            if (attr == null && SemiFunc.IsMultiplayer())
            {
                var pv = PhotonView.Find(photonViewID);
                if (pv == null) return;
                attr = pv.GetComponent<ItemAttributes>();
            }
            if (attr == null) return;

            var equippable = attr.GetComponent<ItemEquippable>();
            if (equippable == null) return;

            var players = SemiFunc.PlayerGetList();
            if (!EquipTracker.Lookup(_name.GetHashCode(), out string sid, out int slotIdx, players))
                return;

            if (!BagExpanderPlugin.LeaveInBase.Value)
            {
                int viewId = -1;
                if (SemiFunc.IsMultiplayer())
                {
                    var owner = players.FirstOrDefault(p => SemiFunc.PlayerGetSteamID(p) == sid);
                    if (owner != null) viewId = owner.photonView.ViewID;
                }
                equippable.RequestEquip(slotIdx, viewId);
            }

            EquipTracker.Erase(sid, slotIdx);
        }
    }
}
