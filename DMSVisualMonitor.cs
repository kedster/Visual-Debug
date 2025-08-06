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
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
    }

    // Attribute to mark DMS components for monitoring
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
    public class DMSMonitorAttribute : Attribute
    {
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public bool AutoTrack { get; set; } = true;
    }

    // Main monitoring engine
    public class DMSMonitoringEngine
    {
        private readonly ConcurrentDictionary<object, ObjectMonitor> _monitoredObjects = new ConcurrentDictionary<object, ObjectMonitor>();
        private readonly Timer _discoveryTimer;
        
        public event EventHandler<DMSStateChangedEventArgs> StateChanged;

        public DMSMonitoringEngine()
        {
            // Auto-discovery timer runs every 5 seconds
            _discoveryTimer = new Timer(DiscoverNewObjects, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        public void RegisterObject(object obj, string name = null)
        {
            if (obj == null) return;

            var monitor = new ObjectMonitor(obj, name ?? obj.GetType().Name);
            monitor.StateChanged += (s, e) => StateChanged?.Invoke(s, e);
            
            _monitoredObjects.TryAdd(obj, monitor);
        }

        public void UnregisterObject(object obj)
        {
            if (_monitoredObjects.TryRemove(obj, out var monitor))
            {
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
                            // Look for static instances or singletons
                            var staticFields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                .Where(f => f.FieldType == type);

                            foreach (var field in staticFields)
                            {
                                var instance = field.GetValue(null);
                                if (instance != null && !_monitoredObjects.ContainsKey(instance))
                                {
                                    RegisterObject(instance, $"{type.Name}.{field.Name}");
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
                    Properties = m.GetCurrentProperties(),
                    LastUpdate = m.LastUpdate
                }).ToList();
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

    // Individual object monitor
    public class ObjectMonitor : IDisposable
    {
        private readonly object _targetObject;
        private readonly Dictionary<string, object> _lastValues = new Dictionary<string, object>();
        private readonly Timer _pollTimer;
        
        public string Name { get; }
        public Type ObjectType => _targetObject.GetType();
        public DateTime LastUpdate { get; private set; }
        
        public event EventHandler<DMSStateChangedEventArgs> StateChanged;

        public ObjectMonitor(object targetObject, string name)
        {
            _targetObject = targetObject ?? throw new ArgumentNullException(nameof(targetObject));
            Name = name;
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
                                ComponentName = Name
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
        public Dictionary<string, object> Properties { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    // Visual monitoring form
    public partial class DMSVisualForm : Form
    {
        private readonly DMSMonitoringEngine _engine;
        private readonly TreeView _treeView;
        private readonly PropertyGrid _propertyGrid;
        private readonly ListView _eventLog;
        private readonly Timer _refreshTimer;
        private readonly List<DMSStateChangedEventArgs> _recentEvents = new List<DMSStateChangedEventArgs>();

        public DMSVisualForm()
        {
            InitializeComponent();
            
            _engine = new DMSMonitoringEngine();
            _engine.StateChanged += OnStateChanged;
            
            _refreshTimer = new Timer { Interval = 500, Enabled = true };
            _refreshTimer.Tick += RefreshDisplay;
        }

        private void InitializeComponent()
        {
            Text = "DMS Visual Monitor";
            Size = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;

            var splitter1 = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 400
            };

            var splitter2 = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 400
            };

            // Tree view for monitored objects
            _treeView = new TreeView
            {
                Dock = DockStyle.Fill,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true
            };
            _treeView.AfterSelect += TreeView_AfterSelect;

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
            _eventLog.Columns.Add("Component", 150);
            _eventLog.Columns.Add("Property", 120);
            _eventLog.Columns.Add("Old Value", 150);
            _eventLog.Columns.Add("New Value", 150);

            splitter1.Panel1.Controls.Add(_treeView);
            splitter2.Panel1.Controls.Add(_propertyGrid);
            splitter2.Panel2.Controls.Add(_eventLog);
            splitter1.Panel2.Controls.Add(splitter2);

            Controls.Add(splitter1);

            // Menu
            var menuStrip = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("File");
            var exitItem = new ToolStripMenuItem("Exit", null, (s, e) => Close());
            fileMenu.DropDownItems.Add(exitItem);
            menuStrip.Items.Add(fileMenu);

            var toolsMenu = new ToolStripMenuItem("Tools");
            var clearLogItem = new ToolStripMenuItem("Clear Event Log", null, (s, e) => ClearEventLog());
            toolsMenu.DropDownItems.Add(clearLogItem);
            menuStrip.Items.Add(toolsMenu);

            Controls.Add(menuStrip);
            MainMenuStrip = menuStrip;
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

        private void RefreshDisplay(object sender, EventArgs e)
        {
            try
            {
                var objects = _engine.GetMonitoredObjects();
                
                _treeView.BeginUpdate();
                _treeView.Nodes.Clear();

                foreach (var obj in objects)
                {
                    var node = new TreeNode($"{obj.Name} ({obj.Type})")
                    {
                        Tag = obj
                    };

                    foreach (var prop in obj.Properties)
                    {
                        var propNode = new TreeNode($"{prop.Key}: {prop.Value}")
                        {
                            Tag = new { Object = obj, Property = prop.Key, Value = prop.Value }
                        };
                        node.Nodes.Add(propNode);
                    }

                    _treeView.Nodes.Add(node);
                }

                _treeView.ExpandAll();
                _treeView.EndUpdate();
            }
            catch (Exception)
            {
                // Ignore refresh errors
            }
        }

        private void TreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Tag is MonitoredObjectInfo obj)
            {
                _propertyGrid.SelectedObject = new PropertyWrapper(obj);
            }
        }

        private void ClearEventLog()
        {
            _eventLog.Items.Clear();
            _recentEvents.Clear();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refreshTimer?.Stop();
            _engine?.Dispose();
            base.OnFormClosed(e);
        }

        // Wrapper class for PropertyGrid display
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
            [DisplayName("Last Update")]
            public DateTime LastUpdate => _obj.LastUpdate;

            [Category("Properties")]
            [DisplayName("Property Count")]
            public int PropertyCount => _obj.Properties?.Count ?? 0;
        }
    }

    // Main program entry point
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

    // Extension methods for easy integration
    public static class DMSMonitorExtensions
    {
        public static void EnableDMSMonitoring(this object obj)
        {
            // This can be called from any DMS application to enable monitoring
            var engine = GetOrCreateGlobalEngine();
            engine.RegisterObject(obj);
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

// Example DMS Session class showing how to integrate
/*
[DMSMonitor(DisplayName = "DMS Session", Category = "Core")]
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
        this.EnableDMSMonitoring();
    }
}
*/
