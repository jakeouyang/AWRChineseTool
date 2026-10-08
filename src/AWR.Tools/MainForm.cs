using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AWR.Tools;

public sealed class MainForm : Form
{
    static readonly Color Accent = Color.FromArgb(235, 32, 39);
    static readonly Color Dark = Color.FromArgb(18, 18, 18);
    readonly TextBox game = Input(), font = Input();
    readonly RichTextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Dark, ForeColor = Color.Silver, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 10), DetectUrls = true };
    readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(40, 200, 90), Anchor = AnchorStyles.Left };
    readonly CheckBox simplified = Check(), replaceFont = Check();
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 185 };
    Button editText = null!;
    readonly List<Control> busyControls = new();
    Label subtitle = null!, gameLabel = null!, fontLabel = null!, optionsLabel = null!, guidance = null!;
    Button install = null!, restore = null!, language = null!, exportText = null!, importText = null!;
    bool busy;
    bool interactionStarted;
    bool Traditional; // UI language: false = 简体 (default), true = 繁體

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

    public MainForm()
    {
        Text = "AWR Chinese Tool"; BackColor = Color.Black; ForeColor = Accent;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 10.5f);
        ClientSize = new Size(1160, 780); MinimumSize = new Size(1160, 720); StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        try { Icon = new Icon(typeof(Program).Assembly.GetManifestResourceStream("AWR.Tools.Resources.app.ico")); } catch { }
        log.LinkClicked += (_, e) => { try { Process.Start(new ProcessStartInfo(e.LinkText) { UseShellExecute = true }); } catch { } };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 12, 28, 12), ColumnCount = 1, RowCount = 10 };
        foreach (int h in new[] { 52, 38, 48, 48, 46, 58, 48, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, h));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(layout);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1 };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        var iconBox = new PictureBox { Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.Left, Margin = Padding.Empty, BackColor = Color.Black };
        try
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("AWR.Tools.Resources.icon48.png");
            if (stream != null) iconBox.Image = Image.FromStream(stream);
        }
        catch { }
        iconBox.MouseDown += DragWindow;
        header.Controls.Add(iconBox, 0, 0);
        var title = new Label { Text = "Alan Wake Remastered 中文化工具", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 16, FontStyle.Bold), Margin = Padding.Empty };
        title.MouseDown += DragWindow;
        header.Controls.Add(title, 1, 0);
        language = Button("", () => SelectInterfaceLanguage(!Traditional));
        language.Dock = DockStyle.Fill; language.Margin = Padding.Empty;
        header.Controls.Add(language, 2, 0);
        var github = new Button { Text = "GitHub", Dock = DockStyle.Fill, ForeColor = Accent, BackColor = Color.Black, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(8, 0, 0, 0), UseVisualStyleBackColor = false, MinimumSize = Size.Empty };
        github.FlatAppearance.BorderSize = 0; github.FlatAppearance.MouseOverBackColor = Color.FromArgb(32, 12, 12);
        github.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://github.com/jakeouyang/AWRChineseTool") { UseShellExecute = true }); } catch { } };
        header.Controls.Add(github, 3, 0);
        header.Controls.Add(Button("—", () => WindowState = FormWindowState.Minimized), 4, 0);
        header.Controls.Add(Button("×", Close), 5, 0); layout.Controls.Add(header, 0, 0);

        subtitle = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Silver };
        layout.Controls.Add(subtitle, 0, 1);

        gameLabel = new Label(); fontLabel = new Label();
        layout.Controls.Add(PathRow(gameLabel, game, true), 0, 2);
        layout.Controls.Add(PathRow(fontLabel, font, false), 0, 3);

        var optionsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionsRow.Controls.Add(optionsLabel = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, 0);
        simplified.Checked = true; replaceFont.Checked = true;
        optionsRow.Controls.Add(simplified, 1, 0);
        optionsRow.Controls.Add(replaceFont, 2, 0);
        layout.Controls.Add(optionsRow, 0, 4);
        busyControls.AddRange(new Control[] { simplified, replaceFont, language });

        guidance = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 9) };
        layout.Controls.Add(guidance, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = Padding.Empty };
        install = Button("", () => Run(true)); restore = Button("", () => Run(false)); exportText = Button("", ExportText); importText = Button("", ImportText);
        install.Width = 200; restore.Width = 160; exportText.Width = 150;
        install.Height = restore.Height = exportText.Height = importText.Height = 40; importText.Width = 150;
        actions.Controls.Add(install); actions.Controls.Add(restore); actions.Controls.Add(exportText); actions.Controls.Add(importText);
        editText = Button("文本工作台", EditText); editText.Width = 160; editText.Height = 40; actions.Controls.Add(editText);
        layout.Controls.Add(actions, 0, 6);
        busyControls.AddRange(new Control[] { install, restore, exportText, importText, editText, quality });
        var qualityRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        qualityRow.Controls.Add(new Label { Text = "字体清晰度", AutoSize = true, Margin = new Padding(0, 6, 14, 0), ForeColor = Color.Silver });
        quality.Items.AddRange(new object[] { "高清（更多显存）", "标准（节省显存）" }); quality.SelectedIndex = 0;
        qualityRow.Controls.Add(quality);
        qualityRow.Controls.Add(new Label { Text = "高清增加资源占用，字号和游戏分辨率不变。", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(14, 6, 0, 0) });
        layout.Controls.Add(qualityRow, 0, 7);

        var panel = new Panel { Dock = DockStyle.Fill, BackColor = log.BackColor, Padding = new Padding(12), Margin = new Padding(0, 6, 0, 6) };
        panel.Controls.Add(log); layout.Controls.Add(panel, 0, 8);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        footer.Controls.Add(status); footer.Controls.Add(new Label { Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev"), Dock = DockStyle.Fill, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 9) }); layout.Controls.Add(footer, 0, 9);

        busyControls.AddRange(new Control[] { game, font });
        LoadSettings();
        ApplyLanguage();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = T("正在处理…", "正在處理…"); } };
        FormClosed += (_, _) => SaveSettings();
    }

    sealed record Settings(string Game, string Font, bool Simplified, int Quality);
    string SettingsPath => Path.Combine(FontService.WorkspaceDir, "settings.json");
    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (saved == null) return;
            game.Text = saved.Game; font.Text = saved.Font; simplified.Checked = saved.Simplified;
            quality.SelectedIndex = Math.Clamp(saved.Quality, 0, 1);
        }
        catch (Exception ex) { Append("无法读取上次设置：" + ex.Message); }
    }
    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(FontService.WorkspaceDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(game.Text, font.Text, simplified.Checked, quality.SelectedIndex)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); }
    }
    static CheckBox Check() => new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.Silver, Margin = new Padding(4, 2, 2, 2) };
    static TextBox Input() => new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 18), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 6, 10, 0) };
    Button Button(string text, Action action)
    {
        var button = new Button { Text = text, Dock = DockStyle.None, ForeColor = Accent, BackColor = Color.Black, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Height = 38, Width = 86, Margin = new Padding(0, 2, 12, 2), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderSize = 0; button.FlatAppearance.MouseOverBackColor = Color.FromArgb(32, 12, 12); button.Click += (_, _) => action(); return button;
    }
    Control PathRow(Label label, TextBox box, bool directory)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        label.Dock = DockStyle.Fill; label.TextAlign = ContentAlignment.MiddleLeft; row.Controls.Add(label, 0, 0); row.Controls.Add(box, 1, 0);
        var browse = Button("", () =>
        {
            if (directory)
            {
                interactionStarted = true;
                using var dialog = new FolderBrowserDialog { Description = T("选择《Alan Wake Remastered》游戏根目录", "選擇《Alan Wake Remastered》遊戲根目錄"), UseDescriptionForTitle = true };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    box.Text = dialog.SelectedPath;
                    var error = FontService.ValidateGame(box.Text);
                    Append(error.Length == 0 ? T("游戏目录校验通过。", "遊戲目錄校驗通過。") : error);
                }
            }
            else
            {
                interactionStarted = true;
                using var dialog = new OpenFileDialog { Filter = "Font files (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc", Title = T("选择字体", "選擇字體") };
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
            }
        });
        browse.Dock = DockStyle.Fill; browse.Margin = new Padding(0, 2, 0, 2); row.Controls.Add(browse, 2, 0);
        busyControls.AddRange(new Control[] { box, browse }); return row;
    }
    string T(string simplifiedText, string traditionalText) => Traditional ? traditionalText : simplifiedText;
    internal void SelectInterfaceLanguage(bool traditional) { Traditional = traditional; ApplyLanguage(); }
    void ApplyLanguage()
    {
        language.Text = Traditional ? "简" : "繁";
        if (!interactionStarted) status.Text = T("就绪", "就緒");
        subtitle.Text = T("繁体转简体 + 字体替换 / 安装与还原", "繁體轉簡體 + 字體替換 / 安裝與還原");
        gameLabel.Text = T("游戏目录", "遊戲目錄"); fontLabel.Text = T("字体文件", "字體檔案");
        optionsLabel.Text = T("选项", "選項");
        simplified.Text = T("简体中文（English 槽位）", "簡體中文（English 槽位）");
        replaceFont.Text = T("替换游戏字体（需选字体文件）", "替換遊戲字體（需選字體檔案）");
        guidance.Text = T(
            "导出文本 → 编辑 TSV → 导入文本 → 生成并安装。简体基底放入 Workspace\\basemod；备份按游戏目录保存在 Workspace\\backup-v3。",
            "匯出文本 → 編輯 TSV → 匯入文本 → 生成並安裝。簡體基底放入 Workspace\\basemod；備份按遊戲目錄保存在 Workspace\\backup-v3。");
        install.Text = T("生成并安装", "生成並安裝"); restore.Text = T("还原", "還原");
        exportText.Text = T("导出文本", "匯出文本"); importText.Text = T("导入文本", "匯入文本"); editText.Text = T("文本工作台", "文字工作台");
        foreach (var row in new[] { game.Parent, font.Parent })
            if (row is TableLayoutPanel table && table.Controls.Count > 2 && table.Controls[2] is Button browse) browse.Text = T("浏览", "瀏覽");
        if (!interactionStarted)
        {
            log.Clear();
            Append(T("选择游戏目录与字体（TTF / OTF / TTC），点击「生成并安装」。文本可在「文本工作台」内直接编辑、校验和保存。",
                     "選擇遊戲目錄與字體（TTF / OTF / TTC），點擊「生成並安裝」。文字可在「文字工作台」內直接編輯、校驗和儲存。"));
            Append(T("勾选简体：安装后使用 English；取消勾选：保留繁体文本，替换繁體中文槽位字体。",
                     "勾選簡體：安裝後使用 English；取消勾選：保留繁體文本，替換繁體中文槽位字體。"));
        }
    }
    void Append(string text) { log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"); log.ScrollToCaret(); }

    void ExportText()
    {
        string root = game.Text.Trim();
        if (root.Length == 0) { Append(T("请先选择游戏目录。", "請先選擇遊戲目錄。")); return; }
        try
        {
            if (simplified.Checked) FontService.ExportSimplifiedText(root, Append); else FontService.ExportText(root, Append);
            Append(T($"编辑 {FontService.ExportedCsv} 后点击「导入文本」，再「生成并安装」。",
                     $"編輯 {FontService.ExportedCsv} 後點擊「匯入文本」，再「生成並安裝」。"));
        }
        catch (Exception ex) { Append(T("导出失败：", "匯出失敗：") + ex.Message); }
    }

    void ImportText()
    {
        if (string.IsNullOrWhiteSpace(game.Text)) { Append(T("请先选择游戏目录。", "請先選擇遊戲目錄。")); return; }
        using var dialog = new OpenFileDialog { Filter = "Translation TSV (*.tsv;*.csv)|*.tsv;*.csv", Title = T("导入文本", "匯入文本") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var baseline = FontService.LoadText(game.Text.Trim(), simplified.Checked, Append);
            var incoming = StringTable.ImportCsv(dialog.FileName);
            FontService.ValidateText(incoming, baseline);
            string saved = FontService.OverridesFor(simplified.Checked);
            var merged = (File.Exists(saved) ? StringTable.ImportCsv(saved) : new List<StringEntry>()).ToDictionary(e => e.Id, StringComparer.Ordinal);
            foreach (var e in incoming) merged[e.Id] = e;
            FontService.SaveText(merged.Values.ToList(), baseline, simplified.Checked);
            Append($"已校验并合并 {incoming.Count} 条文本。可在「文本工作台」检查；点击「生成并安装」后生效。");
        }
        catch (Exception ex) { Append(T("导入失败：", "匯入失敗：") + ex.Message); }
    }

    void EditText()
    {
        if (string.IsNullOrWhiteSpace(game.Text)) { Append("请先选择游戏目录。"); return; }
        try { using var editor = new TextEditorForm(FontService.LoadText(game.Text.Trim(), simplified.Checked, Append), simplified.Checked); editor.ShowDialog(this); }
        catch (Exception ex) { Append("无法打开文本工作台：" + ex.Message); }
    }

    async void Run(bool shouldInstall)
    {
        if (busy) return;
        string root = game.Text.Trim(), file = font.Text.Trim().Trim('"');
        if (root.Length == 0 || (shouldInstall && replaceFont.Checked && !File.Exists(file)))
        { Append(T("请先选择有效的游戏目录和字体文件。", "請先選擇有效的遊戲目錄和字體檔案。")); return; }
        interactionStarted = true;
        bool simp = simplified.Checked, useFont = replaceFont.Checked, highQuality = quality.SelectedIndex == 0;
        busy = true; busyControls.ForEach(control => control.Enabled = false); status.Text = T("正在处理…", "正在處理…");
        try
        {
            var progress = new Progress<string>(Append);
            await Task.Run(() =>
            {
                Action<string> report = value => ((IProgress<string>)progress).Report(value);
                if (shouldInstall) FontService.InstallEn(root, useFont && file.Length > 0 ? file : null, simp, report, highQuality: highQuality);
                else FontService.RestoreEn(root, report);
            });
            status.Text = T("完成", "完成");
        }
        catch (UnauthorizedAccessException) { status.Text = T("没有写入权限", "沒有寫入權限"); Append(T("无法写入游戏目录。请以管理员身份重新运行。", "無法寫入遊戲目錄。請以管理員身份重新執行。")); }
        catch (Exception ex) { status.Text = T("操作未完成", "操作未完成"); Append(T("操作失败：", "操作失敗：") + ex.Message); }
        finally { busy = false; busyControls.ForEach(control => control.Enabled = true); }
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Accent); e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1); }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x84 && (int)message.Result == 1)
        {
            var point = PointToClient(Cursor.Position);
            if (point.X >= Width - 12 && point.Y >= Height - 12) message.Result = (IntPtr)17;
        }
    }
}
