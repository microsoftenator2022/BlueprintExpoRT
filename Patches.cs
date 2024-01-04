using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem.Converters;
using Kingmaker.Blueprints.JsonSystem;

using Kingmaker.Blueprints;

using Kingmaker.Localization;

using Newtonsoft.Json;

using RogueTrader.SharedTypes;
using Kingmaker.RuleSystem.Rules;
using System.IO.Compression;
using Kingmaker.Modding;

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
    }

    class LocalizedStringConverter : JsonConverter
    {
        public SimpleBlueprint? CurrentBlueprint { get; set; }

        public override bool CanConvert(Type objectType) => objectType == typeof(LocalizedString);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
            throw new NotImplementedException();

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            var ls = value as LocalizedString;

            if (ls == null)
                return;

            var key = ls.m_Key;
            var sharedKey = ls.Shared?.String?.Key;

            var ownerPath = writer.Path.Split('.').Last();

            writer.WriteStartObject();
            writer.WritePropertyName("m_Key");
            writer.WriteValue(key);

            if (!string.IsNullOrEmpty(key) || !string.IsNullOrEmpty(sharedKey))
            {
                if (CurrentBlueprint != null)
                {
                    writer.WritePropertyName("m_OwnerString");
                    writer.WriteValue(CurrentBlueprint.AssetGuid);
                }

                writer.WritePropertyName("m_OwnerPropertyPath");
                writer.WriteValue(ownerPath);

                writer.WritePropertyName("Shared");

                if (sharedKey != null)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("stringkey");
                    writer.WriteValue(sharedKey);
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteNull();
                }
            }

            writer.WriteEndObject();
        }
    }

    [HarmonyPatch(typeof(BlueprintsCache), nameof(BlueprintsCache.Init))]
    static class BlueprintsCache_Init
    {
        record class BlueprintJson(string Name, string AssetId, string TypeName, string Json) { }

        static CancellationToken Cancelled = new();

        static Task? SerializeTask;
        static Task? ZipTask;

        static bool serializeComplete;

        static bool waitingShared;

        static List<BlueprintJson> shared = [];

        static readonly EventWaitHandle WaitShared = new(false, EventResetMode.ManualReset);

        public static long Count;
        public static long Errors;

        public static bool Done;
        
        public static long Completed => Count + Errors;
        public static long Total;
        
        static string ZipFilePath =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "blueprints.zip");

        static async void ZipJson()
        {

            ZipArchive? zipFile = null;
            try
            {
                if (File.Exists(ZipFilePath))
                    File.Delete(ZipFilePath);

                zipFile = ZipFile.Open(ZipFilePath, ZipArchiveMode.Create);

                Main.Instance.Logger.Log("Zip task begin");

                var json = shared.ToList();

                while (true)
                {
                    if (json.Count == 0 && shared.Count == 0)
                    {
                        if (serializeComplete)
                        {
#if DEBUG
                            Main.Instance.Logger.Log("Zip task complete");
#endif
                            Main.Instance.Logger.Log($"Dumped {Count} blueprints with {Errors} errors");
                            Done = true;
                            return;
                        }

                        waitingShared = true;
#if DEBUG
                        Main.Instance.Logger.Log("Zip task wait");
#endif

                        WaitShared.WaitOne();
#if DEBUG
                        Main.Instance.Logger.Log("Zip task signalled");
#endif
                        WaitShared.Reset();

                        waitingShared = false;

                        if (shared.Count > 0)
                        {
                            json = shared.ToList();
                            shared.Clear();
                        }
                        else
                        {
                            continue;
                        }
                    }

                    foreach (var bpj in json)
                    {
                        var path = $"{bpj.TypeName}/{bpj.AssetId}/{bpj.Name}.jbp";
#if DEBUG
                        Main.Instance.Logger.Log($"Save blueprint to {path}");
#endif

                        var entry = zipFile.CreateEntry(path);
                        var stream = new StreamWriter(entry.Open());

                        stream.Write(bpj.Json);
                        
                        await stream.FlushAsync();

                        stream.Close();
                    }

                    json.Clear();
                }
            }
            catch (Exception e)
            {
                Main.Instance.Logger.LogException(e);
            }
            finally
            {
                zipFile?.Dispose();
            }
        }

        const int ChunkSize = 100;

        static async void SerializeBlueprints()
        {
            var serializer = JsonSerializer.Create(Json.Settings);
            var lsConverter = new LocalizedStringConverter();
            serializer.Converters.Add(lsConverter);

            var serialized = new List<BlueprintJson>();

            void ResumeZipTask()
            {
                shared = serialized.ToList();
                WaitShared.Set();

                serialized.Clear();
            }

            try
            {
                ZipTask = Task.Run(ZipJson, Cancelled);

                var guids = ResourcesLibrary
                    .BlueprintsCache
                    .m_LoadedBlueprints.Select(e => e.Key)
                    .ToArray()
                    .Where(entryGuid =>
                        OwlcatModificationsManager.Instance.m_Modifications
                            .SelectMany(m => m.Blueprints)
                            .All(guid => guid != entryGuid))
                    .ToArray();

                Total = guids.Length;

                foreach (var assetId in guids)
                {
                    try
                    {
                        var blueprint = ResourcesLibrary.TryGetBlueprint(assetId);

                        if (blueprint == null)
                            return;

                        lsConverter.CurrentBlueprint = blueprint;
#if DEBUG
                        Main.Instance.Logger.Log($"Dump blueprint {blueprint.AssetGuid} {blueprint.name}");
#endif
                        StringWriter writer = new();

                        serializer.Serialize(writer, new BlueprintJsonWrapper(blueprint));

                        await writer.FlushAsync();

                        serialized.Add(new(blueprint.name, blueprint.AssetGuid, blueprint.GetType().ToString(), writer.ToString()));


                        if (waitingShared && serialized.Count > 0 && serialized.Count % ChunkSize == 0)
                        {
                            ResumeZipTask();
                        }

                    }
                    catch (TaskCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        Main.Instance.Logger.LogException(e);
                        Errors++;
                    }

                    Count++;
                }
            }
            catch (Exception e)
            {
                Main.Instance.Logger.LogException(e);
            }
            finally
            {
                serializeComplete = true;

                ResumeZipTask();
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        static void Postfix()
        {
            Task.Run(SerializeBlueprints, Cancelled);
        }
    }
}
