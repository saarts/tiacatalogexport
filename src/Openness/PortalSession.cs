using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HardwareCatalogExporter.Core;

namespace HardwareCatalogExporter.Openness
{
    internal sealed class PortalProcess
    {
        public int Id { get; }
        public string ProjectPath { get; }
        public string Mode { get; }

        internal PortalProcess(int id, string projectPath, string mode)
        {
            Id = id;
            ProjectPath = projectPath;
            Mode = mode;
        }

        public override string ToString() => "PID " + Id + (Mode == null ? "" : " · " + Mode) +
            (string.IsNullOrEmpty(ProjectPath) ? "" : " · " + Path.GetFileNameWithoutExtension(ProjectPath));
    }

    internal sealed class PortalSession : IDisposable
    {
        public const string CompatibilityStatus =
            "CPU compatibility is unsupported. V20 catalog query semantics have not been verified on an installed system.";
        private const string ScopeWarning =
            "Catalog coverage is unverified. The export contains every row returned by the V20 Find(string) query.";
        private static readonly HashSet<string> ClassificationProperties = new HashSet<string>
        {
            "Manufacturer", "ProductFamily", "HardwareGeneration", "DeviceCategory", "ModuleType"
        };
        private Type portalType;
        private object portal;
        private string apiDirectory;

        private string connectionPrefix;

        public string Connection => Connected ? connectionPrefix + " · " + ReadProjectStatus(portal) : "Disconnected";
        public bool Connected => portal != null;

        private Type Load()
        {
            if (portalType != null) return portalType;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("TIA Portal Openness requires Windows and an x64 process.");

            string configured = Environment.GetEnvironmentVariable("TIA_V20_API_PATH");
            if (string.IsNullOrWhiteSpace(configured))
                configured = typeof(PortalSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(attribute => attribute.Key == "SiemensApiPath").Value;
            string directory = Path.GetFullPath(configured);
            string[] candidates = { directory, Path.Combine(directory, "net48") };
            apiDirectory = candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "Siemens.Engineering.dll")));
            if (apiDirectory == null)
                throw new FileNotFoundException("Siemens.Engineering.dll was not found. Install TIA Portal V20 Openness or set TIA_V20_API_PATH to the local V20 .NET Framework Public API directory.", directory);

            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            try
            {
                portalType = Assembly.LoadFrom(Path.Combine(apiDirectory, "Siemens.Engineering.dll"))
                    .GetType("Siemens.Engineering.TiaPortal", throwOnError: true);
                return portalType;
            }
            catch
            {
                AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
                throw;
            }
        }

        private Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            if (!name.Name.StartsWith("Siemens.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(apiDirectory, name.Name + ".dll");
            if (!File.Exists(path) || AssemblyName.GetAssemblyName(path).FullName != name.FullName) return null;
            return Assembly.LoadFrom(path);
        }

        public IReadOnlyList<PortalProcess> Processes() => ProcessHandles().Select(process =>
            new PortalProcess(ProcessId(process), ProjectPath(Read(process, "ProjectPath")), AsText(Read(process, "Mode"))))
            .ToList();

        private IEnumerable<object> ProcessHandles()
        {
            var type = Load();
            var method = type.GetMethod("GetProcesses", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null) ?? throw new MissingMethodException(type.FullName, "GetProcesses()");
            return Rows(Invoke(() => method.Invoke(null, null)));
        }

        private static int ProcessId(object process) =>
            Convert.ToInt32(ReadRequired(process, "Id"), CultureInfo.InvariantCulture);

        public void Attach(int id)
        {
            EnsureDisconnected();
            var process = ProcessHandles().FirstOrDefault(candidate => ProcessId(candidate) == id)
                ?? throw new InvalidOperationException("The selected TIA Portal process is no longer available.");
            var attach = process.GetType().GetMethod("Attach", Type.EmptyTypes)
                ?? throw new MissingMethodException(process.GetType().FullName, "Attach()");
            portal = Invoke(() => attach.Invoke(process, null));
            FinishConnection("Attached", id);
        }

        public void Start(bool headless)
        {
            EnsureDisconnected();
            var type = Load();
            var modeType = type.Assembly.GetType("Siemens.Engineering.TiaPortalMode", throwOnError: true);
            var mode = Enum.Parse(modeType, headless ? "WithoutUserInterface" : "WithUserInterface");
            var constructor = type.GetConstructor(new[] { modeType })
                ?? throw new MissingMethodException(type.FullName, ".ctor(TiaPortalMode)");
            portal = Invoke(() => constructor.Invoke(new[] { mode }));
            try
            {
                var processMethod = portal.GetType().GetMethod("GetCurrentProcess", Type.EmptyTypes)
                    ?? throw new MissingMethodException(type.FullName, "GetCurrentProcess()");
                var process = Invoke(() => processMethod.Invoke(portal, null));
                FinishConnection(headless ? "Started headless" : "Started with interface",
                    ProcessId(process));
            }
            catch
            {
                Disconnect();
                throw;
            }
        }

        private void FinishConnection(string kind, int pid)
        {
            if (!(portal is IDisposable))
            {
                portal = null;
                throw new InvalidOperationException("The API did not return a disposable TIA Portal session.");
            }
            connectionPrefix = "TIA Portal V20 · " + kind + " · PID " + pid;
        }

        private static string ProjectPath(object value) =>
            value is FileSystemInfo path ? path.FullName : value as string;

        internal static string ReadProjectStatus(object connectedPortal)
        {
            try
            {
                if (!(Read(connectedPortal, "Projects") is IEnumerable projects))
                    return "Project information unavailable";

                var labels = new List<string>();
                foreach (object project in projects)
                {
                    string name = Read(project, "Name") as string;
                    if (string.IsNullOrWhiteSpace(name))
                        name = Path.GetFileNameWithoutExtension(ProjectPath(Read(project, "Path")));
                    labels.Add(string.IsNullOrWhiteSpace(name) ? "Unnamed project" : name);
                }
                return labels.Count == 0 ? "No project open" : string.Join(", ", labels);
            }
            catch (Exception exception)
            {
                return "Project information unavailable (" + exception.GetType().Name + ")";
            }
        }

        private void EnsureDisconnected()
        {
            if (Connected) throw new InvalidOperationException("Disconnect the current TIA Portal session first.");
        }

        public string Inspect()
        {
            var type = Load();
            var catalog = Property(type, "HardwareCatalog")?.PropertyType
                ?? throw new MissingMemberException(type.FullName, "HardwareCatalog");
            var methods = catalog.GetMethods().Where(method => method.Name == "Find").ToArray();
            var itemTypes = methods.SelectMany(method => method.ReturnType.GetInterfaces()
                .Concat(new[] { method.ReturnType })).Where(candidate => candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                .Select(candidate => candidate.GetGenericArguments()[0]).Distinct();
            var lines = new List<string>
            {
                type.Assembly.FullName, type.Assembly.Location,
                "Product version " + System.Diagnostics.FileVersionInfo.GetVersionInfo(type.Assembly.Location).ProductVersion,
                catalog.FullName
            };
            lines.AddRange(type.GetMethods().Where(method => method.Name == "GetProcesses" ||
                method.Name == "GetCurrentProcess").Select(method => method.ToString()));
            lines.AddRange(methods.Select(method => method + " · parameter names: " +
                string.Join(", ", method.GetParameters().Select(parameter => parameter.Name))));
            foreach (var item in itemTypes)
            {
                lines.Add(item.FullName);
                lines.AddRange(item.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Select(property => "  " + property.PropertyType + " " + property.Name));
            }
            return string.Join(Environment.NewLine, lines);
        }

        public CatalogResult Extract(string search = "", Action<string> report = null,
            CancellationToken cancellation = default)
        {
            if (portal == null) throw new InvalidOperationException("Connect to TIA Portal first.");
            cancellation.ThrowIfCancellationRequested();
            report?.Invoke("Opening the TIA Portal hardware catalog");
            return ReadCatalog(ReadRequired(portal, "HardwareCatalog"), search, report, cancellation);
        }

        internal static CatalogResult ReadCatalog(object catalog, string search, Action<string> report = null,
            CancellationToken cancellation = default)
        {
            var find = catalog.GetType().GetMethod("Find", new[] { typeof(string) })
                ?? throw new MissingMethodException(catalog.GetType().FullName, "Find(string)");
            var result = new CatalogResult();
            result.Warnings.Add(ScopeWarning);
            var warnings = new HashSet<string>();
            var mappings = new Dictionary<string, Action<CatalogItem, string>>
            {
                ["ArticleNumber"] = (item, value) => item.ArticleNumber = value,
                ["TypeName"] = (item, value) => item.TypeName = value,
                ["Version"] = (item, value) => item.Version = value,
                ["TypeIdentifier"] = (item, value) => item.TypeIdentifier = value,
                ["TypeIdentifierNormalized"] = (item, value) => item.TypeIdentifierNormalized = value,
                ["CatalogPath"] = (item, value) => item.CatalogPath = value,
                ["Description"] = (item, value) => item.Description = value,
                ["Manufacturer"] = (item, value) => item.Manufacturer = value,
                ["ProductFamily"] = (item, value) => item.ProductFamily = value,
                ["HardwareGeneration"] = (item, value) => item.HardwareGeneration = value,
                ["DeviceCategory"] = (item, value) => item.DeviceCategory = value,
                ["ModuleType"] = (item, value) => item.ModuleType = value,
                ["HardwareRevision"] = (item, value) => item.HardwareRevision = value,
                ["FirmwareVersion"] = (item, value) => item.FirmwareVersion = value
            };
            var propertiesByType = new Dictionary<Type, PropertyInfo[]>();
            cancellation.ThrowIfCancellationRequested();
            report?.Invoke("Waiting for TIA Portal catalog search");
            var rows = Rows(Invoke(() => find.Invoke(catalog, new object[] { search })));
            cancellation.ThrowIfCancellationRequested();
            report?.Invoke("Reading catalog rows (0 read)");
            foreach (var raw in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                var item = new CatalogItem();
                var type = raw.GetType();
                if (!propertiesByType.TryGetValue(type, out var properties))
                {
                    properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(property => property.GetGetMethod() != null && property.GetIndexParameters().Length == 0 &&
                            (Scalar(property.PropertyType) || mappings.ContainsKey(property.Name))).ToArray();
                    propertiesByType.Add(type, properties);
                }

                foreach (var property in properties)
                {
                    cancellation.ThrowIfCancellationRequested();
                    object value;
                    try { value = property.GetValue(raw); }
                    catch (TargetInvocationException exception)
                    {
                        warnings.Add("Could not read catalog property " + property.Name + " (" +
                            (exception.InnerException ?? exception).GetType().Name + ").");
                        continue;
                    }
                    if (mappings.TryGetValue(property.Name, out var assign))
                    {
                        bool identifier = property.Name == "TypeIdentifier" || property.Name == "TypeIdentifierNormalized";
                        assign(item, identifier ? value as string : AsText(value));
                        if (identifier && value != null && !(value is string))
                            warnings.Add("Catalog property " + property.Name + " did not return a string.");
                        if (value != null && ClassificationProperties.Contains(property.Name))
                            item.AdditionalMetadata[property.Name] = AsText(value);
                    }
                    else item.AdditionalMetadata[property.Name] = value is Enum ? value.ToString() : value;
                }
                CatalogClassifier.Classify(item);
                result.Items.Add(item);
                if (result.Items.Count % 100 == 0)
                    report?.Invoke($"Reading catalog rows ({result.Items.Count:N0} read)");
                cancellation.ThrowIfCancellationRequested();
            }

            cancellation.ThrowIfCancellationRequested();
            report?.Invoke($"Preparing {result.Items.Count:N0} rows for display or export");
            if (result.Items.Count == 0) result.Warnings.Add("The catalog query returned zero rows.");
            result.Warnings.AddRange(warnings.OrderBy(value => value, StringComparer.Ordinal));
            result.Warnings.AddRange(CatalogVersion.Warnings(result.Items));
            return result;
        }

        private static bool Scalar(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type == typeof(string) || type.IsPrimitive || type.IsEnum || type == typeof(decimal);
        }

        private static IEnumerable<object> Rows(object value)
        {
            if (!(value is IEnumerable sequence) || value is string)
                throw new InvalidOperationException("The Siemens API did not return an enumerable result.");
            return sequence.Cast<object>();
        }

        private static PropertyInfo Property(Type type, string name) =>
            type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        private static object Read(object value, string name) =>
            Invoke(() => Property(value.GetType(), name)?.GetValue(value));

        private static object ReadRequired(object value, string name) => Read(value, name)
            ?? throw new InvalidOperationException("The API did not supply " + value.GetType().Name + "." + name + ".");

        private static string AsText(object value) =>
            value != null && Scalar(value.GetType()) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

        private static object Invoke(Func<object> call)
        {
            try { return call(); }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        public void Disconnect()
        {
            var connected = portal;
            portal = null;
            connectionPrefix = null;
            (connected as IDisposable)?.Dispose();
        }

        public void Dispose()
        {
            try { Disconnect(); }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }
        }

        public static string ErrorMessage(Exception exception)
        {
            string detail = exception.GetBaseException().Message;
            if (exception is UnauthorizedAccessException ||
                exception.GetType().Name.IndexOf("Security", StringComparison.OrdinalIgnoreCase) >= 0 ||
                exception.GetType().Name.IndexOf("Authorization", StringComparison.OrdinalIgnoreCase) >= 0)
                return detail + " Verify Siemens TIA Openness group membership and approve the Openness prompt in the interactive TIA Portal session.";
            return detail;
        }
    }
}
