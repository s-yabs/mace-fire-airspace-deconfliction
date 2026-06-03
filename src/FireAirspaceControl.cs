using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MaceFireAirspace;

internal sealed class FireAirspaceControl : UserControl
{
    private readonly DataGridView _missionsGrid = new();
    private readonly DataGridView _activeGrid = new();
    private readonly DataGridView _conflictsGrid = new();
    private readonly Button _refreshButton = new();
    private readonly NumericUpDown _horizontalNm = new();
    private readonly NumericUpDown _verticalFt = new();
    private readonly NumericUpDown _preFireSeconds = new();
    private readonly Button _plannedAimedColor = new();
    private readonly Button _firingColor = new();
    private readonly Button _coldColor = new();

    public event EventHandler? RefreshRequested;
    public event EventHandler<SeparationSettings>? SettingsChanged;

    public FireAirspaceControl()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildTab("Call For Fire", _missionsGrid));
        tabs.TabPages.Add(BuildTab("Active Airspace", _activeGrid));
        tabs.TabPages.Add(BuildTab("Conflicts", _conflictsGrid));
        tabs.TabPages.Add(BuildSettingsTab());

        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _horizontalNm.DecimalPlaces = 2;
        _horizontalNm.Minimum = 0.1M;
        _horizontalNm.Maximum = 25;
        _horizontalNm.Increment = 0.1M;
        _horizontalNm.Width = 70;
        _horizontalNm.ValueChanged += (_, _) => RaiseSettingsChanged();

        _verticalFt.Minimum = 100;
        _verticalFt.Maximum = 10000;
        _verticalFt.Increment = 100;
        _verticalFt.Width = 80;
        _verticalFt.Value = 1000;
        _verticalFt.ValueChanged += (_, _) => RaiseSettingsChanged();

        _preFireSeconds.Minimum = 0;
        _preFireSeconds.Maximum = 3600;
        _preFireSeconds.Increment = 5;
        _preFireSeconds.Width = 80;
        _preFireSeconds.Value = 60;
        _preFireSeconds.ValueChanged += (_, _) => RaiseSettingsChanged();

        ConfigureColorButton(_plannedAimedColor, Color.Yellow);
        ConfigureColorButton(_firingColor, Color.Red);
        ConfigureColorButton(_coldColor, Color.Black);

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        header.Controls.Add(_refreshButton);

        Controls.Add(tabs);
        Controls.Add(header);

        ConfigureGrid(_missionsGrid);
        ConfigureGrid(_activeGrid);
        ConfigureGrid(_conflictsGrid);
    }

    public void SetData(
        IReadOnlyCollection<CallForFireMissionSnapshot> missions,
        IReadOnlyCollection<AirspaceVolume> activeVolumes,
        IReadOnlyCollection<ConflictRecord> conflictHistory,
        SeparationSettings settings)
    {
        SetSettingValues(settings);

        _missionsGrid.DataSource = missions
            .OrderBy(m => m.DisplayIndex)
            .Select(m => new
            {
                CFF = m.CffFormName,
                Mission = m.MissionName,
                m.TargetNumber,
                m.BatteryName,
                m.Round,
                m.NumberOfRounds,
                m.TargetLocationText,
                GunTargetLine = $"{m.GunTargetLine_deg:0.0}",
                MaxOrdMSL_m = $"{m.MaxOrdinateMSL_m:0}",
                MaxOrdMSL_ft = $"{m.MaxOrdinateMSL_m * 3.280839895:0}",
                TOF_s = $"{m.TimeOfFlight_s:0.0}",
                m.Status
            })
            .ToList();

        _activeGrid.DataSource = activeVolumes
            .Where(v => v.IsExecuted)
            .OrderBy(v => v.DisplayName)
            .Select(v => new
            {
                CFF = v.CffFormName,
                Mission = v.MissionName,
                Battery = v.FiringEntityName,
                Start = FormatTime(v.StartTime),
                NotBefore = FormatTime(v.NotBefore),
                MaxOrdMSL_m = $"{v.UpperAltitudeMsl_m:0}",
                MaxOrdMSL_ft = $"{v.UpperAltitudeMsl_m * 3.280839895:0}",
                GTLBuffer_ft = $"{v.LateralBuffer_m * 3.280839895:0}",
                Conflicts = v.Conflicts.Count
            })
            .ToList();

        _conflictsGrid.DataSource = conflictHistory
            .OrderByDescending(c => c.LastSeen)
            .Select(c => new
            {
                c.Airspace,
                c.Aircraft,
                FirstSeen = FormatTime(c.FirstSeen),
                LastSeen = FormatTime(c.LastSeen),
                c.Severity
            })
            .ToList();
    }

    private void RaiseSettingsChanged()
    {
        SettingsChanged?.Invoke(this, new SeparationSettings
        {
            Horizontal_nm = (double)_horizontalNm.Value,
            Vertical_ft = (double)_verticalFt.Value,
            PreFireActivationSeconds = (double)_preFireSeconds.Value,
            PlannedAimedColor = _plannedAimedColor.BackColor,
            FiringColor = _firingColor.BackColor,
            ColdColor = _coldColor.BackColor
        });
    }

    private void SetSettingValues(SeparationSettings settings)
    {
        if (_horizontalNm.Value != (decimal)settings.Horizontal_nm)
        {
            _horizontalNm.Value = (decimal)settings.Horizontal_nm;
        }

        if (_verticalFt.Value != (decimal)settings.Vertical_ft)
        {
            _verticalFt.Value = (decimal)settings.Vertical_ft;
        }

        if (_preFireSeconds.Value != (decimal)settings.PreFireActivationSeconds)
        {
            _preFireSeconds.Value = (decimal)settings.PreFireActivationSeconds;
        }

        SetColorButton(_plannedAimedColor, settings.PlannedAimedColor);
        SetColorButton(_firingColor, settings.FiringColor);
        SetColorButton(_coldColor, settings.ColdColor);
    }

    private static string FormatTime(DateTime value)
    {
        return value.ToString("HH:mm:ss");
    }

    private static TabPage BuildTab(string text, Control body)
    {
        body.Dock = DockStyle.Fill;
        return new TabPage(text) { Controls = { body } };
    }

    private TabPage BuildSettingsTab()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 6
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

        AddSettingRow(panel, 0, "Horizontal NM", _horizontalNm);
        AddSettingRow(panel, 1, "Vertical ft", _verticalFt);
        AddSettingRow(panel, 2, "Pre-fire sec", _preFireSeconds);
        AddSettingRow(panel, 3, "Planned/Aimed GTL", _plannedAimedColor);
        AddSettingRow(panel, 4, "Firing GTL", _firingColor);
        AddSettingRow(panel, 5, "Cold GTL", _coldColor);

        return new TabPage("Settings") { Controls = { panel } };
    }

    private static void AddSettingRow(TableLayoutPanel panel, int row, string labelText, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 7, 0, 0)
        }, 0, row);
        control.Anchor = AnchorStyles.Left;
        panel.Controls.Add(control, 1, row);
    }

    private void ConfigureColorButton(Button button, Color color)
    {
        button.Width = 70;
        button.Height = 24;
        button.FlatStyle = FlatStyle.Flat;
        SetColorButton(button, color);
        button.Click += (_, _) =>
        {
            using var dialog = new ColorDialog
            {
                Color = button.BackColor,
                FullOpen = true
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                SetColorButton(button, dialog.Color);
                RaiseSettingsChanged();
            }
        };
    }

    private static void SetColorButton(Button button, Color color)
    {
        button.BackColor = color;
        button.ForeColor = color.GetBrightness() < 0.45 ? Color.White : Color.Black;
        button.Text = $"{color.R},{color.G},{color.B}";
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
    }
}
