using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;

using JetBrains.Annotations;

using Kingmaker;
using Kingmaker.Blueprints;
using Kingmaker.Blueprints.JsonSystem;
using Kingmaker.Localization;
using Kingmaker.Modding;

using MicroUtils;

using Newtonsoft.Json;

using UnityEngine;

using UnityModManagerNet;

namespace BlueprintExpoRT
{
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

    static class Extractor
    {
        readonly record struct BlueprintJson(string Name, string AssetId, string TypeName, string Json) { }

        public static readonly CancellationToken CancellationToken = new();

        public static long Dumped;
        public static long CompressQueued;
        public static long Compressed;
        public static long Errors;

        public static bool Done;

        public static long Completed => Dumped + Errors;
        public static long Total;

        static string ZipFilePath =>
            Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "blueprints.zip");

        const int ChunkSize = 128;

        static async Task ZipJson(ZipArchive archive, List<BlueprintJson> json)
        {
            try
            {
#if DEBUG
                Main.Instance.Logger.Log("Zip task begin");
#endif
                foreach (var bpj in json)
                {
                    if (CancellationToken.IsCancellationRequested)
                    {
                        Main.Instance.Logger.Log("Zip task cancelled");
                        return;
                    }

                    var path = $"{bpj.TypeName}/{bpj.AssetId}/{bpj.Name}.jbp";
#if DEBUG
                    Main.Instance.Logger.Log($"Save blueprint to {path}");
#endif
                    var entry = archive.CreateEntry(path);
                    using var stream = new StreamWriter(entry.Open());

                    stream.Write(bpj.Json);

                    await stream.FlushAsync();

                    Compressed++;
                }
            }
            catch (Exception e)
            {
                Main.Instance.Logger.LogException(e);
                throw;
            }
#if DEBUG
            Main.Instance.Logger.Log("Zip task complete");
#endif
        }

        public static async Task<bool> DumpBlueprints(ZipArchive archive)
        {
            try
            {
                var timer = Stopwatch.StartNew();

                var serializer = JsonSerializer.Create(Json.Settings);
                var lsConverter = new LocalizedStringConverter();
                serializer.Converters.Add(lsConverter);

                var serialized = new List<BlueprintJson>(ChunkSize);

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

                var zipTask = Option<Task>.None;

                foreach (var assetId in guids)
                {
                    if (CancellationToken.IsCancellationRequested)
                    {
                        Main.Instance.Logger.Log("Dump task cancelled");
                        return false;
                    }

                    try
                    {
                        var blueprint = ResourcesLibrary.TryGetBlueprint(assetId);

                        if (blueprint == null)
                            return false;

                        lsConverter.CurrentBlueprint = blueprint;
#if DEBUG
                        Main.Instance.Logger.Log($"Dump blueprint {blueprint.AssetGuid} {blueprint.name}");
#endif
                        StringWriter writer = new();

                        await Task.Run(() => serializer.Serialize(writer, new BlueprintJsonWrapper(blueprint)));

                        await writer.FlushAsync();

                        serialized.Add(new(blueprint.name, blueprint.AssetGuid, blueprint.GetType().ToString(), writer.ToString()));

                        Dumped++;

                        if (serialized.Count < ChunkSize && Completed < Total)
                            continue;

                        if (zipTask.IsSome)
                        {
#if DEBUG
                            Main.Instance.Logger.Log("Wait for zip task");
#endif
                            await zipTask.Value;
                        }
#if DEBUG
                        Main.Instance.Logger.Log("Dump task continue");
#endif
                        var toZip = serialized;

                        CompressQueued += toZip.Count;

                        zipTask = Option.Some(Task.Run(() => ZipJson(archive, toZip)));

                        serialized = new(ChunkSize);
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
                }

                if (zipTask.IsSome)
                    await zipTask.Value;

                timer.Stop();

                Main.Instance.Logger.Log($"Dump of {Completed}/{Total} blueprints completed in {timer.ElapsedMilliseconds}ms with {Errors} errors");

                Done = true;
            }
            catch (Exception e)
            {
                Main.Instance.Logger.LogException(e);
                throw;
            }

            return Done;
        }

        public static readonly List<string> ModIdWhitelist =
        [
            "0ToyBox0",
            "AllowModdedAchievements",
            "SteamWorkshopManager",
            "UnityExplorerLoader"
        ];

        public static bool OtherModsActive =>
            UnityModManager.ModEntries
                .Where(me => me.Active)
                .Select(me => me.Info.Id)
                .Any(id => id != Main.Instance.ModEntry.Info.Id && !ModIdWhitelist.Contains(id)) ||
            OwlcatModificationsManager.Instance.IsAnyModActive;

        public static string Status = "Waiting";

        public static IEnumerator Coroutine()
        {
            var targetPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "blueprints.zip");

            if (File.Exists(targetPath))
            {
                Main.Instance.Logger.Log($"{targetPath} already exists. Skipping.");
                Status = "Done";
                yield break;
            }

            if (OtherModsActive)
            {
                Main.Instance.Logger.Warning($"Mods are active. Skipping");
                Status = "Disabled - Mods Active";
                yield break;
            }

            if (File.Exists(ZipFilePath))
                File.Delete(ZipFilePath);

            var archive = ZipFile.Open(ZipFilePath, ZipArchiveMode.Create);
         
            var task = Task.Run(() => DumpBlueprints(archive));

            while (!task.IsCompleted)
            {
                if (Completed < Total)
                {
                    Status = "Serializing";
                }
                else
                {
                    Status = "Writing zip";
                }

                yield return null;
            }

            archive.Dispose();

            if (task.Result)
            {
                Status = "Copying zip";
                File.Copy(ZipFilePath, targetPath);
                Status = "Done";
            }
            else
            {
                Main.Instance.Logger.Error("Dump failed");
                File.Delete(ZipFilePath);
                Status = "Failed";
            }

            yield break;
        }
    }
}
