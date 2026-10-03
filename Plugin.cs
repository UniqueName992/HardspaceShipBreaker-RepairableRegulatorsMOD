using System;
using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using BepInEx;
using BepInEx.Configuration;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace RepairableRegulators
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "shipbreaker.repairableregulators";
        public const string PluginName = "Repairable Regulators";
        public const string PluginVersion = "1.1.0";

        // Lets PurchaseRepairPack (a separate class below, instantiated as the shop
        // item's purchase-action prefab - see TryAddRepairPackToShop) call back into the
        // running plugin instance without needing its own reference wired through.
        public static Plugin Instance { get; private set; }

        private enum RegulatorState
        {
            Pressurized,
            Unpressurized,
            Damaged,
            Broken
        }

        private class RegulatorEntry
        {
            public Renderer[] Renderers;
            public string DisplayName;
            public EntityBlueprintComponent MeshEntityBlueprint;
            public RegulatorState? LastAppliedState;
            public Rigidbody InitialRigidbody;

            // True once this regulator has been seen with a live, functional RoomProbe.
            // Distinguishes "used to work, marker got destroyed" (Broken/red) from "never
            // had a RoomProbe at all" (a non-functional spawned-in-damaged variant -
            // Damaged/orange, see docs/TECHNICAL.md).
            public bool EverHadLiveProbe;

            // Set only when this regulator's RoomProbe was grafted on by a repair, rather
            // than being a real one the ship spawned with. A real regulator's probe is
            // tied to its host StructurePart's own connectivity/lifecycle (see docs/TECHNICAL.md's
            // Known Issues); a grafted one isn't tied to anything, so it has to be watched
            // and torn down by hand once its host is cut loose from the ship - see
            // CheckGraftedProbesStillConnected.
            public RoomProbe GraftedProbe;
            public Rigidbody ShipRigidbodyAtGraftTime;

            // Every top-level object a repair grafted onto this regulator's mesh root -
            // the RoomProbe itself, but also its button, animator, and supporting FX/audio
            // objects (see TryCloneWorkingRegulatorOnto). Destroying only GraftedProbe on
            // disconnect left the button collider behind, fully interactable, just doing
            // nothing - this list is what lets CheckGraftedProbesStillConnected clean up
            // everything at once instead.
            public readonly List<GameObject> GraftedObjects = new List<GameObject>();

            // Last-seen values of the two connectivity signals, purely so
            // CheckGraftedProbesStillConnected can log only on a change instead of every
            // couple of seconds - useful for confirming the heuristic actually tracks
            // real disconnection correctly without drowning the log.
            public bool? LastObservedPartOfGroup;
            public bool? LastObservedSameRigidbody;
        }

        // Hardspace Shipbreaker renders regulators with HDRP/Lit materials, whose main
        // tint lives in "_BaseColor" (see BBI.Unity.Game.SpaceTruck3DHABController for
        // the same pattern). "_Color" is set too as a harmless fallback for any
        // non-HDRP shader that might slip in.
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        // RoomProbe.IsAtmosphereRegulator has no public setter - it's a design-time flag
        // baked into the prefab. A freshly AddComponent<RoomProbe>()'d instance has no way
        // to become an atmosphere regulator through public API alone, so repairing a
        // Damaged regulator sets this private backing field via reflection instead.
        private static readonly FieldInfo IsAtmosphereRegulatorField =
            typeof(RoomProbe).GetField("m_IsAtmosphereRegulator", BindingFlags.NonPublic | BindingFlags.Instance);

        // Individual regulators have no button of their own - the ship's air-pressure
        // toggle control (ECUController) builds its list of RoomProbes exactly once,
        // a few fixed-updates after ship spawn (see ECUController.RegisterRoomProbes in
        // the decompiled source), by walking the ship's rigidbody hierarchy for every
        // IsAtmosphereRegulator probe that exists *at that moment*. A probe added later -
        // like a repaired one - pressurizes/depressurizes on its own just fine, but the
        // ECU never learns about it and so its toggle never includes it. There's no public
        // API to add to that list after the fact, so this reaches the private backing
        // field directly.
        private static readonly FieldInfo EcuProbesField =
            typeof(ECUController).GetField("mProbes", BindingFlags.NonPublic | BindingFlags.Instance);

        // EntityBlueprintComponent.Initialize() has two very different code paths: if its
        // serialized mLastKnownEntityIndex/mLastKnownEntityVersion are non-zero, it treats
        // itself as a *reconnecting* component and literally clones whichever ECS entity
        // that index/version pair pointed to (EntityManager.Instantiate(entity)) - carrying
        // over all of THAT entity's current data (e.g. a RoomProbeComponent already
        // pointing at the donor's own room). If they're zero, it instead creates a brand
        // new, empty entity from scratch. Object.Instantiate() copies serialized fields
        // faithfully, so cloning a GameObject that's already been initialized this session
        // (as any currently-working regulator has) would copy its already-nonzero index/
        // version onto the clone too, sending the clone down the wrong ("reconnect to the
        // donor's entity") path. Zeroing these two fields on the donor immediately before
        // Instantiate - then restoring them right after, since Awake() runs synchronously
        // during Instantiate itself - forces every EntityBlueprintComponent in the cloned
        // subtree down the correct "fresh entity" path instead, without disturbing the
        // donor's own already-running entity at all (Initialized/EntityManager/mEntity
        // aren't [SerializeField], so they're never copied by Instantiate regardless).
        private static readonly FieldInfo EbcLastKnownEntityIndexField =
            typeof(EntityBlueprintComponent).GetField("mLastKnownEntityIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo EbcLastKnownEntityVersionField =
            typeof(EntityBlueprintComponent).GetField("mLastKnownEntityVersion", BindingFlags.NonPublic | BindingFlags.Instance);

        // VendingMachineUIController builds its whole item list once, in Awake(), from a
        // private m_StoreTabs array configured per-machine in the editor - there's no
        // public API to add a new item after the fact. These reach the private pieces
        // needed to do it anyway: the tabs array itself, the private nested StoreTab
        // type's (otherwise public) members, the private AddItem method that does the
        // actual instantiate-and-wire-up work for a single item, and VendingMachineItemData's
        // privately-backed fields (Name/Description/Currency/Cost/OnPurchaseAction all
        // have getters but no setters). See TryAddRepairPackToShop.
        private static readonly FieldInfo VendingStoreTabsField =
            typeof(VendingMachineUIController).GetField("m_StoreTabs", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo VendingAddItemMethod =
            typeof(VendingMachineUIController).GetMethod("AddItem", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type VendingStoreTabType =
            typeof(VendingMachineUIController).GetNestedType("StoreTab", BindingFlags.NonPublic);
        private static readonly PropertyInfo VendingStoreTabItemListProperty =
            VendingStoreTabType?.GetProperty("ItemList", BindingFlags.Public | BindingFlags.Instance);
        private static readonly PropertyInfo VendingStoreTabContainerProperty =
            VendingStoreTabType?.GetProperty("ContainerTransform", BindingFlags.Public | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataNameField =
            typeof(VendingMachineItemData).GetField("m_Name", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataDescriptionField =
            typeof(VendingMachineItemData).GetField("m_Description", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataCurrencyField =
            typeof(VendingMachineItemData).GetField("m_Currency", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataCostField =
            typeof(VendingMachineItemData).GetField("m_Cost", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataOnPurchaseActionField =
            typeof(VendingMachineItemData).GetField("m_OnPurchaseAction", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataPreviewTextureField =
            typeof(VendingMachineItemData).GetField("m_PreviewTexture", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataPreviewMeshField =
            typeof(VendingMachineItemData).GetField("m_PreviewMesh", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataPreviewScaleField =
            typeof(VendingMachineItemData).GetField("m_PreviewScale", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo VendingItemDataPreviewOrientationField =
            typeof(VendingMachineItemData).GetField("m_PreviewOrientation", BindingFlags.NonPublic | BindingFlags.Instance);

        private ConfigEntry<string> _pressurizedColorHex;
        private ConfigEntry<string> _unpressurizedColorHex;
        private ConfigEntry<string> _damagedColorHex;
        private ConfigEntry<string> _brokenColorHex;
        private ConfigEntry<bool> _enableColoring;
        private ConfigEntry<bool> _enableDiagnostics;
        private ConfigEntry<float> _rescanIntervalSeconds;
        private ConfigEntry<KeyCode> _repairKey;
        private ConfigEntry<float> _repairRangeMeters;
        private ConfigEntry<float> _repairMatchRadiusMeters;
        private ConfigEntry<bool> _showRepairPrompt;
        private ConfigEntry<float> _repairDurationSeconds;
        private ConfigEntry<float> _repairPackCostCredits;

        // Which regulator (if any) is currently mid-hold, and how far into
        // RepairDurationSeconds it's gotten. Reset to null/0 the instant the player looks
        // away, moves out of range, switches to a different regulator, or releases
        // RepairKey early - see UpdateRepair.
        private Transform _repairProgressMeshRoot;
        private float _repairProgressSeconds;

        // The specific VendingMachineUIController instance TryAddRepairPackToShop has
        // already added the item to - a new shift spawns a new instance (the old one's
        // destroyed with its scene), so this needs to be re-checked rather than a simple
        // "did it ever happen" bool.
        private VendingMachineUIController _patchedVendingMachine;

        // Backed by the config file rather than a real inventory item (see ROADMAP.md) -
        // there's no in-game persistence hook to hang a consumable off, so this piggybacks
        // on BepInEx's own config save/load instead, which happens to get persistence
        // across restarts for free. UpdateRepair requires at least one to start a hold,
        // and consumes one on a successful repair.
        private ConfigEntry<int> _repairPackCount;

        // Rollback point for _repairPackCount.Value, so abandoning a shift (quitting back
        // to the front end before ever reaching the Hab) doesn't let kits bought mid-shift
        // stick around. Moved forward to the current count every time the Hab is reached -
        // the game's own "shift complete, safe to bank" checkpoint (PreTearDown only saves
        // the player's profile when leaving a Career session from the Hab, never mid-shift) -
        // so only what happens during the shift *after* that point is ever at risk of being
        // reverted. See UpdateRepairPackPersistenceTracking.
        private int _repairPackBaselineBeforeShift;
        private bool _repairPackTrackingShift;
        private GameSession.SessionType _lastRepairPackSessionType = GameSession.SessionType.None;

        // Free Play and Cutter's Race (Challenge) have no meaningful persistent economy to
        // gate repairing behind, so both just get a flat, non-persisted allotment instead -
        // see EffectiveRepairPackCount/IsUnlimitedRepairPackSession.
        private const int UnlimitedRepairPackDisplayCount = 100;

        // See LogGameStateChanges - tracks the last value logged so it only logs on an
        // actual change, not every frame.
        private GameSession.GameState _lastLoggedGameState = GameSession.GameState.None;

        // Local offset of a RoomProbe under its mesh root, sampled from the first live
        // regulator seen. Every atmosphere regulator mesh variant shares the same
        // footprint, so this is a good placement for a newly repaired probe too - reused
        // instead of guessing Vector3.zero so the new probe sits exactly where the
        // original one would have.
        private Vector3? _templateProbeLocalPosition;
        private Quaternion? _templateProbeLocalRotation;

        private Color _pressurizedColor;
        private Color _unpressurizedColor;
        private Color _damagedColor;
        private Color _brokenColor;
        private MaterialPropertyBlock _propertyBlock;

        // Repair prompt state/assets - visibility set once per frame by UpdateRepair,
        // drawn by OnGUI in the game's own circled-key-plus-panel style (see "Why the
        // prompt isn't drawn through the game's own UI" in docs/TECHNICAL.md).
        private bool _repairPromptVisible;
        private bool _repairPromptNeedsPack;
        private Texture2D _promptBackgroundTexture;
        private Texture2D _keyCircleTexture;
        private Texture2D _progressFillTexture;
        private Font _promptFont;

        // Approximations of the game's own HUD palette from the reference screenshot -
        // off-white for the verb/object name, amber for the key circle and the
        // highlighted effect line.
        private static readonly Color PromptTextColor = new Color(0.90f, 0.90f, 0.90f);
        private static readonly Color PromptAccentColor = new Color32(0xE8, 0xA6, 0x3C, 0xFF);
        private static readonly Color PromptDisabledColor = new Color(0.55f, 0.55f, 0.55f);

        // Keyed by the regulator mesh root (the RoomProbe's parent - see "Why the
        // parent?" in docs/TECHNICAL.md). Kept across scans so we can notice a regulator
        // whose RoomProbe marker has disappeared (broken) while its mesh is still
        // there to paint, and so we only touch renderers when the state actually
        // changes instead of every rescan.
        private readonly Dictionary<Transform, RegulatorEntry> _regulators = new Dictionary<Transform, RegulatorEntry>();

        private void Awake()
        {
            Instance = this;

            _pressurizedColorHex = Config.Bind(
                "Appearance",
                "PressurizedColor",
                "#2ECC71",
                "Hex color for atmosphere regulators in a pressurized room.");

            _unpressurizedColorHex = Config.Bind(
                "Appearance",
                "UnpressurizedColor",
                "#3498DB",
                "Hex color for atmosphere regulators in a room that is not fully pressurized (decompressed, venting, breaching, etc.).");

            _damagedColorHex = Config.Bind(
                "Appearance",
                "DamagedColor",
                "#F1C40F",
                "Hex color for atmosphere regulators whose object integrity is below max (including ones that spawn in already damaged) but are not yet destroyed. Takes priority over the pressurized/unpressurized colors.");

            _brokenColorHex = Config.Bind(
                "Appearance",
                "BrokenColor",
                "#E74C3C",
                "Hex color for atmosphere regulators that have been destroyed: the regulator's RoomProbe marker is gone but its mesh is still present.");

            _enableColoring = Config.Bind(
                "Appearance",
                "EnableColoring",
                false,
                "Tint regulators by status at all. Off by default for a clean release build. " +
                "Repairing still works correctly either way - this only ever affects the renderer tint itself, " +
                "never the underlying state tracking repair depends on.");

            _enableDiagnostics = Config.Bind(
                "Behavior",
                "EnableDiagnostics",
                false,
                "Enables the F9 diagnostics dump and other [Diag]-tagged debug logging (connectivity dumps, GameState change log). " +
                "Off by default for a clean release build - turn on when troubleshooting.");

            _rescanIntervalSeconds = Config.Bind(
                "Behavior",
                "RescanIntervalSeconds",
                2f,
                "How often (in seconds) to rescan regulators for new/changed/broken state.");

            _repairKey = Config.Bind(
                "Behavior",
                "RepairKey",
                KeyCode.F6,
                "Key to hold to repair a Damaged (yellow) atmosphere regulator you're looking directly at, from short range. " +
                "Defaults to F6 (unbound in the base game) rather than F, since F is already used for the flashlight - " +
                "change this once you've picked a key that doesn't collide with your own bindings.");

            _repairDurationSeconds = Config.Bind(
                "Behavior",
                "RepairDurationSeconds",
                3f,
                "How long (in seconds) RepairKey must be held down, while looking at a Damaged regulator in range, to repair it. " +
                "Progress resets to zero if you look away, move out of range, or release the key before it completes.");

            _repairRangeMeters = Config.Bind(
                "Behavior",
                "RepairRangeMeters",
                1.5f,
                "Max distance (in meters) to a Damaged regulator you're looking at for RepairKey to work - matches the game's own default interaction range.");

            _repairMatchRadiusMeters = Config.Bind(
                "Behavior",
                "RepairMatchRadiusMeters",
                0.75f,
                "How close the raycast hit point must be to a regulator's own mesh to count as 'looking at' it. " +
                "Regulators have no collider of their own - the raycast actually lands on the ship's combined hull mesh - " +
                "so this is a proximity match against the regulator's renderer bounds rather than an exact hit.");

            _showRepairPrompt = Config.Bind(
                "Behavior",
                "ShowRepairPrompt",
                true,
                "Show a 'Press <key> to Repair' prompt on screen while looking directly at a Damaged (yellow) regulator in range - mirrors the game's own look-at-it interaction prompts.");

            _repairPackCostCredits = Config.Bind(
                "Shop",
                "RepairPackCostCredits",
                200000f,
                "Price (in the same currency as the rest of the shop - normally Credits) of the 'Air Reg Kit' item added to the ship's vending machine. " +
                "Doesn't gate repairing yet - see ROADMAP.md.");

            _repairPackCount = Config.Bind(
                "Shop",
                "OwnedAirRegKits",
                0,
                "How many Air Reg Kits you currently own - saved here so it survives a game restart. " +
                "Updated automatically as you buy and spend them; edit it by hand only if you want to grant or reset your stock.");

            _promptBackgroundTexture = MakeSolidTexture(new Color(0.03f, 0.04f, 0.05f, 0.85f));
            // Baked white rather than amber, so it can be GUI.color-tinted per frame
            // (amber normally, gray when a repair is blocked on owning an Air Reg Kit)
            // instead of needing a second pre-baked texture for the disabled look.
            _keyCircleTexture = MakeRingTexture(64, 3f, Color.white);
            _progressFillTexture = MakeSolidTexture(PromptAccentColor);
            try
            {
                _promptFont = Font.CreateDynamicFontFromOSFont("Consolas", 18);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Couldn't load Consolas for the repair prompt, falling back to the default font: {ex.Message}");
            }

            _propertyBlock = new MaterialPropertyBlock();
            ApplyColorsFromConfig();

            void OnColorSettingChanged(object sender, System.EventArgs args)
            {
                ApplyColorsFromConfig();
                // Force every tracked regulator to be repainted on the next scan,
                // even though its logical state (pressurized/broken/etc.) hasn't changed.
                foreach (RegulatorEntry entry in _regulators.Values)
                {
                    entry.LastAppliedState = null;
                }
            }

            _pressurizedColorHex.SettingChanged += OnColorSettingChanged;
            _unpressurizedColorHex.SettingChanged += OnColorSettingChanged;
            _damagedColorHex.SettingChanged += OnColorSettingChanged;
            _brokenColorHex.SettingChanged += OnColorSettingChanged;

            float interval = Mathf.Max(0.5f, _rescanIntervalSeconds.Value);
            InvokeRepeating(nameof(ScanAndColorRegulators), 0f, interval);

            Logger.LogInfo($"{PluginName} loaded. Pressurized={_pressurizedColorHex.Value}, Unpressurized={_unpressurizedColorHex.Value}, Damaged={_damagedColorHex.Value}, Broken={_brokenColorHex.Value}.");
        }

        private void ApplyColorsFromConfig()
        {
            _pressurizedColor = ParseColorOrFallback(_pressurizedColorHex.Value, new Color(0.18f, 0.80f, 0.44f));
            _unpressurizedColor = ParseColorOrFallback(_unpressurizedColorHex.Value, new Color(0.20f, 0.60f, 0.86f));
            _damagedColor = ParseColorOrFallback(_damagedColorHex.Value, new Color(0.95f, 0.77f, 0.06f));
            _brokenColor = ParseColorOrFallback(_brokenColorHex.Value, new Color(0.91f, 0.30f, 0.24f));
        }

        private Color ParseColorOrFallback(string hex, Color fallback)
        {
            if (ColorUtility.TryParseHtmlString(hex, out Color parsed))
            {
                return parsed;
            }

            Logger.LogWarning($"Could not parse '{hex}' as a color, falling back to default.");
            return fallback;
        }

        private void ScanAndColorRegulators()
        {
            CheckGraftedProbesStillConnected();
            TryAddRepairPackToShop();

            var liveMeshRoots = new HashSet<Transform>();

            // includeInactive: true - a regulator that spawns in already damaged/broken
            // can start life with its RoomProbe (or an ancestor) inactive, and the
            // no-arg overload of FindObjectsOfType silently skips inactive objects.
            RoomProbe[] probes = FindObjectsOfType<RoomProbe>(true);
            foreach (RoomProbe probe in probes)
            {
                if (!probe.IsAtmosphereRegulator)
                {
                    continue;
                }

                Transform meshRoot = probe.transform.parent != null ? probe.transform.parent : probe.transform;
                liveMeshRoots.Add(meshRoot);

                RegulatorEntry entry = GetOrCreateEntry(meshRoot);
                entry.EverHadLiveProbe = true;

                if (_templateProbeLocalPosition == null)
                {
                    _templateProbeLocalPosition = probe.transform.localPosition;
                    _templateProbeLocalRotation = probe.transform.localRotation;
                }

                // Don't permanently cache a failed lookup: for a regulator that starts
                // inactive/pre-damaged, the entity blueprint may not exist yet the first
                // time we see it, so keep retrying on later scans until it resolves.
                if (entry.MeshEntityBlueprint == null)
                {
                    EntityBlueprintComponent.TryGetEntityBlueprint(meshRoot.gameObject, out EntityBlueprintComponent meshEbc);
                    entry.MeshEntityBlueprint = meshEbc;
                }

                RegulatorState state = IsDamaged(entry) ? RegulatorState.Damaged : GetPressurizationState(probe);
                ApplyStateIfChanged(entry, state);
            }

            // Some regulators spawn in already non-functional - no RoomProbe anywhere
            // under them at all, ever (confirmed via the F9 diagnostics: on a ship with
            // 6 atmosphere regulator meshes, 3 had a working RoomProbe and 3 didn't -
            // those 3 were the ones that looked pre-damaged in game). There's no
            // component to key off for these, so fall back to matching the mesh name
            // and treat "regulator-looking mesh with nobody home" as Damaged.
            Transform[] allTransforms = FindObjectsOfType<Transform>(true);
            foreach (Transform t in allTransforms)
            {
                if (liveMeshRoots.Contains(t) || !LooksLikeRegulatorMesh(t.name))
                {
                    continue;
                }

                if (t.GetComponentInChildren<RoomProbe>(true) != null)
                {
                    // Has a probe somewhere under it; the primary pass above already
                    // handles it (or it's momentarily non-functional - see Broken below).
                    continue;
                }

                RegulatorEntry entry = GetOrCreateEntry(t);

                if (entry.EverHadLiveProbe)
                {
                    // Used to be functional and now has no probe at all - that's a
                    // destroyed marker, not a spawned-in-damaged variant. Don't mark it
                    // "handled" here - leave it out of liveMeshRoots so the Broken sweep
                    // below actually sees it and colors it red.
                    continue;
                }

                liveMeshRoots.Add(t);
                ApplyStateIfChanged(entry, RegulatorState.Damaged);
            }

            // Anything we were tracking that had a live RoomProbe before but doesn't
            // now either had its marker destroyed (the regulator broke - see docs/TECHNICAL.md)
            // while its mesh survived, or its whole mesh is gone too (ship despawned).
            List<Transform> toForget = null;
            foreach (KeyValuePair<Transform, RegulatorEntry> kvp in _regulators)
            {
                if (liveMeshRoots.Contains(kvp.Key))
                {
                    continue;
                }

                if (kvp.Key == null)
                {
                    (toForget ??= new List<Transform>()).Add(kvp.Key);
                    continue;
                }

                if (!kvp.Value.EverHadLiveProbe)
                {
                    continue;
                }

                if (kvp.Value.LastAppliedState != RegulatorState.Broken)
                {
                    LogBrokenTransitionDiagnostics(kvp.Key, kvp.Value);
                }

                ApplyStateIfChanged(kvp.Value, RegulatorState.Broken);
            }

            if (toForget != null)
            {
                foreach (Transform meshRoot in toForget)
                {
                    _regulators.Remove(meshRoot);
                }
            }
        }

        // Fires once, right as a tracked regulator is first about to flip to Broken -
        // specifically to catch the case where that regulator was *just* repaired (a
        // RoomProbe grafted onto it moments ago) and something removed it again before
        // the next scan, which would otherwise just look like an unexplained "went red"
        // with no indication of why. Checks the mesh root directly (bypassing the
        // FindObjectsOfType<RoomProbe> pass this scan already did) so it can tell apart
        // "genuinely gone" from "still there, but something about it changed."
        private void LogBrokenTransitionDiagnostics(Transform meshRoot, RegulatorEntry entry)
        {
            if (meshRoot == null)
            {
                return;
            }

            RoomProbe probe = meshRoot.GetComponentInChildren<RoomProbe>(true);
            string probeInfo = probe == null
                ? "no RoomProbe found under the mesh root at all (directly, bypassing this scan's own FindObjectsOfType pass)"
                : $"RoomProbe STILL PRESENT at '{GetHierarchyPath(probe.transform)}' (activeInHierarchy={probe.gameObject.activeInHierarchy}, IsAtmosphereRegulator={probe.IsAtmosphereRegulator}) - FindObjectsOfType<RoomProbe> somehow didn't return it this scan";

            int childCount = meshRoot.childCount;
            var childNames = new string[childCount];
            for (int i = 0; i < childCount; i++)
            {
                Transform child = meshRoot.GetChild(i);
                childNames[i] = $"{child.name}(active={child.gameObject.activeSelf})";
            }

            Logger.LogWarning($"[Repair] '{entry.DisplayName}' is about to flip to Broken. {probeInfo}. Mesh root activeInHierarchy={meshRoot.gameObject.activeInHierarchy}, children=[{string.Join(", ", childNames)}].");
        }

        private RegulatorEntry GetOrCreateEntry(Transform meshRoot)
        {
            if (!_regulators.TryGetValue(meshRoot, out RegulatorEntry entry))
            {
                entry = new RegulatorEntry
                {
                    Renderers = meshRoot.GetComponentsInChildren<Renderer>(true),
                    DisplayName = meshRoot.name,
                    // Captured once, the very first time this regulator is ever seen -
                    // the baseline "still attached to whatever it started on" reference,
                    // independent of repair state. See TryFindRepairableRegulatorLookedAt:
                    // a Damaged regulator that's since been torn loose by the environment
                    // (never repaired, so CheckGraftedProbesStillConnected never looks at
                    // it at all) would otherwise go on reading as "Damaged" and
                    // "repairable" forever, since nothing else ever changes its color
                    // state once it has no RoomProbe to begin with.
                    InitialRigidbody = meshRoot.GetComponent<StructurePart>()?.VirtualRigidbody?.UnityRigidbody
                };
                _regulators[meshRoot] = entry;
            }
            return entry;
        }

        // Matches the mesh root (e.g. "SM_AtmosphereRegulator_Mini_1Bx1xA1_Box"), while
        // excluding its interaction-button sibling ("..._Button") and its StructureGroup
        // sibling ("GRP_...") - both share the "AtmosphereRegulator" substring but aren't
        // the paintable mesh itself.
        private static bool LooksLikeRegulatorMesh(string name)
        {
            return name.IndexOf("AtmosphereRegulator", StringComparison.OrdinalIgnoreCase) >= 0
                && name.IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0
                && !name.StartsWith("GRP_", StringComparison.OrdinalIgnoreCase);
        }

        private RegulatorState GetPressurizationState(RoomProbe probe)
        {
            if (World.DefaultGameObjectInjectionWorld == null)
            {
                return RegulatorState.Unpressurized;
            }

            Entity roomEntity = probe.RoomEntity;
            if (roomEntity == Entity.Null)
            {
                return RegulatorState.Unpressurized;
            }

            EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!entityManager.Exists(roomEntity) || !entityManager.TryGetComponent(roomEntity, out RoomComponent room))
            {
                return RegulatorState.Unpressurized;
            }

            return room.AirPressureState == RoomPressurization.Pressurized
                ? RegulatorState.Pressurized
                : RegulatorState.Unpressurized;
        }

        // Confirmed via the F9 diagnostics: ObjectIntegrityComponent does NOT live on the
        // regulator mesh's own entity - it lives on the entity of its StructureGroup
        // sibling ("GRP_AtmosphereRegulator_..."), reached via the mesh entity's
        // StructureGroupMember.GroupEntity. A regulator that gets damaged during play
        // without being destroyed outright shows up there as Value < ValueMax.
        private bool IsDamaged(RegulatorEntry entry)
        {
            EntityBlueprintComponent ebc = entry.MeshEntityBlueprint;
            if (ebc == null || World.DefaultGameObjectInjectionWorld == null)
            {
                return false;
            }

            EntityManager entityManager = ebc.EntityManager;
            if (!EntityBlueprintComponent.IsValid(entityManager, ebc.Entity))
            {
                return false;
            }

            Entity integrityEntity = ebc.Entity;
            if (entityManager.TryGetComponent(ebc.Entity, out StructureGroupMember groupMember) && entityManager.Exists(groupMember.GroupEntity))
            {
                integrityEntity = groupMember.GroupEntity;
            }

            if (!entityManager.TryGetComponent(integrityEntity, out ObjectIntegrityComponent integrity))
            {
                return false;
            }

            return integrity.Value < integrity.ValueMax;
        }

        private void ApplyStateIfChanged(RegulatorEntry entry, RegulatorState state)
        {
            if (entry.LastAppliedState == state)
            {
                return;
            }

            // entry.LastAppliedState is load-bearing for the repair feature - it's what
            // TryFindRepairableRegulatorLookedAt checks to decide whether a regulator is
            // Damaged (and therefore repairable) at all. That tracking has to keep
            // happening regardless of Appearance.EnableColoring, or disabling the paint
            // job would silently disable repairing too - only the renderer tinting itself
            // (and its log line) is actually optional.
            if (_enableColoring.Value)
            {
                Color color = state switch
                {
                    RegulatorState.Pressurized => _pressurizedColor,
                    RegulatorState.Damaged => _damagedColor,
                    RegulatorState.Broken => _brokenColor,
                    _ => _unpressurizedColor,
                };

                foreach (Renderer renderer in entry.Renderers)
                {
                    if (renderer == null)
                    {
                        continue;
                    }

                    Material material = renderer.sharedMaterial;
                    if (material == null)
                    {
                        continue;
                    }

                    bool hasBaseColor = material.HasProperty(BaseColorId);
                    bool hasColor = material.HasProperty(ColorId);
                    if (!hasBaseColor && !hasColor)
                    {
                        continue;
                    }

                    renderer.GetPropertyBlock(_propertyBlock);
                    if (hasBaseColor)
                    {
                        _propertyBlock.SetColor(BaseColorId, color);
                    }
                    if (hasColor)
                    {
                        _propertyBlock.SetColor(ColorId, color);
                    }
                    renderer.SetPropertyBlock(_propertyBlock);
                }

                Logger.LogInfo($"Regulator '{entry.DisplayName}' is now {state} -> painted {ColorUtility.ToHtmlStringRGB(color)}.");
            }

            entry.LastAppliedState = state;
        }

        // Diagnostics: press F9 to dump everything in the scene with "Regulator" in its
        // name, whether or not it has a RoomProbe, so a regulator variant that isn't
        // being detected/colored (e.g. a pre-damaged wall spawn with a visibly different
        // mesh) can be inspected via BepInEx/LogOutput.log without a special debug build.
        private void Update()
        {
            if (_enableDiagnostics.Value)
            {
                if (Input.GetKeyDown(KeyCode.F9))
                {
                    DumpRegulatorDiagnostics();
                }

                LogGameStateChanges();
            }

            UpdateRepairPackPersistenceTracking();
            UpdateRepair();
        }

        // Runs every frame: figures out whether the player is currently looking at a
        // repairable (Damaged, in range) regulator, drives the hold-to-repair timer
        // against RepairKey, and sets the on-screen prompt's visibility - all from one
        // shared lookup, so the prompt and the actual repair logic can never disagree
        // about what "looking at a regulator" means.
        //
        // Progress is tied to a specific regulator's Transform: looking away, moving out
        // of range, or switching to a different Damaged regulator all reset it to zero,
        // same as releasing the key early does. Logs only on meaningful transitions
        // (started holding, released early, completed, switched target) rather than
        // every frame, to avoid flooding the log the way a per-frame message would.
        private void UpdateRepair()
        {
            bool lookingAtRepairable = TryFindRepairableRegulatorLookedAt(out Transform meshRoot, out RegulatorEntry entry);

            if (meshRoot != _repairProgressMeshRoot)
            {
                if (_repairProgressSeconds > 0f)
                {
                    Logger.LogInfo($"[Repair] Progress reset at {_repairProgressSeconds:F1}s/{_repairDurationSeconds.Value:F1}s - no longer looking at the same regulator.");
                }

                _repairProgressMeshRoot = meshRoot;
                _repairProgressSeconds = 0f;
            }

            bool hasPack = EffectiveRepairPackCount > 0;

            if (lookingAtRepairable && hasPack)
            {
                if (Input.GetKey(_repairKey.Value))
                {
                    if (_repairProgressSeconds <= 0f)
                    {
                        Logger.LogInfo($"[Repair] Started repairing '{entry.DisplayName}' - hold {_repairKey.Value} for {_repairDurationSeconds.Value:F1}s.");
                    }

                    _repairProgressSeconds += Time.deltaTime;

                    if (_repairProgressSeconds >= _repairDurationSeconds.Value)
                    {
                        RepairRegulator(meshRoot, entry);

                        // Free Play/Cutter's Race run on the flat EffectiveRepairPackCount
                        // allotment, not the real saved stock - nothing to spend there.
                        if (!IsUnlimitedRepairPackSession(GameSession.CurrentSessionType))
                        {
                            _repairPackCount.Value--;
                        }
                        Logger.LogInfo($"[Shop] Consumed an Air Reg Kit repairing '{entry.DisplayName}' - {EffectiveRepairPackCount} left.");

                        _repairProgressMeshRoot = null;
                        _repairProgressSeconds = 0f;
                        lookingAtRepairable = false;
                    }
                }
                else if (_repairProgressSeconds > 0f)
                {
                    Logger.LogInfo($"[Repair] {_repairKey.Value} released for '{entry.DisplayName}' at {_repairProgressSeconds:F1}s/{_repairDurationSeconds.Value:F1}s - progress reset.");
                    _repairProgressSeconds = 0f;
                }
            }
            else if (lookingAtRepairable && !hasPack && Input.GetKeyDown(_repairKey.Value))
            {
                Logger.LogInfo($"[Repair] Tried to repair '{entry.DisplayName}' but have no Air Reg Kits.");
            }

            _repairPromptVisible = lookingAtRepairable && _showRepairPrompt.Value;
            _repairPromptNeedsPack = lookingAtRepairable && !hasPack;
        }

        // Mirrors the game's own short-range "look directly at it" style of interaction
        // (InteractionController.m_MaxInteractionDistance is 1.5m by default - see
        // RepairRangeMeters). Used by both UpdateRepair's timer and the on-screen prompt,
        // so they always agree on what counts as "looking at a repairable regulator."
        private bool TryFindRepairableRegulatorLookedAt(out Transform meshRoot, out RegulatorEntry entry)
        {
            meshRoot = null;
            entry = null;

            if (GameSession.CurrentGameState != GameSession.GameState.Gameplay)
            {
                return false;
            }

            Transform cameraTransform = LynxCameraController.MainCameraTransform;
            if (cameraTransform == null)
            {
                return false;
            }

            if (!Physics.Raycast(cameraTransform.position, cameraTransform.forward, out RaycastHit hit, _repairRangeMeters.Value))
            {
                return false;
            }

            if (!TryFindClosestTrackedRegulator(hit.point, out Transform closestMeshRoot, out RegulatorEntry closestEntry, out float distance)
                || distance > _repairMatchRadiusMeters.Value
                || closestEntry.LastAppliedState != RegulatorState.Damaged)
            {
                return false;
            }

            // A Damaged regulator that's since been torn loose by the environment (never
            // repaired, so it has no RoomProbe and CheckGraftedProbesStillConnected never
            // looks at it) has nothing else that would ever change its color state away
            // from Damaged - it would otherwise go on reading as "repairable" forever
            // while floating free. Same rigidbody-identity check used to detect an
            // already-repaired regulator being cut loose, just applied as a precondition
            // for repair eligibility instead of a post-repair teardown trigger.
            StructurePart structurePart = closestMeshRoot.GetComponent<StructurePart>();
            Rigidbody currentRigidbody = structurePart?.VirtualRigidbody?.UnityRigidbody;
            if (structurePart == null || currentRigidbody == null || currentRigidbody != closestEntry.InitialRigidbody)
            {
                return false;
            }

            meshRoot = closestMeshRoot;
            entry = closestEntry;
            return true;
        }

        // Styled after the game's own interaction prompt (a circled key icon beside a
        // dark panel of stacked lines - verb, then object name, then the effect picked
        // out in amber) rather than the plain single-line label this started as. See
        // "Why the prompt isn't drawn through the game's own UI" in docs/TECHNICAL.md for why
        // this is a hand-drawn OnGUI reproduction of that look instead of the real thing.
        private void OnGUI()
        {
            DrawRepairPackCounter();

            if (!_repairPromptVisible || _promptBackgroundTexture == null || _keyCircleTexture == null || _progressFillTexture == null)
            {
                return;
            }

            GUIStyle whiteLine = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 18,
                font = _promptFont
            };
            whiteLine.normal.textColor = PromptTextColor;

            GUIStyle amberLine = new GUIStyle(whiteLine);
            amberLine.normal.textColor = _repairPromptNeedsPack ? PromptDisabledColor : PromptAccentColor;

            bool holding = _repairProgressSeconds > 0f;
            var verbContent = new GUIContent(holding ? "Repairing" : "Repair");
            var nameContent = new GUIContent("Atmosphere Regulator");
            var effectContent = new GUIContent(_repairPromptNeedsPack ? "Requires Air Reg Kit" : "Restore Functionality");

            Vector2 verbSize = whiteLine.CalcSize(verbContent);
            Vector2 nameSize = whiteLine.CalcSize(nameContent);
            Vector2 effectSize = amberLine.CalcSize(effectContent);

            float lineHeight = Mathf.Max(verbSize.y, Mathf.Max(nameSize.y, effectSize.y));
            float textWidth = Mathf.Max(verbSize.x, Mathf.Max(nameSize.x, effectSize.x));

            const float paddingX = 20f;
            const float paddingY = 14f;
            const float circleSize = 44f;
            const float gap = 14f;

            float panelWidth = textWidth + paddingX * 2f;
            float panelHeight = lineHeight * 3f + paddingY * 2f;
            float totalHeight = Mathf.Max(circleSize, panelHeight);

            float originX = Screen.width * 0.56f;
            float originY = Screen.height * 0.42f - totalHeight / 2f;

            var circleRect = new Rect(originX, originY + (totalHeight - circleSize) / 2f, circleSize, circleSize);
            var panelRect = new Rect(circleRect.xMax + gap, originY, panelWidth, panelHeight);

            GUI.DrawTexture(panelRect, _promptBackgroundTexture);

            Color savedGuiColor = GUI.color;
            GUI.color = _repairPromptNeedsPack ? PromptDisabledColor : PromptAccentColor;
            GUI.DrawTexture(circleRect, _keyCircleTexture);
            GUI.color = savedGuiColor;

            string keyLabel = _repairKey.Value.ToString();
            GUIStyle keyStyle = new GUIStyle(amberLine)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = keyLabel.Length > 1 ? 14 : 18
            };
            GUI.Label(circleRect, keyLabel, keyStyle);

            float lineY = panelRect.y + paddingY;
            var lineRect = new Rect(panelRect.x + paddingX, lineY, textWidth, lineHeight);
            GUI.Label(lineRect, verbContent, whiteLine);
            lineRect.y += lineHeight;
            GUI.Label(lineRect, nameContent, whiteLine);
            lineRect.y += lineHeight;
            GUI.Label(lineRect, effectContent, amberLine);

            if (holding)
            {
                const float barHeight = 6f;
                const float barGap = 8f;
                float progress = Mathf.Clamp01(_repairProgressSeconds / _repairDurationSeconds.Value);

                var barBackRect = new Rect(panelRect.x, panelRect.yMax + barGap, panelRect.width, barHeight);
                GUI.DrawTexture(barBackRect, _promptBackgroundTexture);

                var barFillRect = new Rect(barBackRect.x, barBackRect.y, barBackRect.width * progress, barHeight);
                GUI.DrawTexture(barFillRect, _progressFillTexture);
            }
        }

        // A small persistent "AIR REG KITS[N]" readout, independent of the repair prompt -
        // shown any time the game is actually being played, regardless of whether the
        // player is currently looking at a regulator, so owning zero is just as visible as
        // owning several. There's no real inventory item backing these yet (see
        // ROADMAP.md), so this is the only place the count is visible at all.
        //
        // Styled after the game's own bottom-left HUD readouts (THRUSTERS, TETHERS[1000])
        // rather than the repair prompt's dark panel - this sits directly over the 3D
        // world like the rest of the HUD does, bold caps with no background box, so it
        // needed its own outline (see DrawOutlinedLabel) to stay readable over a bright
        // background instead of relying on a panel behind it.
        private void DrawRepairPackCounter()
        {
            if (!IsHudVisibleGameState(GameSession.CurrentGameState))
            {
                return;
            }

            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperLeft,
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                font = _promptFont
            };

            int effectiveCount = EffectiveRepairPackCount;
            Color color = effectiveCount > 0 ? PromptAccentColor : PromptDisabledColor;
            var content = new GUIContent($"AIR REG KITS[{effectiveCount}]");
            Vector2 size = style.CalcSize(content);

            // Same gap above the left-side thruster gauge as before - a fraction of
            // Screen.height rather than a fixed pixel offset, so it stays in roughly the
            // same relative spot across different resolutions.
            var rect = new Rect(24f, Screen.height * 0.23f, size.x, size.y);
            DrawOutlinedLabel(rect, content, style, color);
        }

        // IMGUI has no built-in text outline/glow, unlike the shader-driven look the rest
        // of this game's HUD text has - this fakes it the old way, stamping the label
        // several times in a dark color at small offsets before drawing the real color on
        // top, so it stays readable without needing a background panel behind it.
        private static void DrawOutlinedLabel(Rect rect, GUIContent content, GUIStyle style, Color color)
        {
            GUIStyle outlineStyle = new GUIStyle(style)
            {
                fontStyle = style.fontStyle
            };
            outlineStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);

            foreach (Vector2 offset in OutlineOffsets)
            {
                GUI.Label(new Rect(rect.x + offset.x, rect.y + offset.y, rect.width, rect.height), content, outlineStyle);
            }

            style.normal.textColor = color;
            GUI.Label(rect, content, style);
        }

        private static readonly Vector2[] OutlineOffsets =
        {
            new Vector2(-1f, -1f), new Vector2(0f, -1f), new Vector2(1f, -1f),
            new Vector2(-1f, 0f), new Vector2(1f, 0f),
            new Vector2(-1f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f)
        };

        // First fix attempt: broadened this from a strict "== Gameplay" check to an
        // exclusion list, on the theory that the shop reuses GameState.Paused (going by
        // VendingMachineUIController's own handler already reacting to it). Confirmed live
        // this was wrong on both counts: the counter still didn't show in the shop, AND
        // Paused turned out to be the *real* ESC-pause-menu state after all - broadening to
        // include it just made the counter incorrectly show there too. Reverted to the
        // original strict check so the counter at least stops showing over the real pause
        // menu; the shop case is still unsolved and evidently isn't a GameState problem at
        // all (or not one "Paused" explains), so it needs real data instead of another
        // guess - see LogGameStateChanges below.
        private static bool IsHudVisibleGameState(GameSession.GameState state)
        {
            return state == GameSession.GameState.Gameplay;
        }

        // Added after two wrong guesses in a row about what state the shop runs under -
        // logs the actual value instead of guessing a third time. Cheap to check every
        // frame (a single field compare), so no InvokeRepeating/throttling needed.
        private void LogGameStateChanges()
        {
            GameSession.GameState current = GameSession.CurrentGameState;
            if (current != _lastLoggedGameState)
            {
                Logger.LogInfo($"[Diag] GameSession.CurrentGameState changed: {_lastLoggedGameState} -> {current}.");
                _lastLoggedGameState = current;
            }
        }

        // Free Play (FreeMode) and Cutter's Race (Challenge) have no real economy worth
        // gating repairing behind, so both get a flat, non-persisted allotment instead of
        // the real owned-kit count. Career and WeeklyShip keep the real economy.
        private static bool IsUnlimitedRepairPackSession(GameSession.SessionType sessionType)
        {
            return sessionType == GameSession.SessionType.FreeMode || sessionType == GameSession.SessionType.Challenge;
        }

        private static bool IsPersistentRepairPackSession(GameSession.SessionType sessionType)
        {
            return sessionType == GameSession.SessionType.Career || sessionType == GameSession.SessionType.WeeklyShip;
        }

        // What UpdateRepair's gate and the on-screen counter should actually show/use -
        // the real saved count normally, or a flat number in Free Play/Cutter's Race.
        private int EffectiveRepairPackCount
        {
            get
            {
                return IsUnlimitedRepairPackSession(GameSession.CurrentSessionType)
                    ? UnlimitedRepairPackDisplayCount
                    : _repairPackCount.Value;
            }
        }

        // Keeps _repairPackBaselineBeforeShift honest and reverts _repairPackCount.Value
        // if a Career/WeeklyShip session ends (back to the front end) without ever having
        // reached the Hab since the last baseline - i.e. the shift was abandoned, so
        // whatever was bought or spent mid-shift shouldn't stick. Cheap (a couple of field
        // compares) - safe to run unconditionally every frame rather than gating it behind
        // Behavior.EnableDiagnostics like the pure-logging GameState tracker above.
        private void UpdateRepairPackPersistenceTracking()
        {
            GameSession.SessionType sessionType = GameSession.CurrentSessionType;

            if (_repairPackTrackingShift && GameSession.CurrentGameState == GameSession.GameState.Hab)
            {
                _repairPackBaselineBeforeShift = _repairPackCount.Value;
            }

            if (sessionType == _lastRepairPackSessionType)
            {
                return;
            }

            if (_repairPackTrackingShift && !IsPersistentRepairPackSession(sessionType))
            {
                if (_repairPackCount.Value != _repairPackBaselineBeforeShift)
                {
                    Logger.LogInfo($"[Shop] Shift ended without reaching the Hab - reverting Air Reg Kits from {_repairPackCount.Value} back to {_repairPackBaselineBeforeShift}.");
                    _repairPackCount.Value = _repairPackBaselineBeforeShift;
                }

                _repairPackTrackingShift = false;
            }

            if (!_repairPackTrackingShift && IsPersistentRepairPackSession(sessionType))
            {
                _repairPackBaselineBeforeShift = _repairPackCount.Value;
                _repairPackTrackingShift = true;
            }

            _lastRepairPackSessionType = sessionType;
        }

        private void OnDestroy()
        {
            if (_promptBackgroundTexture != null)
            {
                UnityEngine.Object.Destroy(_promptBackgroundTexture);
            }
            if (_keyCircleTexture != null)
            {
                UnityEngine.Object.Destroy(_keyCircleTexture);
            }
            if (_progressFillTexture != null)
            {
                UnityEngine.Object.Destroy(_progressFillTexture);
            }
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        // A hollow ring (not a filled disc) so the key letter shows through the middle,
        // matching the game's own circled-key style.
        private static Texture2D MakeRingTexture(int size, float thickness, Color color)
        {
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            float radius = size / 2f;
            float inner = radius - thickness;
            var transparent = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - radius;
                    float dy = y + 0.5f - radius;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    texture.SetPixel(x, y, dist <= radius && dist >= inner ? color : transparent);
                }
            }
            texture.Apply();
            return texture;
        }

        // Individual parts (regulators included) turned out not to carry their own
        // collider at all - a raycast from the camera lands on the ship's single combined
        // hull collision mesh, so the hit transform is always some ship-wide root, never
        // anything specific to a regulator. Matching by transform hierarchy therefore
        // never works; matching by proximity of the hit *point* to each tracked
        // regulator's own renderer bounds does. Shared between the verbose key-press
        // handler and the every-frame prompt check, so both agree on what "looking at a
        // regulator" means.
        private bool TryFindClosestTrackedRegulator(Vector3 point, out Transform meshRoot, out RegulatorEntry entry, out float distance)
        {
            meshRoot = null;
            entry = null;
            distance = float.MaxValue;

            foreach (KeyValuePair<Transform, RegulatorEntry> kvp in _regulators)
            {
                if (kvp.Key == null)
                {
                    continue;
                }

                float d = DistanceToRenderers(point, kvp.Value.Renderers);
                if (d < distance)
                {
                    distance = d;
                    meshRoot = kvp.Key;
                    entry = kvp.Value;
                }
            }

            return entry != null;
        }

        // Smallest distance from a world point to the surface of any of the given
        // renderers' (axis-aligned) bounds - 0 if the point is inside one.
        private static float DistanceToRenderers(Vector3 point, Renderer[] renderers)
        {
            float closest = float.MaxValue;
            if (renderers == null)
            {
                return closest;
            }

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                float distance = Vector3.Distance(point, renderer.bounds.ClosestPoint(point));
                if (distance < closest)
                {
                    closest = distance;
                }
            }

            return closest;
        }

        // Damaged atmosphere regulators spawn in with no RoomProbe anywhere under them
        // (see docs/TECHNICAL.md) - there's nothing broken to "fix", just a probe (and, ideally, its
        // button and animator) to add back. Prefers cloning a real, currently-working
        // regulator elsewhere on the ship (see TryCloneWorkingRegulatorOnto) so the button
        // press, its animation, and any door-toggle triggers all come along for free,
        // falling back to a bare RoomProbe (pressurization only, no button/animation) only
        // if no working regulator exists anywhere to clone from. Either way, the result
        // still needs wiring into a room and registering with the ship's ECU - both of
        // those gaps exist regardless of how the RoomProbe was created, see
        // TryWireProbeIntoRoom and RegisterProbeWithShipEcu.
        private void RepairRegulator(Transform meshRoot, RegulatorEntry entry)
        {
            Transform templateMeshRoot = FindTemplateMeshRoot(meshRoot);
            RoomProbe probe = templateMeshRoot != null ? TryCloneWorkingRegulatorOnto(templateMeshRoot, meshRoot, entry) : null;

            if (probe == null)
            {
                Logger.LogInfo(templateMeshRoot != null
                    ? $"[Repair] Cloning a working regulator onto '{entry.DisplayName}' failed - falling back to a bare RoomProbe (pressurization only, no button/animation)."
                    : $"[Repair] No currently-working regulator exists anywhere on this ship to clone from - falling back to a bare RoomProbe for '{entry.DisplayName}' (pressurization only, no button/animation).");
                probe = RepairRegulatorFromScratch(meshRoot, entry);
                if (probe == null)
                {
                    return;
                }
            }

            if (!EntityBlueprintComponent.TryGetEntityBlueprint(probe.gameObject, out EntityBlueprintComponent ebc) || !EntityBlueprintComponent.IsValid(ebc))
            {
                Logger.LogError($"[Repair] '{entry.DisplayName}' has a new RoomProbe but no valid backing entity - can't wire it into a room.");
                return;
            }

            if (!TryWireProbeIntoRoom(probe, ebc))
            {
                Logger.LogWarning($"[Repair] '{entry.DisplayName}' now has a RoomProbe, but it couldn't be wired into a room - it will incorrectly show as Unpressurized (blue) rather than actually functioning.");
            }

            entry.EverHadLiveProbe = true;
            entry.LastAppliedState = null;

            RegisterProbeWithShipEcu(probe);

            // Remember the probe and the ship rigidbody it was grafted onto, so
            // CheckGraftedProbesStillConnected can notice later if this regulator gets
            // physically cut free of the ship - see that method and docs/TECHNICAL.md's Known
            // Issues for why a grafted probe needs this and a real one doesn't.
            entry.GraftedProbe = probe;
            StructurePart targetStructurePart = meshRoot.GetComponent<StructurePart>();
            entry.ShipRigidbodyAtGraftTime = targetStructurePart != null ? targetStructurePart.VirtualRigidbody?.UnityRigidbody : null;

            Logger.LogInfo($"Repaired regulator '{entry.DisplayName}' - reattached a working, wired-in RoomProbe.");
        }

        // A grafted RoomProbe (from a repair) has no tie to its host StructurePart's own
        // connectivity - nothing about cutting that StructurePart free of the ship ever
        // reaches the probe to disconnect or destroy it, so left alone it just keeps
        // pressurizing its room and responding to its button forever, even mid-air, cut
        // loose and floating. This gives it that tie by hand, checked every scan.
        //
        // Confirmed live (see docs/TECHNICAL.md's Known Issues for the full log evidence):
        // StructurePart.IsPartOfGroup does NOT change when a part is physically cut free -
        // it stayed true throughout, both for a repaired regulator that was pulled off the
        // wall and for a real, never-repaired one. What DOES change is
        // StructurePart.VirtualRigidbody.UnityRigidbody: cutting something free gives it its
        // own new Rigidbody, distinct from the one it shared with the rest of the ship. An
        // earlier version of this check required *both* signals to agree before tearing
        // anything down (out of caution - destroying a clone of a StructurePart's own entity
        // had separately proven capable of disturbing an unrelated sibling's StructureGroup
        // state, so IsPartOfGroup alone felt untrustworthy). That caution is exactly what
        // let the zombie survive: the rigidbody demonstrably changed, but IsPartOfGroup's
        // stuck-true reading vetoed the teardown every time. IsPartOfGroup is still logged
        // for visibility, but only the rigidbody signal is used to decide now.
        private void CheckGraftedProbesStillConnected()
        {
            foreach (KeyValuePair<Transform, RegulatorEntry> kvp in _regulators)
            {
                RegulatorEntry entry = kvp.Value;
                RoomProbe graftedProbe = entry.GraftedProbe;
                if (graftedProbe == null)
                {
                    continue;
                }

                Transform meshRoot = kvp.Key;
                if (meshRoot == null)
                {
                    continue;
                }

                StructurePart structurePart = meshRoot.GetComponent<StructurePart>();
                if (structurePart == null)
                {
                    // Can't verify connectivity without something to check - leave it
                    // alone rather than risk a false-positive teardown of a working repair.
                    continue;
                }

                bool stillPartOfGroup = structurePart.IsPartOfGroup;
                Rigidbody currentRigidbody = structurePart.VirtualRigidbody?.UnityRigidbody;
                bool stillSameRigidbody = currentRigidbody != null && currentRigidbody == entry.ShipRigidbodyAtGraftTime;

                if (entry.LastObservedPartOfGroup != stillPartOfGroup || entry.LastObservedSameRigidbody != stillSameRigidbody)
                {
                    Logger.LogInfo($"[Repair] Connectivity check for '{entry.DisplayName}': IsPartOfGroup={stillPartOfGroup}, sameRigidbodyAsWhenRepaired={stillSameRigidbody}.");
                    entry.LastObservedPartOfGroup = stillPartOfGroup;
                    entry.LastObservedSameRigidbody = stillSameRigidbody;
                }

                if (stillSameRigidbody)
                {
                    continue;
                }

                Logger.LogWarning($"[Repair] '{entry.DisplayName}' appears to have been cut loose from the ship (VirtualRigidbody changed) - removing everything this repair grafted on (probe, button, animator, FX) so it stops functioning and stops being interactable, instead of working indefinitely while detached.");

                UnregisterProbeFromAllEcus(graftedProbe);

                // Destroy every object this repair grafted on, not just the RoomProbe -
                // the button ("Interaction Volume") is a separate sibling object, not a
                // child of RoomProbe, so it would otherwise survive fully interactable
                // (just doing nothing) even after the probe itself was removed.
                foreach (GameObject graftedObject in entry.GraftedObjects)
                {
                    if (graftedObject != null)
                    {
                        UnityEngine.Object.Destroy(graftedObject);
                    }
                }

                entry.GraftedObjects.Clear();
                entry.GraftedProbe = null;
                entry.ShipRigidbodyAtGraftTime = null;
            }
        }

        // Used when a grafted probe is torn down after its host is cut loose (see
        // CheckGraftedProbesStillConnected) - removes it from every ECUController's list
        // rather than just the one it was matched to at repair time, since Unity's "fake
        // null" for a destroyed object would otherwise throw if that controller's own
        // ToggleAllAtmosphereRegulators ever dereferenced a stale entry later.
        private void UnregisterProbeFromAllEcus(RoomProbe probe)
        {
            if (EcuProbesField == null)
            {
                return;
            }

            foreach (ECUController ecu in FindObjectsOfType<ECUController>(true))
            {
                if (EcuProbesField.GetValue(ecu) is List<RoomProbe> probes)
                {
                    probes.Remove(probe);
                }
            }
        }

        // VendingMachineUIController builds its entire item list once, in Awake(), from a
        // design-time-configured array - there's no public way to add an item afterward.
        // This finds the live vending machine (the one shop out with the ship during a
        // shift), borrows an existing item's currency reference (so ours charges in the
        // same currency as everything else without needing to locate/construct a
        // CurrencyAsset of our own), and calls the controller's own private AddItem method
        // to add "Air Reg Kit" alongside whatever's already there - the same
        // method the controller uses for every item it ships with, just invoked by hand,
        // once, after the fact.
        //
        // Re-checked every scan rather than once, since a new shift spawns a brand new
        // VendingMachineUIController instance (the previous shift's is destroyed with its
        // scene) - _patchedVendingMachine tracks which specific instance has already been
        // handled, not just whether this has ever happened at all.
        private void TryAddRepairPackToShop()
        {
            VendingMachineUIController vendingMachine = FindObjectOfType<VendingMachineUIController>(true);
            if (vendingMachine == null || vendingMachine == _patchedVendingMachine)
            {
                return;
            }

            _patchedVendingMachine = vendingMachine;

            if (VendingStoreTabsField == null || VendingAddItemMethod == null || VendingStoreTabType == null
                || VendingStoreTabItemListProperty == null || VendingStoreTabContainerProperty == null
                || VendingItemDataNameField == null || VendingItemDataDescriptionField == null
                || VendingItemDataCurrencyField == null || VendingItemDataCostField == null
                || VendingItemDataOnPurchaseActionField == null || VendingItemDataPreviewTextureField == null
                || VendingItemDataPreviewMeshField == null || VendingItemDataPreviewScaleField == null
                || VendingItemDataPreviewOrientationField == null)
            {
                Logger.LogError("[Shop] Reflection lookup against VendingMachineUIController failed - the game's shop code must have changed. Skipping the repair pack item.");
                return;
            }

            if (!(VendingStoreTabsField.GetValue(vendingMachine) is Array storeTabs) || storeTabs.Length == 0)
            {
                Logger.LogWarning("[Shop] Vending machine has no store tabs - can't add the repair pack.");
                return;
            }

            CurrencyAsset currency = null;
            Transform chosenContainer = null;
            Mesh previewMesh = null;
            Texture2D previewTexture = null;
            Vector3 previewScale = Vector3.one;
            Vector3 previewOrientation = Vector3.up;

            foreach (object tab in storeTabs)
            {
                Transform container = VendingStoreTabContainerProperty.GetValue(tab) as Transform;
                if (container == null || !(VendingStoreTabItemListProperty.GetValue(tab) is VendingMachineItemAsset[] itemList))
                {
                    continue;
                }

                foreach (VendingMachineItemAsset existingItem in itemList)
                {
                    if (existingItem == null || existingItem.Data == null)
                    {
                        continue;
                    }

                    if (currency == null && existingItem.Data.Currency != null)
                    {
                        currency = existingItem.Data.Currency;
                        chosenContainer = container;
                    }

                    // Identified by its actual purchase-action type rather than its
                    // display name - a real item's Name is a localization ID, not literal
                    // text, so there's no "Suit Patch Kit" string to match against here.
                    if (previewMesh == null && existingItem.Data.ScriptingPrefab is PurchasePatch)
                    {
                        previewMesh = existingItem.Data.PreviewMesh;
                        previewTexture = existingItem.Data.PreviewTexture;
                        previewScale = existingItem.Data.PreviewScale;
                        previewOrientation = existingItem.Data.PreviewOrientation;
                    }
                }

                if (currency != null && previewMesh != null)
                {
                    break;
                }
            }

            if (currency == null || chosenContainer == null)
            {
                Logger.LogWarning("[Shop] Couldn't find an existing shop item to borrow a currency reference from - skipping the repair pack.");
                return;
            }

            if (previewMesh == null)
            {
                Logger.LogWarning("[Shop] Couldn't find the Suit Patch Kit item (by its PurchasePatch action) to borrow a preview mesh/texture from - the item will still work, just without a 3D preview.");
            }

            var purchaseActionObject = new GameObject("PurchaseRepairPackAction");
            purchaseActionObject.SetActive(false);
            PurchaseRepairPack purchaseAction = purchaseActionObject.AddComponent<PurchaseRepairPack>();

            var itemAsset = ScriptableObject.CreateInstance<VendingMachineItemAsset>();
            VendingItemDataNameField.SetValue(itemAsset.Data, "Air Reg Kit");
            VendingItemDataDescriptionField.SetValue(itemAsset.Data, "Repairs a damaged atmosphere regulator back to working order.");
            VendingItemDataCurrencyField.SetValue(itemAsset.Data, currency);
            VendingItemDataCostField.SetValue(itemAsset.Data, _repairPackCostCredits.Value);
            VendingItemDataOnPurchaseActionField.SetValue(itemAsset.Data, purchaseAction);

            // VendingMachineUIController.SetPreviewAndDescription passes this straight
            // into MaterialPropertyBlock.SetTexture, which throws on a null Texture - and
            // that call happens *before* the line that updates the description text, so a
            // null preview texture silently aborts OnSelect before the description is ever
            // set. Borrowing the Suit Patch Kit's own mesh/texture (close enough in spirit -
            // another small repair-ish consumable) gives a real 3D preview instead of just
            // a blank placeholder; the 1x1 fallback only kicks in if that item couldn't be
            // found at all, purely to avoid the crash.
            VendingItemDataPreviewMeshField.SetValue(itemAsset.Data, previewMesh);
            VendingItemDataPreviewTextureField.SetValue(itemAsset.Data, previewTexture != null ? previewTexture : MakeSolidTexture(Color.black));
            VendingItemDataPreviewScaleField.SetValue(itemAsset.Data, previewScale);
            VendingItemDataPreviewOrientationField.SetValue(itemAsset.Data, previewOrientation);

            VendingAddItemMethod.Invoke(vendingMachine, new object[] { itemAsset.Data, chosenContainer });
            vendingMachine.UpdateButtonStates();

            // VendingMachineUIController's base class, LynxButtonController, caches every
            // LynxButton it finds under itself exactly once (in its own Awake(), well
            // before this ever runs) into mButtons - and every frame, if nothing in THAT
            // cached list is currently selected, it force-reselects the first one in it.
            // Our item (added after the fact) is invisible to that list: clicking it still
            // works fine (Button.onClick is wired independently), but true EventSystem
            // selection never sticks on it, so its description never gets a chance to
            // display - confirmed live (name/cost show and purchasing works, but hovering
            // it never showed its description, while hovering a real item did).
            // LynxButtonController.Initialize() is public and does exactly the
            // repopulation needed; passing false so it doesn't also yank current
            // selection over to the first button right as the item appears.
            vendingMachine.Initialize(trySelectFirstButton: false);

            Logger.LogInfo($"[Shop] Added 'Air Reg Kit' ({_repairPackCostCredits.Value} {currency.Data.Name}) to the shop.");
        }

        // Called by PurchaseRepairPack when the player actually buys one (currency
        // sufficiency is already checked by the game's own VendingMachineItemUI before
        // this ever fires). Always adds to the real saved stock, even in Free Play/
        // Cutter's Race where repairing itself ignores it (EffectiveRepairPackCount) -
        // the credits were already spent by the game's own purchase flow regardless of
        // what this does, so letting the kit go to waste instead of banking it for a
        // later Career session would just be worse.
        public void OnRepairPackPurchased()
        {
            _repairPackCount.Value++;
            Logger.LogInfo($"[Shop] Purchased an Air Reg Kit - you now have {_repairPackCount.Value}.");
        }

        // Any other regulator this plugin is tracking that's currently actually working -
        // a clone source for TryCloneWorkingRegulatorOnto. Excludes the one being repaired
        // (irrelevant anyway, it's Damaged) so this never returns its own target.
        private Transform FindTemplateMeshRoot(Transform exclude)
        {
            foreach (KeyValuePair<Transform, RegulatorEntry> kvp in _regulators)
            {
                if (kvp.Key == null || kvp.Key == exclude)
                {
                    continue;
                }

                if (kvp.Value.LastAppliedState == RegulatorState.Pressurized || kvp.Value.LastAppliedState == RegulatorState.Unpressurized)
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        // Grafts a real, currently-working regulator's functional internals - RoomProbe,
        // its button ("Interaction Volume"), its animator ("Anim_AirlockLights_Root"), and
        // whatever supporting objects sit alongside them, all confirmed via the F9
        // diagnostics to be direct children of the mesh root - onto the target.
        //
        // This deliberately never clones the donor's mesh root itself. An earlier version
        // did exactly that (Object.Instantiate on the whole mesh root, harvest the useful
        // children, discard the rest), and it introduced a new bug: the mesh root also
        // carries a StructurePart/blueprint-backed ECS entity tied into the ship's real
        // structure graph (a shared StructureGroup, physics joints, salvage value - see the
        // F9 diagnostics' StructureGroupMember info). Destroying even a temporary clone of
        // that entity had side effects on an unrelated sibling regulator sharing the same
        // StructureGroup, which is almost certainly what turned a *different* regulator
        // Broken/red after a repair.
        //
        // Instead, this temporarily moves the donor's real children onto a bare, empty,
        // never-initialized GameObject (which carries none of that structural baggage),
        // clones *that* lightweight stand-in, then immediately gives the donor its real
        // children back exactly where they were. Object.Instantiate still remaps
        // references between objects within the one hierarchy it's given, so
        // RoomProbe.InteractableObject/Animator on the clone still correctly point at the
        // clone's own button/animator, and any UnityEvent triggers wired in the editor
        // (button press animation, door toggles, etc.) still come along intact.
        private RoomProbe TryCloneWorkingRegulatorOnto(Transform templateMeshRoot, Transform targetMeshRoot, RegulatorEntry entry)
        {
            if (EbcLastKnownEntityIndexField == null || EbcLastKnownEntityVersionField == null)
            {
                Logger.LogError("[Repair] Reflection lookup of EntityBlueprintComponent's mLastKnownEntity fields failed - the game's code must have changed. Skipping the clone-based repair.");
                return null;
            }

            int childCount = templateMeshRoot.childCount;
            var borrowedChildren = new Transform[childCount];
            for (int i = 0; i < childCount; i++)
            {
                borrowedChildren[i] = templateMeshRoot.GetChild(i);
            }

            var standIn = new GameObject("RepairStandIn");
            foreach (Transform child in borrowedChildren)
            {
                child.SetParent(standIn.transform, worldPositionStays: false);
            }

            // These are the donor's own live components (RoomProbe's, InteractableObject's
            // own ebcs, etc.) - already initialized this session, so their serialized
            // mLastKnownEntity* fields are non-zero. See the EbcLastKnownEntityIndexField
            // comment for why that has to be zeroed before cloning and restored after.
            EntityBlueprintComponent[] borrowedEbcs = standIn.GetComponentsInChildren<EntityBlueprintComponent>(true);
            var savedIndex = new int[borrowedEbcs.Length];
            var savedVersion = new int[borrowedEbcs.Length];
            for (int i = 0; i < borrowedEbcs.Length; i++)
            {
                savedIndex[i] = borrowedEbcs[i].LastKnownEntityIndex;
                savedVersion[i] = borrowedEbcs[i].LastKnownEntityVersion;
                EbcLastKnownEntityIndexField.SetValue(borrowedEbcs[i], 0);
                EbcLastKnownEntityVersionField.SetValue(borrowedEbcs[i], 0);
            }

            GameObject standInClone;
            try
            {
                standInClone = UnityEngine.Object.Instantiate(standIn);
            }
            finally
            {
                for (int i = 0; i < borrowedEbcs.Length; i++)
                {
                    EbcLastKnownEntityIndexField.SetValue(borrowedEbcs[i], savedIndex[i]);
                    EbcLastKnownEntityVersionField.SetValue(borrowedEbcs[i], savedVersion[i]);
                }

                // Give the donor its real children back, exactly where they were, whether
                // or not the clone succeeded.
                foreach (Transform child in borrowedChildren)
                {
                    child.SetParent(templateMeshRoot, worldPositionStays: false);
                }
                UnityEngine.Object.Destroy(standIn);
            }

            RoomProbe cloneProbe = standInClone.GetComponentInChildren<RoomProbe>(true);
            if (cloneProbe == null)
            {
                Logger.LogError($"[Repair] Cloned '{templateMeshRoot.name}''s internals but the clone has no RoomProbe - aborting the clone-based repair.");
                UnityEngine.Object.Destroy(standInClone);
                return null;
            }

            Transform interactableTransform = cloneProbe.InteractableObject != null ? cloneProbe.InteractableObject.transform : null;
            Transform animatorTransform = cloneProbe.Animator != null ? cloneProbe.Animator.transform : null;
            Logger.LogInfo($"[Repair] Cloned '{templateMeshRoot.name}''s internals to graft onto '{entry.DisplayName}' - probe='{cloneProbe.name}', button={(interactableTransform != null ? interactableTransform.name : "none")}, animator={(animatorTransform != null ? animatorTransform.name : "none")}.");

            int cloneChildCount = standInClone.transform.childCount;
            var cloneChildren = new Transform[cloneChildCount];
            for (int i = 0; i < cloneChildCount; i++)
            {
                cloneChildren[i] = standInClone.transform.GetChild(i);
            }
            entry.GraftedObjects.Clear();
            foreach (Transform child in cloneChildren)
            {
                child.SetParent(targetMeshRoot, worldPositionStays: false);
                entry.GraftedObjects.Add(child.gameObject);
            }
            Logger.LogInfo($"[Repair] Moved {cloneChildCount} child object(s) from the clone onto '{entry.DisplayName}'.");

            UnityEngine.Object.Destroy(standInClone);

            return cloneProbe;
        }

        // Fallback for the rare case where no other regulator on the ship is currently
        // working at all, so there's nothing to clone from. Restores pressurization only -
        // RoomProbe.IsAtmosphereRegulator has no public setter and there's no real
        // InteractableObject/Animator to source, so no button press or animation.
        private RoomProbe RepairRegulatorFromScratch(Transform meshRoot, RegulatorEntry entry)
        {
            var probeObject = new GameObject("RoomProbe (Repaired)");
            probeObject.transform.SetParent(meshRoot, worldPositionStays: false);
            probeObject.transform.localPosition = _templateProbeLocalPosition ?? Vector3.zero;
            probeObject.transform.localRotation = _templateProbeLocalRotation ?? Quaternion.identity;

            if (IsAtmosphereRegulatorField == null)
            {
                Logger.LogError("[Repair] Reflection lookup of RoomProbe.m_IsAtmosphereRegulator failed - the game's RoomProbe class must have changed. Aborting repair.");
                UnityEngine.Object.Destroy(probeObject);
                return null;
            }

            RoomProbe probe = probeObject.AddComponent<RoomProbe>();
            IsAtmosphereRegulatorField.SetValue(probe, true);
            Logger.LogInfo($"[Repair] Added bare RoomProbe to new child '{probeObject.name}' of '{entry.DisplayName}' at localPos={probeObject.transform.localPosition}, IsAtmosphereRegulator={probe.IsAtmosphereRegulator}.");

            // Reading RoomEntity has the side effect of lazily creating the probe's
            // backing ECS entity (EntityBlueprintComponent.TryGetOrAddEntityBlueprint) -
            // the same thing that happens the first time the game itself ever queries a
            // fresh probe.
            _ = probe.RoomEntity;

            entry.GraftedObjects.Clear();
            entry.GraftedObjects.Add(probeObject);
            return probe;
        }

        // Replicates RoomProbeInitializeSystem.UpdateUninitializeProbes's room lookup
        // (see the RepairRegulator comment for why the real system can't be relied on):
        // finds whichever room's RoomVolumeIncludeData oriented bounding box contains the
        // probe's world position (and isn't cancelled out by a RoomVolumeExcludeData box),
        // then adds and fills in the exact same set of components
        // (RoomProbeComponent/NotifyOnAirPressureStateChange/DisableOnExternallyExposed/
        // AirPressureState/PreviousAirPressureState) the original system would have.
        private bool TryWireProbeIntoRoom(RoomProbe probe, EntityBlueprintComponent ebc)
        {
            EntityManager entityManager = ebc.EntityManager;
            Entity probeEntity = ebc.Entity;
            float3 point = probe.transform.position;

            EntityQuery roomQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RoomComponent>(),
                ComponentType.ReadOnly<AirPressureState>(),
                ComponentType.ReadOnly<RoomVolumeIncludeData>());
            NativeArray<Entity> roomEntities = roomQuery.ToEntityArray(Allocator.Temp);
            int roomCount = roomEntities.Length;

            Entity matchedRoom = Entity.Null;
            foreach (Entity roomEntity in roomEntities)
            {
                if (IsPointExcludedFromRoom(entityManager, roomEntity, point))
                {
                    continue;
                }

                DynamicBuffer<RoomVolumeIncludeData> includeVolumes = entityManager.GetBuffer<RoomVolumeIncludeData>(roomEntity);
                for (int i = 0; i < includeVolumes.Length; i++)
                {
                    if (includeVolumes[i].BoundingBox.IsPointInside(point))
                    {
                        matchedRoom = roomEntity;
                        break;
                    }
                }

                if (matchedRoom != Entity.Null)
                {
                    break;
                }
            }

            roomEntities.Dispose();
            roomQuery.Dispose();

            if (matchedRoom == Entity.Null)
            {
                Logger.LogWarning($"[Repair] Checked {roomCount} room(s) but none of their bounding volumes contain the probe's position {point}.");
                return false;
            }

            entityManager.AddComponent<RoomProbeComponent>(probeEntity);
            entityManager.AddComponent<NotifyOnAirPressureStateChange>(probeEntity);
            entityManager.AddComponent<DisableOnExternallyExposed>(probeEntity);
            entityManager.AddComponent<AirPressureState>(probeEntity);
            entityManager.AddComponent<PreviousAirPressureState>(probeEntity);

            entityManager.SetComponentData(probeEntity, new RoomProbeComponent
            {
                RoomEntity = matchedRoom,
                AnimatorEntity = Entity.Null,
                DisableOnExternallyExposed = true
            });
            entityManager.SetComponentData(probeEntity, entityManager.GetComponentData<AirPressureState>(matchedRoom));
            entityManager.SetComponentData(probeEntity, new PreviousAirPressureState { Value = RoomPressurization.Decompressed });

            RoomComponent room = entityManager.GetComponentData<RoomComponent>(matchedRoom);
            Logger.LogInfo($"[Repair] Wired the probe into room entity {matchedRoom} (checked {roomCount} room(s)) - AirPressureState={room.AirPressureState}, AllowAirPressureToggle={room.AllowAirPressureToggle}.");
            return true;
        }

        private static bool IsPointExcludedFromRoom(EntityManager entityManager, Entity roomEntity, float3 point)
        {
            if (!entityManager.HasComponent<RoomVolumeExcludeData>(roomEntity))
            {
                return false;
            }

            DynamicBuffer<RoomVolumeExcludeData> excludeVolumes = entityManager.GetBuffer<RoomVolumeExcludeData>(roomEntity);
            for (int i = 0; i < excludeVolumes.Length; i++)
            {
                if (excludeVolumes[i].BoundingBox.IsPointInside(point))
                {
                    return true;
                }
            }
            return false;
        }

        // Makes the repaired probe respond to the ship's manual air-pressure toggle
        // control - see the EcuProbesField comment for why this is necessary at all.
        private void RegisterProbeWithShipEcu(RoomProbe probe)
        {
            if (EcuProbesField == null)
            {
                Logger.LogWarning("[Repair] Reflection lookup of ECUController.mProbes failed - the game's ECUController class must have changed. " +
                    "The repaired regulator will still pressurize/depressurize on its own, but the ship's manual toggle control won't include it.");
                return;
            }

            ECUController[] ecus = FindObjectsOfType<ECUController>(true);
            foreach (ECUController ecu in ecus)
            {
                StructurePart structurePart = ecu.GetComponent<StructurePart>();
                Transform rigidbodyTransform = structurePart != null ? structurePart.VirtualRigidbody?.UnityRigidbody?.transform : null;
                if (rigidbodyTransform == null || !probe.transform.IsChildOf(rigidbodyTransform))
                {
                    continue;
                }

                if (!(EcuProbesField.GetValue(ecu) is List<RoomProbe> probes))
                {
                    continue;
                }

                if (!probes.Contains(probe))
                {
                    probes.Add(probe);
                }

                Logger.LogInfo($"[Repair] Registered the repaired probe with the ECUController on '{ecu.gameObject.name}' ({probes.Count} atmosphere regulator(s) it now tracks) - the ship's air-pressure toggle control will include it.");
                return;
            }

            Logger.LogWarning($"[Repair] Couldn't find this ship's ECUController (checked {ecus.Length}) - the repaired regulator will pressurize/depressurize on its own, but the ship's manual air-pressure toggle control won't include it.");
        }

        private void DumpRegulatorDiagnostics()
        {
            Logger.LogInfo("=== Regulator diagnostics dump start (F9) ===");
            Transform[] allTransforms = FindObjectsOfType<Transform>(true);
            int matchCount = 0;
            foreach (Transform t in allTransforms)
            {
                if (t.name.IndexOf("Regulator", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                matchCount++;
                DumpTransformDiagnostics(t);
            }
            Logger.LogInfo($"=== Regulator diagnostics dump end ({matchCount} match(es) for 'Regulator') ===");
        }

        private void DumpTransformDiagnostics(Transform t)
        {
            string path = GetHierarchyPath(t);

            RoomProbe probe = t.GetComponentInChildren<RoomProbe>(true);
            string probeInfo = probe != null
                ? $"RoomProbe(IsAtmosphereRegulator={probe.IsAtmosphereRegulator}, activeSelf={probe.gameObject.activeSelf})"
                : "no RoomProbe in children";

            string ebcInfo = "no EntityBlueprintComponent";
            if (EntityBlueprintComponent.TryGetEntityBlueprint(t.gameObject, out EntityBlueprintComponent ebc) && ebc != null)
            {
                bool valid = World.DefaultGameObjectInjectionWorld != null && EntityBlueprintComponent.IsValid(ebc.EntityManager, ebc.Entity);
                string ownInfo = valid ? DumpVitalityInfo(ebc.EntityManager, ebc.Entity, "own") : "own=n/a";

                string groupInfo = "no StructureGroupMember";
                if (valid && ebc.EntityManager.TryGetComponent(ebc.Entity, out StructureGroupMember groupMember))
                {
                    bool groupExists = ebc.EntityManager.Exists(groupMember.GroupEntity);
                    groupInfo = groupExists
                        ? $"StructureGroupMember(GroupEntity={groupMember.GroupEntity}) {DumpVitalityInfo(ebc.EntityManager, groupMember.GroupEntity, "group")}"
                        : $"StructureGroupMember(GroupEntity={groupMember.GroupEntity}, exists=false)";
                }

                ebcInfo = $"EntityBlueprintComponent(valid={valid}) {ownInfo} | {groupInfo}";
            }

            Renderer[] renderers = t.GetComponentsInChildren<Renderer>(true);
            string rendererNames = renderers.Length == 0
                ? "none"
                : string.Join(", ", Array.ConvertAll(renderers, r => r.gameObject.name));

            Logger.LogInfo($"[{path}] activeInHierarchy={t.gameObject.activeInHierarchy} | {probeInfo} | {ebcInfo} | renderers=[{rendererNames}]");

            // Where does the button (InteractableObject) that actually toggles THIS probe,
            // and its Animator, sit relative to the mesh root? Needed to know what a
            // "clone a real working regulator into place" repair would need to clone as
            // one unit - RoomProbe.InteractableObject/Animator are plain serialized
            // references, so this is the only reliable way to find out (Instantiate only
            // remaps references within whatever single hierarchy it's given).
            if (probe != null)
            {
                DumpRelatedTransform("InteractableObject", probe.InteractableObject != null ? probe.InteractableObject.transform : null, t);
                DumpRelatedTransform("Animator", probe.Animator != null ? probe.Animator.transform : null, t);
                DumpConnectivityDiagnostics(t, probe);
            }
        }

        // Added to chase the "repaired regulators keep working forever, even cut loose"
        // bug: the first fix attempt (StructurePart.IsPartOfGroup + VirtualRigidbody
        // matching) never fired at all, so that guess was wrong. Rather than guess again,
        // this dumps every connectivity-ish signal available - StructurePart/rigidbody,
        // the room it's wired to (and that room's own state), and its button's
        // interactable/lockable state - so a before/after F9 dump on a real,
        // never-repaired regulator being pulled off the wall can show which signal(s)
        // actually change on a genuine disconnect.
        private void DumpConnectivityDiagnostics(Transform meshRoot, RoomProbe probe)
        {
            StructurePart structurePart = meshRoot.GetComponent<StructurePart>();
            string structurePartInfo;
            if (structurePart == null)
            {
                structurePartInfo = "no StructurePart on mesh root";
            }
            else
            {
                Rigidbody rb = structurePart.VirtualRigidbody?.UnityRigidbody;
                string rbInfo = rb != null ? $"'{rb.gameObject.name}'(instanceId={rb.GetInstanceID()})" : "null";
                structurePartInfo = $"StructurePart(IsPartOfGroup={structurePart.IsPartOfGroup}, VirtualRigidbody.UnityRigidbody={rbInfo})";
            }

            string roomInfo = "RoomEntity=Entity.Null";
            Entity roomEntity = probe.RoomEntity;
            if (roomEntity != Entity.Null && World.DefaultGameObjectInjectionWorld != null)
            {
                EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
                roomInfo = entityManager.Exists(roomEntity) && entityManager.TryGetComponent(roomEntity, out RoomComponent room)
                    ? $"RoomEntity={roomEntity}, AirPressureState={room.AirPressureState}, AllowAirPressureToggle={room.AllowAirPressureToggle}, ExternallyExposed={room.ExternallyExposed}, Sealed={room.Sealed}, Breached={room.Breached}"
                    : $"RoomEntity={roomEntity} but it doesn't exist or has no RoomComponent";
            }

            string interactableInfo = "InteractableObject=null";
            if (probe.InteractableObject != null)
            {
                InteractableObject io = probe.InteractableObject;
                interactableInfo = $"InteractableObject.IsInteractable={io.IsInteractable}";
                if (EntityBlueprintComponent.TryGetEntityBlueprint(io.gameObject, out EntityBlueprintComponent ioEbc) && EntityBlueprintComponent.IsValid(ioEbc))
                {
                    interactableInfo += ioEbc.EntityManager.TryGetComponent(ioEbc.Entity, out InteractableLockable lockable)
                        ? $", InteractableLockable(Locked={lockable.Locked}, LockedByDependency={lockable.LockedByDependency}, LockCooldownTime={lockable.LockCooldownTime})"
                        : ", no InteractableLockable";
                }
            }

            // Added to chase "the light on a repaired regulator never changes color like a
            // real one's does." The pipeline that's supposed to drive it: RoomStateTransferSystem
            // (runs every frame, no dead-system gotcha) keeps a probe's own AirPressureState/
            // PreviousAirPressureState in sync with its room; a change there queues a
            // notification that eventually calls RoomProbe.OnProbeStateChanged, firing the
            // probe's own m_Triggers (editor-wired UnityEvents, presumably driving the
            // Animator/light). Dumping the probe's OWN cached values here - compared
            // before/after an actual room pressurization change - shows whether that first
            // step is even happening, narrowing down whether the break is in the sync
            // system not recognizing this probe, or somewhere later in the trigger/Animator
            // chain this plugin's cloning can't directly inspect.
            string probeAirStateInfo = "no EntityBlueprintComponent";
            if (EntityBlueprintComponent.TryGetEntityBlueprint(probe.gameObject, out EntityBlueprintComponent probeEbc) && EntityBlueprintComponent.IsValid(probeEbc))
            {
                EntityManager em = probeEbc.EntityManager;
                bool hasRpc = em.HasComponent<RoomProbeComponent>(probeEbc.Entity);
                bool hasNotify = em.HasComponent<NotifyOnAirPressureStateChange>(probeEbc.Entity);
                string airState = em.TryGetComponent(probeEbc.Entity, out AirPressureState aps) ? aps.Value.ToString() : "none";
                string prevAirState = em.TryGetComponent(probeEbc.Entity, out PreviousAirPressureState paps) ? paps.Value.ToString() : "none";
                probeAirStateInfo = $"HasRoomProbeComponent={hasRpc}, HasNotifyOnAirPressureStateChange={hasNotify}, probe's own AirPressureState={airState}, PreviousAirPressureState={prevAirState}";
            }

            Logger.LogInfo($"[Diag] Connectivity for '{meshRoot.name}': {structurePartInfo} | {roomInfo} | {interactableInfo} | {probeAirStateInfo}.");
        }

        private void DumpRelatedTransform(string label, Transform related, Transform meshRoot)
        {
            if (related == null)
            {
                Logger.LogInfo($"[Diag] {label}: null.");
                return;
            }

            string relation;
            if (related == meshRoot)
            {
                relation = "IS the mesh root";
            }
            else if (related.IsChildOf(meshRoot))
            {
                relation = "is a DESCENDANT of the mesh root";
            }
            else if (meshRoot.IsChildOf(related))
            {
                relation = "is an ANCESTOR of the mesh root";
            }
            else if (related.parent == meshRoot.parent)
            {
                relation = "is a SIBLING of the mesh root (shares the same parent)";
            }
            else
            {
                relation = "is unrelated - not a shared ancestor/descendant/sibling of the mesh root";
            }

            Logger.LogInfo($"[Diag] {label}: '{GetHierarchyPath(related)}' - {relation}.");
        }

        private static string DumpVitalityInfo(EntityManager entityManager, Entity entity, string label)
        {
            string integrityInfo = entityManager.TryGetComponent(entity, out ObjectIntegrityComponent integrity)
                ? $"ObjectIntegrity={integrity.Value}/{integrity.ValueMax}(Base={integrity.BaseValue})"
                : "ObjectIntegrity=none";

            string healthInfo = entityManager.TryGetComponent(entity, out HealthComponent health)
                ? $"Health={health.Value}/{health.ValueMax}"
                : "Health=none";

            bool vitalityDepleted = entityManager.HasComponent<VitalityDepleted>(entity);

            return $"{label}[{integrityInfo}, {healthInfo}, VitalityDepleted={vitalityDepleted}]";
        }

        private static string GetHierarchyPath(Transform t)
        {
            var segments = new List<string>();
            for (Transform current = t; current != null; current = current.parent)
            {
                segments.Add(current.name);
            }
            segments.Reverse();
            return string.Join("/", segments);
        }
    }

    // VendingMachinePurchaseBase is just an abstract MonoBehaviour with one method to
    // override (see TryAddRepairPackToShop) - the game's own subclasses (PurchaseOxygen,
    // PurchasePatch, etc.) all point at other assets we have no way to construct from a
    // plugin, so this is a minimal one of our own instead. VendingMachineItemUI's own
    // OnClick already checked currency sufficiency before ever calling TryPurchase, so by
    // the time this runs the purchase is already committed - this just needs to report
    // success back and record that it happened.
    public class PurchaseRepairPack : VendingMachinePurchaseBase
    {
        public override void TryPurchase(PurchaseCallback callback)
        {
            Plugin.Instance?.OnRepairPackPurchased();
            callback?.Invoke(canPurchase: true);
        }
    }
}
