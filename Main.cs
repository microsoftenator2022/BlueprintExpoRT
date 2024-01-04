using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BlueprintExpoRT.Patches;

using HarmonyLib;

using Kingmaker.Blueprints;
using Kingmaker.Blueprints.JsonSystem;

using UnityEngine;

using UnityModManagerNet;

namespace BlueprintExpoRT
{
    internal class Main
    {
        internal static Main Instance { get; private set; } = null!;

        internal UnityModManager.ModEntry ModEntry;

        string DisplayName;

        internal UnityModManager.ModEntry.ModLogger Logger => ModEntry.Logger;

        internal Harmony Harmony;

        private Main(UnityModManager.ModEntry modEntry)
        {
            ModEntry = modEntry;

            DisplayName = ModEntry.Info.DisplayName;

            Harmony = new(ModEntry.Info.Id);
        }

        static bool Load(UnityModManager.ModEntry modEntry)
        {
            Instance = new(modEntry);

            Instance.ModEntry.OnGUI = Instance.OnGUI;

            Instance.Harmony.PatchAll();

            return true;
        }

        void OnGUI(UnityModManager.ModEntry modEntry)
        {
            var blueprintsCount = BlueprintsCache_Init.Total;

            if (BlueprintsCache_Init.Count <= 0)
            {
                GUILayout.Label("Waiting");
                return;
            }

            if (BlueprintsCache_Init.Count < blueprintsCount)
            {
                GUILayout.Label($"{BlueprintsCache_Init.Count}/{blueprintsCount}");
                return;
            }
            
            if (!BlueprintsCache_Init.Done)
            {
                GUILayout.Label("Writing zip");
                return;
            }
            
            GUILayout.Label("Done");
        }
    }
}
