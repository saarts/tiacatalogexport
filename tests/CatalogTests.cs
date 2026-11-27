using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HardwareCatalogExporter.Core;
using HardwareCatalogExporter.Export;
using HardwareCatalogExporter.Openness;
using Newtonsoft.Json.Linq;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace HardwareCatalogExporter.Tests
{
    public sealed class CatalogTests
    {
        [Fact]
        public void CatalogReadReportsSearchAndRowsAndHonorsCancellation()
        {
            var phases = new List<string>();
            var api = new FakeCatalog();
            PortalSession.ReadCatalog(api, "CPU", phases.Add);
            Assert.Equal("CPU", api.LastQuery);
            Assert.Collection(phases,
                phase => Assert.Contains("catalog search", phase),
                phase => Assert.Contains("0 read", phase),
                phase => Assert.Contains("2 rows", phase));

            using (var cancellation = new CancellationTokenSource())
            {
                Assert.Throws<OperationCanceledException>(() => PortalSession.ReadCatalog(api, "",
                    phase =>
                    {
                        if (phase.Contains("0 read")) cancellation.Cancel();
                    }, cancellation.Token));
            }
        }

        [Fact]
        public void ProjectStatusReadsCurrentNamesFromConnectedSession()
        {
            var session = new FakePortal();
            Assert.Equal("No project open", PortalSession.ReadProjectStatus(session));

            session.Projects.Add(new FakeProject { Name = "Production Line" });
            session.Projects.Add(new FakeProject { Path = new FileInfo("TestBench.ap20") });
            Assert.Equal("Production Line, TestBench", PortalSession.ReadProjectStatus(session));
            Assert.Equal("Project information unavailable", PortalSession.ReadProjectStatus(new object()));
        }

        private sealed class FakePortal
        {
            public List<FakeProject> Projects { get; } = new List<FakeProject>();
        }

        private sealed class FakeProject
        {
            public string Name { get; set; }
            public FileInfo Path { get; set; }
        }

        [Fact]
        public void CatalogQueryPreservesEveryRowAndUsesOnlyReturnedIdentifiers()
        {
            var api = new FakeCatalog();
            var result = PortalSession.ReadCatalog(api, "");
            Assert.Equal("", api.LastQuery);
            Assert.Collection(result.Items, first =>
            {
                Assert.Equal("OrderNumber:AbX/V2.9 ", first.TypeIdentifier);
                Assert.Equal("Verbatim:AbX/V2.9", first.TypeIdentifierNormalized);
                Assert.Equal("s7-1200-g2", first.ProductFamily);
                Assert.Equal("g2", first.HardwareGeneration);
                Assert.Equal(7, first.AdditionalMetadata["AdditionalCode"]);
            }, second =>
            {
                Assert.Null(second.TypeIdentifier);
                Assert.Null(second.TypeIdentifierNormalized);
                Assert.Null(second.ProductFamily);
            });
            Assert.Contains(result.Warnings, warning => warning.Contains("coverage is unverified"));
            Assert.Contains(result.Warnings, warning => warning.Contains("lack an exact TypeIdentifier"));
        }

        [Fact]
        public void LatestUsesNumericOrderingAndPreservesTiesAndAmbiguousGroups()
        {
            var input = new[]
            {
                Row("A", "V2.9"), Row("A", "V2.10"), Row("a", "2.10.0"),
                Row("B", "v4"), Row("B", "Preview-5"), Row("C", ""), Row(null, "V1"), Row(null, "V2")
            };
            Assert.Equal(input.Skip(1), CatalogVersion.Latest(input));
            Assert.True(CatalogVersion.Compare("V2.10", "V2.9") > 0);
            Assert.True(CatalogVersion.Compare("V2.10.1", "V2.10") > 0);
            Assert.NotEmpty(CatalogVersion.Warnings(input));
        }

        [Fact]
        public void FilterMatchesPartialTextAndNormalizedIdentifier()
        {
            var item = new CatalogItem { ArticleNumber = "ABC123", ProductFamily = "s7-1200-g2",
                TypeIdentifierNormalized = "System:Example" };
            var filter = new CatalogFilter { Article = "bc1", Family = "1200-G2", Identifier = "theme" };
            Assert.False(filter.Matches(item));
            filter.Identifier = "tem:exa";
            Assert.True(filter.Matches(item));
        }

        [Theory]
        [InlineData(',')]
        [InlineData(';')]
        public void CsvIncludesBomAndEscapesDelimitedMultilineFields(char delimiter)
        {
            WithFile(path =>
            {
                var item = new CatalogItem { Description = "first" + delimiter + "\"quoted\"\r\nsecond" };
                CatalogExporter.Write(path, ExportFormat.Csv, new[] { item }, Array.Empty<string>(), delimiter);
                Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(path).Take(3).ToArray());
                Assert.Contains("\"first" + delimiter + "\"\"quoted\"\"\r\nsecond\"", File.ReadAllText(path));
            });
        }

        [Fact]
        public void JsonAndYamlRoundTripExactIdentifiersAndKeepAllVersions()
        {
            WithFile(path =>
            {
                var items = new[] { Row("01234", "2.10", " OrderNumber:AbX/V2.10 "), Row("01234", "2.9", "On") };
                CatalogExporter.Write(path, ExportFormat.Json, items, new[] { "scope unverified" });
                var json = JObject.Parse(File.ReadAllText(path));
                Assert.Equal(items[0].TypeIdentifier, (string)json["items"][0]["type_identifier"]);
                Assert.Equal(JTokenType.Null, json["items"][0]["description"].Type);
                Assert.Equal(2, (int)json["statistics"]["total_entries"]);
                Assert.Equal("scope unverified", (string)json["warnings"][0]);

                CatalogExporter.Write(path, ExportFormat.Yaml, items, Array.Empty<string>());
                var yaml = new YamlStream();
                using (var input = File.OpenText(path)) yaml.Load(input);
                var root = (YamlMappingNode)yaml.Documents[0].RootNode;
                var module = root["hardware_families"][0]["modules"][0];
                Assert.Equal("01234", ((YamlScalarNode)module["order_number"]).Value);
                var versions = (YamlSequenceNode)module["versions"];
                Assert.Equal(2, versions.Children.Count);
                Assert.Equal(items[1].TypeIdentifier, ((YamlScalarNode)versions[0]["type_identifier"]).Value);
                Assert.Equal(items[0].TypeIdentifier, ((YamlScalarNode)versions[1]["type_identifier"]).Value);
                Assert.True(ScalarIsQuoted(versions[0]["type_identifier"]));
            });
        }

        private static bool ScalarIsQuoted(YamlNode node)
        {
            var style = ((YamlScalarNode)node).Style;
            return style == YamlDotNet.Core.ScalarStyle.SingleQuoted ||
                style == YamlDotNet.Core.ScalarStyle.DoubleQuoted;
        }

        private static CatalogItem Row(string article, string version, string identifier = null) =>
            new CatalogItem { ArticleNumber = article, Version = version, TypeIdentifier = identifier };

        private static void WithFile(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try { test(Path.Combine(directory, "export")); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private sealed class FakeCatalog
        {
            public string LastQuery { get; private set; }

            public IEnumerable<FakeItem> Find(string search)
            {
                LastQuery = search;
                yield return new FakeItem
                {
                    ArticleNumber = "ABC1", TypeName = "SM 1221 DI", CatalogPath = "SIMATIC/S7-1200 G2",
                    Version = "V2.9", TypeIdentifier = "OrderNumber:AbX/V2.9 ", TypeIdentifierNormalized = "Verbatim:AbX/V2.9"
                };
                yield return new FakeItem { TypeName = "Unfamiliar item" };
            }
        }

        private sealed class FakeItem
        {
            public string ArticleNumber { get; set; }
            public string TypeName { get; set; }
            public string CatalogPath { get; set; }
            public string Version { get; set; }
            public string TypeIdentifier { get; set; }
            public string TypeIdentifierNormalized { get; set; }
            public int AdditionalCode => 7;
        }
    }
}
