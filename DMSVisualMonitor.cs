// DMSVisualMonitor.csproj
/*
<Project Sdk="Microsoft.NET.Sdk.WindowsDesktop">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <LangVersion>7.3</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="System.ComponentModel.Annotations" Version="5.0.0" />
  </ItemGroup>
</Project>
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using Newtonsoft.Json;

namespace DMSVisualMonitor
{
    // Core interfaces for the monitoring system
    public interface IDMSMonitorable
    {
        event EventHandler<DMSStateChangedEventArgs> StateChanged;
        string GetCurrentState();
        Dictionary<string, object> GetProperties();
    }

    public class DMSStateChangedEventArgs : EventArgs
    {
        public string PropertyName { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public DateTime Timestamp { get; set; }
        public string ComponentName { get; set; }
        public string ProjectName { get; set; }
        public TimeSpan Duration { get; set; }
        public string Phase { get; set; }
    }

    // New class for tracking phase timing
    public class PhaseTimer : IDisposable
    {
        private readonly Stopwatch _stopwatch;
        private readonly string _phaseName;
        private readonly string _componentName;
        private readonly string _projectName;
        private readonly Action<PhaseTimingResult> _onComplete;

        public PhaseTimer(string phaseName, string componentName, string projectName, Action<PhaseTimingResult> onComplete)
        {
            _phaseName = phaseName;
            _componentName = componentName;
            _projectName = projectName;
            _onComplete = onComplete;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _onComplete?.Invoke(new PhaseTimingResult
            {
                PhaseName = _phaseName,
                ComponentName = _componentName,
                ProjectName = _projectName,
                Duration = _stopwatch.Elapsed,
                StartTime = DateTime.Now - _stopwatch.Elapsed,
                EndTime = DateTime.Now
            });
        }
    }

    public class PhaseTimingResult
    {
        public string PhaseName { get; set; }
        public string ComponentName { get; set; }
        public string ProjectName { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    // Attribute to mark DMS components for monitoring
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
    public class DMSMonitorAttribute : Attribute
    {
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public bool AutoTrack { get; set; } = true;
        public string ProjectName { get; set; }
    }

    // Enhanced monitoring engine with multi-project and timing support
    public class DMSMonitoringEngine
    {
        private readonly ConcurrentDictionary<object, ObjectMonitor> _monitoredObjects = new ConcurrentDictionary<object, ObjectMonitor>();
        private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new ConcurrentDictionary<string, ProjectInfo>();
        private readonly List<PhaseTimingResult> _timingHistory = new List<PhaseTimingResult>();
        private readonly Timer _discoveryTimer;
        private readonly object _timingLock = new object();
        
        public event EventHandler<DMSStateChangedEventArgs> StateChanged;
        public event EventHandler<PhaseTimingResult> PhaseCompleted;

        public DMSMonitoringEngine()
        {
            // Auto-discovery timer runs every 5 seconds
            _discoveryTimer = new Timer(DiscoverNewObjects, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        public PhaseTimer StartPhase(string phaseName, string componentName, string projectName = "Default")
        {
            return new PhaseTimer(phaseName, componentName, projectName, OnPhaseCompleted);
        }

        private void OnPhaseCompleted(PhaseTimingResult result)
        {
            lock (_timingLock)
            {
                _timingHistory.Add(result);
                
                // Keep only last 1000 timing results
                if (_timingHistory.Count > 1000)
                {
                    _timingHistory.RemoveRange(0, _timingHistory.Count - 1000);
                }
            }

            PhaseCompleted?.Invoke(this, result);
        }

        public void RegisterObject(object obj, string name = null, string projectName = "Default")
        {
            if (obj == null) return;

            var monitor = new ObjectMonitor(obj, name ?? obj.GetType().Name, projectName);
            monitor.StateChanged += (s, e) => StateChanged?.Invoke(s, e);
            
            _monitoredObjects.TryAdd(obj, monitor);

            // Update project info
            var project = _projects.GetOrAdd(projectName, _ => new ProjectInfo { Name = projectName });
            project.AddObject(monitor);
        }

        public void UnregisterObject(object obj)
        {
            if (_monitoredObjects.TryRemove(obj, out var monitor))
            {
                // Remove from project
                if (_projects.TryGetValue(monitor.ProjectName, out var project))
                {
                    project.RemoveObject(monitor);
                }
                monitor.Dispose();
            }
        }

        private void DiscoverNewObjects(object state)
        {
            try
            {
                // Find all loaded assemblies and look for DMS-marked classes
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        var types = assembly.GetTypes()
                            .Where(t => t.GetCustomAttribute<DMSMonitorAttribute>() != null);

                        foreach (var type in types)
                        {
                            var typeAttribute = type.GetCustomAttribute<DMSMonitorAttribute>();
                            var projectName = typeAttribute?.ProjectName ?? "Default";

                            // Look for static instances or singletons
                            var staticFields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                .Where(f => f.FieldType == type);

                            foreach (var field in staticFields)
                            {
                                var instance = field.GetValue(null);
                                if (instance != null && !_monitoredObjects.ContainsKey(instance))
                                {
                                    RegisterObject(instance, $"{type.Name}.{field.Name}", projectName);
                                }
                            }
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        // Skip assemblies that can't be loaded
                        continue;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore discovery errors
            }
        }

        public List<MonitoredObjectInfo> GetMonitoredObjects()
        {
            return _monitoredObjects.Values
                .Select(m => new MonitoredObjectInfo
                {
                    Name = m.Name,
                    Type = m.ObjectType.Name,
                    ProjectName = m.ProjectName,
                    Properties = m.GetCurrentProperties(),
                    LastUpdate = m.LastUpdate
                }).ToList();
        }

        public List<ProjectInfo> GetProjects()
        {
            return _projects.Values.ToList();
        }

        public List<PhaseTimingResult> GetTimingHistory(string projectName = null, string phaseName = null)
        {
            lock (_timingLock)
            {
                var results = _timingHistory.AsEnumerable();

                if (!string.IsNullOrEmpty(projectName))
                    results = results.Where(r => r.ProjectName == projectName);

                if (!string.IsNullOrEmpty(phaseName))
                    results = results.Where(r => r.PhaseName == phaseName);

                return results.OrderByDescending(r => r.StartTime).ToList();
            }
        }

        public Dictionary<string, TimeSpan> GetAverageTimings(string projectName = null)
        {
            lock (_timingLock)
            {
                var results = _timingHistory.AsEnumerable();

                if (!string.IsNullOrEmpty(projectName))
                    results = results.Where(r => r.ProjectName == projectName);

                return results
                    .GroupBy(r => r.PhaseName)
                    .ToDictionary(g => g.Key, g => TimeSpan.FromMilliseconds(g.Average(r => r.Duration.TotalMilliseconds)));
            }
        }

        public void Dispose()
        {
            _discoveryTimer?.Dispose();
            foreach (var monitor in _monitoredObjects.Values)
            {
                monitor.Dispose();
            }
            _monitoredObjects.Clear();
        }
    }

    // Project information class
    public class ProjectInfo
    {
        private readonly List<ObjectMonitor> _objects = new List<ObjectMonitor>();
        private readonly object _lock = new object();

        public string Name { get; set; }
        public DateTime LastActivity { get; private set; } = DateTime.Now;
        public int ObjectCount { get; private set; }

        public void AddObject(ObjectMonitor monitor)
        {
            lock (_lock)
            {
                if (!_objects.Contains(monitor))
                {
                    _objects.Add(monitor);
                    ObjectCount = _objects.Count;
                    LastActivity = DateTime.Now;
                }
            }
        }

        public void RemoveObject(ObjectMonitor monitor)
        {
            lock (_lock)
            {
                _objects.Remove(monitor);
                ObjectCount = _objects.Count;
                LastActivity = DateTime.Now;
            }
        }

        public List<ObjectMonitor> GetObjects()
        {
            lock (_lock)
            {
                return new List<ObjectMonitor>(_objects);
            }
        }
    }

    // Enhanced individual object monitor
    public class ObjectMonitor : IDisposable
    {
        private readonly object _targetObject;
        private readonly Dictionary<string, object> _lastValues = new Dictionary<string, object>();
        private readonly Timer _pollTimer;
        
        public string Name { get; }
        public string ProjectName { get; }
        public Type ObjectType => _targetObject.GetType();
        public DateTime LastUpdate { get; private set; }
        
        public event EventHandler<DMSStateChangedEventArgs> StateChanged;

        public ObjectMonitor(object targetObject, string name, string projectName)
        {
            _targetObject = targetObject ?? throw new ArgumentNullException(nameof(targetObject));
            Name = name;
            ProjectName = projectName;
            LastUpdate = DateTime.Now;

            // Hook into INotifyPropertyChanged if available
            if (_targetObject is INotifyPropertyChanged notifyObject)
            {
                notifyObject.PropertyChanged += OnPropertyChanged;
            }

            // Poll for changes every 1 second for objects that don't implement INotifyPropertyChanged
            _pollTimer = new Timer(PollForChanges, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            
            // Initialize current values
            UpdateCurrentValues();
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            CheckPropertyChange(e.PropertyName);
        }

        private void PollForChanges(object state)
        {
            try
            {
                var properties = GetMonitorableProperties();
                foreach (var prop in properties)
                {
                    CheckPropertyChange(prop.Name);
                }
            }
            catch (Exception)
            {
                // Ignore polling errors
            }
        }

        private void CheckPropertyChange(string propertyName)
        {
            try
            {
                var prop = _targetObject.GetType().GetProperty(propertyName);
                if (prop?.CanRead == true)
                {
                    var newValue = prop.GetValue(_targetObject);
                    var newValueStr = JsonConvert.SerializeObject(newValue);
                    
                    if (_lastValues.TryGetValue(propertyName, out var oldValueObj))
                    {
                        var oldValueStr = JsonConvert.SerializeObject(oldValueObj);
                        if (oldValueStr != newValueStr)
                        {
                            _lastValues[propertyName] = newValue;
                            LastUpdate = DateTime.Now;
                            
                            StateChanged?.Invoke(this, new DMSStateChangedEventArgs
                            {
                                PropertyName = propertyName,
                                OldValue = oldValueObj,
                                NewValue = newValue,
                                Timestamp = LastUpdate,
                                ComponentName = Name,
                                ProjectName = ProjectName
                            });
                        }
                    }
                    else
                    {
                        _lastValues[propertyName] = newValue;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore property access errors
            }
        }

        private void UpdateCurrentValues()
        {
            var properties = GetMonitorableProperties();
            foreach (var prop in properties)
            {
                try
                {
                    if (prop.CanRead)
                    {
                        var value = prop.GetValue(_targetObject);
                        _lastValues[prop.Name] = value;
                    }
                }
                catch (Exception)
                {
                    // Ignore property access errors
                }
            }
        }

        private PropertyInfo[] GetMonitorableProperties()
        {
            return _targetObject.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && 
                           (p.GetCustomAttribute<DMSMonitorAttribute>()?.AutoTrack != false))
                .ToArray();
        }

        public Dictionary<string, object> GetCurrentProperties()
        {
            return new Dictionary<string, object>(_lastValues);
        }

        public void Dispose()
        {
            _pollTimer?.Dispose();
            if (_targetObject is INotifyPropertyChanged notifyObject)
            {
                notifyObject.PropertyChanged -= OnPropertyChanged;
            }
        }
    }

    public class MonitoredObjectInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string ProjectName { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    // Enhanced visual monitoring form with timing and multi-project support
    public partial class DMSVisualForm : Form
    {
        private readonly DMSMonitoringEngine _engine;
        private readonly TreeView _projectTreeView;
        private readonly PropertyGrid _propertyGrid;
        private readonly ListView _eventLog;
        private readonly ListView _timingLog;
        private readonly Chart _timingChart;
        private readonly Timer _refreshTimer;
        private readonly List<DMSStateChangedEventArgs> _recentEvents = new List<DMSStateChangedEventArgs>();
        private readonly List<PhaseTimingResult> _recentTimings = new List<PhaseTimingResult>();

        public DMSVisualForm()
        {
            InitializeComponent();
            
            _engine = new DMSMonitoringEngine();
            _engine.StateChanged += OnStateChanged;
            _engine.PhaseCompleted += OnPhaseCompleted;
            
            _refreshTimer = new Timer { Interval = 500, Enabled = true };
            _refreshTimer.Tick += RefreshDisplay;
        }

        private void InitializeComponent()
        {
            Text = "DMS Visual Monitor - Multi-Project with Timing";
            Size = new Size(1400, 900);
            StartPosition = FormStartPosition.CenterScreen;

            var mainSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 350
            };

            var rightSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 300
            };

            var bottomSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 400
            };

            var timingSplitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 200
            };

            // Project tree view
            _projectTreeView = new TreeView
            {
                Dock = DockStyle.Fill,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true
            };
            _projectTreeView.AfterSelect += ProjectTreeView_AfterSelect;

            // Property grid for selected object
            _propertyGrid = new PropertyGrid
            {
                Dock = DockStyle.Fill,
                PropertySort = PropertySort.Alphabetical
            };

            // Event log
            _eventLog = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            _eventLog.Columns.Add("Time", 100);
            _eventLog.Columns.Add("Project", 100);
            _eventLog.Columns.Add("Component", 120);
            _eventLog.Columns.Add("Property", 100);
            _eventLog.Columns.Add("Old Value", 120);
            _eventLog.Columns.Add("New Value", 120);

            // Timing log
            _timingLog = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            _timingLog.Columns.Add("Time", 100);
            _timingLog.Columns.Add("Project", 100);
            _timingLog.Columns.Add("Component", 120);
            _timingLog.Columns.Add("Phase", 120);
            _timingLog.Columns.Add("Duration (ms)", 100);

            // Timing chart
            _timingChart = new Chart
            {
                Dock = DockStyle.Fill
            };
            _timingChart.ChartAreas.Add(new ChartArea("MainArea"));
            _timingChart.Legends.Add(new Legend("MainLegend"));
            _timingChart.Series.Add(new Series("PhaseDurations")
            {
                ChartType = SeriesChartType.Column
            });

            // Layout
            mainSplitter.Panel1.Controls.Add(_projectTreeView);
            rightSplitter.Panel1.Controls.Add(_propertyGrid);
            bottomSplitter.Panel1.Controls.Add(_eventLog);
            timingSplitter.Panel1.Controls.Add(_timingLog);
            timingSplitter.Panel2.Controls.Add(_timingChart);
            bottomSplitter.Panel2.Controls.Add(timingSplitter);
            rightSplitter.Panel2.Controls.Add(bottomSplitter);
            mainSplitter.Panel2.Controls.Add(rightSplitter);

            Controls.Add(mainSplitter);

            // Menu
            var menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("File");
            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Close());
            fileMenu.DropDownItems.Add(exitItem);
            menuStrip.Items.Add(fileMenu);

            var toolsMenu = new ToolStripMenuItem("Tools");
            var clearLogItem = new ToolStripMenuItem("Clear Event Log", null, (s, e) => ClearEventLog());
            var clearTimingItem = new ToolStripMenuItem("Clear Timing Log", null, (s, e) => ClearTimingLog());
            var exportTimingItem = new ToolStripMenuItem("Export Timing Data", null, (s, e) => ExportTimingData());
            toolsMenu.DropDownItems.AddRange(new ToolStripItem[] { clearLogItem, clearTimingItem, exportTimingItem });
            menuStrip.Items.Add(toolsMenu);

            var viewMenu = new ToolStripMenuItem("View");
            var refreshItem = new ToolStripMenuItem("Refresh", null, (s, e) => RefreshDisplay(null, null));
            viewMenu.DropDownItems.Add(refreshItem);
            menuStrip.Items.Add(viewMenu);

            Controls.Add(menuStrip);
            MainMenuStrip = menuStrip;

            // Add labels
            var projectLabel = new Label { Text = "Projects & Components", Dock = DockStyle.Top, Height = 20 };
            mainSplitter.Panel1.Controls.Add(projectLabel);
            projectLabel.BringToFront();
        }

        private void OnStateChanged(object sender, DMSStateChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, DMSStateChangedEventArgs>(OnStateChanged), sender, e);
                return;
            }

            _recentEvents.Add(e);
            
            // Keep only last 1000 events
            if (_recentEvents.Count > 1000)
            {
                _recentEvents.RemoveRange(0, _recentEvents.Count - 1000);
            }

            // Add to event log
            var item = new ListViewItem(new[]
            {
                e.Timestamp.ToString("HH:mm:ss.fff"),
                e.ProjectName ?? "Default",
                e.ComponentName ?? "Unknown",
                e.PropertyName ?? "Unknown",
                e.OldValue?.ToString() ?? "null",
                e.NewValue?.ToString() ?? "null"
            });
            
            _eventLog.Items.Insert(0, item);
            
            // Keep only last 100 visible events
            while (_eventLog.Items.Count > 100)
            {
                _eventLog.Items.RemoveAt(_eventLog.Items.Count - 1);
            }
        }

        private void OnPhaseCompleted(object sender, PhaseTimingResult e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, PhaseTimingResult>(OnPhaseCompleted), sender, e);
                return;
            }

            _recentTimings.Add(e);

            // Keep only last 1000 timing results
            if (_recentTimings.Count > 1000)
            {
                _recentTimings.RemoveRange(0, _recentTimings.Count - 1000);
            }

            // Add to timing log
            var item = new ListViewItem(new[]
            {
                e.StartTime.ToString("HH:mm:ss.fff"),
                e.ProjectName ?? "Default",
                e.ComponentName ?? "Unknown",
                e.PhaseName ?? "Unknown",
                $"{e.Duration.TotalMilliseconds:F1}"
            });
            
            _timingLog.Items.Insert(0, item);
            
            // Keep only last 100 visible timing results
            while (_timingLog.Items.Count > 100)
            {
                _timingLog.Items.RemoveAt(_timingLog.Items.Count - 1);
            }

            UpdateTimingChart();
        }

        private void UpdateTimingChart()
        {
            try
            {
                var series = _timingChart.Series["PhaseDurations"];
                series.Points.Clear();

                // Get average timings for last 50 phase completions
                var recentTimings = _recentTimings.TakeLast(50).ToList();
                var averages = recentTimings
                    .GroupBy(t => $"{t.ProjectName}.{t.PhaseName}")
                    .ToDictionary(g => g.Key, g => g.Average(t => t.Duration.TotalMilliseconds));

                foreach (var avg in averages.OrderByDescending(a => a.Value).Take(10))
                {
                    series.Points.AddXY(avg.Key, avg.Value);
                }

                _timingChart.ChartAreas[0].AxisX.Title = "Phase";
                _timingChart.ChartAreas[0].AxisY.Title = "Average Duration (ms)";
                _timingChart.Titles.Clear();
                _timingChart.Titles.Add("Phase Performance (Recent Average)");
            }
            catch (Exception)
            {
                // Ignore chart update errors
            }
        }

        private void RefreshDisplay(object sender, EventArgs e)
        {
            try
            {
                var projects = _engine.GetProjects();
                var objects = _engine.GetMonitoredObjects();
                
                _projectTreeView.BeginUpdate();
                _projectTreeView.Nodes.Clear();

                foreach (var project in projects.OrderBy(p => p.Name))
                {
                    var projectNode = new TreeNode($"{project.Name} ({project.ObjectCount} objects)")
                    {
                        Tag = project,
                        ImageIndex = 0
                    };

                    var projectObjects = objects.Where(o => o.ProjectName == project.Name);
                    foreach (var obj in projectObjects.OrderBy(o => o.Name))
                    {
                        var objNode = new TreeNode($"{obj.Name} ({obj.Type})")
                        {
                            Tag = obj,
                            ImageIndex = 1
                        };

                        foreach (var prop in obj.Properties.OrderBy(p => p.Key))
                        {
                            var propNode = new TreeNode($"{prop.Key}: {prop.Value}")
                            {
                                Tag = new { Object = obj, Property = prop.Key, Value = prop.Value },
                                ImageIndex = 2
                            };
                            objNode.Nodes.Add(propNode);
                        }

                        projectNode.Nodes.Add(objNode);
                    }

                    _projectTreeView.Nodes.Add(projectNode);
                }

                _projectTreeView.ExpandAll();
                _projectTreeView.EndUpdate();
            }
            catch (Exception)
            {
                // Ignore refresh errors
            }
        }

        private void ProjectTreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Tag is MonitoredObjectInfo obj)
            {
                _propertyGrid.SelectedObject = new PropertyWrapper(obj);
            }
            else if (e.Node.Tag is ProjectInfo project)
            {
                _propertyGrid.SelectedObject = new ProjectWrapper(project, _engine);
            }
        }

        private void ClearEventLog()
        {
            _eventLog.Items.Clear();
            _recentEvents.Clear();
        }

        private void ClearTimingLog()
        {
            _timingLog.Items.Clear();
            _recentTimings.Clear();
            _timingChart.Series["PhaseDurations"].Points.Clear();
        }

        private void ExportTimingData()
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"timing_data_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (saveDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    var lines = new List<string>
                    {
                        "StartTime,ProjectName,ComponentName,PhaseName,DurationMs"
                    };

                    lines.AddRange(_recentTimings.Select(t => 
                        $"{t.StartTime:yyyy-MM-dd HH:mm:ss.fff},{t.ProjectName},{t.ComponentName},{t.PhaseName},{t.Duration.TotalMilliseconds:F1}"));

                    System.IO.File.WriteAllLines(saveDialog.FileName, lines);
                    MessageBox.Show($"Timing data exported to {saveDialog.FileName}", "Export Complete");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error exporting data: {ex.Message}", "Export Error");
                }
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer?.Stop();
            _engine?.Dispose();
            base.OnFormClosed(e);
        }

        // Enhanced wrapper class for PropertyGrid display
        private class PropertyWrapper
        {
            private readonly MonitoredObjectInfo _obj;

            public PropertyWrapper(MonitoredObjectInfo obj)
            {
                _obj = obj;
            }

            [Category("General")]
            [DisplayName("Object Name")]
            public string Name => _obj.Name;

            [Category("General")]
            [DisplayName("Object Type")]
            public string Type => _obj.Type;

            [Category("General")]
            [DisplayName("Project Name")]
            public string ProjectName => _obj.ProjectName;

            [Category("General")]
            [DisplayName("Last Update")]
            public DateTime LastUpdate => _obj.LastUpdate;

            [Category("Properties")]
            [DisplayName("Property Count")]
            public int PropertyCount => _obj.Properties?.Count ?? 0;
        }

        // Project wrapper for PropertyGrid
        private class ProjectWrapper
        {
            private readonly ProjectInfo _project;
            private readonly DMSMonitoringEngine _engine;

            public ProjectWrapper(ProjectInfo project, DMSMonitoringEngine engine)
            {
                _project = project;
                _engine = engine;
            }

            [Category("General")]
            [DisplayName("Project Name")]
            public string Name => _project.Name;

            [Category("General")]
            [DisplayName("Object Count")]
            public int ObjectCount => _project.ObjectCount;

            [Category("General")]
            [DisplayName("Last Activity")]
            public DateTime LastActivity => _project.LastActivity;

            [Category("Performance")]
            [DisplayName("Average Phase Time")]
            public string AveragePhaseTime
            {
                get
                {
                    var averages = _engine.GetAverageTimings(_project.Name);
                    if (averages.Any())
                    {
                        var overall = TimeSpan.FromMilliseconds(averages.Values.Average(t => t.TotalMilliseconds));
                        return $"{overall.TotalMilliseconds:F1} ms";
                    }
                    return "No data";
                }
            }
        }
    }

    // Main program entry point (unchanged)
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            // Check if running as monitor or as part of existing application
            bool runAsStandalone = args.Length == 0 || args[0] == "--monitor";
            
            if (runAsStandalone)
            {
                // Run as standalone monitoring application
                using (var form = new DMSVisualForm())
                {
                    Application.Run(form);
                }
            }
            else
            {
                // Run as embedded monitor (headless)
                var engine = new DMSMonitoringEngine();
                Console.WriteLine("DMS Monitor started in embedded mode. Press any key to exit...");
                Console.ReadKey();
                engine.Dispose();
            }
        }

        // Static method for easy integration into existing applications
        public static void StartMonitoring()
        {
            var thread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                
                using (var form = new DMSVisualForm())
                {
                    Application.Run(form);
                }
            });
            
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
    }

    // Enhanced extension methods for easy integration
    public static class DMSMonitorExtensions
    {
        public static void EnableDMSMonitoring(this object obj, string projectName = "Default")
        {
            // This can be called from any DMS application to enable monitoring
            var engine = GetOrCreateGlobalEngine();
            engine.RegisterObject(obj, projectName: projectName);
        }

        public static PhaseTimer StartDMSPhase(this object obj, string phaseName, string projectName = "Default")
        {
            var engine = GetOrCreateGlobalEngine();
            return engine.StartPhase(phaseName, obj.GetType().Name, projectName);
        }

        private static DMSMonitoringEngine _globalEngine;
        private static readonly object _lock = new object();

        private static DMSMonitoringEngine GetOrCreateGlobalEngine()
        {
            if (_globalEngine == null)
            {
                lock (_lock)
                {
                    if (_globalEngine == null)
                    {
                        _globalEngine = new DMSMonitoringEngine();
                        
                        // Auto-start visual form in background thread
                        Task.Run(() =>
                        {
                            var thread = new Thread(() =>
                            {
                                Application.EnableVisualStyles();
                                Application.SetCompatibleTextRenderingDefault(false);
                                
                                using (var form = new DMSVisualForm())
                                {
                                    Application.Run(form);
                                }
                            });
                            
                            thread.SetApartmentState(ApartmentState.STA);
                            thread.IsBackground = false;
                            thread.Start();
                        });
                    }
                }
            }
            return _globalEngine;
        }
    }
}

// Example DMS Session classes showing multi-project integration with timing
/*
[DMSMonitor(DisplayName = "Core Session", Category = "Core", ProjectName = "DMSCore")]
public class DMSSession : INotifyPropertyChanged
{
    private string _currentState = "Idle";
    private int _processedItems = 0;
    private DateTime _lastActivity = DateTime.Now;

    [DMSMonitor(DisplayName = "Current State")]
    public string CurrentState
    {
        get => _currentState;
        set
        {
            if (_currentState != value)
            {
                _currentState = value;
                OnPropertyChanged();
            }
        }
    }

    [DMSMonitor(DisplayName = "Processed Items")]
    public int ProcessedItems
    {
        get => _processedItems;
        set
        {
            if (_processedItems != value)
            {
                _processedItems = value;
                OnPropertyChanged();
            }
        }
    }

    [DMSMonitor(DisplayName = "Last Activity")]
    public DateTime LastActivity
    {
        get => _lastActivity;
        set
        {
            if (_lastActivity != value)
            {
                _lastActivity = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Static instance for auto-discovery
    public static DMSSession Current { get; } = new DMSSession();

    // Method to enable monitoring on this instance
    public void StartMonitoring()
    {
        this.EnableDMSMonitoring("DMSCore");
    }

    // Example method showing phase timing usage
    public void ProcessData()
    {
        using (this.StartDMSPhase("Data Processing", "DMSCore"))
        {
            CurrentState = "Processing";
            
            // Simulate processing work
            Thread.Sleep(100);
            
            using (this.StartDMSPhase("Validation", "DMSCore"))
            {
                // Validation phase
                Thread.Sleep(50);
            }
            
            using (this.StartDMSPhase("Save to Database", "DMSCore"))
            {
                // Save phase
                Thread.Sleep(200);
            }
            
            ProcessedItems++;
            LastActivity = DateTime.Now;
            CurrentState = "Completed";
        }
    }
}

[DMSMonitor(DisplayName = "Data Pipeline", Category = "Pipeline", ProjectName = "DataPipeline")]
public class DataPipelineManager : INotifyPropertyChanged
{
    private string _pipelineStatus = "Idle";
    private int _throughputPerSecond = 0;
    private long _totalProcessed = 0;

    [DMSMonitor(DisplayName = "Pipeline Status")]
    public string PipelineStatus
    {
        get => _pipelineStatus;
        set
        {
            if (_pipelineStatus != value)
            {
                _pipelineStatus = value;
                OnPropertyChanged();
            }
        }
    }

    [DMSMonitor(DisplayName = "Throughput/sec")]
    public int ThroughputPerSecond
    {
        get => _throughputPerSecond;
        set
        {
            if (_throughputPerSecond != value)
            {
                _throughputPerSecond = value;
                OnPropertyChanged();
            }
        }
    }

    [DMSMonitor(DisplayName = "Total Processed")]
    public long TotalProcessed
    {
        get => _totalProcessed;
        set
        {
            if (_totalProcessed != value)
            {
                _totalProcessed = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Static instance for auto-discovery
    public static DataPipelineManager Current { get; } = new DataPipelineManager();

    public void StartMonitoring()
    {
        this.EnableDMSMonitoring("DataPipeline");
    }

    public void ProcessBatch(int batchSize)
    {
        using (this.StartDMSPhase("Batch Processing", "DataPipeline"))
        {
            PipelineStatus = "Processing Batch";
            
            using (this.StartDMSPhase("Data Extraction", "DataPipeline"))
            {
                Thread.Sleep(50);
            }
            
            using (this.StartDMSPhase("Data Transformation", "DataPipeline"))
            {
                Thread.Sleep(100);
            }
            
            using (this.StartDMSPhase("Data Loading", "DataPipeline"))
            {
                Thread.Sleep(75);
            }
            
            TotalProcessed += batchSize;
            ThroughputPerSecond = batchSize * 1000 / 225; // Rough calculation based on timing
            PipelineStatus = "Batch Complete";
        }
    }
}

// Example usage in your application:
public class ExampleUsage
{
    public void DemonstrateMonitoring()
    {
        // Enable monitoring for different projects
        var coreSession = DMSSession.Current;
        coreSession.StartMonitoring();
        
        var pipelineManager = DataPipelineManager.Current;
        pipelineManager.StartMonitoring();
        
        // Simulate some work with timing
        Task.Run(async () =>
        {
            while (true)
            {
                coreSession.ProcessData();
                pipelineManager.ProcessBatch(100);
                await Task.Delay(1000);
            }
        });
    }
}
*/