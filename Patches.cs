using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;

using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.JsonSystem;
using Kingmaker.Blueprints.JsonSystem.Converters;
using Kingmaker.Localization;
using Kingmaker.Modding;
using Kingmaker.RuleSystem.Rules;
using Kingmaker.Utility.UnityExtensions;

using Newtonsoft.Json;

using RogueTrader.SharedTypes;

namespace BlueprintExpoRT.Patches
{
    [HarmonyPatch]
    static class Converters_WriteJson
    {
        [HarmonyPatch(typeof(UnityObjectConverter), nameof(UnityObjectConverter.WriteJson))]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> _)
        {
            yield return new(OpCodes.Ldarg_0);
            yield return new(OpCodes.Ldarg_1);
            yield return new(OpCodes.Ldarg_2);
            yield return new(OpCodes.Ldarg_3);
            yield return CodeInstruction.Call(
                (UnityObjectConverter instance,
                JsonWriter writer,
                object value,
                JsonSerializer serializer) =>
                UnityObjectConverter_WriteJson(instance, writer, value, serializer));
            yield return new(OpCodes.Pop);
            yield return new(OpCodes.Ret);
        }

        static (string AssetId, long FileId)? GetAssetId(
            BlueprintReferencedAssets assets,
            UnityEngine.Object? obj)
        {
            if (obj == null)
                return null;

            var index = assets.IndexOf(obj);

            if (index < 0)
            {
                Main.Instance.Logger.Warning($"Asset {obj.name ?? "NULL"} not found");
                return null;
            }

            var entry = assets.m_Entries[index];

            return (entry.AssetId, entry.FileId);
        }

        //[HarmonyPatch(typeof(UnityObjectConverter), nameof(UnityObjectConverter.WriteJson))]
        //[HarmonyPrefix]
        static bool UnityObjectConverter_WriteJson(
            UnityObjectConverter __instance,
            JsonWriter writer,
            object value,
            JsonSerializer serializer)
        {

            var obj = value as UnityEngine.Object;
            if (obj == null)
            {
                writer.WriteNull();
                return false;
            }

            BlueprintReferencedAssets? assets = UnityObjectConverter.AssetList;

            if (assets == null)
            {
                writer.WriteNull();
                return false;
            }

            var assetData = GetAssetId(assets, obj);

            if (assetData is null)
            {
                writer.WriteNull();
                return false;
            }

            var (text, num) = assetData.Value;

            writer.WriteStartObject();
            writer.WritePropertyName("guid");
            writer.WriteValue(text);
            writer.WritePropertyName("fileid");
            writer.WriteValue(num);
            writer.WriteEndObject();

            return false;
        }

        [HarmonyPatch(typeof(SharedStringConverter), nameof(SharedStringConverter.WriteJson))]
        [HarmonyPostfix]
        static void SharedStringConverter_WriteJson(
            SharedStringConverter __instance,
            JsonWriter writer,
            object value,
            JsonSerializer serializer)
        {
            var asset = value as SharedStringAsset;
            if (asset == null)
                return;

            writer.WriteStartObject();
            writer.WritePropertyName("stringkey");
            writer.WriteValue(asset.String.Key);
            writer.WriteEndObject();
        }

        [HarmonyPatch(typeof(BlueprintsCache), nameof(BlueprintsCache.Init))]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        static void Postfix()
        {
            CoroutineRunner.Instance.StartCoroutine(Extractor.Coroutine());
        }
    }
}
