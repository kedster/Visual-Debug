using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Data.SqlClient;
using Syncfusion.Windows.Forms.Tools;
using System.IO;
using Newtonsoft.Json;
using Syncfusion.Licensing;
using Syncfusion.WinForms.DataGrid;
using Microsoft.Win32;

namespace sqlgridview
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
            SyncfusionLicenseProvider.RegisterLicense(Program.DemoCommon.FindLicenseKey());
            InitializeLogging();
        }

        private string connectionStringA = "";
        private string connectionStringB = "";
        private StringBuilder statusLog = new StringBuilder();
        private const int MAX_LOG_LINES = 10; // Configurable log display limit
        private Timer statusTimer;
        private DataTable currentTable;
        private DataTable currentTableB;

        #region Logging Methods

        private void InitializeLogging()
        {
            LogVerbose("Application", "Form1 initialized successfully");
        }

        /// <summary>
        /// Logs a verbose message with timestamp and category
        /// </summary>
        private void LogVerbose(string category, string message, Exception ex = null)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var logEntry = $"[{timestamp}] [{category}] {message}";
            
            if (ex != null)
            {
                logEntry += $" | Exception: {ex.Message}";
                if (ex.InnerException != null)
                {
                    logEntry += $" | Inner: {ex.InnerException.Message}";
                }
            }

            statusLog.AppendLine(logEntry);
            UpdateStatusDisplay();
            
            // Also write to debug output for development
            System.Diagnostics.Debug.WriteLine(logEntry);
        }

        /// <summary>
        /// Updates the status bar display with recent log entries
        /// </summary>
        private void UpdateStatusDisplay()
        {
            try
            {
                if (statusPanelMessage == null) return;

                var logLines = statusLog.ToString()
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                if (logLines.Length == 0) return;

                // Show the most recent log lines
                int linesToShow = Math.Min(MAX_LOG_LINES, logLines.Length);
                var recentLines = logLines
                    .Skip(Math.Max(0, logLines.Length - linesToShow))
                    .ToArray();

                string displayText = string.Join(Environment.NewLine, recentLines);
                
                // Truncate if too long for display
                if (displayText.Length > 500)
                {
                    displayText = "..." + displayText.Substring(displayText.Length - 497);
                }

                statusPanelMessage.Text = displayText;
                
                // Force refresh of status bar
                if (statusBarAdv != null)
                {
                    statusBarAdv.Invalidate();
                    statusBarAdv.Update();
                }
            }
            catch (Exception ex)
            {
                // Fallback logging to prevent infinite loops
                System.Diagnostics.Debug.WriteLine($"Error updating status display: {ex.Message}");
            }
        }

        /// <summary>
        /// Logs SQL operations with query details
        /// </summary>
        private void LogSqlOperation(string operation, string tableName, string query = null, int? rowCount = null)
        {
            var message = $"{operation}";
            if (!string.IsNullOrEmpty(tableName))
                message += $" on table '{tableName}'";
            if (rowCount.HasValue)
                message += $" - {rowCount.Value} rows affected";
            if (!string.IsNullOrEmpty(query))
                message += $" | Query: {query.Substring(0, Math.Min(query.Length, 100))}...";

            LogVerbose("SQL", message);
        }

        /// <summary>
        /// Logs database connection events
        /// </summary>
        private void LogConnection(string database, string operation, bool success, Exception ex = null)
        {
            var message = $"{operation} - Database: {database} - {(success ? "SUCCESS" : "FAILED")}";
            LogVerbose("CONNECTION", message, ex);
        }

        /// <summary>
        /// Logs UI interactions
        /// </summary>
        private void LogUIAction(string control, string action, string details = null)
        {
            var message = $"{control} - {action}";
            if (!string.IsNullOrEmpty(details))
                message += $" - {details}";
            LogVerbose("UI", message);
        }

        #endregion

        private void UpdateConfigButtonColors()
        {
            LogUIAction("ConfigButtons", "Updating button colors");

            sfBut_conconfigA.BackColor = string.IsNullOrWhiteSpace(connectionStringA)
                ? SystemColors.Control
                : Color.LightGreen;

            sfBut_conconfigB.BackColor = string.IsNullOrWhiteSpace(connectionStringB)
                ? SystemColors.Control
                : Color.LightGreen;

            LogVerbose("CONFIG", $"Button colors updated - A: {(string.IsNullOrWhiteSpace(connectionStringA) ? "Not Set" : "Set")}, B: {(string.IsNullOrWhiteSpace(connectionStringB) ? "Not Set" : "Set")}");
        }

        private void configAButton_Click(object sender, EventArgs e)
        {
            LogUIAction("ConfigA", "Button clicked");
            
            using (var dlg = new ConfigWindow(connectionStringA))
            {
                LogVerbose("CONFIG", "Opening configuration dialog for Connection A");
                
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    connectionStringA = dlg.ConnectionString;
                    UpdateConfigButtonColors();
                    LogVerbose("CONFIG", "Connection A configuration updated successfully");
                }
                else
                {
                    LogVerbose("CONFIG", "Connection A configuration cancelled by user");
                }
            }
        }

        private void configBButton_Click(object sender, EventArgs e)
        {
            LogUIAction("ConfigB", "Button clicked");
            
            using (var dlg = new ConfigWindow(connectionStringB))
            {
                LogVerbose("CONFIG", "Opening configuration dialog for Connection B");
                
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    connectionStringB = dlg.ConnectionString;
                    UpdateConfigButtonColors();
                    LogVerbose("CONFIG", "Connection B configuration updated successfully");
                }
                else
                {
                    LogVerbose("CONFIG", "Connection B configuration cancelled by user");
                }
            }
        }

        private void sfconnecta_Click(object sender, EventArgs e)
        {
            LogUIAction("ConnectA", "Connect button clicked");

            if (string.IsNullOrWhiteSpace(connectionStringA))
            {
                LogVerbose("CONNECTION", "Connection A failed - No connection string configured");
                MessageBox.Show("Please configure Connection A first.", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                LogVerbose("CONNECTION", "Clearing tree view A and starting connection process");
                sqltreeViewAdvA.Nodes.Clear();
                var tableInfos = new List<(string TableName, int RowCount)>();

                using (var conn = new SqlConnection(connectionStringA))
                {
                    LogVerbose("CONNECTION", "Opening connection A...");
                    conn.Open();
                    LogConnection("A", "Connected", true);

                    LogVerbose("SQL", "Retrieving table schema for Connection A");
                    var dt = conn.GetSchema("Tables");
                    LogVerbose("SCHEMA", $"Found {dt.Rows.Count} tables in database A");

                    foreach (System.Data.DataRow row in dt.Rows)
                    {
                        string tableName = row["TABLE_NAME"].ToString();
                        
                        try
                        {
                            LogVerbose("SQL", $"Getting row count for table '{tableName}'");
                            int rowCount = 0;
                            using (var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{tableName}]", conn))
                            {
                                rowCount = (int)cmd.ExecuteScalar();
                            }
                            tableInfos.Add((tableName, rowCount));
                            LogSqlOperation("Row count retrieved", tableName, null, rowCount);
                        }
                        catch (Exception tableEx)
                        {
                            LogVerbose("SQL", $"Failed to get row count for table '{tableName}'", tableEx);
                            tableInfos.Add((tableName, 0)); // Add with 0 count if query fails
                        }
                    }
                }

                LogVerbose("UI", $"Populating tree view A with {tableInfos.Count} tables");
                foreach (var info in tableInfos.OrderByDescending(t => t.RowCount))
                {
                    var parentNode = new TreeNodeAdv(info.TableName);
                    var childNode = new TreeNodeAdv($"Rows: {info.RowCount}".Replace("(", "").Replace(")", ""));
                    parentNode.Nodes.Add(childNode);
                    parentNode.Expanded = true;
                    sqltreeViewAdvA.Nodes.Add(parentNode);
                }

                LogVerbose("CONNECTION", $"Database A connected successfully - {tableInfos.Count} tables loaded");
            }
            catch (Exception ex)
            {
                LogConnection("A", "Connection attempt", false, ex);
                MessageBox.Show($"Connection failed: {ex.Message}", "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void button1_connectA_Click(object sender, EventArgs e)
        {
            LogUIAction("ConnectA_Alt", "Alternative connect button clicked");
            
            if (string.IsNullOrWhiteSpace(connectionStringA))
            {
                LogVerbose("CONNECTION", "Connection A failed - No connection string configured");
                MessageBox.Show("Please configure Connection A first.", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                LogVerbose("CONNECTION", "Clearing tree view A and starting connection process (alphabetical sort)");
                sqltreeViewAdvA.Nodes.Clear();
                var tableInfos = new List<(string TableName, int RowCount)>();

                using (var conn = new SqlConnection(connectionStringA))
                {
                    LogVerbose("CONNECTION", "Opening connection A...");
                    conn.Open();
                    LogConnection("A", "Connected", true);

                    var dt = conn.GetSchema("Tables");
                    LogVerbose("SCHEMA", $"Found {dt.Rows.Count} tables in database A");

                    foreach (System.Data.DataRow row in dt.Rows)
                    {
                        string tableName = row["TABLE_NAME"].ToString();
                        
                        try
                        {
                            int rowCount;
                            using (var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{tableName}]", conn))
                            {
                                rowCount = (int)cmd.ExecuteScalar();
                            }
                            tableInfos.Add((tableName, rowCount));
                            LogSqlOperation("Row count retrieved", tableName, null, rowCount);
                        }
                        catch (Exception tableEx)
                        {
                            LogVerbose("SQL", $"Failed to get row count for table '{tableName}'", tableEx);
                            tableInfos.Add((tableName, 0));
                        }
                    }
                }

                // Sort alphabetically instead of by row count
                LogVerbose("UI", "Sorting tables alphabetically for tree view A");
                foreach (var info in tableInfos.OrderBy(t => t.TableName))
                {
                    var parentNode = new TreeNodeAdv(info.TableName);
                    var childNode = new TreeNodeAdv($"Rows: {info.RowCount}");
                    parentNode.Nodes.Add(childNode);
                    parentNode.Expanded = true;
                    sqltreeViewAdvA.Nodes.Add(parentNode);
                }

                LogVerbose("CONNECTION", $"Database A connected successfully (alphabetical) - {tableInfos.Count} tables loaded");
            }
            catch (Exception ex)
            {
                LogConnection("A", "Connection attempt (alphabetical)", false, ex);
                MessageBox.Show($"Connection failed: {ex.Message}", "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void button1_connectB_Click(object sender, EventArgs e)
        {
            LogUIAction("ConnectB", "Connect button clicked");
            
            if (string.IsNullOrWhiteSpace(connectionStringB))
            {
                LogVerbose("CONNECTION", "Connection B failed - No connection string configured");
                MessageBox.Show("Please configure Connection B first.", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                LogVerbose("CONNECTION", "Clearing tree view B and starting connection process");
                sqltreeViewAdvB.Nodes.Clear();
                var tableInfos = new List<(string TableName, int RowCount)>();

                using (var conn = new SqlConnection(connectionStringB))
                {
                    LogVerbose("CONNECTION", "Opening connection B...");
                    conn.Open();
                    LogConnection("B", "Connected", true);

                    var dt = conn.GetSchema("Tables");
                    LogVerbose("SCHEMA", $"Found {dt.Rows.Count} tables in database B");

                    foreach (System.Data.DataRow row in dt.Rows)
                    {
                        string tableName = row["TABLE_NAME"].ToString();
                        
                        try
                        {
                            int rowCount;
                            using (var cmd = new SqlCommand($"SELECT COUNT(*) FROM [{tableName}]", conn))
                            {
                                rowCount = (int)cmd.ExecuteScalar();
                            }
                            tableInfos.Add((tableName, rowCount));
                            LogSqlOperation("Row count retrieved", tableName, null, rowCount);
                        }
                        catch (Exception tableEx)
                        {
                            LogVerbose("SQL", $"Failed to get row count for table '{tableName}'", tableEx);
                            tableInfos.Add((tableName, 0));
                        }
                    }
                }

                LogVerbose("UI", "Populating tree view B with tables (alphabetical sort)");
                foreach (var info in tableInfos.OrderBy(t => t.TableName))
                {
                    var parentNode = new TreeNodeAdv(info.TableName);
                    var childNode = new TreeNodeAdv($"Rows: {info.RowCount}");
                    parentNode.Nodes.Add(childNode);
                    parentNode.Expanded = true;
                    sqltreeViewAdvB.Nodes.Add(parentNode);
                }

                LogVerbose("CONNECTION", $"Database B connected successfully - {tableInfos.Count} tables loaded");
            }
            catch (Exception ex)
            {
                LogConnection("B", "Connection attempt", false, ex);
                MessageBox.Show($"Connection failed: {ex.Message}", "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void sqltreeViewAdv1_AfterSelect(object sender, EventArgs e)
        {
            if (sqltreeViewAdvA.SelectedNode == null) 
            {
                LogUIAction("TreeViewA", "Selection cleared");
                return;
            }

            string tableName = sqltreeViewAdvA.SelectedNode.Text;
            LogUIAction("TreeViewA", $"Table selected: {tableName}");

            if (string.IsNullOrWhiteSpace(connectionStringA))
            {
                LogVerbose("SQL", "Cannot load table data - Connection A not configured");
                return;
            }

            try
            {
                LogVerbose("SQL", $"Loading all data from table '{tableName}' for Connection A");
                
                using (var conn = new SqlConnection(connectionStringA))
                {
                    conn.Open();
                    var cmd = new SqlCommand($"SELECT * FROM [{tableName}]", conn);
                    var adapter = new SqlDataAdapter(cmd);
                    var dt = new DataTable();
                    
                    LogVerbose("SQL", "Executing SELECT * query for tree selection");
                    adapter.Fill(dt);
                    
                    LogSqlOperation("Table data loaded", tableName, $"SELECT * FROM [{tableName}]", dt.Rows.Count);
                    BindTableToGridControl(dt);
                }
            }
            catch (Exception ex)
            {
                LogVerbose("SQL", $"Failed to load table '{tableName}' data", ex);
                MessageBox.Show($"Error loading table data: {ex.Message}", "SQL Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void sfBut_selectallA_Click(object sender, EventArgs e)
        {
            LogUIAction("SelectAllA", "Button clicked");

            if (sqltreeViewAdvA.SelectedNode == null) 
            {
                LogVerbose("UI", "Select All A - No table selected");
                MessageBox.Show("Please select a table first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string tableName = sqltreeViewAdvA.SelectedNode.Text;
            LogUIAction("SelectAllA", $"Loading all data for table: {tableName}");

            if (string.IsNullOrWhiteSpace(connectionStringA))
            {
                LogVerbose("SQL", "Cannot execute Select All A - Connection not configured");
                return;
            }

            try
            {
                using (var conn = new SqlConnection(connectionStringA))
                {
                    conn.Open();
                    LogVerbose("SQL", $"Executing SELECT * for table '{tableName}' (Connection A)");
                    
                    var cmd = new SqlCommand($"SELECT * FROM [{tableName}]", conn);
                    var adapter = new SqlDataAdapter(cmd);
                    var dt = new DataTable();
                    adapter.Fill(dt);

                    LogSqlOperation("Select All executed", tableName, $"SELECT * FROM [{tableName}]", dt.Rows.Count);
                    BindTableToGridControl(dt);
                }
            }
            catch (Exception ex)
            {
                LogVerbose("SQL", $"Select All A failed for table '{tableName}'", ex);
                MessageBox.Show($"Error executing Select All: {ex.Message}", "SQL Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void sfBut_selectallB_Click(object sender, EventArgs e)
        {
            LogUIAction("SelectAllB", "Button clicked");

            if (sqltreeViewAdvB.SelectedNode == null) 
            {
                LogVerbose("UI", "Select All B - No table selected");
                MessageBox.Show("Please select a table first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string tableName = sqltreeViewAdvB.SelectedNode.Text;
            LogUIAction("SelectAllB", $"Loading all data for table: {tableName}");

            try
            {
                using (var conn = new SqlConnection(connectionStringB))
                {
                    conn.Open();
                    LogVerbose("SQL", $"Executing SELECT * for table '{tableName}' (Connection B)");
                    
                    var cmd = new SqlCommand($"SELECT * FROM [{tableName}]", conn);
                    var adapter = new SqlDataAdapter(cmd);
                    var dt = new DataTable();
                    adapter.Fill(dt);

                    LogSqlOperation("Select All executed", tableName, $"SELECT * FROM [{tableName}]", dt.Rows.Count);
                    BindTableToGridControlB(dt);
                }
            }
            catch (Exception ex)
            {
                LogVerbose("SQL", $"Select All B failed for table '{tableName}'", ex);
                MessageBox.Show($"Error executing Select All: {ex.Message}", "SQL Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BindTableToGridControl(DataTable dt)
        {
            LogVerbose("UI", $"Binding data to Grid A - {dt.Rows.Count} rows, {dt.Columns.Count} columns");
            currentTable = dt;
            BindTableToSfDataGrid(dt, sfDataGridA);
        }

        private void BindTableToGridControlB(DataTable dt)
        {
            LogVerbose("UI", $"Binding data to Grid B - {dt.Rows.Count} rows, {dt.Columns.Count} columns");
            currentTableB = dt;
            BindTableToSfDataGrid(dt, sfDataGridB);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            LogVerbose("STARTUP", "Form1_Load event triggered");

            try
            {
                // Initialize status timer
                statusTimer = new Timer();
                statusTimer.Interval = 1000;
                statusTimer.Tick += StatusTimer_Tick;
                statusTimer.Start();
                LogVerbose("STARTUP", "Status timer initialized and started");

                // Configure data grids
                LogVerbose("STARTUP", "Configuring SfDataGrid controls");
                ConfigureDataGrid(sfDataGridA, "A");
                ConfigureDataGrid(sfDataGridB, "B");

                UpdateConfigButtonColors();
                LoadConnectionStringsToComboBoxes();

                // Setup combo box events
                LogVerbose("STARTUP", "Setting up ComboBox events");
                SetupComboBoxEvents();

                LogVerbose("STARTUP", "Form initialization completed successfully");
            }
            catch (Exception ex)
            {
                LogVerbose("STARTUP", "Error during form initialization", ex);
                MessageBox.Show($"Error during form initialization: {ex.Message}", "Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ConfigureDataGrid(Syncfusion.WinForms.DataGrid.SfDataGrid grid, string gridName)
        {
            LogVerbose("STARTUP", $"Configuring SfDataGrid {gridName}");
            
            grid.AllowFiltering = true;
            grid.AutoGenerateColumns = true;

            // Configure specific columns if they exist
            var contactNumberCol = grid.Columns.FirstOrDefault(c => c.MappingName == "ContactNumber");
            if (contactNumberCol != null)
            {
                contactNumberCol.CellStyle.HorizontalAlignment = HorizontalAlignment.Right;
                LogVerbose("STARTUP", $"Grid {gridName}: ContactNumber column configured");
            }

            var productNameCol = grid.Columns.FirstOrDefault(c => c.MappingName == "ProductName");
            if (productNameCol != null)
            {
                productNameCol.FilterRowEditorType = "MultiSelectComboBox";
                LogVerbose("STARTUP", $"Grid {gridName}: ProductName column configured");
            }

            var quantityCol = grid.Columns.FirstOrDefault(c => c.MappingName == "Quantity");
            if (quantityCol != null)
            {
                quantityCol.FilterRowEditorType = "ComboBox";
                LogVerbose("STARTUP", $"Grid {gridName}: Quantity column configured");
            }
        }

        private void SetupComboBoxEvents()
        {
            // Refactored for Syncfusion SfComboBox
            sfComboBoxconstringA.DropDownOpening += (s, args) =>
            {
                LogVerbose("COMBOBOX", "ComboBox A dropdown opening");
                var filtered = GetFilteredConnectionStrings(textBox_connectionsearch?.ToString() ?? string.Empty);
                sfComboBoxconstringA.DataSource = filtered;
            };
            
            sfComboBoxconstringB.DropDownOpening += (s, args) =>
            {
                LogVerbose("COMBOBOX", "ComboBox B dropdown opening");
                var filtered = GetFilteredConnectionStrings(textBox_connectionsearch?.ToString() ?? string.Empty);
                sfComboBoxconstringB.DataSource = filtered;
            };

            // Wire up DropDown events
            sfComboBoxconstringA.DropDownOpened += sfComboBoxconstringA_DropDown;
            sfComboBoxconstringB.DropDownOpened += sfComboBoxconstringB_DropDown;
        }

        private void StatusTimer_Tick(object sender, EventArgs e)
        {
            if (statusPanelDate != null)
                statusPanelDate.Text = DateTime.Now.ToShortDateString();
            if (statusPanelTime != null)
                statusPanelTime.Text = DateTime.Now.ToLongTimeString();
        }

        // Cell click handlers
        private void sfDataGridA_CellClick(object sender, Syncfusion.WinForms.DataGrid.Events.CellClickEventArgs e)
        {
            LogUIAction("GridA", "Cell clicked");
            ShowRowDetails(e, currentTable, "Grid A");
        }

        private void sfDataGridB_CellClick(object sender, Syncfusion.WinForms.DataGrid.Events.CellClickEventArgs e)
        {
            LogUIAction("GridB", "Cell clicked");
            ShowRowDetails(e, currentTableB, "Grid B");
        }

        private void ShowRowDetails(Syncfusion.WinForms.DataGrid.Events.CellClickEventArgs e, DataTable table, string gridName)
        {
            if (table == null || e.DataRow == null) 
            {
                LogVerbose("UI", $"{gridName}: No row data available for details");
                return;
            }

            try
            {
                var row = ((DataRowView)e.DataRow.RowData).Row;
                string details = $"{gridName} Row Details:\n\n";
                
                foreach (DataColumn col in table.Columns)
                {
                    details += $"{col.ColumnName}: {row[col]}\n";
                }
                
                LogVerbose("UI", $"{gridName}: Displaying row details dialog");
                MessageBox.Show(details, $"{gridName} Row Details");
            }
            catch (Exception ex)
            {
                LogVerbose("UI", $"{gridName}: Error showing row details", ex);
            }
        }

        // Search functionality
        private void searchTextBox_TextChanged(object sender, EventArgs e)
        {
            if (currentTable == null) 
            {
                LogVerbose("SEARCH", "Search attempted but no table loaded");
                return;
            }

            string filter = searchTextBox.Text;
            LogUIAction("Search", $"Text changed: '{filter}'");

            if (string.IsNullOrWhiteSpace(filter))
            {
                LogVerbose("SEARCH", "Search cleared - showing all data");
                BindTableToGridControl(currentTable);
                return;
            }

            try
            {
                LogVerbose("SEARCH", $"Applying filter: '{filter}'");
                var dv = currentTable.DefaultView;
                var filters = currentTable.Columns.Cast<DataColumn>()
                    .Select(c => $"CONVERT([{c.ColumnName}], System.String) LIKE '%{filter}%'");
                dv.RowFilter = string.Join(" OR ", filters);
                
                var filteredTable = dv.ToTable();
                LogVerbose("SEARCH", $"Filter applied - {filteredTable.Rows.Count} rows match criteria");
                BindTableToGridControl(filteredTable);
            }
            catch (Exception ex)
            {
                LogVerbose("SEARCH", "Error applying search filter", ex);
            }
        }

        private void sqltreeViewAdv1_Click(object sender, EventArgs e)
        {
            LogUIAction("TreeView", "Clicked - clearing search");
            searchTextBox.Clear();
        }

        // Import functionality
        private void importButton_Click(object sender, EventArgs e)
        {
            LogUIAction("Import", "Import button clicked");

            try
            {
                LogVerbose("CONFIG", "Reading queries.json file");
                var json = File.ReadAllText("queries.json");
                var queries = JsonConvert.DeserializeObject<List<QueryItem>>(json);

                LogVerbose("CONFIG", $"Loaded {queries?.Count ?? 0} queries from JSON");
                CreateDynamicButtons(queries);
            }
            catch (FileNotFoundException)
            {
                LogVerbose("CONFIG", "queries.json file not found");
                MessageBox.Show("queries.json file not found in the application directory.", "File Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                LogVerbose("CONFIG", "Error importing queries", ex);
                MessageBox.Show($"Error importing queries: {ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void importConfigButton_Click(object sender, EventArgs e)
        {
            LogUIAction("ImportConfig", "Import config button clicked");

            using (var openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Title = "Select a config file";
                openFileDialog.Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*";
                openFileDialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                LogVerbose("CONFIG", "Opening file dialog for config import");

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedFile = openFileDialog.FileName;
                    LogVerbose("CONFIG", $"User selected file: {selectedFile}");

                    try
                    {
                        var json = File.ReadAllText(selectedFile);
                        var queries = JsonConvert.DeserializeObject<List<QueryItem>>(json);
                        
                        LogVerbose("CONFIG", $"Successfully loaded {queries?.Count ?? 0} queries from {selectedFile}");
                        CreateDynamicButtons(queries);
                    }
                    catch (Exception ex)
                    {
                        LogVerbose("CONFIG", $"Failed to load config file: {selectedFile}", ex);
                        MessageBox.Show("Failed to load config file: " + ex.Message);
                    }
                }
                else
                {
                    LogVerbose("CONFIG", "Config import cancelled by user");
                }
            }
        }

        private void CreateDynamicButtons(List<QueryItem> queries)
        {
            LogVerbose("UI", "Creating dynamic query buttons");

            toolboxPanel.Controls.Clear();
            if (sfButton1Inport != null)
                toolboxPanel.Controls.Add(sfButton1Inport);

            int y = sfButton1Inport != null ? sfButton1Inport.Bottom + 10 : 10;
            int buttonCount = 0;

            if (queries != null)
            {
                foreach (var query in queries)
                {
                    var btn = new Button
                    {
                        Text = query.label,
                        Width = 180,
                        Location = new Point(10, y)
                    };
                    btn.Click += (s, ea) => 
                    {
                        LogVerbose("QUERY", $"Dynamic button clicked: {query.label}");
                        ExecuteDynamicQuery(query.command);
                    };
                    toolboxPanel.Controls.Add(btn);
                    y += btn.Height + 5;
                    buttonCount++;
                }
            }

            LogVerbose("UI", $"Created {buttonCount} dynamic query buttons");
        }

        private void ExecuteDynamicQuery(string sqlTemplate)
        {
            LogVerbose("QUERY", $"Executing dynamic query template: {sqlTemplate.Substring(0, Math.Min(sqlTemplate.Length, 50))}...");

            // For Connection A
            if (!string.IsNullOrWhiteSpace(connectionStringA) && sqltreeViewAdvA.SelectedNode != null)
            {
                string tableNameA = GetSelectedTableName(sqltreeViewAdvA);
                if (!string.IsNullOrWhiteSpace(tableNameA))
                {
                    ExecuteTemplateQuery(sqlTemplate, tableNameA, connectionStringA, sfDataGridA, "A");
                }
                else
                {
                    LogVerbose("QUERY", "Connection A - No valid table selected for dynamic query");
                }
            }
            else
            {
                LogVerbose("QUERY", "Connection A - Not available for dynamic query (connection or selection missing)");
            }

            // For Connection B
            if (!string.IsNullOrWhiteSpace(connectionStringB) && sqltreeViewAdvB.SelectedNode != null)
            {
                string tableNameB = GetSelectedTableName(sqltreeViewAdvB);
                if (!string.IsNullOrWhiteSpace(tableNameB))
                {
                    ExecuteTemplateQuery(sqlTemplate, tableNameB, connectionStringB, sfDataGridB, "B");
                }
                else
                {
                    LogVerbose("QUERY", "Connection B - No valid table selected for dynamic query");
                }
            }
            else
            {
                LogVerbose("QUERY", "Connection B - Not available for dynamic query (connection or selection missing)");
            }

            LogVerbose("QUERY", "Dynamic query execution completed");
        }

        private void ExecuteTemplateQuery(string sqlTemplate, string tableName, string connectionString, 
            Syncfusion.WinForms.DataGrid.SfDataGrid grid, string connectionId)
        {
            try
            {
                // Get variable values
                string var1 = string.IsNullOrWhiteSpace(gridAwareTextBoxvar1.Text) ? "*" : gridAwareTextBoxvar1.Text;
                string var2 = gridAwareTextBoxvar2.Text;
                string var3 = gridAwareTextBoxvar3.Text;
                string var4 = gridAwareTextBoxvar4.Text;

                LogVerbose("QUERY", $"Connection {connectionId} - Variables: var1='{var1}', var2='{var2}', var3='{var3}', var4='{var4}'");

                // Replace template variables
                string sql = sqlTemplate
                    .Replace("{{tableName}}", tableName ?? "")
                    .Replace("{{var1}}", var1)
                    .Replace("{{var2}}", var2)
                    .Replace("{{var3}}", var3)
                    .Replace("{{var4}}", var4);

                LogVerbose("QUERY", $"Connection {connectionId} - Final SQL: {sql}");
                RunQueryAndBind(sql, connectionString, grid, connectionId);
            }
            catch (Exception ex)
            {
                LogVerbose("QUERY", $"Connection {connectionId} - Error executing template query on table '{tableName}'", ex);
            }
        }

        private string GetSelectedTableName(TreeViewAdv treeView)
        {
            if (treeView.SelectedNode != null)
            {
                // If selected node is a child node (row count), return parent's name
                if (treeView.SelectedNode.Parent != null &&
                    treeView.SelectedNode.Text.StartsWith("Rows:"))
                    return treeView.SelectedNode.Parent.Text;
                // If selected node is parent or doesn't start with "Rows:", return its text
                else if (treeView.SelectedNode.Parent == null ||
                         !treeView.SelectedNode.Text.StartsWith("Rows:"))
                    return treeView.SelectedNode.Text;
            }
            return "";
        }

        private void RunQueryAndBind(string sql, string connectionString, 
            Syncfusion.WinForms.DataGrid.SfDataGrid grid, string connectionId = "")
        {
            if (string.IsNullOrWhiteSpace(sql) || string.IsNullOrWhiteSpace(connectionString))
            {
                LogVerbose("QUERY", $"Connection {connectionId} - Cannot execute query: missing SQL or connection string");
                return;
            }

            try
            {
                LogVerbose("QUERY", $"Connection {connectionId} - Executing query");
                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    LogConnection(connectionId, "Query execution", true);
                    
                    var cmd = new SqlCommand(sql, conn);
                    var adapter = new SqlDataAdapter(cmd);
                    var dt = new DataTable();
                    
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    adapter.Fill(dt);
                    stopwatch.Stop();

                    LogSqlOperation($"Query executed in {stopwatch.ElapsedMilliseconds}ms", "", sql, dt.Rows.Count);
                    BindTableToSfDataGrid(dt, grid);
                }
            }
            catch (Exception ex)
            {
                LogVerbose("QUERY", $"Connection {connectionId} - Query execution failed", ex);
                MessageBox.Show($"SQL Error: {ex.Message}", "Query Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Find Duplicates functionality
        private void sfButtonFindDuplicates_Click(object sender, EventArgs e)
        {
            LogUIAction("FindDuplicates", "Button clicked");

            // For Connection A
            if (!string.IsNullOrWhiteSpace(connectionStringA) && sqltreeViewAdvA.SelectedNode != null)
            {
                string tableNameA = GetSelectedTableName(sqltreeViewAdvA);
                if (!string.IsNullOrWhiteSpace(tableNameA))
                {
                    LogVerbose("DUPLICATES", $"Building duplicate search query for table '{tableNameA}' (Connection A)");
                    string sqlA = BuildFindDuplicatesQuery(tableNameA, connectionStringA);
                    if (!string.IsNullOrEmpty(sqlA))
                    {
                        LogVerbose("DUPLICATES", "Executing duplicate search for Connection A");
                        RunQueryAndBind(sqlA, connectionStringA, sfDataGridA, "A");
                    }
                    else
                    {
                        LogVerbose("DUPLICATES", "Failed to build duplicate search query for Connection A");
                    }
                }
            }

            // For Connection B
            if (!string.IsNullOrWhiteSpace(connectionStringB) && sqltreeViewAdvB.SelectedNode != null)
            {
                string tableNameB = GetSelectedTableName(sqltreeViewAdvB);
                if (!string.IsNullOrWhiteSpace(tableNameB))
                {
                    LogVerbose("DUPLICATES", $"Building duplicate search query for table '{tableNameB}' (Connection B)");
                    string sqlB = BuildFindDuplicatesQuery(tableNameB, connectionStringB);
                    if (!string.IsNullOrEmpty(sqlB))
                    {
                        LogVerbose("DUPLICATES", "Executing duplicate search for Connection B");
                        RunQueryAndBind(sqlB, connectionStringB, sfDataGridB, "B");
                    }
                    else
                    {
                        LogVerbose("DUPLICATES", "Failed to build duplicate search query for Connection B");
                    }
                }
            }

            LogVerbose("DUPLICATES", "Find duplicates operation completed");
        }

        private string BuildFindDuplicatesQuery(string tableName, string connectionString)
        {
            try
            {
                LogVerbose("DUPLICATES", $"Getting schema for table '{tableName}' to build duplicate search");
                
                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    var dtSchema = conn.GetSchema("Columns", new[] { null, null, tableName, null });
                    var columnNames = dtSchema.Rows.Cast<System.Data.DataRow>()
                        .Select(row => row["COLUMN_NAME"].ToString())
                        .ToList();

                    LogVerbose("DUPLICATES", $"Found {columnNames.Count} columns in table '{tableName}'");

                    if (columnNames.Count == 0)
                    {
                        LogVerbose("DUPLICATES", $"No columns found for table '{tableName}'");
                        return null;
                    }

                    var whereClauses = columnNames
                        .Select(col => $"CONVERT([{col}], NVARCHAR) LIKE '%dup%'");

                    string where = string.Join(" OR ", whereClauses);
                    string query = $"SELECT * FROM [{tableName}] WHERE {where}";
                    
                    LogVerbose("DUPLICATES", $"Built duplicate search query: {query}");
                    return query;
                }
            }
            catch (Exception ex)
            {
                LogVerbose("DUPLICATES", $"Error building duplicate search query for table '{tableName}'", ex);
                return null;
            }
        }

        private void BindTableToSfDataGrid(DataTable dt, Syncfusion.WinForms.DataGrid.SfDataGrid grid)
        {
            try
            {
                LogVerbose("UI", $"Binding DataTable to SfDataGrid - {dt.Rows.Count} rows, {dt.Columns.Count} columns");
                
                grid.DataSource = dt;
                grid.AllowFiltering = true;
                
                int configuredColumns = 0;
                foreach (var col in grid.Columns)
                {
                    col.AllowFiltering = true;
                    col.ImmediateUpdateColumnFilter = true;
                    col.AllowBlankFilters = false;
                    configuredColumns++;
                }
                
                LogVerbose("UI", $"SfDataGrid binding completed - {configuredColumns} columns configured for filtering");
            }
            catch (Exception ex)
            {
                LogVerbose("UI", "Error binding data to SfDataGrid", ex);
                throw;
            }
        }

        // Reset and reload functionality
        private void sfButtonreload_Click(object sender, EventArgs e)
        {
            LogUIAction("Reload", "Reload button clicked");
            ResetToolbox();
        }

        private void ResetToolbox()
        {
            LogVerbose("UI", "Resetting toolbox panel");

            try
            {
                toolboxPanel.Controls.Clear();

                if (sfButton1Inport != null)
                {
                    sfButton1Inport.Location = new Point(10, 10);
                    toolboxPanel.Controls.Add(sfButton1Inport);
                    sfButton1Inport.Enabled = true;
                    sfButton1Inport.Visible = true;
                    LogVerbose("UI", "Import button restored to toolbox");
                }

                // Clear variable text boxes
                if (gridAwareTextBoxvar1 != null) gridAwareTextBoxvar1.Text = "";
                if (gridAwareTextBoxvar2 != null) gridAwareTextBoxvar2.Text = "";
                if (gridAwareTextBoxvar3 != null) gridAwareTextBoxvar3.Text = "";
                if (gridAwareTextBoxvar4 != null) gridAwareTextBoxvar4.Text = "";

                LogVerbose("UI", "Variable text boxes cleared");
                LogVerbose("UI", "Toolbox reset completed successfully");
            }
            catch (Exception ex)
            {
                LogVerbose("UI", "Error during toolbox reset", ex);
            }
        }

        // Connection string management
        private void LoadConnectionStringsToComboBoxes()
        {
            LogVerbose("REGISTRY", "Loading connection strings from registry");

            try
            {
                var connectionStrings = GetAllConnectionStrings();
                LogVerbose("REGISTRY", $"Loaded {connectionStrings.Count} connection strings from registry");

                sfComboBoxconstringA.DataSource = connectionStrings.ToList();
                sfComboBoxconstringB.DataSource = connectionStrings.ToList();
                
                LogVerbose("UI", "Connection strings populated in combo boxes");
            }
            catch (Exception ex)
            {
                LogVerbose("REGISTRY", "Error loading connection strings from registry", ex);
            }
        }

        private List<string> GetAllConnectionStrings()
        {
            var connectionStrings = new List<string>();
            
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\MyApps\ConnectionStrings"))
                {
                    if (key != null)
                    {
                        var valueNames = key.GetValueNames();
                        LogVerbose("REGISTRY", $"Found {valueNames.Length} registry entries");

                        foreach (string name in valueNames)
                        {
                            string value = key.GetValue(name)?.ToString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                connectionStrings.Add(value);
                                LogVerbose("REGISTRY", $"Loaded connection string: {name}");
                            }
                        }
                    }
                    else
                    {
                        LogVerbose("REGISTRY", "Connection strings registry key not found");
                    }
                }
            }
            catch (Exception ex)
            {
                LogVerbose("REGISTRY", "Error accessing registry for connection strings", ex);
            }

            return connectionStrings;
        }

        private List<string> GetFilteredConnectionStrings(string searchText)
        {
            LogVerbose("SEARCH", $"Filtering connection strings with search text: '{searchText}'");
            
            var filtered = new List<string>();
            
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\MyApps\ConnectionStrings"))
                {
                    if (key != null)
                    {
                        foreach (string name in key.GetValueNames())
                        {
                            string value = key.GetValue(name)?.ToString();
                            if (!string.IsNullOrEmpty(value) &&
                                (string.IsNullOrEmpty(searchText) ||
                                 value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                filtered.Add(value);
                            }
                        }
                    }
                }
                
                LogVerbose("SEARCH", $"Filtered results: {filtered.Count} connection strings match");
            }
            catch (Exception ex)
            {
                LogVerbose("SEARCH", "Error filtering connection strings", ex);
            }

            return filtered;
        }

        // ComboBox dropdown handlers
        private void sfComboBoxconstringA_DropDown(object sender, EventArgs e)
        {
            LogVerbose("COMBOBOX", "ComboBox A dropdown opened - loading connection strings");
            
            var allStrings = GetAllConnectionStrings();
            LogVerbose("COMBOBOX", $"ComboBox A: Loading {allStrings.Count} connection strings");
            
            if (allStrings.Count == 0)
            {
                LogVerbose("COMBOBOX", "ComboBox A: No connection strings found in registry");
            }

            sfComboBoxconstringA.DataSource = allStrings;
            LogVerbose("COMBOBOX", "ComboBox A: DropDown population completed");
        }

        private void sfComboBoxconstringB_DropDown(object sender, EventArgs e)
        {
            LogVerbose("COMBOBOX", "ComboBox B dropdown opened - loading connection strings");
            
            var allStrings = GetAllConnectionStrings();
            LogVerbose("COMBOBOX", $"ComboBox B: Loading {allStrings.Count} connection strings");
            
            if (allStrings.Count == 0)
            {
                LogVerbose("COMBOBOX", "ComboBox B: No connection strings found in registry");
            }

            sfComboBoxconstringB.DataSource = allStrings;
            LogVerbose("COMBOBOX", "ComboBox B: DropDown population completed");
        }

        private void textBox_connectionsearch_TextChanged(object sender, EventArgs e)
        {
            string searchText = textBox_connectionsearch?.ToString() ?? string.Empty;
            LogVerbose("SEARCH", $"Connection search text changed: '{searchText}'");
            
            var filtered = GetFilteredConnectionStrings(searchText);

            sfComboBoxconstringA.DataSource = filtered.Any() ? filtered : null;
            sfComboBoxconstringB.DataSource = filtered.Any() ? filtered : null;
            
            LogVerbose("SEARCH", $"Updated combo boxes with {filtered.Count} filtered results");
        }

        // Menu and other event handlers
        private void exitItem_Click(object sender, EventArgs e)
        {
            LogVerbose("APPLICATION", "Exit menu item clicked - closing application");
            this.Close();
        }

        private void sfButton1_Click(object sender, EventArgs e)
        {
            LogUIAction("Button1", "Clicked");
        }

        private void Form1_Load_1(object sender, EventArgs e)
        {
            LogVerbose("STARTUP", "Form1_Load_1 event triggered");
        }

        private void sfButton3_Click(object sender, EventArgs e)
        {
            LogUIAction("Button3", "Clicked");
        }

        // QueryItem class for JSON deserialization
        public class QueryItem
        {
            public string label { get; set; }
            public string command { get; set; }
        }
    }
}