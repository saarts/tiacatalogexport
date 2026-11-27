using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace HardwareCatalogExporter.Core
{
    internal sealed class CatalogItem
    {
        public string ArticleNumber { get; set; }
        public string TypeName { get; set; }
        public string Version { get; set; }
        public string TypeIdentifier { get; set; }
        public string TypeIdentifierNormalized { get; set; }
        public string CatalogPath { get; set; }
        public string Description { get; set; }
        public string Manufacturer { get; set; }
        public string ProductFamily { get; set; }
        public string HardwareGeneration { get; set; }
        public string DeviceCategory { get; set; }
        public string ModuleType { get; set; }
        public string HardwareRevision { get; set; }
        public string FirmwareVersion { get; set; }
        public Dictionary<string, object> AdditionalMetadata { get; } = new Dictionary<string, object>();
    }

    internal sealed class CatalogResult
    {
        public List<CatalogItem> Items { get; } = new List<CatalogItem>();
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class CatalogVersion
    {
        private static readonly Regex Numeric = new Regex(@"^\s*[vV]?([0-9]+(?:\.[0-9]+)*)\s*$",
            RegexOptions.CultureInvariant);

        private static BigInteger[] Parse(string value)
        {
            var match = Numeric.Match(value ?? "");
            return match.Success ? match.Groups[1].Value.Split('.').Select(BigInteger.Parse).ToArray() : null;
        }

        private static int CompareParts(BigInteger[] left, BigInteger[] right)
        {
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                int comparison = (i < left.Length ? left[i] : BigInteger.Zero)
                    .CompareTo(i < right.Length ? right[i] : BigInteger.Zero);
                if (comparison != 0) return comparison;
            }
            return 0;
        }

        public static int Compare(string left, string right)
        {
            var a = Parse(left);
            var b = Parse(right);
            if (a == null && b == null) return StringComparer.OrdinalIgnoreCase.Compare(left, right);
            if (a == null) return -1;
            if (b == null) return 1;
            return CompareParts(a, b);
        }

        public static IEnumerable<string> Warnings(IEnumerable<CatalogItem> items)
        {
            int count = items.Count(item => Parse(item.Version) == null);
            if (count > 0)
                yield return count + " entries have missing or unrecognized catalog versions. Latest-version selection keeps all entries for each affected article.";
            int missingArticle = items.Count(item => string.IsNullOrWhiteSpace(item.ArticleNumber));
            if (missingArticle > 0)
                yield return missingArticle + " entries lack an article number and are retained individually.";
            int missingIdentifier = items.Count(item => string.IsNullOrWhiteSpace(item.TypeIdentifier));
            if (missingIdentifier > 0)
                yield return missingIdentifier + " entries lack an exact TypeIdentifier.";
        }

        public static List<CatalogItem> Latest(IEnumerable<CatalogItem> items)
        {
            return items.GroupBy(item => item.ArticleNumber, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group =>
                {
                    var versions = group.Select(item => new { Item = item, Parts = Parse(item.Version) }).ToList();
                    if (string.IsNullOrWhiteSpace(group.Key) || versions.Any(entry => entry.Parts == null))
                        return group.AsEnumerable();
                    var maximum = versions[0].Parts;
                    foreach (var version in versions)
                        if (CompareParts(version.Parts, maximum) > 0) maximum = version.Parts;
                    return versions.Where(entry => CompareParts(entry.Parts, maximum) == 0).Select(entry => entry.Item);
                }).ToList();
        }
    }

    internal static class CatalogClassifier
    {
        public const string Unknown = "Unknown";

        public static void Classify(CatalogItem item)
        {
            string label = string.Join(" ", item.CatalogPath, item.TypeName, item.Description);
            string family = string.Join(" ", item.ProductFamily, item.HardwareGeneration, label);
            var category = Category(item.TypeName) ?? Category(item.CatalogPath) ?? Category(item.Description);
            if (Matches(family, @"\bS7[\s-]*1200\b") &&
                Matches(family, @"\b(?:G2|GEN(?:ERATION)?[\s-]*2|SECOND GENERATION)\b"))
            {
                item.ProductFamily = "s7-1200-g2";
                item.HardwareGeneration = "g2";
            }
            else if (Matches(family, @"\bS7[\s-]*1200\b"))
            {
                item.ProductFamily = "s7-1200";
                if (Matches(label, @"\b(?:G1|GEN(?:ERATION)?\s*1|FIRST GENERATION)\b") ||
                    (!Matches(label, @"\b(?:G2|GEN(?:ERATION)?\s*2)\b") &&
                     Matches(item.ArticleNumber, @"^6ES7\s*2\d{2}-[A-Z0-9]{3}(?:[123][0-2]|40)-")))
                    item.HardwareGeneration = item.HardwareGeneration ?? "g1";
            }
            else if (Matches(family, @"\bS7[\s-]*1500\b")) item.ProductFamily = "s7-1500";
            else
            {
                foreach (var known in new[] { "ET 200SP", "ET 200MP", "ET 200AL", "ET 200eco PN", "SINAMICS G120C",
                    "SINAMICS G120", "SINAMICS V90", "SINAMICS S210", "SINAMICS S120",
                    "Unified Comfort", "Unified Basic", "Comfort Panel", "Basic Panel", "PC Runtime" })
                {
                    if (!Matches(family, @"\b" + Regex.Escape(known) + @"s?\b")) continue;
                    item.ProductFamily = known.ToLowerInvariant().Replace(' ', '-');
                    break;
                }
            }

            if (item.Manufacturer == null &&
                (Matches(label, @"\bSiemens\b") || Matches(item.ArticleNumber, @"^(?:6ES7|6AV2|6SL)")))
                item.Manufacturer = "Siemens";

            item.DeviceCategory = item.DeviceCategory ?? category;
            item.ModuleType = item.ModuleType ?? Category(item.TypeName);
        }

        private static string Category(string label)
        {
            if (Matches(label, @"\bCPU\b")) return "cpu";
            if (Matches(label, @"\b(?:RTD|thermocouple)\b")) return "temperature-input";
            if (Matches(label, @"\b(?:digital|DI|DQ|DO)\b") && Matches(label, @"\b(?:F-DI|F-DQ|fail.safe)\b"))
                return "fail-safe-io";
            if (Matches(label, @"\b(?:digital|DI|DQ|DO)\b")) return "digital-io";
            if (Matches(label, @"\b(?:AI|analog input)\b")) return "analog-input";
            if (Matches(label, @"\b(?:AQ|AO|analog output)\b")) return "analog-output";
            if (Matches(label, @"\banalog\b")) return "analog-io";
            if (Matches(label, @"\b(?:signal board|SB 12\d{2})\b")) return "signal-board";
            if (Matches(label, @"\b(?:communication board|CB 12\d{2})\b")) return "communication-board";
            if (Matches(label, @"\b(?:communication|CP|CM)\b")) return "communication";
            if (Matches(label, @"\b(?:energy meter|SM 1238)\b")) return "energy-meter";
            if (Matches(label, @"\b(?:IO-Link|SM 1278)\b")) return "io-link";
            if (Matches(label, @"\b(?:technology module|TM)\b")) return "technology";
            if (Matches(label, @"\b(?:HMI|panel|runtime)\b")) return "hmi";
            if (Matches(label, @"\b(?:SINAMICS|drive)\b")) return "drive";
            return null;
        }

        private static bool Matches(string input, string pattern) =>
            Regex.IsMatch(input ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal sealed class CatalogFilter
    {
        public string Article { get; set; }
        public string Name { get; set; }
        public string Identifier { get; set; }
        public string Family { get; set; }
        public string Generation { get; set; }
        public string Category { get; set; }
        public string ModuleType { get; set; }
        public string Version { get; set; }
        public string Path { get; set; }

        public bool Matches(CatalogItem item) =>
            Contains(item.ArticleNumber, Article) && Contains(item.TypeName, Name) &&
            (Contains(item.TypeIdentifier, Identifier) || Contains(item.TypeIdentifierNormalized, Identifier)) &&
            Contains(item.ProductFamily ?? CatalogClassifier.Unknown, Family) &&
            Contains(item.HardwareGeneration ?? CatalogClassifier.Unknown, Generation) &&
            Contains(item.DeviceCategory ?? CatalogClassifier.Unknown, Category) &&
            Contains(item.ModuleType ?? CatalogClassifier.Unknown, ModuleType) &&
            Contains(item.Version, Version) && Contains(item.CatalogPath, Path);

        private static bool Contains(string value, string query) =>
            string.IsNullOrWhiteSpace(query) || (value ?? "").IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal sealed class CatalogStatistics
    {
        public CatalogStatistics(ICollection<CatalogItem> items)
        {
            Total = items.Count;
            Articles = items.Select(item => item.ArticleNumber).Where(IsPresent).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Identifiers = items.Select(item => item.TypeIdentifier).Where(IsPresent).Distinct(StringComparer.Ordinal).Count();
        }

        private static bool IsPresent(string value) => !string.IsNullOrWhiteSpace(value);
        public int Total { get; }
        public int Articles { get; }
        public int Identifiers { get; }
    }
}
