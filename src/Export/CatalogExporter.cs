using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HardwareCatalogExporter.Core;
using Newtonsoft.Json;
using YamlDotNet.Serialization;

namespace HardwareCatalogExporter.Export
{
    internal enum ExportFormat { Csv, Json, Yaml }

    internal static class CatalogExporter
    {
        public static void Write(string path, ExportFormat format, IReadOnlyList<CatalogItem> items,
            IReadOnlyList<string> warnings, char delimiter = ';', IProgress<int> progress = null)
        {
            if (delimiter != ',' && delimiter != ';') throw new ArgumentOutOfRangeException(nameof(delimiter));
            path = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            progress?.Report(0);
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(format == ExportFormat.Csv)))
                {
                    switch (format)
                    {
                        case ExportFormat.Csv: WriteCsv(writer, items, delimiter, progress); break;
                        case ExportFormat.Json: WriteJson(writer, items, warnings, progress); break;
                        case ExportFormat.Yaml: WriteYaml(writer, items, warnings, progress); break;
                        default: throw new ArgumentOutOfRangeException(nameof(format));
                    }
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                progress?.Report(100);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static void WriteCsv(TextWriter writer, IReadOnlyList<CatalogItem> items, char delimiter, IProgress<int> progress)
        {
            writer.NewLine = "\r\n";
            writer.WriteLine(string.Join(delimiter.ToString(), new[] {
                "ArticleNumber", "TypeName", "Version", "TypeIdentifier", "TypeIdentifierNormalized",
                "CatalogPath", "Description", "Manufacturer", "ProductFamily", "HardwareGeneration",
                "DeviceCategory", "ModuleType", "HardwareRevision", "FirmwareVersion", "AdditionalMetadata"
            }));
            int last = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var fields = new[] { item.ArticleNumber, item.TypeName, item.Version, item.TypeIdentifier,
                    item.TypeIdentifierNormalized, item.CatalogPath, item.Description, item.Manufacturer,
                    item.ProductFamily, item.HardwareGeneration, item.DeviceCategory, item.ModuleType,
                    item.HardwareRevision, item.FirmwareVersion,
                    item.AdditionalMetadata.Count == 0 ? null : JsonConvert.SerializeObject(item.AdditionalMetadata) };
                writer.WriteLine(string.Join(delimiter.ToString(), fields.Select(value => Escape(value, delimiter))));
                Report(progress, i + 1, items.Count, ref last);
            }
        }

        private static string Escape(string value, char delimiter)
        {
            if (value == null) return "";
            return value.IndexOfAny(new[] { delimiter, '"', '\r', '\n' }) < 0
                ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static Dictionary<string, object> Header(IReadOnlyList<CatalogItem> items, IReadOnlyList<string> warnings)
        {
            var stats = new CatalogStatistics(items.ToList());
            return new Dictionary<string, object>
            {
                ["schema_version"] = "1.0",
                ["source"] = new Dictionary<string, object>
                {
                    ["application"] = "Siemens TIA Portal",
                    ["portal_version"] = "V20",
                    ["exported_at_utc"] = DateTime.UtcNow.ToString("O")
                },
                ["statistics"] = new Dictionary<string, int>
                {
                    ["total_entries"] = stats.Total,
                    ["unique_article_numbers"] = stats.Articles,
                    ["unique_type_identifiers"] = stats.Identifiers
                },
                ["warnings"] = warnings
            };
        }

        private static Dictionary<string, object> Item(CatalogItem item) => new Dictionary<string, object>
        {
            ["article_number"] = item.ArticleNumber,
            ["type_name"] = item.TypeName,
            ["catalog_version"] = item.Version,
            ["type_identifier"] = item.TypeIdentifier,
            ["type_identifier_normalized"] = item.TypeIdentifierNormalized,
            ["catalog_path"] = item.CatalogPath,
            ["description"] = item.Description,
            ["hardware_revision"] = item.HardwareRevision,
            ["firmware_version"] = item.FirmwareVersion,
            ["classification"] = new Dictionary<string, string>
            {
                ["manufacturer"] = item.Manufacturer,
                ["product_family"] = item.ProductFamily,
                ["hardware_generation"] = item.HardwareGeneration,
                ["device_category"] = item.DeviceCategory,
                ["module_type"] = item.ModuleType
            },
            ["additional_metadata"] = item.AdditionalMetadata
        };

        private static void WriteJson(TextWriter output, IReadOnlyList<CatalogItem> items,
            IReadOnlyList<string> warnings, IProgress<int> progress)
        {
            var serializer = JsonSerializer.CreateDefault();
            using (var writer = new JsonTextWriter(output) { Formatting = Formatting.Indented, CloseOutput = false })
            {
                writer.WriteStartObject();
                foreach (var pair in Header(items, warnings))
                {
                    writer.WritePropertyName(pair.Key);
                    serializer.Serialize(writer, pair.Value);
                }
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                int last = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    serializer.Serialize(writer, Item(items[i]));
                    Report(progress, i + 1, items.Count, ref last);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }

        private static void WriteYaml(TextWriter writer, IReadOnlyList<CatalogItem> items,
            IReadOnlyList<string> warnings, IProgress<int> progress)
        {
            int count = 0, last = 0;
            var document = Header(items, warnings);
            document["hardware_families"] = items.GroupBy(item => item.ProductFamily).Select(family =>
                new Dictionary<string, object>
                {
                    ["id"] = family.Key,
                    ["modules"] = family.GroupBy(item => item.ArticleNumber, StringComparer.OrdinalIgnoreCase)
                        .SelectMany(group => string.IsNullOrWhiteSpace(group.Key)
                            ? group.Select(item => Module(group.Key, new[] { item }))
                            : new[] { Module(group.Key, group) }).ToArray()
                }).ToArray();
            new SerializerBuilder().WithQuotingNecessaryStrings(quoteYaml1_1Strings: true).DisableAliases()
                .Build().Serialize(writer, document);

            Dictionary<string, object> Module(string article, IEnumerable<CatalogItem> group)
            {
                var versions = group.ToList();
                var names = versions.Select(item => item.TypeName).Distinct().ToList();
                var categories = versions.Select(item => item.DeviceCategory).Distinct().ToList();
                var result = new Dictionary<string, object>
                {
                    ["order_number"] = article,
                    ["name"] = names.Count == 1 ? names[0] : null,
                    ["category"] = categories.Count == 1 ? categories[0] : null,
                    ["versions"] = versions.OrderBy(item => item.Version, Comparer<string>.Create(CatalogVersion.Compare))
                        .Select(Item).ToArray()
                };
                count += versions.Count;
                Report(progress, count, items.Count, ref last, 70);
                return result;
            }
        }

        private static void Report(IProgress<int> progress, int count, int total, ref int last, int limit = 99)
        {
            int next = (int)((long)count * limit / total);
            if (next <= last) return;
            last = next;
            progress?.Report(next);
        }
    }
}
