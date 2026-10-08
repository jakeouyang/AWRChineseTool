using System.ComponentModel;

namespace AWR.Tools;

public sealed class TextEditorForm : Form
{
    static readonly Color Accent = Color.FromArgb(235, 32, 39);
    static readonly Color Dark = Color.FromArgb(18, 18, 18);
    static readonly Color Darker = Color.FromArgb(10, 10, 10);
    sealed class Row
    {
        public string Id { get; set; } = "";
        public string Original { get; set; } = "";
        public string Value { get; set; } = "";
        public string State => Value == Original ? "基底" : "已修改";
    }
    readonly List<StringEntry> baseline;
    readonly List<Row> rows;
    readonly bool simplified;
    readonly TextBox search = new() { Width = 270, PlaceholderText = "搜索 ID、基底文本、修改文本", BackColor = Dark, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    readonly CheckBox modified = new() { Text = "只看已修改", AutoSize = true, ForeColor = Color.Silver };
    readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = Darker, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(28, 28, 28), ForeColor = Color.Silver },
        DefaultCellStyle = { BackColor = Dark, ForeColor = Color.Silver, SelectionBackColor = Color.FromArgb(60, 20, 20), SelectionForeColor = Color.White },
        GridColor = Color.FromArgb(40, 40, 40),
    };
    readonly TextBox original = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Dark, ForeColor = Color.Gray, BorderStyle = BorderStyle.FixedSingle };
    readonly TextBox edit = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, BackColor = Dark, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    readonly Label summary = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray };
    Row? selected;
    bool loading, dirty;

    public TextEditorForm(List<StringEntry> baseline, bool simplified)
    {
        this.baseline = baseline; this.simplified = simplified;
        Text = simplified ? "简体文本工作台 · 保存后安装生效" : "繁体文本工作台 · 保存后安装生效";
        BackColor = Color.Black; ForeColor = Accent;
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(1180, 760); MinimumSize = new Size(900, 600); StartPosition = FormStartPosition.CenterParent;
        rows = baseline.Select(e => new Row { Id = e.Id, Original = e.Value, Value = e.Value }).ToList();
        string saved = FontService.OverridesFor(simplified);
        if (File.Exists(saved)) Merge(StringTable.ImportCsv(saved));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(layout);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Black };
        toolbar.Controls.Add(search); toolbar.Controls.Add(modified);
        toolbar.Controls.Add(ActionButton("导入 TSV", Import));
        toolbar.Controls.Add(ActionButton("导出全部", () => Export(false)));
        toolbar.Controls.Add(ActionButton("导出修改", () => Export(true)));
        toolbar.Controls.Add(ActionButton("恢复此条", () => { if (selected != null) edit.Text = selected.Original; }));
        layout.Controls.Add(toolbar, 0, 0);
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "State", HeaderText = "状态", FillWeight = 12 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Id", HeaderText = "ID（保持不变）", FillWeight = 40 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Original", HeaderText = "基底文本", FillWeight = 65 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = "Value", HeaderText = "当前文本", FillWeight = 65 });
        layout.Controls.Add(grid, 0, 1);
        var hint = new Label { Text = "选择一条，在右下方编辑。请保留原文中的按键标记、占位符及格式标签。", Dock = DockStyle.Fill, ForeColor = Color.Gray };
        layout.Controls.Add(hint, 0, 2);
        var editors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editors.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); editors.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editors.Controls.Add(new Label { Text = "基底（只读）", AutoSize = true, ForeColor = Color.Gray }, 0, 0);
        editors.Controls.Add(new Label { Text = "修改文本（可编辑）", AutoSize = true, ForeColor = Color.Gray }, 1, 0);
        editors.Controls.Add(original, 0, 1); editors.Controls.Add(edit, 1, 1); layout.Controls.Add(editors, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Black };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        footer.Controls.Add(summary, 0, 0); footer.Controls.Add(ActionButton("校验并保存修改", Save), 1, 0); layout.Controls.Add(footer, 0, 4);
        grid.SelectionChanged += (_, _) => SelectRow();
        search.TextChanged += (_, _) => Filter(); modified.CheckedChanged += (_, _) => Filter();
        edit.TextChanged += (_, _) => { if (!loading && selected != null) { selected.Value = edit.Text; dirty = true; grid.InvalidateRow(grid.CurrentRow?.Index ?? -1); UpdateSummary(); } };
        FormClosing += (_, e) =>
        {
            if (!dirty) return;
            var answer = MessageBox.Show(this, "有未保存的修改。保存后关闭？", "文本工作台", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveCore())) e.Cancel = true;
        };
        Filter(); dirty = false;
    }
    static Button ActionButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, Height = 32, Margin = new Padding(5, 0, 5, 0),
            ForeColor = Accent, BackColor = Color.Black, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(32, 12, 12);
        button.Click += (_, _) => action(); return button;
    }
    void Filter()
    {
        string query = search.Text.Trim();
        grid.DataSource = new BindingList<Row>(rows.Where(r => (!modified.Checked || r.Value != r.Original) &&
            (query.Length == 0 || r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Original.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Value.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList());
        SelectRow(); UpdateSummary();
    }
    void SelectRow()
    {
        loading = true; selected = grid.CurrentRow?.DataBoundItem as Row;
        original.Text = selected?.Original ?? ""; edit.Text = selected?.Value ?? ""; edit.Enabled = selected != null; loading = false;
    }
    void UpdateSummary() => summary.Text = $"共 {rows.Count} 条 · 显示 {grid.Rows.Count} 条 · 修改 {rows.Count(r => r.Value != r.Original)} 条" + (dirty ? " · 尚未保存" : " · 已保存，安装后生效");
    List<StringEntry> Entries(bool onlyChanges) => rows.Where(r => !onlyChanges || r.Value != r.Original).Select(r => new StringEntry { Id = r.Id, Value = r.Value }).ToList();
    void Merge(List<StringEntry> entries)
    {
        FontService.ValidateText(entries, baseline);
        var map = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var e in entries) map[e.Id].Value = e.Value;
    }
    void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "制表符文本 (*.tsv;*.csv)|*.tsv;*.csv", Title = "导入文本（合并到当前编辑，保存后生效）" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { var entries = StringTable.ImportCsv(dialog.FileName); Merge(entries); dirty = true; Filter(); MessageBox.Show(this, $"已合并 {entries.Count} 条。请检查修改后保存。", "导入完成"); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导入失败：未应用此文件", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    void Export(bool onlyChanges)
    {
        using var dialog = new SaveFileDialog { Filter = "制表符文本 (*.tsv)|*.tsv", FileName = onlyChanges ? "changes.tsv" : "strings.tsv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { StringTable.ExportCsv(Entries(onlyChanges), dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败"); }
    }
    void Save() => SaveCore();
    bool SaveCore()
    {
        try { FontService.SaveText(Entries(true), baseline, simplified); dirty = false; UpdateSummary(); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }
}
