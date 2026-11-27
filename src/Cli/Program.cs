using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HardwareCatalogExporter.Core;
using HardwareCatalogExporter.Export;
using HardwareCatalogExporter.Openness;
using HardwareCatalogExporter.WinForms;

namespace HardwareCatalogExporter.Cli
{
    internal static class Program
    {
        private const int Success = 0, Partial = 1, Failure = 2;
        private const string Usage = @"HardwareCatalogExporter.exe [connection] --output FILE [options]
  --attach [--pid ID]   Attach to a running process (default)
  --start              Start TIA Portal with its interface
  --headless           Start TIA Portal without its interface
  --output FILE        Output filename or path
  --output-dir DIR     Output directory (combine with a relative filename)
  --format FORMAT      csv, json (default), or yaml
  --delimiter VALUE    semicolon (default) or comma for CSV
  --all                Export every returned row
  --family TEXT        Partial product family filter
  --article TEXT       Partial article number filter
  --category TEXT      Partial device category filter
  --all-versions       Include every catalog version (default)
  --latest             Select latest catalog versions, retaining ambiguous groups
  --search TEXT        API search string (default is empty)
  --compatibility ID   Request CPU compatibility (currently unsupported)
  --list-processes     List visible TIA Portal processes
  --inspect-api        Inspect local assembly signatures without connecting
  --verbose            Print connection and timing details
  --help               Print usage
Exit codes are 0 for success, 1 for partial output and 2 for failure.";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return Success;
            }
            try { return Run(Parse(args)); }
            catch (Exception exception)
            {
                Console.Error.WriteLine("Error: " + PortalSession.ErrorMessage(exception));
                return Failure;
            }
        }

        private static int Run(Dictionary<string, string> options)
        {
            if (options.ContainsKey("--help"))
            {
                Console.WriteLine(Usage);
                return Success;
            }
            if (options.ContainsKey("--compatibility"))
                throw new NotSupportedException(PortalSession.CompatibilityStatus);

            using (var session = new PortalSession())
            {
                if (options.ContainsKey("--inspect-api"))
                {
                    Console.WriteLine(session.Inspect());
                    return Success;
                }
                if (options.ContainsKey("--list-processes"))
                {
                    foreach (var process in session.Processes()) Console.WriteLine(process);
                    return Success;
                }
                int modes = new[] { "--attach", "--start", "--headless" }.Count(options.ContainsKey);
                bool start = options.ContainsKey("--start") || options.ContainsKey("--headless");
                string pidOption = Get(options, "--pid");
                if (modes > 1 || (start && pidOption != null))
                    throw new ArgumentException("Choose one connection mode and use --pid only when attaching.");
                if (options.ContainsKey("--latest") && options.ContainsKey("--all-versions"))
                    throw new ArgumentException("Choose one version selection mode.");
                var filter = new CatalogFilter
                {
                    Article = Get(options, "--article"),
                    Family = Get(options, "--family"),
                    Category = Get(options, "--category")
                };
                if (options.ContainsKey("--all") && (filter.Article != null || filter.Family != null || filter.Category != null))
                    throw new ArgumentException("--all cannot be combined with a filter.");
                string output = Get(options, "--output") ?? throw new ArgumentException("--output is required.");
                string directory = Get(options, "--output-dir");
                if (directory != null)
                {
                    if (Path.IsPathRooted(output)) throw new ArgumentException("--output must be relative when using --output-dir.");
                    output = Path.Combine(directory, output);
                }
                if (!Enum.TryParse(Get(options, "--format") ?? "json", true, out ExportFormat format) ||
                    !(new[] { "csv", "json", "yaml" }).Contains((Get(options, "--format") ?? "json").ToLowerInvariant()))
                    throw new ArgumentException("Use --format csv, json or yaml.");
                string delimiter = (Get(options, "--delimiter") ?? "semicolon").ToLowerInvariant();
                if (delimiter != "semicolon" && delimiter != "comma")
                    throw new ArgumentException("Use --delimiter semicolon or comma.");

                if (start) session.Start(options.ContainsKey("--headless"));
                else if (pidOption != null)
                {
                    if (!int.TryParse(pidOption, out int pid) || pid <= 0)
                        throw new ArgumentException("--pid must be a positive integer.");
                    session.Attach(pid);
                }
                else
                {
                    var processes = session.Processes();
                    if (processes.Count == 0) throw new InvalidOperationException("No TIA Portal V20 processes found. Start TIA Portal or use --start or --headless.");
                    if (processes.Count > 1)
                        throw new InvalidOperationException("Multiple TIA Portal processes found. Specify --pid. " + string.Join(", ", processes));
                    session.Attach(processes[0].Id);
                }

                bool verbose = options.ContainsKey("--verbose");
                if (verbose) Console.Error.WriteLine(session.Connection);
                var stopwatch = Stopwatch.StartNew();
                var result = session.Extract(Get(options, "--search") ?? "",
                    verbose ? (Action<string>)Console.Error.WriteLine : null);
                stopwatch.Stop();
                var items = result.Items.Where(filter.Matches).ToList();
                if (options.ContainsKey("--latest")) items = CatalogVersion.Latest(items);
                foreach (string warning in result.Warnings) Console.Error.WriteLine("Warning: " + warning);
                CatalogExporter.Write(output, format, items, result.Warnings, delimiter == "comma" ? ',' : ';');
                if (verbose) Console.Error.WriteLine("Extraction took " + stopwatch.Elapsed + ".");
                Console.WriteLine("Exported " + items.Count + " rows to " + Path.GetFullPath(output));
                return result.Warnings.Count == 0 ? Success : Partial;
            }
        }

        private static string Get(Dictionary<string, string> options, string key) =>
            options.TryGetValue(key, out string value) ? value : null;

        private static Dictionary<string, string> Parse(string[] args)
        {
            var flags = new HashSet<string> { "--attach", "--start", "--headless", "--all", "--all-versions",
                "--latest", "--verbose", "--list-processes", "--inspect-api", "--help" };
            var valued = new HashSet<string> { "--pid", "--output", "--output-dir", "--format", "--delimiter",
                "--family", "--article", "--category", "--search", "--compatibility" };
            var options = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (options.ContainsKey(key)) throw new ArgumentException("Repeated option " + key);
                if (flags.Contains(key)) options.Add(key, null);
                else if (valued.Contains(key))
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Missing value for " + key);
                    options.Add(key, args[i]);
                }
                else throw new ArgumentException("Unknown option " + key + ". Use --help for usage.");
            }
            return options;
        }
    }
}
