using System;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ModCoverage.Tests")]

[assembly: MelonInfo(typeof(CableThrottle.CableThrottleMod), "gregMod.CableThrottle", "1.4.1", "TeamGreg Modding")]
[assembly: MelonGame("Waseku", "Data Center")]

namespace CableThrottle
{
    public class CableThrottleMod : MelonMod
    {
        internal static MelonPreferences_Entry<float>  MaxIopsEntry;
        internal static MelonPreferences_Entry<bool>   EnabledEntry;
        internal static MelonPreferences_Entry<string> ToggleKeyEntry;

        // Runtime visible state — false means packets are fully suppressed.
        internal static bool PacketsVisible = true;

        private static KeyCode _toggleKey = KeyCode.Semicolon;

        // Cached private ResetAllSpawners method for instant clear on hide.
        private static MethodInfo _resetAllSpawners;

        public override void OnInitializeMelon()
        {
            var cat = MelonPreferences.CreateCategory("CableThrottle");
            MaxIopsEntry   = cat.CreateEntry("MaxIOPS",    100f,  "MaxIOPS",
                "Maximum IOPS value passed to packet spawners. Lower = fewer white " +
                "data-packet particles on cables. Vanilla games often pass 500–2000+.");
            EnabledEntry   = cat.CreateEntry("Enabled",    true,  "Enabled",
                "Set to false to disable throttle and restore vanilla density.");
            ToggleKeyEntry = cat.CreateEntry("ToggleKey",  "Semicolon",  "ToggleKey",
                "KeyCode name to toggle cable packets on/off at runtime. Ctrl is always required (e.g. Semicolon, F5, H).");

            if (System.Enum.TryParse<KeyCode>(ToggleKeyEntry.Value, out var parsed))
                _toggleKey = parsed;
            else
                LoggerInstance.Warning($"[CableThrottle] Unknown KeyCode '{ToggleKeyEntry.Value}', defaulting to Semicolon.");

            var flags      = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var targetType = typeof(WaypointInitializationSystem);
            var harmony    = new HarmonyLib.Harmony("com.tindolt.cablethrottle");
            var capPrefix  = new HarmonyMethod(typeof(PacketSpawnerPatches)
                                .GetMethod(nameof(PacketSpawnerPatches.CapIops),
                                           BindingFlags.Static | BindingFlags.NonPublic));

            _resetAllSpawners = targetType.GetMethod("ResetAllSpawners",
                                    BindingFlags.Instance | BindingFlags.NonPublic);

            int patched = 0;
            foreach (var name in new[] { "ActivateSpawnersForCable", "ActivateSpawnerOnCable" })
            {
                var m = targetType.GetMethod(name, flags);
                if (m != null) { harmony.Patch(m, capPrefix); patched++; LoggerInstance.Msg($"[CableThrottle] Patched {name}"); }
                else LoggerInstance.Warning($"[CableThrottle] Could not find {name} on {targetType.FullName} — skipped.");
            }

            LoggerInstance.Msg($"[CableThrottle] Ready. {patched}/2 methods patched. " +
                               $"MaxIOPS={MaxIopsEntry.Value}, ToggleKey={_toggleKey}");

        }

        public override void OnDeinitializeMelon()
        {
        }


        // ── Toggle key ──────────────────────────────────────────────────────────

        public override void OnGUI()
        {
            var e = UnityEngine.Event.current;
            if (e == null || e.type != UnityEngine.EventType.KeyDown || e.keyCode != _toggleKey) return;
            e.Use();

            PacketsVisible = !PacketsVisible;
            LoggerInstance.Msg($"[CableThrottle] Cable packets {(PacketsVisible ? "SHOWN" : "HIDDEN")}");

            var wis = WaypointInitializationSystem.Instance;
            if (wis == null) return;

            if (!PacketsVisible)
            {
                // Reset spawner component data, tag all spawner entities with
                // Unity.Entities.Disabled so the Burst PacketSpawnerSystem query
                // skips them entirely, then clear any orbs already in the world.
                _resetAllSpawners?.Invoke(wis, null);
                DisableAllSpawners();
                DestroyExistingPackets();
            }
            else
            {
                // Remove Disabled from all spawner entities (query must include
                // disabled entities), then trigger route re-evaluation so cables
                // repopulate with fresh orbs.
                EnableAllSpawners();
                wis.RequestRouteEvaluation();
            }
        }

        // ── ECS spawner enable/disable ──────────────────────────────────────────

        // Adds Unity.Entities.Disabled to every spawner entity so the Burst
        // PacketSpawnerSystem query skips them without any Harmony patching.
        private static void DisableAllSpawners()
        {
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null) return;
                var em = world.EntityManager;

                var spawnerCT  = ComponentType.ReadOnly(
                    TypeManager.GetTypeIndex(Il2CppType.Of<PacketSpawnerComponent>()));
                var disabledCT = ComponentType.ReadWrite(
                    TypeManager.GetTypeIndex(Il2CppType.Of<Disabled>()));

                var builder = new EntityQueryBuilder(Allocator.Temp);
                builder = builder.AddAll(spawnerCT);
                var query = em.CreateEntityQuery(ref builder);
                builder.Dispose();

                int count = query.CalculateEntityCount();
                if (count > 0)
                {
                    em.AddComponent(query, disabledCT);
                    MelonLogger.Msg($"[CableThrottle] DisableAllSpawners: tagged {count} spawner(s) Disabled.");
                }
                query.Dispose();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CableThrottle] DisableAllSpawners error: {ex.Message}");
            }
        }

        // Removes Unity.Entities.Disabled from all spawner entities so the
        // PacketSpawnerSystem can see them again on the next tick.
        private static void EnableAllSpawners()
        {
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null) return;
                var em = world.EntityManager;

                var spawnerCT  = ComponentType.ReadOnly(
                    TypeManager.GetTypeIndex(Il2CppType.Of<PacketSpawnerComponent>()));
                var disabledCT = ComponentType.ReadWrite(
                    TypeManager.GetTypeIndex(Il2CppType.Of<Disabled>()));

                // Must use IncludeDisabledEntities so the query sees the entities
                // we tagged — by default disabled entities are invisible to queries.
                var builder = new EntityQueryBuilder(Allocator.Temp);
                builder = builder.AddAll(spawnerCT)
                                 .WithOptions(EntityQueryOptions.IncludeDisabledEntities);
                var query = em.CreateEntityQuery(ref builder);
                builder.Dispose();

                int count = query.CalculateEntityCount();
                if (count > 0)
                {
                    em.RemoveComponent(query, disabledCT);
                    MelonLogger.Msg($"[CableThrottle] EnableAllSpawners: un-tagged {count} spawner(s).");
                }
                query.Dispose();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CableThrottle] EnableAllSpawners error: {ex.Message}");
            }
        }

        private static void DestroyExistingPackets()
        {
            try
            {
                var world = World.DefaultGameObjectInjectionWorld;
                if (world == null) return;
                var em = world.EntityManager;

                var ct      = ComponentType.ReadOnly(
                    TypeManager.GetTypeIndex(Il2CppType.Of<PacketComponent>()));
                var builder = new EntityQueryBuilder(Allocator.Temp);
                builder     = builder.AddAll(ct);
                var query   = em.CreateEntityQuery(ref builder);
                builder.Dispose();

                int count = query.CalculateEntityCount();
                if (count > 0)
                {
                    em.DestroyEntity(query);
                    MelonLogger.Msg($"[CableThrottle] DestroyExistingPackets: destroyed {count} packet(s).");
                }
                query.Dispose();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CableThrottle] DestroyExistingPackets error: {ex.Message}");
            }
        }
    }

    internal static class PacketSpawnerPatches
    {
        // Prefix for ActivateSpawnersForCable and ActivateSpawnerOnCable.
        // When packets are hidden: skip the original entirely so no spawner
        // components are activated at all.
        // When visible: cap the IOPS value (__1) to MaxIOPS before passing through.
        internal static bool CapIops(ref float __1)
        {
            if (!CableThrottleMod.EnabledEntry.Value) return true;
            if (!CableThrottleMod.PacketsVisible)     return false; // skip original
            __1 = Mathf.Min(__1, CableThrottleMod.MaxIopsEntry.Value);
            return true;
        }
    }
}
