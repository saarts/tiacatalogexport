using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using System.Windows.Forms;
using HardwareCatalogExporter.Core;
using HardwareCatalogExporter.Export;
using HardwareCatalogExporter.Openness;

namespace HardwareCatalogExporter.WinForms
{
    internal sealed class MainForm : Form
    {
        private readonly PortalWorker worker = new PortalWorker();
        private readonly ComboBox processes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        private readonly ComboBox scope = Choices("Complete query result", "Filtered entries", "Selected entries");
        private readonly ComboBox format = Choices("JSON", "CSV", "YAML");
        private readonly ComboBox delimiter = Choices("Semicolon", "Comma");
        private readonly CheckBox latest = new CheckBox { Text = "Latest catalog version", AutoSize = true };
        private readonly TextBox apiSearch = new TextBox { Width = 140, AccessibleName = "API search" };
        private readonly Label connection = new Label { Text = "Disconnected", AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly Label extractionStatus = new Label { AutoSize = true };
        private readonly Button cancelExtraction = new Button { Text = "Cancel extraction", AutoSize = true, Enabled = false };
        private CancellationTokenSource extractionCancellation;
        private readonly Label status = new Label { AutoSize = true };
        private readonly ProgressBar progress = new ProgressBar { Width = 120 };
        private readonly TextBox messages = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        private readonly DataGridView grid = new DataGridView
        {
            Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true, MultiSelect = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable
        };
        private readonly Button refresh, attach, startGui, startHeadless, disconnect, extract, export, copy;
        private readonly Timer filterTimer = new Timer { Interval = 250 };
        private readonly List<Func<CatalogItem, string>> columns = new List<Func<CatalogItem, string>>();
        private readonly CatalogFilter filter = new CatalogFilter();
        private CatalogResult result = new CatalogResult();
        private CatalogStatistics totals;
        private List<CatalogItem> visible = new List<CatalogItem>();
        private string duration = "—";
        private bool connected, busy, closeReady;
        private int sortColumn = -1;
        private SortOrder sortOrder = SortOrder.None;

        public MainForm()
        {
            Text = "TIA Portal V20 Hardware Catalog Exporter";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            MinimumSize = new Size(850, 640);
            Size = new Size(1350, 800);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 8
            };
            for (int i = 0; i < layout.RowCount; i++)
                layout.RowStyles.Add(i == 4 ? new RowStyle(SizeType.Percent, 100) :
                    i == 7 ? new RowStyle(SizeType.Absolute, 84) : new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            refresh = ActionButton("Refresh", async () => await RefreshProcesses());
            attach = ActionButton("Attach", async () =>
            {
                if (processes.SelectedItem is PortalProcess selected)
                    await Connect(session => session.Attach(selected.Id));
            });
            startGui = ActionButton("Start with interface", async () => await Connect(session => session.Start(false)));
            startHeadless = ActionButton("Start headless", async () => await Connect(session => session.Start(true)));
            disconnect = ActionButton("Disconnect", async () => await Work(async () =>
            {
                try { await worker.Run(session => { session.Disconnect(); return true; }); }
                finally
                {
                    connected = false;
                    connection.Text = "Disconnected";
                }
            }));
            extract = ActionButton("Extract catalog", async () => await Extract());
            cancelExtraction.Click += (sender, args) =>
            {
                extractionCancellation?.Cancel();
                cancelExtraction.Enabled = false;
                Log("Cancellation requested. Waiting for the current Siemens API call to return.");
            };

            layout.Controls.Add(Flow(Caption("TIA Portal process"), processes, refresh, attach, startGui, startHeadless, disconnect), 0, 0);
            layout.Controls.Add(Flow(connection, Caption("API search"), apiSearch, extract, cancelExtraction), 0, 1);
            layout.Controls.Add(Flow(FilterBox("Order number", value => filter.Article = value),
                FilterBox("Device name", value => filter.Name = value),
                FilterBox("TypeIdentifier", value => filter.Identifier = value),
                FilterBox("Product family", value => filter.Family = value),
                FilterBox("Generation", value => filter.Generation = value)), 0, 2);
            layout.Controls.Add(Flow(FilterBox("Device category", value => filter.Category = value),
                FilterBox("Module category", value => filter.ModuleType = value),
                FilterBox("Catalog version", value => filter.Version = value),
                FilterBox("Catalog path", value => filter.Path = value)), 0, 3);

            AddColumn("Order number", 140, item => item.ArticleNumber);
            AddColumn("Device name", 200, item => item.TypeName);
            AddColumn("Catalog version", 102, item => item.Version);
            AddColumn("TypeIdentifier", 270, item => item.TypeIdentifier);
            AddColumn("Normalized TypeIdentifier", 210, item => item.TypeIdentifierNormalized);
            AddColumn("Manufacturer", 105, item => item.Manufacturer ?? CatalogClassifier.Unknown);
            AddColumn("Product family", 115, item => item.ProductFamily ?? CatalogClassifier.Unknown);
            AddColumn("Generation", 85, item => item.HardwareGeneration ?? CatalogClassifier.Unknown);
            AddColumn("Device category", 120, item => item.DeviceCategory ?? CatalogClassifier.Unknown);
            AddColumn("Module category", 120, item => item.ModuleType ?? CatalogClassifier.Unknown);
            AddColumn("Catalog path", 250, item => item.CatalogPath);
            AddColumn("Description", 250, item => item.Description);
            grid.CellValueNeeded += (sender, args) =>
            {
                if (args.RowIndex < visible.Count) args.Value = columns[args.ColumnIndex](visible[args.RowIndex]);
            };
            grid.ColumnHeaderMouseClick += (sender, args) =>
            {
                sortOrder = sortColumn == args.ColumnIndex && sortOrder == SortOrder.Ascending
                    ? SortOrder.Descending : SortOrder.Ascending;
                if (sortColumn >= 0) grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = SortOrder.None;
                sortColumn = args.ColumnIndex;
                grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = sortOrder;
                ApplyFilters();
            };
            grid.SelectionChanged += (sender, args) => UpdateButtons();
            grid.KeyDown += (sender, args) =>
            {
                if (!args.Control || args.KeyCode != Keys.C || !copy.Enabled) return;
                CopyIdentifiers();
                args.SuppressKeyPress = true;
            };
            layout.Controls.Add(grid, 0, 4);

            copy = ActionButton("Copy exact identifiers", () => { CopyIdentifiers(); return Task.CompletedTask; });
            export = ActionButton("Export", async () => await Export());
            var compatibility = new Button { Text = "CPU-compatible modules", AutoSize = true };
            compatibility.Click += (sender, args) => Log(PortalSession.CompatibilityStatus);
            layout.Controls.Add(Flow(copy, Caption("Export scope"), scope, format, delimiter, latest, export, compatibility), 0, 5);
            layout.Controls.Add(Flow(status, progress, extractionStatus), 0, 6);
            layout.Controls.Add(messages, 0, 7);

            format.SelectedIndexChanged += (sender, args) => UpdateButtons();
            scope.SelectedIndexChanged += (sender, args) => UpdateButtons();
            processes.SelectedIndexChanged += (sender, args) => UpdateButtons();
            filterTimer.Tick += (sender, args) => { filterTimer.Stop(); ApplyFilters(); };
            Shown += async (sender, args) => await RefreshProcesses();
            FormClosing += ClosingForm;
            FormClosed += (sender, args) => filterTimer.Dispose();
            SetResult(new CatalogResult());
        }

        private void AddColumn(string name, int width, Func<CatalogItem, string> read)
        {
            columns.Add(read);
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = name, Width = width, SortMode = DataGridViewColumnSortMode.Programmatic
            });
        }

        private void SetResult(CatalogResult value)
        {
            result = value;
            totals = new CatalogStatistics(value.Items);
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            var matching = result.Items.Where(filter.Matches);
            if (sortColumn >= 0)
            {
                IComparer<string> comparison = StringComparer.OrdinalIgnoreCase;
                if (sortColumn == 2) comparison = Comparer<string>.Create(CatalogVersion.Compare);
                matching = sortOrder == SortOrder.Ascending
                    ? matching.OrderBy(columns[sortColumn], comparison)
                    : matching.OrderByDescending(columns[sortColumn], comparison);
            }
            visible = matching.ToList();
            grid.RowCount = visible.Count;
            grid.ClearSelection();
            grid.Invalidate();
            status.Text = $"Entries {totals.Total:N0}   Articles {totals.Articles:N0}   Exact identifiers {totals.Identifiers:N0}   Matches {visible.Count:N0}   Extraction {duration}";
            UpdateButtons();
        }

        private async Task Work(Func<Task> operation)
        {
            if (busy) return;
            busy = true;
            UpdateButtons();
            try { await operation(); }
            catch (Exception exception) { Log("Error · " + PortalSession.ErrorMessage(exception)); }
            finally { busy = false; UpdateButtons(); }
        }

        private Task RefreshProcesses() => Work(async () =>
        {
            int? selected = (processes.SelectedItem as PortalProcess)?.Id;
            var state = await worker.Run(session => (Processes: session.Processes(), Connection: session.Connection));
            var list = state.Processes;
            connection.Text = state.Connection;
            processes.Items.Clear();
            foreach (var process in list) processes.Items.Add(process);
            if (list.Count > 0)
                processes.SelectedItem = list.FirstOrDefault(process => process.Id == selected) ?? list[0];
            else Log("No visible TIA Portal V20 processes.");
        });

        private Task Connect(Action<PortalSession> connect) => Work(async () =>
        {
            connection.Text = "Connecting…";
            try
            {
                connection.Text = await worker.Run(session => { connect(session); return session.Connection; });
                connected = true;
                duration = "—";
                SetResult(new CatalogResult());
            }
            catch
            {
                connection.Text = "Disconnected";
                throw;
            }
        });

        private Task Extract() => Work(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            string search = apiSearch.Text;
            string phase = "Starting catalog extraction";
            progress.Style = ProgressBarStyle.Marquee;
            Log(string.IsNullOrEmpty(search) ? "Extracting catalog with a blank API search."
                : "Extracting catalog with API search “" + search + "”.");
            using (var cancellation = new CancellationTokenSource())
            using (var timer = new Timer { Interval = 500 })
            {
                extractionCancellation = cancellation;
                cancelExtraction.Enabled = true;
                void ShowPhase()
                {
                    string prefix = cancellation.IsCancellationRequested ? "Cancelling · " : "";
                    extractionStatus.Text = $"{prefix}{phase} · {stopwatch.Elapsed:hh\\:mm\\:ss}";
                }
                IProgress<string> reporter = new Progress<string>(value =>
                {
                    if (extractionCancellation != cancellation) return;
                    phase = value;
                    ShowPhase();
                });
                timer.Tick += (sender, args) => ShowPhase();
                ShowPhase();
                timer.Start();
                try
                {
                    var extracted = await worker.Run(session => session.Extract(search,
                        reporter.Report, cancellation.Token));
                    cancellation.Token.ThrowIfCancellationRequested();
                    duration = stopwatch.Elapsed.TotalSeconds.ToString("0.00") + " s";
                    SetResult(extracted);
                    Log($"Extraction finished · {extracted.Items.Count:N0} rows in {duration}");
                    foreach (string warning in extracted.Warnings) Log("Warning · " + warning);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    Log("Extraction cancelled.");
                }
                finally
                {
                    extractionCancellation = null;
                    cancelExtraction.Enabled = false;
                    extractionStatus.Text = "";
                    progress.Style = ProgressBarStyle.Blocks;
                    progress.Value = 0;
                }
            }
        });

        private IEnumerable<CatalogItem> Selected() => grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(row => row.Index).Select(row => visible[row.Index]);

        private void CopyIdentifiers()
        {
            try
            {
                var exact = Selected().Select(item => item.TypeIdentifier).Where(value => !string.IsNullOrEmpty(value));
                string text = string.Join(Environment.NewLine, exact);
                if (text.Length > 0) Clipboard.SetText(text);
            }
            catch (Exception exception) { Log("Clipboard error · " + exception.Message); }
        }

        private async Task Export()
        {
            if (filterTimer.Enabled)
            {
                filterTimer.Stop();
                ApplyFilters();
                if (!export.Enabled) return;
            }
            var exportFormat = (ExportFormat)Enum.Parse(typeof(ExportFormat), (string)format.SelectedItem, true);
            string extension = exportFormat.ToString().ToLowerInvariant();
            using (var dialog = new SaveFileDialog { Filter = extension.ToUpperInvariant() + " files|*." + extension,
                       DefaultExt = extension, FileName = "catalog." + extension, AddExtension = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var items = (scope.SelectedIndex == 0 ? result.Items : scope.SelectedIndex == 1 ? visible : Selected()).ToList();
                bool latestOnly = latest.Checked;
                char separator = delimiter.SelectedIndex == 1 ? ',' : ';';
                string path = dialog.FileName;
                var reporter = new Progress<int>(value => progress.Value = value);
                await Work(async () =>
                {
                    int count = await Task.Run(() =>
                    {
                        if (latestOnly) items = CatalogVersion.Latest(items);
                        CatalogExporter.Write(path, exportFormat, items, result.Warnings, separator, reporter);
                        return items.Count;
                    });
                    Log("Exported " + count + " rows to " + path);
                });
            }
        }

        private async void ClosingForm(object sender, FormClosingEventArgs args)
        {
            if (closeReady) return;
            args.Cancel = true;
            if (busy)
            {
                Log("Wait for the current operation to finish before closing.");
                return;
            }
            await Work(async () => await worker.Stop());
            closeReady = true;
            Close();
        }

        private void UpdateButtons()
        {
            if (export == null) return;
            refresh.Enabled = !busy;
            attach.Enabled = !busy && !connected && processes.SelectedItem != null;
            startGui.Enabled = startHeadless.Enabled = !busy && !connected;
            disconnect.Enabled = extract.Enabled = !busy && connected;
            copy.Enabled = !busy && grid.SelectedRows.Count > 0;
            export.Enabled = !busy && (scope.SelectedIndex == 0 ? result.Items.Count > 0 :
                scope.SelectedIndex == 1 ? visible.Count > 0 : grid.SelectedRows.Count > 0);
            delimiter.Enabled = format.SelectedIndex == 1;
        }

        private void Log(string message) => messages.AppendText(message + Environment.NewLine);

        private Control FilterBox(string title, Action<string> change)
        {
            var box = new TextBox { Width = 155, AccessibleName = title };
            box.TextChanged += (sender, args) =>
            {
                change(box.Text);
                filterTimer.Stop();
                filterTimer.Start();
            };
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            panel.Controls.Add(Caption(title));
            panel.Controls.Add(box);
            return panel;
        }

        private static Label Caption(string title) => new Label { Text = title, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            panel.Controls.AddRange(controls);
            return panel;
        }

        private static ComboBox Choices(params string[] options)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = options[0].Length > 14 ? 165 : 100 };
            box.Items.AddRange(options);
            box.SelectedIndex = 0;
            return box;
        }

        private static Button ActionButton(string title, Func<Task> click)
        {
            var button = new Button { Text = title, AutoSize = true };
            button.Click += async (sender, args) => await click();
            return button;
        }
    }
}
