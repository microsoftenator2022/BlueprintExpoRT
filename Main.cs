using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

        public static string ZipFilePath =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "blueprints.zip");

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
            var blueprintsCount = Extractor.Total;

            GUILayout.BeginVertical();

            GUILayout.Label(Extractor.Status);

            if (Extractor.Status != "Waiting")
            {
                GUILayout.Label($"Dumped: {Extractor.Dumped}/{blueprintsCount}");
                GUILayout.Label($"Compressed: {Extractor.Compressed}/{Extractor.CompressQueued}");
            }
            
            GUILayout.EndVertical();
        }
    }
}
