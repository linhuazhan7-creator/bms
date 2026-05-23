using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Timers;
using Modbus.Device;

namespace BMSMonitor
{
    public partial class Form1 : Form
    {
        // ==================== USB/串口 相关变量 ====================
        private SerialPort? _serialPort;
        private ModbusSerialMaster? _modbusMaster;
        private bool _isConnected = false;
        private string _currentPort = "COM1";
        private int _baudRate = 9600;
        private byte _slaveId = 1;
        
        // 缓存最新数据
        private float _lastTotalVoltage = 0;
        private float _lastCurrent = 0;
        private int _lastSOC = 0;
        private int _lastSOH = 0;
        private float[] _lastCellVoltages = new float[16];
        private float[] _lastTemperatures = new float[5];
        
        // SOC计算相关参数
        private float _batteryMinVoltage = 40.0f;      // 电池组最低电压 (16串*2.5V)
        private float _batteryMaxVoltage = 67.2f;     // 电池组最高电压 (16串*4.2V)
        private float _cellMinVoltage = 2.5f;          // 单体最低电压
        private float _cellMaxVoltage = 4.2f;          // 单体最高电压
        private float _nominalCapacity = 50000f;       // 标称容量 mAh
        private float _remainingCapacity = 0f;         // 剩余容量 mAh
        private DateTime _lastCapacityUpdate;
        private bool _useVoltageMethod = true;         // true:电压法, false:库伦计数法
        
        // 电压-SOC映射表 (电压V, SOC%)
        private readonly Dictionary<float, float> _voltageSOCMap = new Dictionary<float, float>
        {
            { 67.2f, 100 },  // 满电 4.2V * 16
            { 65.6f, 90 },
            { 64.0f, 80 },
            { 62.4f, 70 },
            { 60.8f, 60 },
            { 58.4f, 50 },
            { 56.0f, 40 },
            { 53.6f, 30 },
            { 51.2f, 20 },
            { 48.0f, 10 },
            { 40.0f, 0 }     // 亏电 2.5V * 16
        };
        
        // 单体电压-SOC映射表
        private readonly Dictionary<float, float> _cellVoltageSOCMap = new Dictionary<float, float>
        {
            { 4.20f, 100 },
            { 4.10f, 90 },
            { 4.00f, 80 },
            { 3.90f, 70 },
            { 3.80f, 60 },
            { 3.65f, 50 },
            { 3.50f, 40 },
            { 3.35f, 30 },
            { 3.20f, 20 },
            { 3.00f, 10 },
            { 2.50f, 0 }
        };
        
        // 读取线程控制
        private Thread? _readThread;
        private bool _keepReading = false;
        
        // ==================== 实时监控 UI 控件引用 ====================
        private Label? lblTotalVoltage, lblCurrent, lblSOC, lblSOH;
        private Label? lblRemainCapacity, lblFullCapacity, lblCycleCount;
        private Label? lblIndepVoltage1, lblIndepCurrent1;
        private Label? lblVoltageMethod, lblCoulombMethod;
        private Label[] cellVoltageLabels = new Label[16];
        private Label[] tempLabels = new Label[5];
        private Label? lblChgMos, lblDsgMos, lblChgValid, lblDsgValid, lblACin, lblCurrentLimit, lblFullChg;
        
        // ==================== 参数设置 UI 控件引用 ====================
        private TabControl? mainTabControl;
        private CheckBox? chkChargeEnable;
        private NumericUpDown? nudChargeAlarm, nudChargeProtect, nudChargeDelay;
        private CheckBox? chkDischargeEnable;
        private NumericUpDown? nudDisAlarm, nudDisProtect1, nudDisDelay1, nudDisProtect2, nudDisDelay2, nudShortDelay;
        private NumericUpDown? nudCutVoltage, nudCutCurrent, nudLowBatteryAlarm;
        private NumericUpDown? nudBalanceVoltage, nudBalanceDiff, nudSleepVoltage, nudSleepDelay;
        
        // SOC校准参数
        private NumericUpDown? nudMinVoltage, nudMaxVoltage, nudNominalCapacity;
        private ComboBox? cmbSOCMethod;
        
        // ==================== UI 面板 ====================
        private Panel? monitorPanel;
        private Panel? settingsPanel;
        private Label? lblStatusBar;
        private System.Timers.Timer? _statusTimer;

        public Form1()
        {
            InitializeComponent();
            InitializeSOCParameters();
        }

        private void InitializeComponent()
        {
            this.Text = "PACE BMS Monitor - 派能电池监控系统";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(1200, 800);
            this.BackColor = Color.FromArgb(240, 242, 245);
            this.FormClosing += Form1_FormClosing;

            Panel topMenuPanel = CreateTopMenu();
            this.Controls.Add(topMenuPanel);

            Panel contentContainer = new Panel
            {
                Location = new Point(0, 40),
                Size = new Size(this.ClientSize.Width, this.ClientSize.Height - 75),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            monitorPanel = CreateMonitorPanel();
            monitorPanel.Visible = true;
            contentContainer.Controls.Add(monitorPanel);

            settingsPanel = CreateSettingsPanel();
            settingsPanel.Visible = false;
            contentContainer.Controls.Add(settingsPanel);

            this.Controls.Add(contentContainer);

            Panel statusPanel = CreateStatusBar();
            this.Controls.Add(statusPanel);
        }

        private void InitializeSOCParameters()
        {
            _remainingCapacity = _nominalCapacity;
            _lastCapacityUpdate = DateTime.Now;
        }

        private Panel CreateTopMenu()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(52, 73, 94)
            };

            string[] menuItems = { "实时监控", "参数设置" };
            int xPos = 10;
            
            for (int i = 0; i < menuItems.Length; i++)
            {
                Button btn = new Button
                {
                    Text = menuItems[i],
                    Location = new Point(xPos, 5),
                    Size = new Size(120, 30),
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.White,
                    BackColor = i == 0 ? Color.FromArgb(41, 128, 185) : Color.Transparent,
                    Font = new Font("微软雅黑", 9, FontStyle.Bold),
                    Tag = menuItems[i]
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += MenuButton_Click!;
                panel.Controls.Add(btn);
                xPos += 130;
            }
            
            Button btnConnect = new Button
            {
                Text = "连接设备",
                Location = new Point(panel.Width - 230, 5),
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(46, 204, 113),
                Font = new Font("微软雅黑", 9),
                Name = "btnConnect"
            };
            btnConnect.FlatAppearance.BorderSize = 0;
            btnConnect.Click += BtnConnect_Click!;
            panel.Controls.Add(btnConnect);
            
            Button btnDisconnect = new Button
            {
                Text = "断开",
                Location = new Point(panel.Width - 120, 5),
                Size = new Size(60, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(231, 76, 60),
                Font = new Font("微软雅黑", 9),
                Name = "btnDisconnect",
                Enabled = false
            };
            btnDisconnect.FlatAppearance.BorderSize = 0;
            btnDisconnect.Click += BtnDisconnect_Click!;
            panel.Controls.Add(btnDisconnect);
            
            Label lblComStatus = new Label
            {
                Text = "● 未连接",
                Location = new Point(panel.Width - 190, 12),
                ForeColor = Color.Red,
                Font = new Font("微软雅黑", 9),
                AutoSize = true,
                Name = "lblComStatus"
            };
            panel.Controls.Add(lblComStatus);
            
            return panel;
        }

        private void MenuButton_Click(object sender, EventArgs e)
        {
            if (sender is Button btn)
            {
                string menuItem = btn.Tag?.ToString() ?? "";
                
                if (btn.Parent is Panel parent)
                {
                    foreach (Control ctrl in parent.Controls)
                    {
                        if (ctrl is Button b && b != btn && (b.Text == "实时监控" || b.Text == "参数设置"))
                            b.BackColor = Color.Transparent;
                    }
                }
                btn.BackColor = Color.FromArgb(41, 128, 185);
                
                if (monitorPanel != null && settingsPanel != null)
                {
                    monitorPanel.Visible = menuItem == "实时监控";
                    settingsPanel.Visible = menuItem == "参数设置";
                }
            }
        }

        // ==================== 实时监控面板 ====================
        private Panel CreateMonitorPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };

            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 60F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));

            // 左上方：实时监控
            GroupBox monitorBox = new GroupBox 
            { 
                Text = "实时监控", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            TableLayoutPanel monitorTable = new TableLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                ColumnCount = 2, 
                RowCount = 11, 
                Padding = new Padding(5) 
            };
            for (int i = 0; i < 11; i++) 
                monitorTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            
            string[] names = { "总电压:", "电流:", "SOC:", "SOH:", "剩余容量:", "满充容量:", "循环次数:", "独立总压1:", "独立电流1:", "SOC计算方法:", "单体最低电压:" };
            string[] units = { " V", " A", " %", " %", " Ah", " Ah", "", " V", " A", "", " V" };
            
            for (int i = 0; i < names.Length; i++)
            {
                monitorTable.Controls.Add(new Label { Text = names[i], AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, i);
                Label valueLabel = new Label { Text = "0" + units[i], AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
                monitorTable.Controls.Add(valueLabel, 1, i);
                
                switch (i)
                {
                    case 0: lblTotalVoltage = valueLabel; break;
                    case 1: lblCurrent = valueLabel; break;
                    case 2: lblSOC = valueLabel; break;
                    case 3: lblSOH = valueLabel; break;
                    case 4: lblRemainCapacity = valueLabel; break;
                    case 5: lblFullCapacity = valueLabel; break;
                    case 6: lblCycleCount = valueLabel; break;
                    case 7: lblIndepVoltage1 = valueLabel; break;
                    case 8: lblIndepCurrent1 = valueLabel; break;
                    case 9: lblVoltageMethod = valueLabel; break;
                    case 10: lblCoulombMethod = valueLabel; break;
                }
            }
            monitorBox.Controls.Add(monitorTable);
            mainLayout.Controls.Add(monitorBox, 0, 0);

            // 中间上方：单体电压
            GroupBox cellBox = new GroupBox 
            { 
                Text = "单体电压", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            TableLayoutPanel cellPanel = new TableLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                ColumnCount = 2, 
                RowCount = 17, 
                AutoScroll = true 
            };
            cellPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            for (int i = 1; i <= 16; i++) 
                cellPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            
            cellPanel.Controls.Add(new Label { Text = "电池编号", Font = new Font("微软雅黑", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter }, 0, 0);
            cellPanel.Controls.Add(new Label { Text = "电压(V)", Font = new Font("微软雅黑", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
            
            for (int i = 1; i <= 16; i++)
            {
                cellPanel.Controls.Add(new Label { Text = i.ToString(), TextAlign = ContentAlignment.MiddleCenter }, 0, i);
                Label voltLabel = new Label { Text = "0.000", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Blue };
                cellPanel.Controls.Add(voltLabel, 1, i);
                cellVoltageLabels[i - 1] = voltLabel;
            }
            cellBox.Controls.Add(cellPanel);
            mainLayout.Controls.Add(cellBox, 1, 0);

            // 右上方：均衡 + 主动均衡 + 当前PACK
            Panel rightTop = new Panel { Dock = DockStyle.Fill };
            TableLayoutPanel rightTopLayout = new TableLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                ColumnCount = 1, 
                RowCount = 3 
            };
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));
            rightTopLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            GroupBox balanceBox = new GroupBox 
            { 
                Text = "均衡", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            Button btnBalance = new Button 
            { 
                Text = "均衡", 
                Size = new Size(100, 40), 
                BackColor = Color.LightBlue, 
                Location = new Point(20, 20),
                UseVisualStyleBackColor = false
            };
            btnBalance.Click += BtnBalance_Click!;
            balanceBox.Controls.Add(btnBalance);
            rightTopLayout.Controls.Add(balanceBox, 0, 0);

            GroupBox activeBalanceBox = new GroupBox 
            { 
                Text = "主动均衡", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            Button btnActive = new Button 
            { 
                Text = "主动均衡", 
                Size = new Size(100, 40), 
                BackColor = Color.LightGreen, 
                Location = new Point(20, 20),
                UseVisualStyleBackColor = false
            };
            btnActive.Click += BtnActiveBalance_Click!;
            activeBalanceBox.Controls.Add(btnActive);
            rightTopLayout.Controls.Add(activeBalanceBox, 0, 1);

            GroupBox packBox = new GroupBox 
            { 
                Text = "当前PACK", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            packBox.Controls.Add(new Label { Text = "PACE BMS", Location = new Point(20, 25), AutoSize = true, Font = new Font("微软雅黑", 10, FontStyle.Bold) });
            packBox.Controls.Add(new Label { Text = "从站地址: 1", Location = new Point(20, 55), AutoSize = true, Font = new Font("微软雅黑", 10, FontStyle.Bold) });
            rightTopLayout.Controls.Add(packBox, 0, 2);

            rightTop.Controls.Add(rightTopLayout);
            mainLayout.Controls.Add(rightTop, 2, 0);

            // 左下方：温度信息
            GroupBox tempBox = new GroupBox 
            { 
                Text = "温度信息", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            TableLayoutPanel tempPanel = new TableLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                ColumnCount = 2, 
                RowCount = 6 
            };
            tempPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            for (int i = 1; i <= 5; i++) 
                tempPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            
            tempPanel.Controls.Add(new Label { Text = "温度含义", Font = new Font("微软雅黑", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter }, 0, 0);
            tempPanel.Controls.Add(new Label { Text = "温度(°C)", Font = new Font("微软雅黑", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
            
            string[] tempNames = { "电池温度1", "电池温度2", "电池温度3", "电池温度4", "MOS温度" };
            for (int i = 0; i < tempNames.Length; i++)
            {
                tempPanel.Controls.Add(new Label { Text = tempNames[i], TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(5, 0, 0, 0) }, 0, i + 1);
                Label tempLabel = new Label { Text = "0.0", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Blue };
                tempPanel.Controls.Add(tempLabel, 1, i + 1);
                tempLabels[i] = tempLabel;
            }
            tempBox.Controls.Add(tempPanel);
            mainLayout.Controls.Add(tempBox, 0, 1);

            // 中间下方：系统状态
            GroupBox statusBox = new GroupBox 
            { 
                Text = "系统状态(只读)", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            FlowLayoutPanel statusFlow = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                FlowDirection = FlowDirection.TopDown, 
                Padding = new Padding(10) 
            };
            
            string[] statusNames = { "充电MOS", "放电MOS", "充电电流有效", "放电电流有效", "ACin", "限流", "满充" };
            Label[] statusLabels = new Label[statusNames.Length];
            for (int i = 0; i < statusNames.Length; i++)
            {
                var lbl = new Label { Text = "● " + statusNames[i], AutoSize = true, Font = new Font("微软雅黑", 9), ForeColor = Color.Red };
                statusFlow.Controls.Add(lbl);
                statusLabels[i] = lbl;
            }
            if (statusLabels.Length >= 7)
            {
                lblChgMos = statusLabels[0];
                lblDsgMos = statusLabels[1];
                lblChgValid = statusLabels[2];
                lblDsgValid = statusLabels[3];
                lblACin = statusLabels[4];
                lblCurrentLimit = statusLabels[5];
                lblFullChg = statusLabels[6];
            }
            statusBox.Controls.Add(statusFlow);
            mainLayout.Controls.Add(statusBox, 1, 1);

            // 右下方：告警状态
            GroupBox alarmBox = new GroupBox 
            { 
                Text = "告警状态", 
                Dock = DockStyle.Fill, 
                Font = new Font("微软雅黑", 10, FontStyle.Bold), 
                BackColor = Color.White 
            };
            FlowLayoutPanel alarmFlow = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Fill, 
                FlowDirection = FlowDirection.TopDown, 
                Padding = new Padding(10) 
            };
            alarmFlow.Controls.Add(new Label { Text = "● 无", ForeColor = Color.Green, Font = new Font("微软雅黑", 9), AutoSize = true });
            alarmFlow.Controls.Add(new Label { Text = "● 保护状态", ForeColor = Color.Orange, Font = new Font("微软雅黑", 9), AutoSize = true });
            alarmFlow.Controls.Add(new Label { Text = "● 故障状态", ForeColor = Color.Red, Font = new Font("微软雅黑", 9), AutoSize = true });
            alarmBox.Controls.Add(alarmFlow);
            mainLayout.Controls.Add(alarmBox, 2, 1);

            panel.Controls.Add(mainLayout);
            return panel;
        }

        // ==================== 参数设置面板 ====================
        private Panel CreateSettingsPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };

            mainTabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10),
                Padding = new Point(10, 5)
            };

            mainTabControl.TabPages.Add(CreateProtectionTabPage());
            mainTabControl.TabPages.Add(CreateParametersTabPage());
            mainTabControl.TabPages.Add(CreateAdvancedSettingsTabPage());
            mainTabControl.TabPages.Add(CreateSOCSettingsTabPage());

            panel.Controls.Add(mainTabControl);
            return panel;
        }

        private TabPage CreateProtectionTabPage()
        {
            TabPage page = new TabPage("保护参数");
            
            DataGridView protectionGridView = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = false,
                RowHeadersWidth = 40,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.Fixed3D,
                Font = new Font("微软雅黑", 9)
            };

            protectionGridView.Columns.Add("ParamName", "参数");
            protectionGridView.Columns.Add("Alarm", "告警(V/°C)");
            protectionGridView.Columns.Add("Protect", "保护(V/°C)");
            protectionGridView.Columns.Add("Recover", "保护恢复(V/°C)");
            protectionGridView.Columns.Add("Enable", "是否启用");
            protectionGridView.Columns.Add("Delay", "保护延时(ms)");

            if (protectionGridView.Columns["Alarm"] != null)
                protectionGridView.Columns["Alarm"]!.DefaultCellStyle.Format = "0.00";
            if (protectionGridView.Columns["Protect"] != null)
                protectionGridView.Columns["Protect"]!.DefaultCellStyle.Format = "0.00";
            if (protectionGridView.Columns["Recover"] != null)
                protectionGridView.Columns["Recover"]!.DefaultCellStyle.Format = "0.00";

            string[][] rows = new string[][]
            {
                new string[] { "单体过充", "4.25", "4.30", "4.10", "True", "1000" },
                new string[] { "单体过放", "2.80", "2.50", "3.00", "True", "1000" },
                new string[] { "总体过充", "58.00", "60.00", "56.00", "True", "1000" },
                new string[] { "总体过放", "40.00", "36.00", "42.00", "True", "1000" },
                new string[] { "充电过流", "50", "100", "30", "True", "500" },
                new string[] { "放电过流", "80", "150", "60", "True", "500" },
                new string[] { "MOS温度", "80", "90", "75", "True", "1000" },
                new string[] { "环境温度", "60", "70", "55", "True", "1000" }
            };

            foreach (var row in rows)
            {
                int rowIndex = protectionGridView.Rows.Add();
                protectionGridView.Rows[rowIndex].Cells[0].Value = row[0];
                protectionGridView.Rows[rowIndex].Cells[1].Value = decimal.Parse(row[1]);
                protectionGridView.Rows[rowIndex].Cells[2].Value = decimal.Parse(row[2]);
                protectionGridView.Rows[rowIndex].Cells[3].Value = decimal.Parse(row[3]);
                protectionGridView.Rows[rowIndex].Cells[4].Value = bool.Parse(row[4]);
                protectionGridView.Rows[rowIndex].Cells[5].Value = int.Parse(row[5]);
            }

            page.Controls.Add(protectionGridView);
            return page;
        }

        private TabPage CreateParametersTabPage()
        {
            TabPage page = new TabPage("参数设置");
            
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));

            layout.Controls.Add(CreateChargeParamGroup(), 0, 0);
            layout.Controls.Add(CreateBatteryParamGroup(), 1, 0);
            layout.Controls.Add(CreateDischargeParamGroup(), 0, 1);
            layout.Controls.Add(CreateBalanceSleepGroup(), 1, 1);

            Panel buttonPanel = CreateButtonPanel();
            layout.Controls.Add(buttonPanel, 0, 2);
            layout.SetColumnSpan(buttonPanel, 2);

            page.Controls.Add(layout);
            return page;
        }

        private GroupBox CreateChargeParamGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "充电过流参数",
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                BackColor = Color.White,
                Margin = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

            chkChargeEnable = new CheckBox { Text = "是否启用", Checked = true, AutoSize = true };
            layout.Controls.Add(chkChargeEnable, 0, 0);
            layout.SetColumnSpan(chkChargeEnable, 2);

            layout.Controls.Add(new Label { Text = "充电过流告警(A):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            nudChargeAlarm = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Value = 50, Width = 120 };
            layout.Controls.Add(nudChargeAlarm, 1, 1);

            layout.Controls.Add(new Label { Text = "充电过流保护(A):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 2);
            nudChargeProtect = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Value = 100, Width = 120 };
            layout.Controls.Add(nudChargeProtect, 1, 2);

            layout.Controls.Add(new Label { Text = "充电过流保护延时(ms):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 3);
            nudChargeDelay = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 1000, Width = 120 };
            layout.Controls.Add(nudChargeDelay, 1, 3);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateBatteryParamGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "设置电池参数",
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                BackColor = Color.White,
                Margin = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            layout.Controls.Add(new Label { Text = "电池包截止电压(V):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 0);
            nudCutVoltage = new NumericUpDown { DecimalPlaces = 1, Minimum = 20, Maximum = 100, Value = 42, Width = 120 };
            layout.Controls.Add(nudCutVoltage, 1, 0);

            layout.Controls.Add(new Label { Text = "电池包截止电流(A):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            nudCutCurrent = new NumericUpDown { Minimum = 0, Maximum = 500, Value = 50, Width = 120, Increment = 1 };
            layout.Controls.Add(nudCutCurrent, 1, 1);

            layout.Controls.Add(new Label { Text = "低电量告警(%):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 2);
            nudLowBatteryAlarm = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 20, Width = 120 };
            layout.Controls.Add(nudLowBatteryAlarm, 1, 2);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateDischargeParamGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "放电过流参数",
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                BackColor = Color.White,
                Margin = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 7,
                Padding = new Padding(10)
            };
            for (int i = 0; i < 7; i++)
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

            chkDischargeEnable = new CheckBox { Text = "是否启用", Checked = true, AutoSize = true };
            layout.Controls.Add(chkDischargeEnable, 0, 0);
            layout.SetColumnSpan(chkDischargeEnable, 2);

            layout.Controls.Add(new Label { Text = "放电过流告警(A):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            nudDisAlarm = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Value = 80, Width = 120 };
            layout.Controls.Add(nudDisAlarm, 1, 1);

            layout.Controls.Add(new Label { Text = "放电过流保护1(A):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 2);
            nudDisProtect1 = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Value = 150, Width = 120 };
            layout.Controls.Add(nudDisProtect1, 1, 2);

            layout.Controls.Add(new Label { Text = "放电过流保护延时1(ms):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 3);
            nudDisDelay1 = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 500, Width = 120 };
            layout.Controls.Add(nudDisDelay1, 1, 3);

            layout.Controls.Add(new Label { Text = "放电过流保护2(A):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 4);
            nudDisProtect2 = new NumericUpDown { DecimalPlaces = 1, Minimum = 0, Maximum = 500, Value = 200, Width = 120 };
            layout.Controls.Add(nudDisProtect2, 1, 4);

            layout.Controls.Add(new Label { Text = "放电过流保护延时2(ms):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 5);
            nudDisDelay2 = new NumericUpDown { Minimum = 0, Maximum = 10000, Value = 100, Width = 120 };
            layout.Controls.Add(nudDisDelay2, 1, 5);

            layout.Controls.Add(new Label { Text = "短路保护延时(μs):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 6);
            nudShortDelay = new NumericUpDown { Minimum = 0, Maximum = 1000, Value = 50, Width = 120 };
            layout.Controls.Add(nudShortDelay, 1, 6);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateBalanceSleepGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "均衡/休眠电压设置",
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                BackColor = Color.White,
                Margin = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            for (int i = 0; i < 4; i++)
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            layout.Controls.Add(new Label { Text = "均衡开启电压(V):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 0);
            nudBalanceVoltage = new NumericUpDown { DecimalPlaces = 2, Minimum = 3.0m, Maximum = 4.5m, Value = 3.40m, Width = 120, Increment = 0.01m };
            layout.Controls.Add(nudBalanceVoltage, 1, 0);

            layout.Controls.Add(new Label { Text = "均衡开启压差(mV):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            nudBalanceDiff = new NumericUpDown { Minimum = 0, Maximum = 500, Value = 30, Width = 120 };
            layout.Controls.Add(nudBalanceDiff, 1, 1);

            layout.Controls.Add(new Label { Text = "单体休眠电压(V):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 2);
            nudSleepVoltage = new NumericUpDown { DecimalPlaces = 2, Minimum = 2.5m, Maximum = 3.5m, Value = 3.00m, Width = 120, Increment = 0.01m };
            layout.Controls.Add(nudSleepVoltage, 1, 2);

            layout.Controls.Add(new Label { Text = "单体休眠延时(min):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 3);
            nudSleepDelay = new NumericUpDown { Minimum = 0, Maximum = 120, Value = 30, Width = 120 };
            layout.Controls.Add(nudSleepDelay, 1, 3);

            group.Controls.Add(layout);
            return group;
        }

        private TabPage CreateSOCSettingsTabPage()
        {
            TabPage page = new TabPage("SOC校准设置");
            
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(20),
                AutoScroll = true
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

            layout.Controls.Add(new Label { Text = "SOC计算方法:", AutoSize = true, Font = new Font("微软雅黑", 10, FontStyle.Bold) }, 0, 0);
            cmbSOCMethod = new ComboBox 
            { 
                Items = { "电压法（查表）", "电压法（线性）", "库伦计数法", "BMS直接读取" },
                SelectedIndex = 3,
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbSOCMethod.SelectedIndexChanged += CmbSOCMethod_SelectedIndexChanged!;
            layout.Controls.Add(cmbSOCMethod, 1, 0);

            layout.Controls.Add(new Label { Text = "电池组最低电压(V):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            nudMinVoltage = new NumericUpDown { DecimalPlaces = 1, Minimum = 20, Maximum = 100, Value = 40m, Width = 150 };
            layout.Controls.Add(nudMinVoltage, 1, 1);

            layout.Controls.Add(new Label { Text = "电池组最高电压(V):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 2);
            nudMaxVoltage = new NumericUpDown { DecimalPlaces = 1, Minimum = 20, Maximum = 100, Value = 67.2m, Width = 150 };
            layout.Controls.Add(nudMaxVoltage, 1, 2);

            layout.Controls.Add(new Label { Text = "标称容量(Ah):", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 3);
            nudNominalCapacity = new NumericUpDown { Minimum = 0, Maximum = 2000, Value = 500, Width = 150, Increment = 10 };
            layout.Controls.Add(nudNominalCapacity, 1, 3);

            Panel buttonPanel = new Panel { Height = 40 };
            Button btnCalibrateSOC = new Button 
            { 
                Text = "校准SOC", 
                Size = new Size(100, 35),
                BackColor = Color.FromArgb(52, 152, 219),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(0, 0)
            };
            btnCalibrateSOC.Click += BtnCalibrateSOC_Click!;
            buttonPanel.Controls.Add(btnCalibrateSOC);
            layout.Controls.Add(buttonPanel, 1, 4);

            page.Controls.Add(layout);
            return page;
        }

        private Panel CreateButtonPanel()
        {
            Panel panel = new Panel
            {
                Height = 60,
                BackColor = Color.White
            };

            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10)
            };

            Button[] buttons = {
                new Button { Text = "读取参数", Size = new Size(100, 40), BackColor = Color.FromArgb(52, 152, 219), ForeColor = Color.White, FlatStyle = FlatStyle.Flat },
                new Button { Text = "写入参数", Size = new Size(100, 40), BackColor = Color.FromArgb(46, 204, 113), ForeColor = Color.White, FlatStyle = FlatStyle.Flat },
                new Button { Text = "恢复默认", Size = new Size(100, 40), BackColor = Color.FromArgb(241, 196, 15), ForeColor = Color.White, FlatStyle = FlatStyle.Flat },
                new Button { Text = "清空参数", Size = new Size(100, 40), BackColor = Color.FromArgb(230, 126, 34), ForeColor = Color.White, FlatStyle = FlatStyle.Flat },
                new Button { Text = "导出参数", Size = new Size(100, 40), BackColor = Color.FromArgb(155, 89, 182), ForeColor = Color.White, FlatStyle = FlatStyle.Flat },
                new Button { Text = "导入参数", Size = new Size(100, 40), BackColor = Color.FromArgb(52, 73, 94), ForeColor = Color.White, FlatStyle = FlatStyle.Flat }
            };

            buttons[0].Click += (s, e) => ReadAllParameters();
            buttons[1].Click += (s, e) => WriteAllParameters();
            buttons[2].Click += (s, e) => RestoreAllDefaults();
            buttons[3].Click += (s, e) => ClearAllParameters();
            buttons[4].Click += (s, e) => ExportAllParameters();
            buttons[5].Click += (s, e) => ImportAllParameters();

            foreach (var btn in buttons)
            {
                btn.FlatAppearance.BorderSize = 0;
                flow.Controls.Add(btn);
            }

            panel.Controls.Add(flow);
            return panel;
        }

        private Panel CreateStatusBar()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 35,
                BackColor = Color.FromArgb(236, 240, 241)
            };

            lblStatusBar = new Label
            {
                Text = $"日期和时间: {DateTime.Now:MM/dd/yyyy HH:mm:ss}    固件版本: PACE V2.0    BMS信息: 等待连接",
                Location = new Point(10, 8),
                AutoSize = true,
                Font = new Font("微软雅黑", 9),
                ForeColor = Color.FromArgb(52, 73, 94),
                Name = "lblStatusBar"
            };

            _statusTimer = new System.Timers.Timer(1000);
            _statusTimer.Elapsed += (s, e) => 
            {
                this.Invoke(new Action(() =>
                {
                    if (lblStatusBar != null)
                    {
                        string status = _isConnected ? $"正常  SOC: {_lastSOC}%" : "等待连接";
                        lblStatusBar.Text = $"日期和时间: {DateTime.Now:MM/dd/yyyy HH:mm:ss}    固件版本: PACE V2.0    BMS信息: {status}    PACK: PACE BMS";
                    }
                }));
            };
            _statusTimer.Start();

            panel.Controls.Add(lblStatusBar);
            return panel;
        }

        // ==================== SOC计算核心方法 ====================
        
        private int CalculateSOCFromVoltageTable(float voltage)
        {
            if (voltage <= _batteryMinVoltage) return 0;
            if (voltage >= _batteryMaxVoltage) return 100;
            
            var sortedPoints = _voltageSOCMap.OrderBy(kv => kv.Key).ToList();
            
            for (int i = 0; i < sortedPoints.Count - 1; i++)
            {
                if (voltage >= sortedPoints[i].Key && voltage <= sortedPoints[i + 1].Key)
                {
                    float voltageRange = sortedPoints[i + 1].Key - sortedPoints[i].Key;
                    float socRange = sortedPoints[i + 1].Value - sortedPoints[i].Value;
                    float voltageOffset = voltage - sortedPoints[i].Key;
                    float ratio = voltageOffset / voltageRange;
                    int soc = (int)(sortedPoints[i].Value + ratio * socRange);
                    return Math.Clamp(soc, 0, 100);
                }
            }
            
            return 50;
        }

        private int CalculateSOCFromVoltageLinear(float voltage)
        {
            if (voltage <= _batteryMinVoltage) return 0;
            if (voltage >= _batteryMaxVoltage) return 100;
            
            float soc = (voltage - _batteryMinVoltage) / (_batteryMaxVoltage - _batteryMinVoltage) * 100;
            return (int)Math.Clamp(soc, 0, 100);
        }

        private int CalculateSOCFromCellVoltages()
        {
            if (_lastCellVoltages.Length == 0) return 0;
            
            float minCellVoltage = _lastCellVoltages.Min();
            float maxCellVoltage = _lastCellVoltages.Max();
            float avgCellVoltage = _lastCellVoltages.Average();
            
            if (minCellVoltage <= _cellMinVoltage) return 0;
            if (minCellVoltage >= _cellMaxVoltage) return 100;
            
            var sortedPoints = _cellVoltageSOCMap.OrderBy(kv => kv.Key).ToList();
            
            for (int i = 0; i < sortedPoints.Count - 1; i++)
            {
                if (minCellVoltage >= sortedPoints[i].Key && minCellVoltage <= sortedPoints[i + 1].Key)
                {
                    float voltageRange = sortedPoints[i + 1].Key - sortedPoints[i].Key;
                    float socRange = sortedPoints[i + 1].Value - sortedPoints[i].Value;
                    float voltageOffset = minCellVoltage - sortedPoints[i].Key;
                    float ratio = voltageOffset / voltageRange;
                    int soc = (int)(sortedPoints[i].Value + ratio * socRange);
                    return Math.Clamp(soc, 0, 100);
                }
            }
            
            return 50;
        }

        private int CalculateSOCFromCoulomb(float current)
        {
            DateTime now = DateTime.Now;
            TimeSpan elapsed = now - _lastCapacityUpdate;
            double hours = elapsed.TotalHours;
            
            float capacityChange = (float)(current * hours * 1000);
            
            if (current > 0)
            {
                _remainingCapacity += capacityChange;
                if (_remainingCapacity > _nominalCapacity)
                    _remainingCapacity = _nominalCapacity;
            }
            else if (current < 0)
            {
                _remainingCapacity += capacityChange;
                if (_remainingCapacity < 0)
                    _remainingCapacity = 0;
            }
            
            _lastCapacityUpdate = now;
            
            int soc = (int)((_remainingCapacity / _nominalCapacity) * 100);
            return Math.Clamp(soc, 0, 100);
        }

        private void CalibrateSOC(int manualSOC)
        {
            _lastSOC = manualSOC;
            _remainingCapacity = _nominalCapacity * manualSOC / 100;
            _lastCapacityUpdate = DateTime.Now;
            
            this.Invoke(new Action(() =>
            {
                if (lblSOC != null)
                    lblSOC.Text = _lastSOC.ToString() + " %";
                MessageBox.Show($"SOC已校准为 {manualSOC}%", "校准成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }));
        }

        // ==================== USB/串口 通信方法 ====================
        
        private void BtnConnect_Click(object sender, EventArgs e)
        {
            using (var dialog = new USBDeviceDialog())
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _currentPort = dialog.SelectedPort;
                    _baudRate = dialog.SelectedBaudRate;
                    _slaveId = dialog.SelectedSlaveId;
                    ConnectToUSB();
                }
            }
        }

        private void BtnDisconnect_Click(object sender, EventArgs e)
        {
            DisconnectUSB();
        }

        private void ConnectToUSB()
        {
            try
            {
                // PACE BMS 使用偶校验或无校验，这里使用无校验
                _serialPort = new SerialPort(_currentPort, _baudRate, Parity.None, 8, StopBits.One);
                _serialPort.ReadTimeout = 1000;
                _serialPort.WriteTimeout = 1000;
                _serialPort.Open();
                
                _modbusMaster = ModbusSerialMaster.CreateRtu(_serialPort);
                
                _isConnected = true;
                _keepReading = true;
                
                _readThread = new Thread(ReadModbusData);
                _readThread.IsBackground = true;
                _readThread.Start();
                
                UpdateComStatus(true, $"{_currentPort} ({_baudRate}bps)");
                UpdateButtonsState(true);
                
                MessageBox.Show($"成功连接到 {_currentPort} (波特率: {_baudRate})！\n请确保从站地址设置为 {_slaveId}", 
                    "连接成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败: {ex.Message}\n\n请确保：\n1. USB转串口设备已连接\n2. 串口号正确\n3. 波特率设置正确\n4. 从站地址正确", 
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateComStatus(false, "");
            }
        }
        
        // ==================== PACE BMS 核心读取方法（修改后） ====================
        private void ReadModbusData()
        {
            while (_keepReading && _isConnected && _modbusMaster != null)
            {
                try
                {
                    // PACE BMS 标准协议：读取保持寄存器 0-36 (共37个寄存器)
                    // 寄存器地址: 
                    //   0=电流(10mA), 1=总电压(10mV), 2=SOC(%), 3=SOH(%),
                    //   4=剩余容量(10mAh), 5=满充容量(10mAh), 7=循环次数,
                    //   9=告警标志, 10=保护标志, 11=状态标志, 12=均衡状态,
                    //   15-30=单体电压(16个, mV), 
                    //   31-34=电池温度1-4(0.1°C), 35=MOS温度(0.1°C), 36=环境温度(0.1°C)
                    ushort[] registers = _modbusMaster.ReadHoldingRegisters(_slaveId, 0, 37);
                    
                    if (registers != null && registers.Length >= 37)
                    {
                        // 地址0: 电流 (有符号16位，单位10mA)
                        short currentRaw = (short)registers[0];
                        _lastCurrent = currentRaw / 100.0f;  // 10mA -> A
                        
                        // 地址1: 总电压 (单位10mV)
                        _lastTotalVoltage = registers[1] / 100.0f;  // 10mV -> V
                        
                        // 地址2: SOC (UINT8，低字节有效)
                        int bmsSOC = registers[2] & 0xFF;
                        
                        // 地址3: SOH (UINT8，低字节有效)
                        _lastSOH = registers[3] & 0xFF;
                        
                        // 地址4: 剩余容量 (单位10mAh) -> mAh 保持与原代码兼容
                        float remainmAh = registers[4] * 10;  // 10mAh -> mAh
                        
                        // 地址5: 满充容量 (单位10mAh) -> mAh
                        _nominalCapacity = registers[5] * 10;  // 10mAh -> mAh
                        
                        // 地址7: 循环次数
                        int cycleCount = registers[7];
                        
                        // 地址9: 告警标志
                        ushort warningFlag = registers[9];
                        
                        // 地址10: 保护标志
                        ushort protectionFlag = registers[10];
                        
                        // 地址11: 状态/故障标志 (包含MOS状态)
                        ushort statusFlag = registers[11];
                        
                        // 地址12: 均衡状态
                        ushort balanceStatus = registers[12];
                        
                        // 根据选择的方法计算SOC
                        string method = cmbSOCMethod?.SelectedItem?.ToString() ?? "BMS直接读取";
                        
                        switch (method)
                        {
                            case "电压法（查表）":
                                _useVoltageMethod = true;
                                _lastSOC = CalculateSOCFromVoltageTable(_lastTotalVoltage);
                                break;
                            case "电压法（线性）":
                                _useVoltageMethod = true;
                                _lastSOC = CalculateSOCFromVoltageLinear(_lastTotalVoltage);
                                break;
                            case "库伦计数法":
                                _useVoltageMethod = false;
                                _lastSOC = CalculateSOCFromCoulomb(_lastCurrent);
                                break;
                            default: // BMS直接读取
                                _lastSOC = bmsSOC;
                                break;
                        }
                        
                        // 地址15-30: 单体电压 (16个，单位mV)
                        for (int i = 0; i < 16 && i + 15 < registers.Length; i++)
                        {
                            _lastCellVoltages[i] = registers[15 + i] / 1000.0f;  // mV -> V
                            UpdateCellVoltage(i, _lastCellVoltages[i]);
                        }
                        
                        // 地址31-34: 电池温度1-4 (有符号16位，单位0.1°C)
                        for (int i = 0; i < 4 && i + 31 < registers.Length; i++)
                        {
                            _lastTemperatures[i] = (short)registers[31 + i] / 10.0f;
                            UpdateTemperature(i, _lastTemperatures[i]);
                        }
                        
                        // 地址35: MOS温度 (有符号16位，单位0.1°C)
                        if (35 < registers.Length)
                        {
                            _lastTemperatures[4] = (short)registers[35] / 10.0f;
                            UpdateTemperature(4, _lastTemperatures[4]);
                        }
                        
                        // 从状态标志中解析MOS状态 (通常位0=充电MOS, 位1=放电MOS)
                        bool chgMos = (statusFlag & 0x0001) != 0;
                        bool dsgMos = (statusFlag & 0x0002) != 0;
                        UpdateMosStatus(chgMos, dsgMos);
                        
                        // 更新剩余容量
                        _remainingCapacity = remainmAh;
                        
                        UpdateUIWithData();
                    }
                    
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Modbus 读取异常: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }
        
        private void UpdateCellVoltage(int cellIndex, float voltage)
        {
            if (cellIndex >= 0 && cellIndex < cellVoltageLabels.Length && cellVoltageLabels[cellIndex] != null)
            {
                this.Invoke(new Action(() =>
                {
                    cellVoltageLabels[cellIndex].Text = voltage.ToString("F3");
                    
                    if (voltage < 3.0f)
                        cellVoltageLabels[cellIndex].ForeColor = Color.Red;
                    else if (voltage > 4.2f)
                        cellVoltageLabels[cellIndex].ForeColor = Color.Orange;
                    else
                        cellVoltageLabels[cellIndex].ForeColor = Color.Blue;
                }));
            }
        }
        
        private void UpdateTemperature(int index, float temperature)
        {
            if (index >= 0 && index < tempLabels.Length && tempLabels[index] != null)
            {
                this.Invoke(new Action(() =>
                {
                    tempLabels[index].Text = temperature.ToString("F1");
                    if (temperature > 60)
                        tempLabels[index].ForeColor = Color.Red;
                    else if (temperature < 0)
                        tempLabels[index].ForeColor = Color.Orange;
                    else
                        tempLabels[index].ForeColor = Color.Blue;
                }));
            }
        }
        
        private void UpdateMosStatus(bool chgMos, bool dsgMos)
        {
            this.Invoke(new Action(() =>
            {
                if (lblChgMos != null)
                    lblChgMos.Text = chgMos ? "✅ 充电MOS" : "❌ 充电MOS";
                if (lblDsgMos != null)
                    lblDsgMos.Text = dsgMos ? "✅ 放电MOS" : "❌ 放电MOS";
            }));
        }
        
        private void UpdateUIWithData()
        {
            this.Invoke(new Action(() =>
            {
                if (lblTotalVoltage != null)
                    lblTotalVoltage.Text = _lastTotalVoltage.ToString("F2") + " V";
                
                if (lblCurrent != null)
                    lblCurrent.Text = _lastCurrent.ToString("F2") + " A";
                
                if (lblSOC != null)
                    lblSOC.Text = _lastSOC.ToString() + " %";
                
                if (lblSOH != null)
                    lblSOH.Text = _lastSOH.ToString() + " %";
                
                if (lblRemainCapacity != null)
                    lblRemainCapacity.Text = (_remainingCapacity / 1000).ToString("F1") + " Ah";
                
                if (lblFullCapacity != null)
                    lblFullCapacity.Text = (_nominalCapacity / 1000).ToString("F1") + " Ah";
                
                if (lblVoltageMethod != null && cmbSOCMethod != null)
                    lblVoltageMethod.Text = cmbSOCMethod.SelectedItem?.ToString() ?? "";
                
                if (lblCoulombMethod != null && _lastCellVoltages.Length > 0)
                {
                    float minVoltage = _lastCellVoltages.Min();
                    lblCoulombMethod.Text = minVoltage.ToString("F3") + " V";
                }
                
                UpdateSystemStatus(true);
            }));
        }
        
        private void UpdateSystemStatus(bool connected)
        {
            if (!connected) return;
            
            if (lblChgValid != null)
                lblChgValid.Text = _lastCurrent > 0 ? "✅ 充电电流有效" : "● 充电电流有效";
                
            if (lblDsgValid != null)
                lblDsgValid.Text = _lastCurrent < 0 ? "✅ 放电电流有效" : "● 放电电流有效";
                
            if (lblFullChg != null)
                lblFullChg.Text = _lastSOC >= 100 ? "✅ 满充" : "● 满充";
        }
        
        private void DisconnectUSB()
        {
            _keepReading = false;
            
            if (_readThread != null && _readThread.IsAlive)
            {
                _readThread.Join(1000);
            }
            
            if (_modbusMaster != null)
            {
                _modbusMaster.Dispose();
                _modbusMaster = null;
            }
            
            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }
            
            _isConnected = false;
            
            UpdateComStatus(false, "");
            UpdateButtonsState(false);
        }
        
        private void UpdateButtonsState(bool connected)
        {
            this.Invoke(new Action(() =>
            {
                Control? btnConnect = this.Controls.Find("btnConnect", true).FirstOrDefault();
                Control? btnDisconnect = this.Controls.Find("btnDisconnect", true).FirstOrDefault();
                
                if (btnConnect is Button btnConn) btnConn.Enabled = !connected;
                if (btnDisconnect is Button btnDis) btnDis.Enabled = connected;
            }));
        }
        
        private void UpdateComStatus(bool connected, string deviceInfo)
        {
            this.Invoke(new Action(() =>
            {
                Control? statusLabel = this.Controls.Find("lblComStatus", true).FirstOrDefault();
                if (statusLabel is Label lbl)
                {
                    if (connected)
                    {
                        lbl.Text = $"● 已连接 ({deviceInfo})";
                        lbl.ForeColor = Color.LightGreen;
                    }
                    else
                    {
                        lbl.Text = "● 未连接";
                        lbl.ForeColor = Color.Red;
                    }
                }
            }));
        }
        
        // ==================== 按钮事件 ====================
        
        private void BtnBalance_Click(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("请先连接设备！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            try
            {
                if (_modbusMaster != null)
                {
                    _modbusMaster.WriteSingleCoil(_slaveId, 10, true);
                    MessageBox.Show("均衡功能已触发", "均衡", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"均衡触发失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void BtnActiveBalance_Click(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("请先连接设备！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            try
            {
                if (_modbusMaster != null)
                {
                    _modbusMaster.WriteSingleCoil(_slaveId, 11, true);
                    MessageBox.Show("主动均衡功能已触发", "主动均衡", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"主动均衡触发失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void CmbSOCMethod_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (cmbSOCMethod != null)
            {
                string method = cmbSOCMethod.SelectedItem?.ToString() ?? "";
                
                if (method == "库伦计数法")
                {
                    _remainingCapacity = _nominalCapacity * _lastSOC / 100;
                    _lastCapacityUpdate = DateTime.Now;
                }
                
                MessageBox.Show($"SOC计算方法已切换为: {method}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        
        private void BtnCalibrateSOC_Click(object sender, EventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("请先连接设备！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            using (var dialog = new CalibrateSOCDialog(_lastSOC))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    int newSOC = dialog.SOCValue;
                    CalibrateSOC(newSOC);
                    
                    try
                    {
                        if (_modbusMaster != null)
                        {
                            _modbusMaster.WriteSingleRegister(_slaveId, 2, (ushort)newSOC);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"写入SOC到BMS失败: {ex.Message}");
                    }
                }
            }
        }
        
        private void ReadAllParameters()
        {
            if (!_isConnected)
            {
                MessageBox.Show("请先连接设备！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            try
            {
                if (_modbusMaster != null)
                {
                    ushort[] voltageParams = _modbusMaster.ReadHoldingRegisters(_slaveId, 100, 4);
                    if (voltageParams.Length >= 4)
                    {
                        _batteryMinVoltage = voltageParams[0] / 10.0f;
                        _batteryMaxVoltage = voltageParams[1] / 10.0f;
                        _cellMinVoltage = voltageParams[2] / 1000.0f;
                        _cellMaxVoltage = voltageParams[3] / 1000.0f;
                        
                        if (nudMinVoltage != null) nudMinVoltage.Value = (decimal)_batteryMinVoltage;
                        if (nudMaxVoltage != null) nudMaxVoltage.Value = (decimal)_batteryMaxVoltage;
                    }
                    
                    ushort[] capacityParams = _modbusMaster.ReadHoldingRegisters(_slaveId, 104, 1);
                    if (capacityParams.Length >= 1)
                    {
                        _nominalCapacity = capacityParams[0];
                        if (nudNominalCapacity != null) nudNominalCapacity.Value = (decimal)_nominalCapacity;
                    }
                }
                
                MessageBox.Show("参数读取成功！", "读取参数", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取参数失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void WriteAllParameters()
        {
            if (!_isConnected)
            {
                MessageBox.Show("请先连接设备！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            try
            {
                if (_modbusMaster != null)
                {
                    if (nudMinVoltage != null) _batteryMinVoltage = (float)nudMinVoltage.Value;
                    if (nudMaxVoltage != null) _batteryMaxVoltage = (float)nudMaxVoltage.Value;
                    if (nudNominalCapacity != null) _nominalCapacity = (float)nudNominalCapacity.Value;
                    
                    _modbusMaster.WriteSingleRegister(_slaveId, 100, (ushort)(_batteryMinVoltage * 10));
                    _modbusMaster.WriteSingleRegister(_slaveId, 101, (ushort)(_batteryMaxVoltage * 10));
                    _modbusMaster.WriteSingleRegister(_slaveId, 104, (ushort)_nominalCapacity);
                }
                
                MessageBox.Show("参数写入成功！", "写入参数", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"写入参数失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        private void RestoreAllDefaults()
        {
            if (MessageBox.Show("恢复所有参数为默认值？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (nudChargeAlarm != null) nudChargeAlarm.Value = 50;
                if (nudChargeProtect != null) nudChargeProtect.Value = 100;
                if (nudChargeDelay != null) nudChargeDelay.Value = 1000;
                if (nudCutVoltage != null) nudCutVoltage.Value = 42;
                if (nudCutCurrent != null) nudCutCurrent.Value = 50;
                if (nudLowBatteryAlarm != null) nudLowBatteryAlarm.Value = 20;
                if (nudDisAlarm != null) nudDisAlarm.Value = 80;
                if (nudDisProtect1 != null) nudDisProtect1.Value = 150;
                if (nudDisDelay1 != null) nudDisDelay1.Value = 500;
                if (nudDisProtect2 != null) nudDisProtect2.Value = 200;
                if (nudDisDelay2 != null) nudDisDelay2.Value = 100;
                if (nudShortDelay != null) nudShortDelay.Value = 50;
                if (nudBalanceVoltage != null) nudBalanceVoltage.Value = 3.40m;
                if (nudBalanceDiff != null) nudBalanceDiff.Value = 30;
                if (nudSleepVoltage != null) nudSleepVoltage.Value = 3.00m;
                if (nudSleepDelay != null) nudSleepDelay.Value = 30;
                if (nudMinVoltage != null) nudMinVoltage.Value = 40m;
                if (nudMaxVoltage != null) nudMaxVoltage.Value = 67.2m;
                if (nudNominalCapacity != null) nudNominalCapacity.Value = 500;
                if (cmbSOCMethod != null) cmbSOCMethod.SelectedIndex = 3;
                if (chkChargeEnable != null) chkChargeEnable.Checked = true;
                if (chkDischargeEnable != null) chkDischargeEnable.Checked = true;
                
                MessageBox.Show("所有参数已恢复默认值", "恢复默认", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        
        private void ClearAllParameters()
        {
            if (MessageBox.Show("清空所有参数？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                if (nudChargeAlarm != null) nudChargeAlarm.Value = 0;
                if (nudChargeProtect != null) nudChargeProtect.Value = 0;
                if (nudChargeDelay != null) nudChargeDelay.Value = 0;
                if (nudCutVoltage != null) nudCutVoltage.Value = 0;
                if (nudCutCurrent != null) nudCutCurrent.Value = 0;
                if (nudLowBatteryAlarm != null) nudLowBatteryAlarm.Value = 0;
                if (nudDisAlarm != null) nudDisAlarm.Value = 0;
                if (nudDisProtect1 != null) nudDisProtect1.Value = 0;
                if (nudDisDelay1 != null) nudDisDelay1.Value = 0;
                if (nudDisProtect2 != null) nudDisProtect2.Value = 0;
                if (nudDisDelay2 != null) nudDisDelay2.Value = 0;
                if (nudShortDelay != null) nudShortDelay.Value = 0;
                if (nudBalanceVoltage != null) nudBalanceVoltage.Value = 0;
                if (nudBalanceDiff != null) nudBalanceDiff.Value = 0;
                if (nudSleepVoltage != null) nudSleepVoltage.Value = 0;
                if (nudSleepDelay != null) nudSleepDelay.Value = 0;
                if (nudMinVoltage != null) nudMinVoltage.Value = 0;
                if (nudMaxVoltage != null) nudMaxVoltage.Value = 0;
                if (nudNominalCapacity != null) nudNominalCapacity.Value = 0;
                
                MessageBox.Show("所有参数已清空", "清空参数", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        
        private void ExportAllParameters()
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "JSON文件|*.json|CSV文件|*.csv",
                Title = "导出参数",
                FileName = $"BMSParams_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                MessageBox.Show($"参数已导出到: {sfd.FileName}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        
        private void ImportAllParameters()
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "JSON文件|*.json|CSV文件|*.csv",
                Title = "导入参数"
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                MessageBox.Show($"参数已从 {ofd.FileName} 导入", "导入成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        
        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
                _statusTimer.Dispose();
            }
            DisconnectUSB();
        }

        // ==================== 高级设置界面 ====================
        
        private TabPage CreateAdvancedSettingsTabPage()
        {
            TabPage page = new TabPage("高级设置");
            page.AutoScroll = true;
            
            TableLayoutPanel mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(10)
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            mainLayout.Controls.Add(CreateMCUInfoGroup(), 0, 0);
            mainLayout.Controls.Add(CreateProductionInfoGroup(), 1, 0);
            mainLayout.Controls.Add(CreateAdvancedButtonPanel(), 0, 1);
            mainLayout.SetColumnSpan(CreateAdvancedButtonPanel(), 2);

            page.Controls.Add(mainLayout);
            return page;
        }

        private GroupBox CreateMCUInfoGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "MCU信息",
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                BackColor = Color.White,
                Margin = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            layout.Controls.Add(new Label { Text = "固件版本:", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 0);
            Label lblFirmware = new Label { Text = "PACE V2.0", AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
            layout.Controls.Add(lblFirmware, 1, 0);

            layout.Controls.Add(new Label { Text = "硬件版本:", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            Label lblHardware = new Label { Text = "PACE V1.0", AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
            layout.Controls.Add(lblHardware, 1, 1);

            layout.Controls.Add(new Label { Text = "电池串数:", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 2);
            Label lblCellCount = new Label { Text = "16", AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
            layout.Controls.Add(lblCellCount, 1, 2);

            layout.Controls.Add(new Label { Text = "温度传感器数:", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 3);
            Label lblTempCount = new Label { Text = "5", AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
            layout.Controls.Add(lblTempCount, 1, 3);

            group.Controls.Add(layout);
            return group;
        }

        private GroupBox CreateProductionInfoGroup()
        {
            GroupBox group = new GroupBox
            {
                Text = "生产信息",
                Dock = DockStyle.Fill,
                Font = new Font("微软雅黑", 10, FontStyle.Bold),
                BackColor = Color.White,
                Margin = new Padding(5)
            };

            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

            layout.Controls.Add(new Label { Text = "生产日期:", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 0);
            Label lblProdDate = new Label { Text = DateTime.Now.ToString("yyyy-MM-dd"), AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
            layout.Controls.Add(lblProdDate, 1, 0);

            layout.Controls.Add(new Label { Text = "序列号:", AutoSize = true, Font = new Font("微软雅黑", 9) }, 0, 1);
            Label lblSN = new Label { Text = "PACE-BMS-001", AutoSize = true, Font = new Font("微软雅黑", 9, FontStyle.Bold), ForeColor = Color.Blue };
            layout.Controls.Add(lblSN, 1, 1);

            group.Controls.Add(layout);
            return group;
        }

        private Panel CreateAdvancedButtonPanel()
        {
            Panel panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White
            };

            FlowLayoutPanel flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10)
            };

            Button btnRefresh = new Button 
            { 
                Text = "刷新信息", 
                Size = new Size(120, 40), 
                BackColor = Color.FromArgb(52, 152, 219), 
                ForeColor = Color.White, 
                FlatStyle = FlatStyle.Flat 
            };
            
            btnRefresh.Click += (s, e) => {
                ReadAllParameters();
                MessageBox.Show("信息已刷新", "刷新", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            btnRefresh.FlatAppearance.BorderSize = 0;
            flow.Controls.Add(btnRefresh);
            
            panel.Controls.Add(flow);
            return panel;
        }
    }

    // ==================== USB/串口设备设置对话框 ====================
    public class USBDeviceDialog : Form
    {
        private ComboBox cmbPorts = null!;
        private ComboBox cmbBaudRate = null!;
        private NumericUpDown nudSlaveId = null!;
        private Button btnOK = null!;
        private Button btnCancel = null!;
        
        public string SelectedPort => cmbPorts.SelectedItem?.ToString() ?? "COM1";
        public int SelectedBaudRate => int.Parse(cmbBaudRate.SelectedItem?.ToString() ?? "9600");
        public byte SelectedSlaveId => (byte)nudSlaveId.Value;
        
        public USBDeviceDialog()
        {
            InitializeComponents();
            RefreshPorts();
        }
        
        private void RefreshPorts()
        {
            cmbPorts.Items.Clear();
            string[] ports = SerialPort.GetPortNames();
            if (ports.Length > 0)
            {
                cmbPorts.Items.AddRange(ports);
                cmbPorts.SelectedIndex = 0;
            }
            else
            {
                cmbPorts.Items.Add("未检测到串口");
                cmbPorts.SelectedIndex = 0;
                cmbPorts.Enabled = false;
            }
        }
        
        private void InitializeComponents()
        {
            this.Text = "PACE BMS 串口设置";
            this.Size = new Size(380, 220);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            
            Label lblPort = new Label { Text = "串口号:", Location = new Point(20, 25), AutoSize = true };
            cmbPorts = new ComboBox
            {
                Location = new Point(100, 22),
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            
            Label lblBaud = new Label { Text = "波特率:", Location = new Point(20, 55), AutoSize = true };
            cmbBaudRate = new ComboBox
            {
                Location = new Point(100, 52),
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbBaudRate.Items.AddRange(new object[] { "2400", "4800", "9600", "19200", "38400", "57600", "115200" });
            cmbBaudRate.SelectedItem = "9600";
            
            Label lblSlaveId = new Label { Text = "从站地址:", Location = new Point(20, 85), AutoSize = true };
            nudSlaveId = new NumericUpDown
            {
                Location = new Point(100, 82),
                Width = 60,
                Minimum = 1,
                Maximum = 247,
                Value = 1
            };
            
            btnOK = new Button { Text = "确定", Location = new Point(170, 130), Size = new Size(70, 25), DialogResult = DialogResult.OK };
            btnCancel = new Button { Text = "取消", Location = new Point(250, 130), Size = new Size(70, 25), DialogResult = DialogResult.Cancel };
            
            Button btnRefresh = new Button
            {
                Text = "刷新",
                Location = new Point(290, 22),
                Size = new Size(60, 23)
            };
            btnRefresh.Click += (s, e) => RefreshPorts();
            
            this.Controls.AddRange(new Control[] { lblPort, cmbPorts, lblBaud, cmbBaudRate, lblSlaveId, nudSlaveId, btnOK, btnCancel, btnRefresh });
        }
    }

    // ==================== SOC校准对话框 ====================
    public class CalibrateSOCDialog : Form
    {
        private NumericUpDown nudSOC = null!;
        private Button btnOK = null!;
        private Button btnCancel = null!;
        
        public int SOCValue => (int)nudSOC.Value;
        
        public CalibrateSOCDialog(int currentSOC)
        {
            InitializeComponents(currentSOC);
        }
        
        private void InitializeComponents(int currentSOC)
        {
            this.Text = "SOC校准";
            this.Size = new Size(300, 150);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            
            Label lblInfo = new Label 
            { 
                Text = $"当前SOC: {currentSOC}%\n请输入正确的SOC值:", 
                Location = new Point(20, 20), 
                AutoSize = true,
                Font = new Font("微软雅黑", 9)
            };
            
            nudSOC = new NumericUpDown
            {
                Location = new Point(20, 65),
                Width = 100,
                Minimum = 0,
                Maximum = 100,
                Value = currentSOC,
                Font = new Font("微软雅黑", 10)
            };
            
            btnOK = new Button 
            { 
                Text = "确定", 
                Location = new Point(130, 65), 
                Size = new Size(70, 30), 
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(46, 204, 113),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            
            btnCancel = new Button 
            { 
                Text = "取消", 
                Location = new Point(210, 65), 
                Size = new Size(70, 30), 
                DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(231, 76, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            
            this.Controls.AddRange(new Control[] { lblInfo, nudSOC, btnOK, btnCancel });
        }
    }
}