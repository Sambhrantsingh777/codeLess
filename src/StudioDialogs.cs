using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace codeLess.Studio
{
    public class PromptInputDialog : CodeLessChromelessForm
    {
        public string InputValue = "";
        private CodeLessDarkInputBox inputBox;

        public PromptInputDialog(string title, string promptText, string defaultVal, StudioTheme theme)
        {
            this.Text = title;
            this.Size = new Size(468, 198);
            this.EnableEdgeResize = false;
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            Panel bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgWorkbench
            };

            Label lbl = new Label
            {
                Text = promptText,
                AutoSize = false,
                Location = new Point(18, 14),
                Size = new Size(424, 22),
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.5f),
                ForeColor = theme.FgPrimary
            };

            inputBox = new CodeLessDarkInputBox
            {
                Text = defaultVal ?? "",
                Location = new Point(18, 42),
                Size = new Size(424, 30),
                Font = new Font("Consolas", 10f)
            };
            inputBox.ApplyTheme(theme);

            Button okBtn = new Button
            {
                Text = "Confirm",
                Location = new Point(256, 92),
                Size = new Size(92, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            okBtn.FlatAppearance.BorderSize = 0;
            okBtn.Click += (s, e) =>
            {
                InputValue = inputBox.Text.Trim();
                if (!string.IsNullOrEmpty(InputValue))
                {
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            };

            Button cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(356, 92),
                Size = new Size(86, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.BgElevated,
                ForeColor = theme.FgPrimary,
                UseMnemonic = false,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            cancelBtn.FlatAppearance.BorderColor = theme.BorderSubtle;
            cancelBtn.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

            inputBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    okBtn.PerformClick();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    cancelBtn.PerformClick();
                }
            };

            bodyPanel.Controls.Add(lbl);
            bodyPanel.Controls.Add(inputBox);
            bodyPanel.Controls.Add(okBtn);
            bodyPanel.Controls.Add(cancelBtn);
            this.AcceptButton = okBtn;
            this.CancelButton = cancelBtn;

            Panel titleBar = BuildDarkDialogTitleBar(title, theme, false, false);
            this.Controls.Add(bodyPanel);
            this.Controls.Add(titleBar);

            this.Shown += (s, e) =>
            {
                inputBox.Focus();
                inputBox.SelectAll();
            };
        }
    }

    public class UnsavedChangesDialog : CodeLessChromelessForm
    {
        public Button SaveButton;
        public Button DontSaveButton;
        public Button CancelBtn;

        public UnsavedChangesDialog(IList<string> dirtyFileNames, StudioTheme theme)
        {
            StudioTheme t = theme ?? StudioTheme.GetBuiltInThemes()[0];
            IList<string> files = (dirtyFileNames != null && dirtyFileNames.Count > 0)
                ? dirtyFileNames
                : new List<string> { "Untitled-1" };
            bool isMulti = files.Count > 1;

            this.Text = "codeLess";
            this.Size = new Size(480, isMulti ? 268 : 196);
            this.EnableEdgeResize = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = t.BgSidebar;
            this.ForeColor = t.FgPrimary;

            Panel body = new Panel { Dock = DockStyle.Fill, BackColor = t.BgSidebar, Padding = new Padding(20) };

            Panel warnIcon = new Panel { Location = new Point(20, 16), Size = new Size(32, 32), BackColor = Color.Transparent };
            warnIcon.Paint += (s, e) =>
            {
                CodeLessVectorIcons.DrawIcon(e.Graphics, "warning-triangle", new Rectangle(2, 2, 28, 28), t.WarnAmber, 1.8f);
            };
            body.Controls.Add(warnIcon);

            string headingText = isMulti
                ? string.Format("Do you want to save the changes to the following {0} files?", files.Count)
                : string.Format("Do you want to save the changes you made to {0}?", files[0]);

            Label heading = new Label
            {
                Location = new Point(62, 16),
                Size = new Size(392, 42),
                Text = headingText,
                Font = new Font("Segoe UI Semibold", 10.0f, FontStyle.Bold),
                ForeColor = t.FgPrimary,
                UseMnemonic = false
            };
            body.Controls.Add(heading);

            int nextY = 62;
            if (isMulti)
            {
                CodeLessListBox fileList = new CodeLessListBox
                {
                    Location = new Point(62, nextY),
                    Size = new Size(390, 72),
                    BackColor = t.BgEditor,
                    ForeColor = t.FgSecondary,
                    Font = new Font("Segoe UI", 9.0f),
                    ItemHeight = 20
                };
                fileList.ApplyTheme(t);
                foreach (string fn in files)
                    fileList.Items.Add("  " + fn);
                body.Controls.Add(fileList);
                nextY += 78;
            }

            Label detail = new Label
            {
                Location = new Point(62, nextY),
                Size = new Size(390, 22),
                Text = "Your changes will be lost if you don't save them.",
                Font = new Font("Segoe UI", 8.8f),
                ForeColor = t.FgSecondary,
                UseMnemonic = false
            };
            body.Controls.Add(detail);

            int btnY = nextY + 32;
            SaveButton = new Button
            {
                Location = new Point(164, btnY),
                Size = new Size(96, 30),
                Text = isMulti ? "Save All" : "Save",
                FlatStyle = FlatStyle.Flat,
                BackColor = t.AccentPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            SaveButton.FlatAppearance.BorderSize = 0;
            SaveButton.Click += (s, e) => { this.DialogResult = DialogResult.Yes; this.Close(); };

            DontSaveButton = new Button
            {
                Location = new Point(268, btnY),
                Size = new Size(94, 30),
                Text = "Don't Save",
                FlatStyle = FlatStyle.Flat,
                BackColor = t.BgElevated,
                ForeColor = t.FgPrimary,
                Font = new Font("Segoe UI", 9.0f),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            DontSaveButton.FlatAppearance.BorderColor = t.BorderSubtle;
            DontSaveButton.Click += (s, e) => { this.DialogResult = DialogResult.No; this.Close(); };

            CancelBtn = new Button
            {
                Location = new Point(370, btnY),
                Size = new Size(84, 30),
                Text = "Cancel",
                FlatStyle = FlatStyle.Flat,
                BackColor = t.BgElevated,
                ForeColor = t.FgPrimary,
                Font = new Font("Segoe UI", 9.0f),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            CancelBtn.FlatAppearance.BorderColor = t.BorderSubtle;
            CancelBtn.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; this.Close(); };

            body.Controls.Add(SaveButton);
            body.Controls.Add(DontSaveButton);
            body.Controls.Add(CancelBtn);
            this.AcceptButton = SaveButton;
            this.CancelButton = CancelBtn;

            Panel titleBar = BuildDarkDialogTitleBar("codeLess", t, false, false);
            this.Controls.Add(body);
            this.Controls.Add(titleBar);
        }
    }

    public class NewProjectWizardDialog : CodeLessChromelessForm
    {
        public string CreatedProjectPath = null;
        public string EntryFileToOpen = null;

        public NewProjectWizardDialog(string defaultParentDir, StudioTheme theme)
        {
            this.Text = "Create New Project — codeLess Native Studio";
            this.Size = new Size(668, 480);
            this.EnableEdgeResize = false;
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            string baseParent = !string.IsNullOrEmpty(defaultParentDir) && Directory.Exists(defaultParentDir)
                ? defaultParentDir
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            Panel bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgWorkbench
            };

            Label hdr = new Label
            {
                Text = "Scaffold a Clean, Runnable Project Workspace",
                AutoSize = true,
                Location = new Point(22, 16),
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold),
                ForeColor = theme.FgPrimary
            };

            Label nameLbl = new Label { Text = "Project Name:", AutoSize = true, Location = new Point(22, 52), UseMnemonic = false, ForeColor = theme.FgSecondary };
            CodeLessDarkInputBox nameBox = new CodeLessDarkInputBox
            {
                Text = "codeless-app",
                Location = new Point(22, 74),
                Size = new Size(616, 28),
                Placeholder = "my-project",
                Font = new Font("Consolas", 9.8f)
            };
            nameBox.ApplyTheme(theme);

            Label locLbl = new Label { Text = "Parent Directory:", AutoSize = true, Location = new Point(22, 114), UseMnemonic = false, ForeColor = theme.FgSecondary };
            CodeLessDarkInputBox locBox = new CodeLessDarkInputBox
            {
                Text = baseParent,
                Location = new Point(22, 136),
                Size = new Size(518, 28),
                Placeholder = "Select parent directory...",
                Font = new Font("Consolas", 9.3f)
            };
            locBox.ApplyTheme(theme);

            Button browseBtn = new Button
            {
                Text = "Browse...",
                Location = new Point(548, 136),
                Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.BgElevated,
                ForeColor = theme.FgPrimary,
                UseMnemonic = false,
                Cursor = Cursors.Hand
            };
            browseBtn.FlatAppearance.BorderColor = theme.BorderSubtle;
            browseBtn.Click += (s, e) =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog { SelectedPath = locBox.Text })
                {
                    if (fbd.ShowDialog(this) == DialogResult.OK)
                        locBox.Text = fbd.SelectedPath;
                }
            };

            Label tplLbl = new Label { Text = "Select Project Template:", AutoSize = true, Location = new Point(22, 178), UseMnemonic = false, ForeColor = theme.FgSecondary };

            Panel tplFrame = new Panel
            {
                Location = new Point(22, 200),
                Size = new Size(616, 152),
                Padding = new Padding(1),
                BackColor = theme.BorderSubtle
            };
            CodeLessListBox tplList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgSidebar,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.3f),
                ItemHeight = 24
            };
            tplList.ApplyTheme(theme);
            tplList.Items.Add("Python Application (main.py + utils.py + .codelessrules) — Run with F5");
            tplList.Items.Add("Node.js / ES Module App (index.mjs + package.json + .codelessrules) — Run with F5");
            tplList.Items.Add("C# Native Console Application (Program.cs + .codelessrules) — Compile & Run with F5");
            tplList.Items.Add("Rust Cargo Application (src/main.rs + Cargo.toml + .codelessrules)");
            tplList.Items.Add("HTML5 / JS Interactive Web App (index.html + app.js + styles.css)");
            tplList.Items.Add("Empty Workspace (.codelessrules + README.md)");
            tplList.SelectedIndex = 0;
            tplFrame.Controls.Add(tplList);

            Button createBtn = new Button
            {
                Text = "Create & Open Project",
                Location = new Point(422, 368),
                Size = new Size(216, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            createBtn.FlatAppearance.BorderSize = 0;
            this.AcceptButton = createBtn;

            createBtn.Click += (s, e) =>
            {
                string projName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(projName)) return;
                string targetDir = Path.Combine(locBox.Text.Trim(), projName);
                try
                {
                    Directory.CreateDirectory(targetDir);
                    File.WriteAllText(Path.Combine(targetDir, ".codelessrules"),
                        "# codeLess IDE Project Rules\n- Write clean, modular, strongly-typed production code.\n- Emit surgical SEARCH/REPLACE diffs.\n- Include error handling and clear documentation.\n");

                    int sel = tplList.SelectedIndex;
                    if (sel == 0)
                    {
                        string mainPy = Path.Combine(targetDir, "main.py");
                        File.WriteAllText(mainPy,
                            "from utils import compute_fibonacci, format_report\n\n" +
                            "def main() -> None:\n" +
                            "    seq = compute_fibonacci(10)\n" +
                            "    print(format_report(\"" + projName + "\", seq))\n\n" +
                            "if __name__ == \"__main__\":\n" +
                            "    main()\n");
                        File.WriteAllText(Path.Combine(targetDir, "utils.py"),
                            "from typing import List\n\n" +
                            "def compute_fibonacci(count: int) -> List[int]:\n" +
                            "    if count <= 0:\n" +
                            "        return []\n" +
                            "    seq = [0, 1]\n" +
                            "    while len(seq) < count:\n" +
                            "        seq.append(seq[-1] + seq[-2])\n" +
                            "    return seq[:count]\n\n" +
                            "def format_report(title: str, values: List[int]) -> str:\n" +
                            "    total = sum(values)\n" +
                            "    return f\"[{title}] Generated {len(values)} items | Sum = {total} | Values = {values}\"\n");
                        EntryFileToOpen = mainPy;
                    }
                    else if (sel == 1)
                    {
                        string idxMjs = Path.Combine(targetDir, "index.mjs");
                        File.WriteAllText(idxMjs,
                            "import os from 'node:os';\n\n" +
                            "export function analyzeEnvironment() {\n" +
                            "  return {\n" +
                            "    project: '" + projName + "',\n" +
                            "    platform: os.platform(),\n" +
                            "    arch: os.arch(),\n" +
                            "    cpus: os.cpus().length,\n" +
                            "    freeMemMb: Math.round(os.freemem() / (1024 * 1024)),\n" +
                            "  };\n" +
                            "}\n\n" +
                            "console.log('[codeLess Runner]', JSON.stringify(analyzeEnvironment(), null, 2));\n");
                        File.WriteAllText(Path.Combine(targetDir, "package.json"),
                            "{\n  \"name\": \"" + projName.ToLowerInvariant() + "\",\n  \"version\": \"1.0.0\",\n  \"type\": \"module\",\n  \"scripts\": {\n    \"start\": \"node index.mjs\"\n  }\n}\n");
                        EntryFileToOpen = idxMjs;
                    }
                    else if (sel == 2)
                    {
                        string progCs = Path.Combine(targetDir, "Program.cs");
                        File.WriteAllText(progCs,
                            "using System;\nusing System.Linq;\n\n" +
                            "class Program\n{\n" +
                            "    static void Main(string[] args)\n    {\n" +
                            "        Console.WriteLine(\"=== " + projName + " (codeLess Native C#) ===\");\n" +
                            "        int[] nums = Enumerable.Range(1, 8).ToArray();\n" +
                            "        Console.WriteLine(\"Squares: \" + string.Join(\", \", nums.Select(n => n * n)));\n" +
                            "    }\n}\n");
                        EntryFileToOpen = progCs;
                    }
                    else if (sel == 3)
                    {
                        string srcDir = Path.Combine(targetDir, "src");
                        Directory.CreateDirectory(srcDir);
                        string mainRs = Path.Combine(srcDir, "main.rs");
                        File.WriteAllText(mainRs,
                            "fn main() {\n    let values = vec![1, 2, 3, 5, 8, 13];\n    let sum: i32 = values.iter().sum();\n    println!(\"[" + projName + "] Sum of sequence = {}\", sum);\n}\n");
                        File.WriteAllText(Path.Combine(targetDir, "Cargo.toml"),
                            "[package]\nname = \"" + projName.ToLowerInvariant() + "\"\nversion = \"0.1.0\"\nedition = \"2021\"\n\n[dependencies]\n");
                        EntryFileToOpen = mainRs;
                    }
                    else if (sel == 4)
                    {
                        string idxHtml = Path.Combine(targetDir, "index.html");
                        File.WriteAllText(idxHtml,
                            "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n  <meta charset=\"UTF-8\">\n  <title>" + projName + "</title>\n</head>\n<body>\n  <h1>" + projName + "</h1>\n  <script src=\"app.js\"></script>\n</body>\n</html>\n");
                        File.WriteAllText(Path.Combine(targetDir, "app.js"),
                            "console.log('Loaded " + projName + "');\n");
                        EntryFileToOpen = idxHtml;
                    }
                    else
                    {
                        string readme = Path.Combine(targetDir, "README.md");
                        File.WriteAllText(readme, "# " + projName + "\n\nCreated with codeLess Native IDE.\n");
                        EntryFileToOpen = readme;
                    }

                    CreatedProjectPath = targetDir;
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed creating project: " + ex.Message, "codeLess IDE");
                }
            };

            bodyPanel.Controls.AddRange(new Control[] { hdr, nameLbl, nameBox, locLbl, locBox, browseBtn, tplLbl, tplFrame, createBtn });

            Panel titleBar = BuildDarkDialogTitleBar("Create New Project — codeLess Native Studio", theme, false, false);
            this.Controls.Add(bodyPanel);
            this.Controls.Add(titleBar);
        }
    }

    public class ByokAndHarnessDialog : CodeLessChromelessForm
    {
        private readonly LlmHarnessAndAgentEngine engine;
        private readonly IdePersistentSettings settings;
        private readonly StudioTheme theme;
        private CodeLessListBox vendorList;
        private CodeLessDarkInputBox apiKeyBox;
        private CodeLessDarkInputBox baseUrlBox;
        private CodeLessDropdown modelCombo;
        private CodeLessDropdown harnessCombo;
        private Label harnessDetailLabel;
        private Label statusLabel;
        private ByokVendor selectedVendor;

        public ByokAndHarnessDialog(LlmHarnessAndAgentEngine engine, IdePersistentSettings settings, StudioTheme theme)
        {
            this.engine = engine;
            this.settings = settings;
            this.theme = theme;
            this.Text = "Universal BYOK Multi-Vendor Router & LLM-Specific Harness Studio";
            this.Size = new Size(864, 588);
            this.EnableEdgeResize = false;
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            BuildUi();
        }

        private void BuildUi()
        {
            Panel bodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgWorkbench
            };

            Panel leftBorderHost = new Panel
            {
                Dock = DockStyle.Left,
                Width = 252,
                Padding = new Padding(0, 0, 1, 0),
                BackColor = theme.BorderSubtle
            };

            vendorList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgSidebar,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI Semibold", 9.2f),
                ItemHeight = 28
            };
            vendorList.ApplyTheme(theme);
            leftBorderHost.Controls.Add(vendorList);

            RefreshVendorListLabels();

            Panel rightPane = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgWorkbench
            };

            Label titleLbl = new Label
            {
                Text = "Direct Zero-Markup Connections (9 Vendors) + 5 Model-Specific Coding Harnesses",
                AutoSize = true,
                Location = new Point(22, 16),
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 10.2f, FontStyle.Bold),
                ForeColor = theme.FgPrimary
            };

            Label keyLbl = new Label
            {
                Text = "API Key (persisted locally to %AppData%\\codeLess\\settings.json):",
                AutoSize = true,
                Location = new Point(22, 48),
                UseMnemonic = false,
                Font = new Font("Segoe UI", 9f),
                ForeColor = theme.FgSecondary
            };

            apiKeyBox = new CodeLessDarkInputBox
            {
                Location = new Point(22, 70),
                Size = new Size(560, 28),
                PasswordChar = '•',
                Placeholder = "Paste vendor API key...",
                Font = new Font("Consolas", 9.8f)
            };
            apiKeyBox.ApplyTheme(theme);
            apiKeyBox.TextChanged += (s, e) =>
            {
                if (selectedVendor != null)
                {
                    selectedVendor.ApiKey = apiKeyBox.Text.Trim();
                    engine.SyncVendorsToSettings(settings);
                }
            };

            Label urlLbl = new Label
            {
                Text = "Vendor Base API Endpoint URL:",
                AutoSize = true,
                Location = new Point(22, 110),
                UseMnemonic = false,
                Font = new Font("Segoe UI", 9f),
                ForeColor = theme.FgSecondary
            };

            baseUrlBox = new CodeLessDarkInputBox
            {
                Location = new Point(22, 132),
                Size = new Size(560, 28),
                Placeholder = "https://api...",
                Font = new Font("Consolas", 9.3f)
            };
            baseUrlBox.ApplyTheme(theme);
            baseUrlBox.TextChanged += (s, e) =>
            {
                if (selectedVendor != null)
                {
                    selectedVendor.BaseUrl = baseUrlBox.Text.Trim();
                    engine.SyncVendorsToSettings(settings);
                }
            };

            Label modelLbl = new Label
            {
                Text = "Active Coding Model (select or type custom model ID):",
                AutoSize = true,
                Location = new Point(22, 172),
                UseMnemonic = false,
                Font = new Font("Segoe UI", 9f),
                ForeColor = theme.FgSecondary
            };

            modelCombo = new CodeLessDropdown
            {
                Location = new Point(22, 194),
                Width = 560,
                Height = 28,
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                Font = new Font("Consolas", 9.4f)
            };
            modelCombo.ApplyTheme(theme);
            modelCombo.SelectedIndexChanged += (s, e) =>
            {
                if (selectedVendor != null && !string.IsNullOrWhiteSpace(modelCombo.Text))
                {
                    selectedVendor.SelectedModel = modelCombo.Text.Trim();
                    engine.SyncVendorsToSettings(settings);
                }
            };

            Label harnessLbl = new Label
            {
                Text = "LLM-Specific Prompt & Edit Harness Profile:",
                AutoSize = true,
                Location = new Point(22, 236),
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = theme.AccentPrimary
            };

            harnessCombo = new CodeLessDropdown
            {
                Location = new Point(22, 258),
                Width = 560,
                Height = 28,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                Font = new Font("Consolas", 9.1f)
            };
            harnessCombo.ApplyTheme(theme);
            harnessCombo.Items.Add("auto — Automatically match optimal harness for active model family");
            foreach (LlmHarnessProfile hp in engine.Harnesses)
                harnessCombo.Items.Add(hp.Id + " — " + hp.Name + " [" + hp.EditProtocol + "]");

            harnessDetailLabel = new Label
            {
                Location = new Point(22, 294),
                Size = new Size(560, 50),
                UseMnemonic = false,
                Font = new Font("Consolas", 8.5f),
                ForeColor = theme.FgSecondary,
                Text = "Select a harness profile above to inspect its edit protocol, token budget, and reasoning directives."
            };

            harnessCombo.SelectedIndexChanged += (s, e) =>
            {
                if (harnessCombo.SelectedIndex <= 0)
                {
                    settings.ActiveHarnessId = "auto";
                    LlmHarnessProfile resolved = engine.ResolveHarnessForVendor(selectedVendor, "auto");
                    harnessDetailLabel.Text = "Auto-Resolved: " + resolved.Name + "\n" + resolved.Description;
                }
                else
                {
                    LlmHarnessProfile hp = engine.Harnesses[harnessCombo.SelectedIndex - 1];
                    settings.ActiveHarnessId = hp.Id;
                    harnessDetailLabel.Text = string.Format("Protocol: {0}  •  Context Budget: {1:N0} tok  •  Temp: {2}\n{3}",
                        hp.EditProtocol, hp.ContextTokenBudget, hp.RecommendedTemp, hp.Description);
                }
                settings.Save();
            };

            Button testBtn = new Button
            {
                Text = "⚡ Test Live Endpoint",
                Location = new Point(22, 354),
                Size = new Size(180, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            testBtn.FlatAppearance.BorderSize = 0;

            Button probeOllamaBtn = new Button
            {
                Text = "Probe Local Ollama",
                Location = new Point(212, 354),
                Size = new Size(180, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.BgElevated,
                ForeColor = theme.FgPrimary,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            probeOllamaBtn.FlatAppearance.BorderColor = theme.BorderSubtle;

            Button closeBtn = new Button
            {
                Text = "Save & Close",
                Location = new Point(402, 354),
                Size = new Size(180, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(25, 120, 229),
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            closeBtn.FlatAppearance.BorderSize = 0;
            this.AcceptButton = closeBtn;
            this.CancelButton = closeBtn;
            closeBtn.Click += (s, e) =>
            {
                engine.SyncVendorsToSettings(settings);
                this.Close();
            };

            statusLabel = new Label
            {
                Location = new Point(22, 404),
                Size = new Size(560, 120),
                UseMnemonic = false,
                Font = new Font("Consolas", 8.8f),
                ForeColor = theme.FgSecondary,
                Text = "Select any vendor on the left to paste your API key. Keys are stored locally in %AppData%\\codeLess\\settings.json and connect directly to vendor endpoints with zero middleman markup."
            };

            testBtn.Click += async (s, e) =>
            {
                if (selectedVendor == null) return;
                statusLabel.ForeColor = theme.AccentPrimary;
                statusLabel.Text = "Pinging " + selectedVendor.Name + " (" + selectedVendor.SelectedModel + ")...";
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    string reply = await engine.CallByokVendorAsync(
                        selectedVendor,
                        "Reply with a short 1-sentence confirmation.",
                        "Ping from codeLess Native IDE."
                    );
                    sw.Stop();
                    statusLabel.ForeColor = Color.FromArgb(110, 231, 183);
                    statusLabel.Text = string.Format("✓ Connected to {0} in {1}ms:\n{2}", selectedVendor.Name, sw.ElapsedMilliseconds, reply);
                    RefreshVendorListLabels();
                }
                catch (Exception ex)
                {
                    statusLabel.ForeColor = Color.FromArgb(252, 165, 165);
                    if (ex.Message == "NO_API_KEY")
                        statusLabel.Text = "Please paste a valid API key for " + selectedVendor.Name + " above first.";
                    else
                        statusLabel.Text = "Endpoint Error: " + ex.Message;
                }
            };

            probeOllamaBtn.Click += async (s, e) =>
            {
                statusLabel.Text = "Probing http://127.0.0.1:11434/api/tags for local Ollama models...";
                bool found = await engine.ProbeLocalOllamaAsync();
                if (found)
                {
                    var ol = engine.Vendors.FirstOrDefault(v => v.Id == "ollama");
                    statusLabel.ForeColor = Color.FromArgb(110, 231, 183);
                    statusLabel.Text = "✓ Local Ollama detected! Models: " + (ol != null ? string.Join(", ", ol.Models) : "ready");
                    RefreshVendorListLabels();
                }
                else
                {
                    statusLabel.ForeColor = Color.FromArgb(252, 165, 165);
                    statusLabel.Text = "No local Ollama server responded on 127.0.0.1:11434. Start Ollama (`ollama serve`) or configure a cloud BYOK key.";
                }
            };

            vendorList.SelectedIndexChanged += (s, e) =>
            {
                int idx = vendorList.SelectedIndex;
                if (idx < 0 || idx >= engine.Vendors.Count) return;
                selectedVendor = engine.Vendors[idx];
                apiKeyBox.Text = selectedVendor.ApiKey ?? "";
                baseUrlBox.Text = selectedVendor.BaseUrl ?? "";
                modelCombo.Items.Clear();
                foreach (string m in selectedVendor.Models)
                    modelCombo.Items.Add(m);
                modelCombo.Text = selectedVendor.SelectedModel;

                if (settings.ActiveHarnessId == "auto")
                {
                    LlmHarnessProfile resolved = engine.ResolveHarnessForVendor(selectedVendor, "auto");
                    harnessDetailLabel.Text = "Auto-Resolved for " + selectedVendor.Name + ": " + resolved.Name + "\n" + resolved.Description;
                }
            };

            rightPane.Controls.AddRange(new Control[]
            {
                titleLbl, keyLbl, apiKeyBox, urlLbl, baseUrlBox, modelLbl, modelCombo,
                harnessLbl, harnessCombo, harnessDetailLabel,
                testBtn, probeOllamaBtn, closeBtn, statusLabel
            });

            bodyPanel.Controls.Add(rightPane);
            bodyPanel.Controls.Add(leftBorderHost);

            Panel titleBar = BuildDarkDialogTitleBar("Universal BYOK Multi-Vendor Router & LLM-Specific Harness Studio", theme, false, false);
            this.Controls.Add(bodyPanel);
            this.Controls.Add(titleBar);

            harnessCombo.SelectedIndex = 0;
            if (engine.Vendors.Count > 0)
                vendorList.SelectedIndex = 0;
        }

        private void RefreshVendorListLabels()
        {
            int savedIdx = vendorList.SelectedIndex;
            vendorList.Items.Clear();
            foreach (ByokVendor v in engine.Vendors)
            {
                bool hasKey = (v.Id == "ollama" && engine.LocalOllamaDetected) || (v.Id != "ollama" && !string.IsNullOrWhiteSpace(v.ApiKey) && v.ApiKey.Length >= 5);
                string badge = hasKey ? "✓ " : "○ ";
                vendorList.Items.Add(badge + v.Name);
            }
            if (savedIdx >= 0 && savedIdx < vendorList.Items.Count)
                vendorList.SelectedIndex = savedIdx;
        }
    }

    public class ContextInspectorDialog : CodeLessChromelessForm
    {
        public ContextInspectorDialog(
            LlmHarnessAndAgentEngine engine,
            EditorTabBuffer activeTab,
            string lastQuery,
            StudioTheme theme
        )
        {
            this.Text = "Context, RepoMap & LLM Harness Inspector (BM25 + AST Symbol Graph)";
            this.Size = new Size(880, 620);
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            List<Bm25CodeChunk> topChunks = engine.RetrieveTopChunks(
                string.IsNullOrEmpty(lastQuery) ? (activeTab != null ? activeTab.FileName : "workspace") : lastQuery,
                5
            );

            LlmHarnessProfile harness = engine.Harnesses[0];
            string sysPrompt = LlmHarnessAndAgentEngine.BuildSystemHarnessPrompt("agent", harness);
            int sysTokens = LlmHarnessAndAgentEngine.EstimateTokens(sysPrompt);
            int rulesTokens = LlmHarnessAndAgentEngine.EstimateTokens(engine.ProjectRulesText);
            int repoMapTokens = LlmHarnessAndAgentEngine.EstimateTokens(engine.CompactRepoMap);
            int activeEditorTokens = activeTab != null ? LlmHarnessAndAgentEngine.EstimateTokens(activeTab.Content) : 0;
            int chunkTokens = topChunks.Sum(c => c.TokenLength);
            int totalTokens = sysTokens + rulesTokens + repoMapTokens + activeEditorTokens + chunkTokens;

            Label summaryHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(14, 10, 14, 6),
                BackColor = theme.BgSidebar,
                ForeColor = theme.FgPrimary,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.3f, FontStyle.Bold),
                Text = string.Format(
                    "Token Budget: {0:N0} / {1:N0} tok  (Harness: {2}t • Rules: {3}t • AST RepoMap: {4}t • Editor: {5}t • BM25: {6}t)",
                    totalTokens, harness.ContextTokenBudget, sysTokens, rulesTokens, repoMapTokens, activeEditorTokens, chunkTokens
                )
            };
            summaryHeader.Paint += (s, e) =>
            {
                using (Pen bp = new Pen(theme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(bp, 0, summaryHeader.Height - 1, summaryHeader.Width, summaryHeader.Height - 1);
            };

            CodeLessStreamView inspectorBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.4f)
            };
            inspectorBox.ApplyTheme(theme);

            List<string> sections = new List<string>();
            sections.Add("=== 1. ACTIVE LLM-SPECIFIC CODING HARNESS PROMPT ===\n" + sysPrompt);
            if (!string.IsNullOrEmpty(engine.ProjectRulesText))
            {
                sections.Add("\n=== 2. PROJECT RULES (.codelessrules / .cursorrules) ===\n" + engine.ProjectRulesText);
            }
            sections.Add("\n=== 3. COMPACT AST REPOSITORY MAP ===\n" + engine.CompactRepoMap);
            sections.Add("\n=== 4. TOP BM25 + SYMBOL-BOOSTED CODEBASE CHUNKS ===");
            foreach (Bm25CodeChunk c in topChunks)
            {
                sections.Add(string.Format(
                    "\n--- [{0}:L{1}-L{2}] (BM25 Score: {3}, Symbol: {4}) ---\n{5}",
                    c.RelativePath, c.StartLine, c.EndLine, c.Score,
                    string.IsNullOrEmpty(c.SymbolName) ? "module" : c.SymbolName,
                    c.Snippet
                ));
            }

            inspectorBox.Text = string.Join("\n", sections);

            Panel titleBar = BuildDarkDialogTitleBar("Context, RepoMap & LLM Harness Inspector (BM25 + AST Symbol Graph)", theme, false, true);
            this.Controls.Add(inspectorBox);
            this.Controls.Add(summaryHeader);
            this.Controls.Add(titleBar);
        }
    }

    public class CommandPaletteDialog : CodeLessChromelessForm
    {
        public string SelectedFilePath = null;
        public StudioTheme SelectedTheme = null;
        public string SelectedCommandId = null;
        public int SelectedLineToJump = -1;
        public int SelectedColumnToJump = -1;
        public CodeLessDarkInputBox SearchInput = null;
        public CodeLessListBox ResultsList = null;

        private class PaletteEntry
        {
            public string RawText;
            public string DisplayTitle;
            public string Category;
            public string Shortcut;
            public Action Callback;
        }

        public CommandPaletteDialog(
            List<string> workspaceFiles,
            string workspaceRoot,
            List<StudioTheme> themes,
            List<LlmHarnessProfile> harnesses,
            EditorTabBuffer activeTab,
            StudioTheme currentTheme,
            string initialPrefix = "",
            List<WorkspaceSymbolLocation> workspaceSymbols = null,
            List<string> recentItems = null
        )
        {
            this.Text = initialPrefix == ">"
                ? "Command Palette"
                : "Quick Open";
            this.Size = new Size(710, 468);
            this.EnableEdgeResize = false;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = currentTheme.BgSidebar;
            this.ForeColor = currentTheme.FgPrimary;
            // Elevated quick-input widget: subtle 1 px frame instead of an accent outline (docs/UI_GUIDELINES.md).
            this.OuterFrameColor = UiTokens.WidgetBorder(currentTheme);

            Panel headerWrap = new Panel
            {
                Dock = DockStyle.Top,
                Height = 52,
                BackColor = currentTheme.BgActivity,
                Padding = new Padding(10, 10, 10, 9)
            };
            headerWrap.Paint += (s, e) =>
            {
                using (Pen p = new Pen(currentTheme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(p, 0, headerWrap.Height - 1, headerWrap.Width, headerWrap.Height - 1);
            };

            CodeLessDarkInputBox searchBox = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Fill,
                Placeholder = "Search files by name, @symbol, :line, or >command...",
                Font = new Font("Segoe UI", 10.2f),
                Text = initialPrefix ?? ""
            };
            searchBox.ApplyTheme(currentTheme);
            this.SearchInput = searchBox;
            headerWrap.Controls.Add(searchBox);

            Panel footerHint = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                BackColor = currentTheme.BgActivity
            };
            footerHint.Paint += (s, e) =>
            {
                using (Pen p = new Pen(currentTheme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(p, 0, 0, footerHint.Width, 0);
                using (Font ff = new Font("Segoe UI", 7.8f))
                {
                    TextRenderer.DrawText(e.Graphics,
                        "Up/Down Navigate    Enter Execute / Open    > Commands    @ Symbols    : Go to Line    Esc Dismiss",
                        ff, new Rectangle(12, 0, footerHint.Width - 24, footerHint.Height),
                        currentTheme.FgMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                }
            };

            CodeLessListBox resultsList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BackColor = currentTheme.BgSidebar,
                ForeColor = currentTheme.FgPrimary,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                Font = new Font("Segoe UI", 9.3f),
                ItemHeight = 30
            };
            resultsList.ApplyTheme(currentTheme);
            this.ResultsList = resultsList;

            List<PaletteEntry> entries = new List<PaletteEntry>();
            // Quick Open "file:line[:col]" suffix (VS Code), parsed by refreshList and applied when a file is picked.
            int quickOpenLine = 0;
            int quickOpenCol = 0;

            Action<string, string, string, Action> addCmd = (category, title, shortcut, cb) =>
            {
                entries.Add(new PaletteEntry
                {
                    RawText = "> [" + category + "] " + title,
                    DisplayTitle = title,
                    Category = category,
                    Shortcut = shortcut ?? "",
                    Callback = cb
                });
            };

            addCmd("File", "File: New Text File", "Ctrl+N",
                () => { SelectedCommandId = "new_file"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: New File...", "Ctrl+Alt+Win+N",
                () => { SelectedCommandId = "new_file_dialog"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: New Window", "Ctrl+Shift+N",
                () => { SelectedCommandId = "new_window"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Open File...", "Ctrl+O",
                () => { SelectedCommandId = "open_file"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Open Folder...", "Ctrl+K Ctrl+O",
                () => { SelectedCommandId = "open_folder"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Open Recent...", "Ctrl+R",
                () => { SelectedCommandId = "open_recent"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Save", "Ctrl+S",
                () => { SelectedCommandId = "save_file"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Save As...", "Ctrl+Shift+S",
                () => { SelectedCommandId = "save_as"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Save All", "Ctrl+K S",
                () => { SelectedCommandId = "save_all"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Toggle Auto Save", "",
                () => { SelectedCommandId = "toggle_autosave"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Revert File", "",
                () => { SelectedCommandId = "revert_file"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Close Editor", "Ctrl+W",
                () => { SelectedCommandId = "close_editor"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Close Folder", "Ctrl+K F",
                () => { SelectedCommandId = "close_folder"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("File", "File: Reopen Closed Editor", "Ctrl+Shift+T",
                () => { SelectedCommandId = "reopen_closed_tab"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Undo", "Ctrl+Z",
                () => { SelectedCommandId = "edit_undo"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Redo", "Ctrl+Y",
                () => { SelectedCommandId = "edit_redo"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Cut", "Ctrl+X",
                () => { SelectedCommandId = "edit_cut"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Copy", "Ctrl+C",
                () => { SelectedCommandId = "edit_copy"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Paste", "Ctrl+V",
                () => { SelectedCommandId = "edit_paste"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Find", "Ctrl+F",
                () => { SelectedCommandId = "edit_find"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Replace", "Ctrl+H",
                () => { SelectedCommandId = "edit_replace"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Find in Files", "Ctrl+Shift+F",
                () => { SelectedCommandId = "find_in_files"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Replace in Files", "Ctrl+Shift+H",
                () => { SelectedCommandId = "replace_in_files"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Toggle Line Comment", "Ctrl+/",
                () => { SelectedCommandId = "toggle_comment"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Edit", "Edit: Toggle Block Comment", "Shift+Alt+A",
                () => { SelectedCommandId = "toggle_block_comment"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Select All", "Ctrl+A",
                () => { SelectedCommandId = "select_all"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Expand Selection", "Shift+Alt+Right",
                () => { SelectedCommandId = "expand_selection"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Shrink Selection", "Shift+Alt+Left",
                () => { SelectedCommandId = "shrink_selection"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Copy Line Up", "Shift+Alt+Up",
                () => { SelectedCommandId = "copy_line_up"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Copy Line Down", "Shift+Alt+Down",
                () => { SelectedCommandId = "copy_line_down"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Move Line Up", "Alt+Up",
                () => { SelectedCommandId = "move_line_up"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Move Line Down", "Alt+Down",
                () => { SelectedCommandId = "move_line_down"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Duplicate Selection", "",
                () => { SelectedCommandId = "duplicate_selection"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Add Cursor Above", "Ctrl+Alt+Up",
                () => { SelectedCommandId = "add_cursor_above"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Add Cursor Below", "Ctrl+Alt+Down",
                () => { SelectedCommandId = "add_cursor_below"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Add Next Occurrence", "Ctrl+D",
                () => { SelectedCommandId = "add_next_occurrence"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Select All Occurrences", "Ctrl+Shift+L",
                () => { SelectedCommandId = "select_all_occurrences"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Selection", "Selection: Toggle Column Selection Mode", "",
                () => { SelectedCommandId = "column_selection_mode"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Full Screen", "F11",
                () => { SelectedCommandId = "toggle_fullscreen"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Zen Mode", "Ctrl+K Z",
                () => { SelectedCommandId = "toggle_zen"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Primary Side Bar", "Ctrl+B",
                () => { SelectedCommandId = "toggle_sidebar"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Panel / Terminal", "Ctrl+J",
                () => { SelectedCommandId = "toggle_bottom"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Secondary Side Bar (AI Chat)", "Ctrl+Alt+B",
                () => { SelectedCommandId = "toggle_right"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Minimap", "",
                () => { SelectedCommandId = "toggle_minimap"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Breadcrumbs", "",
                () => { SelectedCommandId = "toggle_breadcrumbs"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Status Bar", "",
                () => { SelectedCommandId = "toggle_statusbar"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Zoom In", "Ctrl+=",
                () => { SelectedCommandId = "zoom_in"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Zoom Out", "Ctrl+-",
                () => { SelectedCommandId = "zoom_out"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Reset Zoom", "Ctrl+NumPad0",
                () => { SelectedCommandId = "zoom_reset"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Split Editor Right", "Ctrl+\\",
                () => { SelectedCommandId = "split_editor"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Explorer", "Ctrl+Shift+E",
                () => { SelectedCommandId = "view_explorer"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Search", "Ctrl+Shift+F",
                () => { SelectedCommandId = "find_in_files"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Source Control", "Ctrl+Shift+G",
                () => { SelectedCommandId = "view_scm"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Extensions", "Ctrl+Shift+X",
                () => { SelectedCommandId = "view_extensions"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Problems", "Ctrl+Shift+M",
                () => { SelectedCommandId = "view_problems"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Output", "Ctrl+Shift+U",
                () => { SelectedCommandId = "view_output"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Show Terminal", "Ctrl+`",
                () => { SelectedCommandId = "view_terminal"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "View: Toggle Word Wrap", "Alt+Z",
                () => { SelectedCommandId = "toggle_wrap"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Back", "Alt+Left",
                () => { SelectedCommandId = "go_back"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Forward", "Alt+Right",
                () => { SelectedCommandId = "go_forward"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Go to File...", "Ctrl+P",
                () => { searchBox.Text = ""; searchBox.SelectionStart = 0; });
            addCmd("Go", "Go: Go to Symbol in Workspace...", "Ctrl+T",
                () => { searchBox.Text = "#"; searchBox.SelectionStart = searchBox.Text.Length; });
            addCmd("Go", "Go: Go to Symbol in Editor...", "Ctrl+Shift+O",
                () => { searchBox.Text = "@"; searchBox.SelectionStart = searchBox.Text.Length; });
            addCmd("Go", "Go: Go to Definition", "F12",
                () => { SelectedCommandId = "go_definition"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Go to Line/Column...", "Ctrl+G",
                () => { searchBox.Text = ":"; searchBox.SelectionStart = searchBox.Text.Length; });
            addCmd("Go", "Go: Go to Bracket", "Ctrl+Shift+\\",
                () => { SelectedCommandId = "go_bracket"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Next Problem", "F8",
                () => { SelectedCommandId = "next_problem"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Previous Problem", "Shift+F8",
                () => { SelectedCommandId = "prev_problem"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Run", "Debug: Start Debugging", "F5",
                () => { SelectedCommandId = "debug_start"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Run", "Run: Run Without Debugging", "Ctrl+F5",
                () => { SelectedCommandId = "run_file"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Run", "Run: Stop Execution", "Shift+F5",
                () => { SelectedCommandId = "stop_run"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Run", "Run: Toggle Breakpoint", "F9",
                () => { SelectedCommandId = "toggle_breakpoint"; this.DialogResult = DialogResult.OK; this.Close(); });
            string[][] debugCmds = new string[][]
            {
                new string[] { "Debug: Stop", "Shift+F5", "debug_stop" },
                new string[] { "Debug: Restart", "Ctrl+Shift+F5", "debug_restart" },
                new string[] { "Debug: Pause", "F6", "debug_pause" },
                new string[] { "Debug: Step Over", "F10", "debug_step_over" },
                new string[] { "Debug: Step Into", "F11", "debug_step_into" },
                new string[] { "Debug: Step Out", "Shift+F11", "debug_step_out" },
                new string[] { "Debug: Add Conditional Breakpoint / Logpoint...", "", "debug_conditional_breakpoint" },
                new string[] { "Debug: Remove All Breakpoints", "", "debug_remove_all_breakpoints" },
                new string[] { "Debug: Debug Active File", "", "debug_active_file" },
                new string[] { "Debug: Create launch.json", "", "debug_create_launch_json" },
                new string[] { "View: Show Run and Debug", "Ctrl+Shift+D", "debug_open_view" },
                new string[] { "Debug: Open Debug Console", "Ctrl+Shift+Y", "debug_console" },
                new string[] { "Python: Select Interpreter", "", "python_select_interpreter" },
                new string[] { "Python: Run Python File", "", "python_run_file" },
                new string[] { "Python: Debug Python File", "", "python_debug_file" }
            };
            foreach (string[] dc in debugCmds)
            {
                string dcId = dc[2];
                addCmd("Run", dc[0], dc[1], () => { SelectedCommandId = dcId; this.DialogResult = DialogResult.OK; this.Close(); });
            }
            addCmd("Terminal", "Terminal: Create New Terminal", "Ctrl+Shift+`",
                () => { SelectedCommandId = "terminal_new"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Split Terminal", "Ctrl+Shift+5",
                () => { SelectedCommandId = "terminal_split"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Focus Next Terminal", "Ctrl+PageDown",
                () => { SelectedCommandId = "terminal_next"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Focus Previous Terminal", "Ctrl+PageUp",
                () => { SelectedCommandId = "terminal_prev"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Run Build Task", "Ctrl+Shift+B",
                () => { SelectedCommandId = "terminal_build"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Run Active File in Active Terminal", "",
                () => { SelectedCommandId = "run_file"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Run Selected Text in Active Terminal", "",
                () => { SelectedCommandId = "terminal_run_selected"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Kill Active Terminal Instance", "",
                () => { SelectedCommandId = "terminal_kill"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Terminal", "Terminal: Clear", "",
                () => { SelectedCommandId = "terminal_clear"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Help", "Help: Welcome", "",
                () => { SelectedCommandId = "help_welcome"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Help", "Help: Toggle Developer Tools", "Ctrl+Shift+I",
                () => { SelectedCommandId = "help_devtools"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Help", "Help: About codeLess", "",
                () => { SelectedCommandId = "help_about"; this.DialogResult = DialogResult.OK; this.Close(); });
            foreach (string[] dev in CodeLessMainForm.DevToolsPaletteCommands)
            {
                string devId = dev[3];
                addCmd(dev[0], dev[1], dev[2], () => { SelectedCommandId = devId; this.DialogResult = DialogResult.OK; this.Close(); });
            }
            foreach (string[] par in CodeLessMainForm.ParityPaletteCommands)
            {
                string parId = par[3];
                addCmd(par[0], par[1], par[2], () => { SelectedCommandId = parId; this.DialogResult = DialogResult.OK; this.Close(); });
            }
            addCmd("Arena", "Launch Multi-Harness Parallel Arena (Race 2 Harnesses Side-by-Side)", "",
                () => { SelectedCommandId = "open_arena"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Architecture", "Open Interactive Live Code & Dependency Map", "",
                () => { SelectedCommandId = "open_arch_map"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Time-Travel", "Open Zero-RAM Shadow Checkpoints & Instant Rollback", "",
                () => { SelectedCommandId = "open_checkpoints"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("API Client", "Open Native HTTP / REST & Latency Playground", "",
                () => { SelectedCommandId = "open_rest_client"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("View", "Show Network Activity", "",
                () => { SelectedCommandId = "show_network_activity"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Preferences", "Toggle Offline Mode", "",
                () => { SelectedCommandId = "toggle_offline_mode"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Self-Heal", "Auto-Heal Last Terminal / Compiler Crash", "",
                () => { SelectedCommandId = "auto_heal_terminal"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Workspace", "Create New Project from Template Wizard...", "",
                () => { SelectedCommandId = "new_project"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("AI & BYOK", "Configure 9 BYOK Vendors & 5 LLM Harnesses", "",
                () => { SelectedCommandId = "open_byok"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Preferences", "Preferences: Open Settings (UI)", "Ctrl+,",
                () => { SelectedCommandId = "open_settings"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Preferences", "Preferences: Open Keyboard Shortcuts", "Ctrl+K Ctrl+S",
                () => { SelectedCommandId = "open_shortcuts"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Editor", "Rename Symbol Across Buffer", "F2",
                () => { SelectedCommandId = "rename_symbol"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Editor", "Format Active Document", "Shift+Alt+F",
                () => { SelectedCommandId = "format_doc"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Go to Type Definition", "",
                () => { SelectedCommandId = "lsp_typedef"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Go to Implementation", "Ctrl+F12",
                () => { SelectedCommandId = "lsp_impl"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Go", "Go: Find All References", "Shift+F12",
                () => { SelectedCommandId = "lsp_refs"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Editor", "Rename Symbol (Language Server)", "F2",
                () => { SelectedCommandId = "lsp_rename"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Editor", "Format Document (Language Server)", "Shift+Alt+F",
                () => { SelectedCommandId = "lsp_format"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Editor", "Quick Fix...", "Ctrl+.",
                () => { SelectedCommandId = "lsp_quickfix"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Language Server", "Language Server: Restart Language Server", "",
                () => { SelectedCommandId = "lsp_restart"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Language Server", "Language Server: Show Language Server Log", "",
                () => { SelectedCommandId = "lsp_log"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Language Server", "Language Server: Configure Language Servers...", "",
                () => { SelectedCommandId = "lsp_config"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Context", "Open Prompt, RepoMap & BM25 Context Inspector", "",
                () => { SelectedCommandId = "open_context"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Extensions", "Install VS Code Extension from .VSIX File...", "Ctrl+Shift+X",
                () => { SelectedCommandId = "install_vsix"; this.DialogResult = DialogResult.OK; this.Close(); });
            addCmd("Audit", "Run Built-In AST Complexity & Security Shield Audit", "",
                () => { SelectedCommandId = "ext_complexity"; this.DialogResult = DialogResult.OK; this.Close(); });

            if (activeTab != null && activeTab.Symbols != null)
            {
                foreach (AstSymbolItem sym in activeTab.Symbols)
                {
                    AstSymbolItem capSym = sym;
                    entries.Add(new PaletteEntry
                    {
                        RawText = string.Format("@{0} [{1}] Ln {2} ({3})", capSym.Name, capSym.Kind, capSym.Line, capSym.ComplexityHint ?? "O(1)"),
                        DisplayTitle = capSym.Name + "  (" + capSym.Kind + " • " + (capSym.ComplexityHint ?? "O(1)") + ")",
                        Category = "SYM",
                        Shortcut = "Ln " + capSym.Line,
                        Callback = () => { SelectedLineToJump = capSym.Line; this.DialogResult = DialogResult.OK; this.Close(); }
                    });
                }
            }

            if (workspaceSymbols != null && workspaceSymbols.Count > 0)
            {
                foreach (WorkspaceSymbolLocation wsym in workspaceSymbols.Take(1000))
                {
                    WorkspaceSymbolLocation capWs = wsym;
                    entries.Add(new PaletteEntry
                    {
                        RawText = string.Format("#{0} [{1}] {2}:{3}", capWs.SymbolName, capWs.Kind, capWs.RelativePath, capWs.Line),
                        DisplayTitle = capWs.SymbolName + "  (" + capWs.Kind + " • " + capWs.RelativePath + ")",
                        Category = "WS-SYM",
                        Shortcut = "Ln " + capWs.Line,
                        Callback = () =>
                        {
                            SelectedFilePath = capWs.FilePath;
                            SelectedLineToJump = capWs.Line;
                            this.DialogResult = DialogResult.OK;
                            this.Close();
                        }
                    });
                }
            }
            else if (activeTab != null && activeTab.Symbols != null)
            {
                foreach (AstSymbolItem sym in activeTab.Symbols)
                {
                    AstSymbolItem capSym = sym;
                    entries.Add(new PaletteEntry
                    {
                        RawText = string.Format("#{0} [{1}] Ln {2}", capSym.Name, capSym.Kind, capSym.Line),
                        DisplayTitle = capSym.Name + "  (" + capSym.Kind + ")",
                        Category = "WS-SYM",
                        Shortcut = "Ln " + capSym.Line,
                        Callback = () => { SelectedLineToJump = capSym.Line; this.DialogResult = DialogResult.OK; this.Close(); }
                    });
                }
            }

            if (recentItems != null && recentItems.Count > 0)
            {
                foreach (string rec in recentItems)
                {
                    string capRec = rec;
                    bool isDir = Directory.Exists(capRec);
                    entries.Add(new PaletteEntry
                    {
                        RawText = "recent: " + capRec,
                        DisplayTitle = capRec,
                        Category = isDir ? "FOLDER" : "RECENT",
                        Shortcut = Path.GetFileName(capRec.TrimEnd('\\', '/')),
                        Callback = () => { SelectedFilePath = capRec; this.DialogResult = DialogResult.OK; this.Close(); }
                    });
                }
            }

            foreach (StudioTheme th in themes)
            {
                StudioTheme captured = th;
                entries.Add(new PaletteEntry
                {
                    RawText = "> [Theme] Preferences: Color Theme -> " + captured.Name,
                    DisplayTitle = "Preferences: Color Theme -> " + captured.Name,
                    Category = "Theme",
                    Shortcut = "",
                    Callback = () => { SelectedTheme = captured; this.DialogResult = DialogResult.OK; this.Close(); }
                });
            }

            if (workspaceFiles != null)
            {
                foreach (string fp in workspaceFiles.Take(50000))
                {
                    string capturedFile = fp;
                    string rel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, capturedFile);
                    string ext = Path.GetExtension(rel).TrimStart('.').ToUpperInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = "FILE";
                    entries.Add(new PaletteEntry
                    {
                        RawText = "≡ " + rel,
                        DisplayTitle = rel,
                        Category = ext,
                        Shortcut = Path.GetFileName(capturedFile),
                        Callback = () =>
                        {
                            SelectedFilePath = capturedFile;
                            if (quickOpenLine > 0)
                            {
                                SelectedLineToJump = quickOpenLine;
                                SelectedColumnToJump = Math.Max(1, quickOpenCol);
                            }
                            this.DialogResult = DialogResult.OK;
                            this.Close();
                        }
                    });
                }
            }

            List<PaletteEntry> filtered = new List<PaletteEntry>();

            resultsList.DrawItem += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= filtered.Count) return;
                PaletteEntry entry = filtered[e.Index];
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = e.Bounds;
                bool isSel = ((e.State & DrawItemState.Selected) == DrawItemState.Selected);

                Color selBg = UiTokens.ListActiveSelection(currentTheme, currentTheme.BgSidebar);
                using (SolidBrush bg = new SolidBrush(isSel ? selBg : currentTheme.BgSidebar))
                    g.FillRectangle(bg, r);

                // Category label: quiet secondary text (VS Code "File: New Text File"), no colored badge.
                Rectangle catRect = new Rectangle(r.X + 10, r.Y + 6, 90, r.Height - 12);
                TextRenderer.DrawText(g, entry.Category, resultsList.Font, catRect, currentTheme.FgSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                int shortcutW = 0;
                if (!string.IsNullOrEmpty(entry.Shortcut) && entry.Category != entry.Shortcut)
                {
                    using (Font sf = new Font("Segoe UI", UiTokens.Pt(UiTokens.FontSmall - 1), FontStyle.Regular))
                    {
                        Size sz = TextRenderer.MeasureText(g, entry.Shortcut, sf);
                        shortcutW = sz.Width + 12;
                        Rectangle keyRect = new Rectangle(r.Right - shortcutW - 10, r.Y + 6, shortcutW, r.Height - 12);
                        using (SolidBrush kb = new SolidBrush(UiTokens.ButtonSecondaryBackground(currentTheme)))
                            g.FillRectangle(kb, keyRect);
                        TextRenderer.DrawText(g, entry.Shortcut, sf, keyRect, currentTheme.FgSecondary,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                }

                Rectangle titleRect = new Rectangle(catRect.Right + 10, r.Y, Math.Max(40, r.Width - catRect.Right - shortcutW - 26), r.Height);
                TextRenderer.DrawText(g, entry.DisplayTitle, resultsList.Font, titleRect,
                    currentTheme.FgPrimary,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };

            Func<string, string, int> fuzzyScore = (candidate, query) =>
            {
                if (string.IsNullOrEmpty(query)) return 1;
                if (string.IsNullOrEmpty(candidate)) return 0;
                string cLow = candidate.ToLowerInvariant();
                string qLow = query.ToLowerInvariant();
                int exactIdx = cLow.IndexOf(qLow, StringComparison.Ordinal);
                if (exactIdx >= 0)
                    return 1000 - exactIdx * 2 - Math.Min(200, cLow.Length);
                int qi = 0;
                int score = 100;
                int lastMatch = -2;
                for (int ci = 0; ci < cLow.Length && qi < qLow.Length; ci++)
                {
                    if (cLow[ci] == qLow[qi])
                    {
                        score += (ci == lastMatch + 1) ? 15 : 5;
                        if (ci == 0 || cLow[ci - 1] == '/' || cLow[ci - 1] == '\\' || cLow[ci - 1] == '_' || cLow[ci - 1] == '-' || char.IsUpper(candidate[ci]))
                            score += 12;
                        lastMatch = ci;
                        qi++;
                    }
                }
                return qi == qLow.Length ? Math.Max(1, score - cLow.Length) : 0;
            };

            Action refreshList = () =>
            {
                string rawQ = searchBox.Text.Trim();
                quickOpenLine = 0;
                quickOpenCol = 0;
                if (rawQ == ">")
                {
                    filtered = entries.Where(x => x.RawText.StartsWith(">")).Take(60).ToList();
                }
                else if (rawQ.StartsWith(">"))
                {
                    string cmdQ = rawQ.Substring(1).Trim().ToLowerInvariant();
                    filtered = entries
                        .Where(x => x.RawText.StartsWith(">"))
                        .Select(x => new { Entry = x, Score = fuzzyScore(x.RawText, cmdQ) })
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Entry)
                        .Take(60)
                        .ToList();
                }
                else if (rawQ.StartsWith("@"))
                {
                    string symQ = rawQ.Substring(1).Trim().ToLowerInvariant();
                    filtered = entries
                        .Where(x => x.RawText.StartsWith("@"))
                        .Select(x => new { Entry = x, Score = fuzzyScore(x.DisplayTitle, symQ) })
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Entry)
                        .Take(60)
                        .ToList();
                }
                else if (rawQ.StartsWith("#"))
                {
                    string symQ = rawQ.Substring(1).Trim().ToLowerInvariant();
                    filtered = entries
                        .Where(x => x.RawText.StartsWith("#"))
                        .Select(x => new { Entry = x, Score = fuzzyScore(x.DisplayTitle, symQ) })
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Entry)
                        .Take(60)
                        .ToList();
                }
                else if (rawQ.StartsWith(":"))
                {
                    string lnPart = rawQ.Substring(1).Trim();
                    string[] parts = lnPart.Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    int parsedLn = 1;
                    int parsedCol = 1;
                    bool validLn = parts.Length >= 1 && int.TryParse(parts[0].Trim(), out parsedLn);
                    if (parts.Length >= 2) int.TryParse(parts[1].Trim(), out parsedCol);
                    int totalLines = (activeTab != null && activeTab.Lines != null) ? activeTab.Lines.Count : 1;
                    string msg = validLn
                        ? string.Format("Go to line {0} and character {1} (1 - {2})", parsedLn, parsedCol, totalLines)
                        : string.Format("Type a line number between 1 and {0} to navigate to (e.g. :12 or :12:5)", totalLines);
                    filtered = new List<PaletteEntry>
                    {
                        new PaletteEntry
                        {
                            RawText = rawQ,
                            DisplayTitle = msg,
                            Category = "LINE",
                            Shortcut = validLn ? ("Ln " + parsedLn + ", Col " + parsedCol) : "",
                            Callback = () =>
                            {
                                if (validLn)
                                {
                                    SelectedLineToJump = parsedLn;
                                    SelectedColumnToJump = parsedCol;
                                    this.DialogResult = DialogResult.OK;
                                    this.Close();
                                }
                            }
                        }
                    };
                }
                else if (string.IsNullOrEmpty(rawQ) && recentItems != null && recentItems.Count > 0)
                {
                    filtered = entries.Where(x => x.RawText.StartsWith("recent:")).Take(60).ToList();
                }
                else if (string.IsNullOrEmpty(rawQ))
                {
                    filtered = entries.OrderBy(x => x.RawText.StartsWith("≡") ? 0 : 1).Take(60).ToList();
                }
                else
                {
                    int qoL;
                    int qoC;
                    string fileQ = WorkspaceSearchEngine.SplitQuickOpenLineSuffix(rawQ, out qoL, out qoC);
                    quickOpenLine = qoL;
                    quickOpenCol = qoC;
                    string q = fileQ.ToLowerInvariant();
                    filtered = entries
                        .Select(x => new { Entry = x, Score = fuzzyScore(x.DisplayTitle + " " + x.RawText, q) + (x.RawText.StartsWith("≡") ? 20 : 0) })
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Entry)
                        .Take(60)
                        .ToList();
                }

                resultsList.Items.Clear();
                foreach (PaletteEntry item in filtered)
                    resultsList.Items.Add(item.DisplayTitle);
                if (resultsList.Items.Count > 0)
                    resultsList.SelectedIndex = 0;
            };

            searchBox.TextChanged += (s, e) => refreshList();
            searchBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string raw = searchBox.Text.Trim();
                    if (raw.StartsWith(":"))
                    {
                        string[] parts = raw.Substring(1).Split(new[] { ':', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        int jumpLn;
                        if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out jumpLn))
                        {
                            SelectedLineToJump = jumpLn;
                            int jumpCol;
                            if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out jumpCol))
                                SelectedColumnToJump = jumpCol;
                            this.DialogResult = DialogResult.OK;
                            this.Close();
                            return;
                        }
                    }
                    if (resultsList.SelectedIndex >= 0 && resultsList.SelectedIndex < filtered.Count)
                        filtered[resultsList.SelectedIndex].Callback();
                }
                else if (e.KeyCode == Keys.Down && resultsList.SelectedIndex + 1 < resultsList.Items.Count)
                {
                    e.SuppressKeyPress = true;
                    resultsList.SelectedIndex++;
                }
                else if (e.KeyCode == Keys.Up && resultsList.SelectedIndex > 0)
                {
                    e.SuppressKeyPress = true;
                    resultsList.SelectedIndex--;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    this.DialogResult = DialogResult.Cancel;
                    this.Close();
                }
            };

            resultsList.DoubleClick += (s, e) =>
            {
                if (resultsList.SelectedIndex >= 0 && resultsList.SelectedIndex < filtered.Count)
                    filtered[resultsList.SelectedIndex].Callback();
            };
            // VS Code: a single click picks the row (was double-click only).
            resultsList.ItemClicked += (s, e) =>
            {
                if (resultsList.SelectedIndex >= 0 && resultsList.SelectedIndex < filtered.Count)
                    filtered[resultsList.SelectedIndex].Callback();
            };

            Action positionTopCenter = () =>
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                int x = area.Left + (area.Width - this.Width) / 2;
                int y = area.Top + 46;
                if (this.Owner != null)
                {
                    x = this.Owner.Left + (this.Owner.Width - this.Width) / 2;
                    y = this.Owner.Top + 44;
                }
                x = Math.Max(area.Left + 8, Math.Min(area.Right - this.Width - 8, x));
                y = Math.Max(area.Top + 8, Math.Min(area.Bottom - this.Height - 8, y));
                this.Location = new Point(x, y);
            };

            this.Load += (s, e) => positionTopCenter();
            this.Shown += (s, e) =>
            {
                positionTopCenter();
                searchBox.Focus();
                searchBox.SelectionStart = searchBox.Text.Length;
            };

            this.Controls.Add(resultsList);
            this.Controls.Add(footerHint);
            this.Controls.Add(headerWrap);
            refreshList();
        }
    }

    /// <summary>
    /// Beats Cursor: Pits two specialized LLM Harnesses side-by-side in parallel on the active buffer,
    /// benchmarks latency + syntax verifier diagnostics, and lets the user apply the winning patch in 1 click.
    /// </summary>
    public class MultiHarnessArenaDialog : CodeLessChromelessForm
    {
        public string WinningCodeToStage = null;
        public string WinningHarnessLabel = null;
        public Button RaceButton = null;
        public CodeLessStreamView OutBoxA = null;
        public CodeLessStreamView OutBoxB = null;

        public MultiHarnessArenaDialog(
            LlmHarnessAndAgentEngine engine,
            EditorTabBuffer activeTab,
            ByokVendor activeVendor,
            StudioTheme theme
        )
        {
            this.Text = "codeLess Multi-Harness Parallel Arena — Side-by-Side Model & Harness Race";
            this.Size = new Size(1060, 690);
            this.MinimumSize = new Size(640, 420);
            this.EnableEdgeResize = true;
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = theme.BgSidebar, Padding = new Padding(12) };
            topPanel.Paint += (s, e) =>
            {
                using (Pen bp = new Pen(theme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(bp, 0, topPanel.Height - 1, topPanel.Width, topPanel.Height - 1);
            };

            Label promptLbl = new Label
            {
                Text = "Arena Engineering Task (Runs two coding harnesses in parallel & auto-verifies AST syntax):",
                Location = new Point(14, 10),
                AutoSize = true,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.1f, FontStyle.Bold),
                ForeColor = theme.AccentPrimary
            };

            CodeLessDarkInputBox promptBox = new CodeLessDarkInputBox
            {
                Location = new Point(14, 34),
                Size = new Size(462, 28),
                Placeholder = "Enter prompt for parallel harness race...",
                Font = new Font("Segoe UI", 9.3f),
                Text = "Add comprehensive error handling, input validation, and performance telemetry"
            };
            promptBox.ApplyTheme(theme);

            CodeLessDropdown harnessACombo = new CodeLessDropdown
            {
                Location = new Point(488, 34),
                Width = 198,
                Height = 28,
                BackColor = theme.BgElevated,
                ForeColor = theme.FgPrimary,
                Font = new Font("Segoe UI Semibold", 8.8f)
            };
            harnessACombo.ApplyTheme(theme);
            CodeLessDropdown harnessBCombo = new CodeLessDropdown
            {
                Location = new Point(696, 34),
                Width = 198,
                Height = 28,
                BackColor = theme.BgElevated,
                ForeColor = theme.FgPrimary,
                Font = new Font("Segoe UI Semibold", 8.8f)
            };
            harnessBCombo.ApplyTheme(theme);
            foreach (var h in engine.Harnesses)
            {
                harnessACombo.Items.Add(h.Name);
                harnessBCombo.Items.Add(h.Name);
            }
            harnessACombo.SelectedIndex = 0;
            harnessBCombo.SelectedIndex = Math.Min(5, engine.Harnesses.Count - 1);

            Button raceBtn = new Button
            {
                Text = "Race Harnesses",
                Location = new Point(906, 33),
                Size = new Size(134, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            raceBtn.FlatAppearance.BorderSize = 0;
            this.RaceButton = raceBtn;
            this.AcceptButton = raceBtn;

            topPanel.Controls.AddRange(new Control[] { promptLbl, promptBox, harnessACombo, harnessBCombo, raceBtn });
            Action layoutArenaTop = () =>
            {
                int w = Math.Max(480, topPanel.ClientSize.Width);
                int btnW = w < 720 ? 112 : 134;
                int comboW = w < 720 ? Math.Max(110, (w - btnW - 220) / 2) : 198;
                raceBtn.Width = btnW;
                raceBtn.Location = new Point(w - raceBtn.Width - 14, 33);
                harnessBCombo.Width = comboW;
                harnessBCombo.Location = new Point(raceBtn.Left - harnessBCombo.Width - 8, 34);
                harnessACombo.Width = comboW;
                harnessACombo.Location = new Point(harnessBCombo.Left - harnessACombo.Width - 8, 34);
                promptBox.Width = Math.Max(140, harnessACombo.Left - promptBox.Left - 10);
            };
            topPanel.Resize += (s, e) => layoutArenaTop();

            TableLayoutPanel splitGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = theme.BorderSubtle,
                Padding = new Padding(1)
            };
            splitGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            splitGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            splitGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            Panel leftPane = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 1, 0), BackColor = theme.BgEditor };
            Panel rightPane = new Panel { Dock = DockStyle.Fill, Margin = new Padding(1, 0, 0, 0), BackColor = theme.BgEditor };

            // Left Contender (Harness A)
            Label headerA = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = theme.BgActivity,
                ForeColor = theme.AccentPrimary,
                Font = new Font("Consolas", 9.1f, FontStyle.Bold),
                Padding = new Padding(10, 7, 10, 4),
                UseMnemonic = false,
                Text = "CONTENDER A: " + harnessACombo.Text
            };
            CodeLessStreamView boxA = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                ReadOnly = true,
                Text = "[Contender A Ready]\nClick 'Race Harnesses' above to execute this harness in parallel and benchmark latency + AST syntax diagnostics."
            };
            boxA.ApplyTheme(theme);
            this.OutBoxA = boxA;
            Button acceptABtn = new Button
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Text = "Stage Contender A Patch in Editor",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            acceptABtn.FlatAppearance.BorderSize = 0;
            leftPane.Controls.Add(boxA);
            leftPane.Controls.Add(acceptABtn);
            leftPane.Controls.Add(headerA);

            // Right Contender (Harness B)
            Label headerB = new Label
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = theme.BgActivity,
                ForeColor = theme.AccentSecondary,
                Font = new Font("Consolas", 9.1f, FontStyle.Bold),
                Padding = new Padding(10, 7, 10, 4),
                UseMnemonic = false,
                Text = "CONTENDER B: " + harnessBCombo.Text
            };
            CodeLessStreamView boxB = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                ReadOnly = true,
                Text = "[Contender B Ready]\nClick 'Race Harnesses' above to execute this harness in parallel and benchmark latency + AST syntax diagnostics."
            };
            boxB.ApplyTheme(theme);
            this.OutBoxB = boxB;
            Button acceptBBtn = new Button
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                Text = "Stage Contender B Patch in Editor",
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold),
                Enabled = false,
                Cursor = Cursors.Hand
            };
            acceptBBtn.FlatAppearance.BorderSize = 0;
            rightPane.Controls.Add(boxB);
            rightPane.Controls.Add(acceptBBtn);
            rightPane.Controls.Add(headerB);

            splitGrid.Controls.Add(leftPane, 0, 0);
            splitGrid.Controls.Add(rightPane, 1, 0);

            string candidateCodeA = null;
            string candidateCodeB = null;

            raceBtn.Click += async (s, e) =>
            {
                if (activeTab == null)
                {
                    MessageBox.Show("Open a code file in the editor first to run a Multi-Harness Arena race.", "codeLess Arena");
                    return;
                }
                raceBtn.Enabled = false;
                raceBtn.Text = "Racing...";
                headerA.Text = "Running " + harnessACombo.Text + "...";
                headerB.Text = "Running " + harnessBCombo.Text + "...";

                string taskPrompt = promptBox.Text.Trim();
                LlmHarnessProfile hA = engine.Harnesses[Math.Max(0, harnessACombo.SelectedIndex)];
                LlmHarnessProfile hB = engine.Harnesses[Math.Max(0, harnessBCombo.SelectedIndex)];

                var swA = Stopwatch.StartNew();
                Task<AgentExecutionTurnResult> tA = engine.RunAutonomousAgentLoopAsync(
                    "ctrl_k",
                    taskPrompt + " [" + hA.Id + "]",
                    activeVendor,
                    hA.Id,
                    activeTab,
                    "",
                    new List<string>(),
                    new List<string>(),
                    ""
                );

                var swB = Stopwatch.StartNew();
                Task<AgentExecutionTurnResult> tB = engine.RunAutonomousAgentLoopAsync(
                    "ctrl_k",
                    taskPrompt + " [" + hB.Id + "] [arena-contender-b]",
                    activeVendor,
                    hB.Id,
                    activeTab,
                    "",
                    new List<string>(),
                    new List<string>(),
                    ""
                );

                await Task.WhenAll(tA, tB);
                swA.Stop();
                swB.Stop();

                bool hasStagedA = tA.Result.StagedFileContents != null && tA.Result.StagedFileContents.Count > 0;
                bool hasStagedB = tB.Result.StagedFileContents != null && tB.Result.StagedFileContents.Count > 0;
                candidateCodeA = hasStagedA
                    ? tA.Result.StagedFileContents.Values.First()
                    : (!string.IsNullOrWhiteSpace(tA.Result.AssistantReply) ? tA.Result.AssistantReply : activeTab.Content);
                candidateCodeB = hasStagedB
                    ? tB.Result.StagedFileContents.Values.First()
                    : (!string.IsNullOrWhiteSpace(tB.Result.AssistantReply) ? tB.Result.AssistantReply : activeTab.Content);

                var tempBufA = new EditorTabBuffer { Content = hasStagedA ? candidateCodeA : (activeTab.Content ?? ""), FileName = activeTab.FileName, Language = activeTab.Language };
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tempBufA);
                int errsA = tempBufA.Diagnostics.Count(d => d.Severity == "Error");

                var tempBufB = new EditorTabBuffer { Content = hasStagedB ? candidateCodeB : (activeTab.Content ?? ""), FileName = activeTab.FileName, Language = activeTab.Language };
                LlmHarnessAndAgentEngine.AnalyzeBufferAstAndDiagnostics(tempBufB);
                int errsB = tempBufB.Diagnostics.Count(d => d.Severity == "Error");

                headerA.Text = hasStagedA
                    ? string.Format("{0}  •  {1} ms  •  {2} AST Errors", hA.Name, swA.ElapsedMilliseconds, errsA)
                    : string.Format("{0}  •  {1}", hA.Name, tA.Result.ProviderUsed);
                headerB.Text = hasStagedB
                    ? string.Format("{0}  •  {1} ms  •  {2} AST Errors", hB.Name, swB.ElapsedMilliseconds, errsB)
                    : string.Format("{0}  •  {1}", hB.Name, tB.Result.ProviderUsed);

                boxA.Text = candidateCodeA;
                boxB.Text = candidateCodeB;
                acceptABtn.Enabled = hasStagedA;
                acceptBBtn.Enabled = hasStagedB;
                raceBtn.Enabled = true;
                raceBtn.Text = "Race Harnesses";
            };

            acceptABtn.Click += (s, e) =>
            {
                WinningCodeToStage = candidateCodeA;
                WinningHarnessLabel = harnessACombo.Text;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            acceptBBtn.Click += (s, e) =>
            {
                WinningCodeToStage = candidateCodeB;
                WinningHarnessLabel = harnessBCombo.Text;
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            Panel titleBar = BuildDarkDialogTitleBar("codeLess Multi-Harness Parallel Arena — Side-by-Side Model & Harness Race", theme, false, true);
            this.Controls.Add(splitGrid);
            this.Controls.Add(topPanel);
            this.Controls.Add(titleBar);
        }
    }

    /// <summary>
    /// Custom-painted interactive node graph parsing cross-file imports and AST symbol references
    /// with curved Bezier dependency edges.
    /// </summary>
    public class ArchitectureGraphDialog : CodeLessChromelessForm
    {
        public string SelectedFilePathToOpen = null;
        public int SelectedLineToJump = 1;
        public int DetectedEdgeCount { get; private set; }

        private class GraphNode
        {
            public int Index;
            public string Title;
            public string RelativePath;
            public string Subtitle;
            public string FilePath;
            public string ModuleStem;
            public int Line;
            public Rectangle Bounds;
            public Color Accent;
            public List<int> OutgoingIndices = new List<int>();
            public List<int> IncomingIndices = new List<int>();
        }

        private class GraphEdge
        {
            public int FromIndex;
            public int ToIndex;
            public string Reason;
        }

        public ArchitectureGraphDialog(
            LlmHarnessAndAgentEngine engine,
            List<string> workspaceFiles,
            string workspaceRoot,
            StudioTheme theme
        )
        {
            this.Text = "codeLess Interactive Architecture & Symbol Dependency Radar (Click any node to jump)";
            this.Size = new Size(1060, 680);
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;
            this.DoubleBuffered = true;

            // Prioritize primary source files cleanly up to 16 modules
            HashSet<string> codeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".py", ".ts", ".tsx", ".js", ".mjs", ".rs", ".go", ".ps1"
            };
            List<string> topFiles = workspaceFiles
                .OrderByDescending(f => codeExts.Contains(Path.GetExtension(f)) ? 1 : 0)
                .ThenBy(f => f.Length)
                .Take(16)
                .ToList();

            List<GraphNode> nodes = new List<GraphNode>();
            Dictionary<int, List<string>> nodeDefinedSymbols = new Dictionary<int, List<string>>();
            Dictionary<int, string> nodeSourceTexts = new Dictionary<int, string>();

            for (int i = 0; i < topFiles.Count; i++)
            {
                string fp = topFiles[i];
                string rel = LlmHarnessAndAgentEngine.GetRelativePath(workspaceRoot, fp);
                var syms = engine.WorkspaceSymbolMap
                    .Where(s => string.Equals(s.FilePath, fp, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                List<string> symNames = syms
                    .Select(s => s.SymbolName)
                    .Where(n => !string.IsNullOrEmpty(n) && n.Length >= 4)
                    .Distinct(StringComparer.Ordinal)
                    .Take(14)
                    .ToList();
                nodeDefinedSymbols[i] = symNames;

                string sub = syms.Count > 0
                    ? string.Join(", ", syms.Take(3).Select(s => s.SymbolName))
                    : Path.GetExtension(fp).ToUpperInvariant() + " Module";

                string bodySample = "";
                try
                {
                    if (File.Exists(fp))
                    {
                        using (StreamReader sr = new StreamReader(fp, Encoding.UTF8, true))
                        {
                            char[] buf = new char[24576];
                            int read = sr.Read(buf, 0, buf.Length);
                            if (read > 0) bodySample = new string(buf, 0, read);
                        }
                    }
                }
                catch { }
                nodeSourceTexts[i] = bodySample;

                Color colAccent = (i % 3 == 0) ? theme.AccentPrimary : ((i % 3 == 1) ? theme.AccentSecondary : theme.TokenFunction);
                nodes.Add(new GraphNode
                {
                    Index = i,
                    Title = Path.GetFileName(fp),
                    RelativePath = rel,
                    Subtitle = sub,
                    FilePath = fp,
                    ModuleStem = Path.GetFileNameWithoutExtension(fp),
                    Line = syms.Count > 0 ? syms[0].Line : 1,
                    Accent = colAccent
                });
            }

            // Parse real cross-file imports & AST symbol references
            List<GraphEdge> edges = new List<GraphEdge>();
            HashSet<long> edgeSeen = new HashSet<long>();
            Regex importRx = new Regex(
                @"(?:^\s*(?:from\s+([A-Za-z0-9_\.]+)\s+import|import\s+([A-Za-z0-9_\.]+)|using\s+([A-Za-z0-9_\.]+)\s*;)|require\(\s*['""]([^'""]+)['""]\s*\)|import\s+.*?\s+from\s+['""]([^'""]+)['""])",
                RegexOptions.Multiline | RegexOptions.Compiled
            );

            for (int i = 0; i < nodes.Count; i++)
            {
                string src = nodeSourceTexts.ContainsKey(i) ? nodeSourceTexts[i] : "";
                if (string.IsNullOrEmpty(src)) continue;

                HashSet<string> importedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in importRx.Matches(src))
                {
                    for (int gIdx = 1; gIdx <= 5; gIdx++)
                    {
                        if (m.Groups[gIdx].Success && !string.IsNullOrEmpty(m.Groups[gIdx].Value))
                        {
                            string tok = m.Groups[gIdx].Value.Trim();
                            string leaf = tok.Replace("./", "").Replace("../", "").Split(new[] { '/', '.', ':' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                            if (!string.IsNullOrEmpty(leaf)) importedTargets.Add(leaf);
                        }
                    }
                }

                for (int j = 0; j < nodes.Count; j++)
                {
                    if (i == j) continue;
                    string reason = null;

                    if (importedTargets.Contains(nodes[j].ModuleStem))
                    {
                        reason = "import " + nodes[j].ModuleStem;
                    }
                    else if (nodes[j].ModuleStem.Length >= 5 &&
                             Regex.IsMatch(src, @"\b" + Regex.Escape(nodes[j].ModuleStem) + @"\b"))
                    {
                        reason = "ref " + nodes[j].ModuleStem;
                    }
                    else
                    {
                        List<string> targetSyms = nodeDefinedSymbols[j];
                        foreach (string sym in targetSyms)
                        {
                            if (Regex.IsMatch(src, @"\b" + Regex.Escape(sym) + @"\b"))
                            {
                                reason = "uses " + sym;
                                break;
                            }
                        }
                    }

                    if (reason != null)
                    {
                        long key = ((long)i << 16) | (uint)j;
                        if (!edgeSeen.Contains(key))
                        {
                            edgeSeen.Add(key);
                            edges.Add(new GraphEdge { FromIndex = i, ToIndex = j, Reason = reason });
                            nodes[i].OutgoingIndices.Add(j);
                            nodes[j].IncomingIndices.Add(i);
                        }
                    }
                }
            }
            this.DetectedEdgeCount = edges.Count;

            Panel topBanner = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = theme.BgSidebar,
                Padding = new Padding(12, 8, 12, 8)
            };
            topBanner.Paint += (s, e) =>
            {
                using (Pen bp = new Pen(theme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(bp, 0, topBanner.Height - 1, topBanner.Width, topBanner.Height - 1);
            };

            Label topInfoLbl = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                ForeColor = theme.FgPrimary,
                Font = new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                Text = string.Format(
                    "Live Repository Dependency Radar — {0} Modules • {1} Cross-File Edges • {2} Symbols (Hover to inspect, click to jump)",
                    nodes.Count, edges.Count, engine.WorkspaceSymbolMap.Count)
            };

            CodeLessDarkInputBox filterBox = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Right,
                Width = 240,
                Placeholder = "Filter modules or symbols...",
                Font = new Font("Segoe UI", 8.8f)
            };
            filterBox.ApplyTheme(theme);

            Panel gapSpacer = new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent };
            topBanner.Controls.Add(topInfoLbl);
            topBanner.Controls.Add(gapSpacer);
            topBanner.Controls.Add(filterBox);

            Panel graphCanvas = new Panel { Dock = DockStyle.Fill, BackColor = theme.BgEditor, Cursor = Cursors.Hand };
            typeof(Panel).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, graphCanvas, new object[] { true });

            Action layoutNodes = () =>
            {
                int count = nodes.Count;
                if (count == 0) return;
                int cols = (count <= 6) ? Math.Min(3, count) : 4;
                int rows = (int)Math.Ceiling((double)count / cols);
                int padX = 28;
                int padY = 30;
                int gapX = 34;
                int gapY = 44;
                int availW = Math.Max(420, graphCanvas.ClientSize.Width - padX * 2);
                int availH = Math.Max(260, graphCanvas.ClientSize.Height - padY * 2 - 22);
                int nodeW = Math.Max(190, Math.Min(252, (availW - gapX * (cols - 1)) / cols));
                int nodeH = Math.Max(108, Math.Min(124, (availH - gapY * Math.Max(0, rows - 1)) / Math.Max(1, rows)));

                int totalW = cols * nodeW + Math.Max(0, cols - 1) * gapX;
                int startX = Math.Max(padX, (graphCanvas.ClientSize.Width - totalW) / 2);

                for (int i = 0; i < count; i++)
                {
                    int r = i / cols;
                    int c = i % cols;
                    int x = startX + c * (nodeW + gapX);
                    int y = padY + r * (nodeH + gapY);
                    nodes[i].Bounds = new Rectangle(x, y, nodeW, nodeH);
                }
            };

            layoutNodes();
            graphCanvas.Resize += (s, e) => { layoutNodes(); graphCanvas.Invalidate(); };

            int hoveredIdx = -1;
            string activeFilter = "";

            filterBox.TextChanged += (s, e) =>
            {
                activeFilter = filterBox.Text.Trim().ToLowerInvariant();
                graphCanvas.Invalidate();
            };

            Func<int, bool> nodeMatchesFilter = idx =>
            {
                if (string.IsNullOrEmpty(activeFilter)) return true;
                GraphNode gn = nodes[idx];
                return gn.Title.ToLowerInvariant().Contains(activeFilter) ||
                       gn.RelativePath.ToLowerInvariant().Contains(activeFilter) ||
                       gn.Subtitle.ToLowerInvariant().Contains(activeFilter);
            };

            graphCanvas.Paint += (s, e) =>
            {
                if (nodes.Count > 0 && nodes[0].Bounds.Width <= 0)
                    layoutNodes();

                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // Subtle architectural dot grid
                using (SolidBrush dotB = new SolidBrush(Color.FromArgb(28, theme.FgMuted)))
                {
                    for (int gx = 18; gx < graphCanvas.Width; gx += 32)
                        for (int gy = 18; gy < graphCanvas.Height; gy += 32)
                            g.FillRectangle(dotB, gx, gy, 2, 2);
                }

                // Draw curved Bezier dependency edges with directional arrowheads
                foreach (GraphEdge edge in edges)
                {
                    if (edge.FromIndex < 0 || edge.FromIndex >= nodes.Count || edge.ToIndex < 0 || edge.ToIndex >= nodes.Count) continue;
                    GraphNode nFrom = nodes[edge.FromIndex];
                    GraphNode nTo = nodes[edge.ToIndex];

                    bool involvesHover = (hoveredIdx >= 0) && (edge.FromIndex == hoveredIdx || edge.ToIndex == hoveredIdx);
                    bool involvesFilter = nodeMatchesFilter(edge.FromIndex) && nodeMatchesFilter(edge.ToIndex);

                    int alpha = 135;
                    Color edgeCol = theme.AccentPrimary;
                    float width = 1.65f;

                    if (hoveredIdx >= 0)
                    {
                        if (edge.FromIndex == hoveredIdx)
                        {
                            alpha = 245;
                            edgeCol = theme.AccentPrimary;
                            width = 2.5f;
                        }
                        else if (edge.ToIndex == hoveredIdx)
                        {
                            alpha = 245;
                            edgeCol = theme.AccentSecondary;
                            width = 2.5f;
                        }
                        else
                        {
                            alpha = 24;
                            width = 1.0f;
                        }
                    }
                    else if (!involvesFilter)
                    {
                        alpha = 24;
                    }

                    PointF pStart = new PointF(nFrom.Bounds.X + nFrom.Bounds.Width / 2f, nFrom.Bounds.Y + nFrom.Bounds.Height / 2f);
                    PointF pEnd = new PointF(nTo.Bounds.X + nTo.Bounds.Width / 2f, nTo.Bounds.Y + nTo.Bounds.Height / 2f);

                    float dx = pEnd.X - pStart.X;
                    float dy = pEnd.Y - pStart.Y;
                    float curveOffset = (Math.Abs(dy) < 20) ? -58f : (Math.Abs(dx) < 20 ? 52f : 32f);
                    PointF c1 = new PointF(pStart.X + dx * 0.35f + curveOffset * 0.4f, pStart.Y + dy * 0.15f + curveOffset);
                    PointF c2 = new PointF(pStart.X + dx * 0.65f + curveOffset * 0.4f, pStart.Y + dy * 0.85f + curveOffset);

                    using (Pen edgePen = new Pen(Color.FromArgb(alpha, edgeCol), width))
                    {
                        g.DrawBezier(edgePen, pStart, c1, c2, pEnd);
                    }

                    // Directional arrowhead near midpoint of Bezier curve
                    if (alpha >= 60)
                    {
                        float t = 0.54f;
                        float u = 1f - t;
                        float mx = u * u * u * pStart.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * pEnd.X;
                        float my = u * u * u * pStart.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * pEnd.Y;
                        float tx = 3 * u * u * (c1.X - pStart.X) + 6 * u * t * (c2.X - c1.X) + 3 * t * t * (pEnd.X - c2.X);
                        float ty = 3 * u * u * (c1.Y - pStart.Y) + 6 * u * t * (c2.Y - c1.Y) + 3 * t * t * (pEnd.Y - c2.Y);
                        double ang = Math.Atan2(ty, tx);
                        float aLen = involvesHover ? 9.5f : 7.8f;
                        PointF tip = new PointF(mx, my);
                        PointF w1 = new PointF(mx - (float)(aLen * Math.Cos(ang - 0.45)), my - (float)(aLen * Math.Sin(ang - 0.45)));
                        PointF w2 = new PointF(mx - (float)(aLen * Math.Cos(ang + 0.45)), my - (float)(aLen * Math.Sin(ang + 0.45)));
                        using (SolidBrush ab = new SolidBrush(Color.FromArgb(Math.Min(255, alpha + 45), edgeCol)))
                            g.FillPolygon(ab, new PointF[] { tip, w1, w2 });
                    }
                }

                // Draw module cards
                using (Font fTitle = new Font("Consolas", 9.3f, FontStyle.Bold))
                using (Font fSub = new Font("Segoe UI", 8.0f))
                using (Font fBadge = new Font("Consolas", 7.8f, FontStyle.Bold))
                {
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        GraphNode n = nodes[i];
                        bool hov = (i == hoveredIdx);
                        bool match = nodeMatchesFilter(i);
                        bool neighborOfHover = (hoveredIdx >= 0) &&
                            (nodes[hoveredIdx].OutgoingIndices.Contains(i) || nodes[hoveredIdx].IncomingIndices.Contains(i));

                        int cardAlpha = (!match) ? 85 : ((hoveredIdx >= 0 && !hov && !neighborOfHover) ? 140 : 255);
                        Color cardFill = hov ? theme.BgHover : theme.BgSidebar;

                        using (SolidBrush cardBg = new SolidBrush(Color.FromArgb(cardAlpha, cardFill)))
                            g.FillRectangle(cardBg, n.Bounds);
                        using (SolidBrush topStripe = new SolidBrush(Color.FromArgb(cardAlpha, n.Accent)))
                            g.FillRectangle(topStripe, n.Bounds.X, n.Bounds.Y, n.Bounds.Width, 4);

                        Color borderCol = hov ? n.Accent : (neighborOfHover ? theme.AccentSecondary : theme.BorderSubtle);
                        using (Pen borderP = new Pen(Color.FromArgb(cardAlpha, borderCol), hov ? 2f : 1f))
                            g.DrawRectangle(borderP, n.Bounds);

                        TextRenderer.DrawText(g, n.Title, fTitle,
                            new Rectangle(n.Bounds.X + 10, n.Bounds.Y + 11, n.Bounds.Width - 20, 20),
                            hov ? theme.FgPrimary : Color.FromArgb(cardAlpha, theme.FgPrimary),
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                        string[] parts = (n.Subtitle ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        string line1 = parts.Length > 0 ? parts[0].Trim() : (n.Subtitle ?? "");
                        string line2 = parts.Length > 1 ? string.Join(", ", parts.Skip(1).Select(p => p.Trim())) : (n.RelativePath ?? "");

                        TextRenderer.DrawText(g, line1, fSub,
                            new Rectangle(n.Bounds.X + 10, n.Bounds.Y + 35, n.Bounds.Width - 20, 17),
                            Color.FromArgb(cardAlpha, theme.FgSecondary),
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        TextRenderer.DrawText(g, line2, fSub,
                            new Rectangle(n.Bounds.X + 10, n.Bounds.Y + 52, n.Bounds.Width - 20, 17),
                            Color.FromArgb(cardAlpha, theme.FgMuted),
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                        string degText = string.Format("out:{0}  in:{1}  @L{2}", n.OutgoingIndices.Count, n.IncomingIndices.Count, n.Line);
                        TextRenderer.DrawText(g, degText, fBadge,
                            new Rectangle(n.Bounds.X + 10, n.Bounds.Bottom - 24, n.Bounds.Width - 20, 18),
                            Color.FromArgb(cardAlpha, n.Accent),
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    }
                }

                // Bottom legend overlay
                using (Font lf = new Font("Segoe UI", 8.0f))
                {
                    Rectangle leg = new Rectangle(18, graphCanvas.Height - 26, Math.Max(200, graphCanvas.Width - 36), 20);
                    TextRenderer.DrawText(g,
                        "Legend:  --> Outgoing Imports/Symbol Calls    --> Incoming Dependents (hover node to isolate)",
                        lf, leg, theme.FgMuted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                }
            };

            graphCanvas.MouseMove += (s, e) =>
            {
                int found = -1;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Bounds.Contains(e.Location)) { found = i; break; }
                }
                if (found != hoveredIdx)
                {
                    hoveredIdx = found;
                    graphCanvas.Invalidate();
                }
            };

            graphCanvas.MouseClick += (s, e) =>
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].Bounds.Contains(e.Location))
                    {
                        SelectedFilePathToOpen = nodes[i].FilePath;
                        SelectedLineToJump = nodes[i].Line;
                        this.DialogResult = DialogResult.OK;
                        this.Close();
                        return;
                    }
                }
            };

            Panel titleBar = BuildDarkDialogTitleBar("codeLess Interactive Architecture & Symbol Dependency Radar", theme, false, true);
            this.Controls.Add(graphCanvas);
            this.Controls.Add(topBanner);
            this.Controls.Add(titleBar);
        }
    }

    /// <summary>
    /// Zero-RAM Disk-Backed Time-Travel Checkpoint Browser & 1-Click Rollback Dialog.
    /// </summary>
    public class TimeTravelCheckpointDialog : CodeLessChromelessForm
    {
        public DiskCheckpointMetadata SelectedCheckpointToRestore = null;

        public TimeTravelCheckpointDialog(EditorTabBuffer activeTab, StudioTheme theme)
            : this(theme)
        {
        }

        public TimeTravelCheckpointDialog(StudioTheme theme)
        {
            this.Text = "codeLess Zero-RAM Time-Travel Checkpoints (Instant Disk-Backed Rollback)";
            this.Size = new Size(880, 548);
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            Panel leftWrap = new Panel
            {
                Dock = DockStyle.Left,
                Width = 322,
                Padding = new Padding(0, 0, 1, 0),
                BackColor = theme.BorderSubtle
            };

            CodeLessListBox cpList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgSidebar,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.0f),
                ItemHeight = 26
            };
            cpList.ApplyTheme(theme);
            leftWrap.Controls.Add(cpList);

            CodeLessStreamView previewBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.2f),
                ReadOnly = true
            };
            previewBox.ApplyTheme(theme);

            Button restoreBtn = new Button
            {
                Dock = DockStyle.Bottom,
                Height = 38,
                Text = "Restore Selected Time-Travel Checkpoint to Editor & Disk",
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            restoreBtn.FlatAppearance.BorderSize = 0;

            foreach (var cp in TimeTravelCheckpointManager.Checkpoints)
            {
                cpList.Items.Add(string.Format("[{0}] {1} ({2}L)", cp.Timestamp, Path.GetFileName(cp.OriginalFilePath), cp.LineCount));
            }
            if (cpList.Items.Count == 0)
            {
                previewBox.Text = "No Time-Travel Checkpoints recorded yet in this session.\n\nCheckpoints are automatically saved to disk (with 0 bytes of RAM overhead) whenever you:\n • Run a Ctrl+K Surgical Inline Edit\n • Apply an AI Agent / Composer patch\n • Click 'Create Manual Time-Travel Snapshot' in the AI Harness menu";
                restoreBtn.Enabled = false;
            }

            cpList.SelectedIndexChanged += (s, e) =>
            {
                int idx = cpList.SelectedIndex;
                if (idx >= 0 && idx < TimeTravelCheckpointManager.Checkpoints.Count)
                {
                    var cp = TimeTravelCheckpointManager.Checkpoints[idx];
                    if (File.Exists(cp.SnapshotDiskPath))
                        previewBox.Text = "// Checkpoint: " + cp.Label + " @ " + cp.Timestamp + "\n// File: " + cp.OriginalFilePath + "\n\n" + File.ReadAllText(cp.SnapshotDiskPath);
                }
            };

            restoreBtn.Click += (s, e) =>
            {
                int idx = cpList.SelectedIndex;
                if (idx >= 0 && idx < TimeTravelCheckpointManager.Checkpoints.Count)
                {
                    SelectedCheckpointToRestore = TimeTravelCheckpointManager.Checkpoints[idx];
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            };

            if (cpList.Items.Count > 0) cpList.SelectedIndex = 0;
            this.AcceptButton = restoreBtn;

            Panel titleBar = BuildDarkDialogTitleBar("codeLess Zero-RAM Time-Travel Checkpoints (Instant Rollback)", theme, false, true);
            this.Controls.Add(previewBox);
            this.Controls.Add(leftWrap);
            this.Controls.Add(restoreBtn);
            this.Controls.Add(titleBar);
        }
    }

    /// <summary>
    /// Built-in Zero-RAM Native HTTP / REST & Latency Playground (replaces heavy Postman / REST extensions).
    /// </summary>
    public class RestApiPlaygroundDialog : CodeLessChromelessForm
    {
        public RestApiPlaygroundDialog(StudioTheme theme)
        {
            this.Text = "codeLess Native HTTP / REST & Latency Benchmarker (Zero-Extension API Studio)";
            this.Size = new Size(900, 584);
            this.BackColor = theme.BgWorkbench;
            this.ForeColor = theme.FgPrimary;

            Panel topBar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = theme.BgSidebar, Padding = new Padding(10) };
            topBar.Paint += (s, e) =>
            {
                using (Pen bp = new Pen(theme.BorderSubtle, 1f))
                    e.Graphics.DrawLine(bp, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            CodeLessDropdown methodCombo = new CodeLessDropdown
            {
                Location = new Point(12, 12),
                Width = 90,
                Height = 28,
                BackColor = theme.BgElevated,
                ForeColor = theme.AccentPrimary,
                Font = new Font("Consolas", 9.5f, FontStyle.Bold)
            };
            methodCombo.ApplyTheme(theme);
            methodCombo.Items.AddRange(new object[] { "GET", "POST", "PUT", "DELETE", "HEAD" });
            methodCombo.SelectedIndex = 0;

            CodeLessDarkInputBox urlBox = new CodeLessDarkInputBox
            {
                Location = new Point(110, 12),
                Size = new Size(524, 28),
                Placeholder = "https://...",
                Font = new Font("Consolas", 9.5f),
                Text = "https://open-vsx.org/api/-/search?query=python&size=3"
            };
            urlBox.ApplyTheme(theme);

            Button sendBtn = new Button
            {
                Text = "▶ Send Request",
                Location = new Point(644, 11),
                Size = new Size(126, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                UseMnemonic = false,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            sendBtn.FlatAppearance.BorderSize = 0;

            Label latencyBadge = new Label
            {
                Location = new Point(778, 17),
                AutoSize = true,
                UseMnemonic = false,
                Font = new Font("Consolas", 8.8f, FontStyle.Bold),
                ForeColor = theme.AccentSecondary,
                Text = "Ready"
            };

            topBar.Controls.AddRange(new Control[] { methodCombo, urlBox, sendBtn, latencyBadge });
            Action layoutRestTopBar = () =>
            {
                int w = Math.Max(420, topBar.ClientSize.Width);
                int sendW = 148;
                int badgeW = 130;
                int urlW = Math.Max(140, w - 118 - sendW - badgeW - 24);
                urlBox.Width = urlW;
                sendBtn.Location = new Point(118 + urlW + 8, 11);
                latencyBadge.Location = new Point(sendBtn.Right + 10, 17);
            };
            topBar.Resize += (s, e) => layoutRestTopBar();
            layoutRestTopBar();

            CodeLessStreamView responseBox = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.2f),
                ReadOnly = true,
                Text = "// Enter any HTTP/REST endpoint above and click '▶ Send Request' to inspect headers, status code, and millisecond latency."
            };
            responseBox.ApplyTheme(theme);

            sendBtn.Click += async (s, e) =>
            {
                sendBtn.Enabled = false;
                latencyBadge.Text = "Sending...";
                string method = methodCombo.Text;
                string url = urlBox.Text.Trim();

                await Task.Run(() =>
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        // Routed through the NetworkLedger (Network Activity panel; refused in Offline Mode unless loopback).
                        LedgerHttpRequest lreq = new LedgerHttpRequest();
                        lreq.Feature = "HTTP Client (REST playground)";
                        lreq.Method = method;
                        lreq.Url = url;
                        lreq.UserAgent = "codeLess-Native-REST/3.0";
                        lreq.TimeoutMs = 10000;
                        LedgerHttpResponse res = LedgerHttp.Send(lreq);
                        if (res.Status == 0) throw new Exception(res.Error);
                        {
                            string body = res.Body ?? "";
                            sw.Stop();
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine(string.Format("HTTP/{0} {1} {2}  •  Latency: {3} ms  •  Payload: {4:N0} bytes",
                                res.ProtocolVersion, res.Status, res.StatusDescription, sw.ElapsedMilliseconds, res.BytesReceived));
                            sb.AppendLine("--------------------------------------------------------------------------------");
                            foreach (KeyValuePair<string, string> hk in res.Headers)
                                sb.AppendLine(hk.Key + ": " + hk.Value);
                            sb.AppendLine("--------------------------------------------------------------------------------\n");
                            sb.AppendLine(body.Length > 12000 ? body.Substring(0, 12000) + "\n... [Truncated]" : body);

                            this.BeginInvoke(new Action(() =>
                            {
                                latencyBadge.Text = string.Format((res.IsSuccess ? "✓" : "✗") + " {0} ({1} ms)", res.Status, sw.ElapsedMilliseconds);
                                responseBox.Text = sb.ToString();
                                sendBtn.Enabled = true;
                            }));
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        this.BeginInvoke(new Action(() =>
                        {
                            latencyBadge.Text = "✗ Failed (" + sw.ElapsedMilliseconds + " ms)";
                            responseBox.Text = "Request Error:\n" + ex.Message;
                            sendBtn.Enabled = true;
                        }));
                    }
                });
            };

            this.AcceptButton = sendBtn;
            Panel titleBar = BuildDarkDialogTitleBar("⊕ codeLess Native HTTP / REST & Latency Benchmarker", theme, false, true);
            this.Controls.Add(responseBox);
            this.Controls.Add(topBar);
            this.Controls.Add(titleBar);
        }
    }

    // =========================================================================
    // 10. IDE SETTINGS & PREFERENCES DIALOG (PERSISTENT SETTINGS + AI SECTION)
    // =========================================================================
    public class SettingsDialog : CodeLessChromelessForm
    {
        private IdePersistentSettings settings;
        private List<StudioTheme> allThemes;
        private List<ByokVendor> allVendors;
        private LlmHarnessAndAgentEngine llmEngine;
        public CodeLessDropdown ThemeCombo;
        public CodeLessDropdown FontFamilyCombo;
        public CodeLessDropdown FontSizeInput;
        public CodeLessDropdown TabSizeInput;
        public CheckBox InsertSpacesBox;
        public CheckBox WordWrapBox;
        public CheckBox MinimapBox;
        public CheckBox GhostTextBox;
        public CheckBox AutoSaveBox;
        public CheckBox AgentApprovalBox;
        public CheckBox ShowWelcomeBox;
        public CheckBox AutoUpdateCheckBox;
        public CheckBox ActivatePythonEnvBox;
        public CheckBox OfflineModeBox;
        public CodeLessDarkInputBox ExcludedFoldersInput;
        public ParitySettingsBlockWin ParityBlock;

        // AI Section controls
        public CodeLessDropdown ProviderCombo;
        public CodeLessDarkInputBox ApiKeyInput;
        public CodeLessDarkInputBox BaseUrlInput;
        public Button TestConnButton;
        public Label TestConnStatusLabel;
        public CodeLessDropdown DefaultAgentModelCombo;
        public CodeLessDropdown DefaultAskModelCombo;
        public CodeLessDropdown DefaultEditModelCombo;
        public CodeLessDropdown TemperatureInput;
        public CheckBox PrivacyModeBox;
        public Action<IdePersistentSettings> OnSettingsLiveChanged;

        public SettingsDialog(IdePersistentSettings currentSettings, List<StudioTheme> themes, StudioTheme activeTheme, List<ByokVendor> vendors = null, LlmHarnessAndAgentEngine engine = null)
        {
            this.settings = currentSettings;
            string origThemeId = currentSettings.ActiveThemeId;
            string origFontFam = currentSettings.EditorFontFamily;
            float origFontSize = currentSettings.EditorFontSize;
            int origTabSize = currentSettings.TabSize;
            bool origInsertSpaces = currentSettings.InsertSpaces;
            bool origWordWrap = currentSettings.WordWrap;
            bool origMinimap = currentSettings.ShowMinimap;
            bool origGhost = currentSettings.EnableInlineGhostText;
            bool origAutoSave = currentSettings.AutoSave;
            bool origApproval = currentSettings.RequireAgentToolApproval;
            bool origWelcome = currentSettings.ShowWelcomeOnStartup;
            bool origActivateVenv = currentSettings.ActivatePythonEnvironmentInTerminal;
            bool origOffline = currentSettings.OfflineMode;
            bool origAutoUpdate = currentSettings.AutoCheckForUpdates;
            string origExcluded = currentSettings.ExcludedFolders;
            string origAgentModel = currentSettings.DefaultAgentModel;
            string origAskModel = currentSettings.DefaultAskModel;
            string origEditModel = currentSettings.DefaultEditModel;
            double origTemp = currentSettings.AiTemperature;
            bool origPrivacy = currentSettings.PrivacyMode;
            this.allThemes = themes;
            this.llmEngine = engine ?? new LlmHarnessAndAgentEngine(currentSettings);
            this.allVendors = vendors ?? this.llmEngine.Vendors;
            this.Text = "codeLess — Settings & AI Preferences";
            this.Size = new Size(760, 680);
            this.MinimumSize = new Size(640, 560);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = activeTheme.BgSidebar;
            this.ForeColor = activeTheme.FgPrimary;
            this.EnableEdgeResize = true;

            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20), BackColor = activeTheme.BgSidebar, AutoScroll = true };

            int y = 10;
            Func<string, int, Label> addSectionHeader = (txt, top) =>
            {
                Label hdr = new Label
                {
                    Location = new Point(22, top),
                    Size = new Size(680, 22),
                    Text = txt,
                    Font = new Font("Segoe UI Semibold", 9.2f, FontStyle.Regular),
                    ForeColor = activeTheme.AccentPrimary,
                    UseMnemonic = false
                };
                body.Controls.Add(hdr);
                return hdr;
            };

            Func<string, int, Label> addLabel = (txt, top) =>
            {
                Label lbl = new Label
                {
                    Location = new Point(22, top + 3),
                    Size = new Size(215, 22),
                    Text = txt,
                    Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                    ForeColor = activeTheme.FgPrimary,
                    UseMnemonic = false
                };
                body.Controls.Add(lbl);
                return lbl;
            };

            addSectionHeader("EDITOR & WORKBENCH PREFERENCES", y);
            y += 26;

            addLabel("Color Theme:", y);
            ThemeCombo = new CodeLessDropdown
            {
                Location = new Point(240, y),
                Size = new Size(260, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.0f)
            };
            ThemeCombo.ApplyTheme(activeTheme);
            int selThemeIdx = 0;
            for (int i = 0; i < themes.Count; i++)
            {
                ThemeCombo.Items.Add(themes[i].Name);
                if (themes[i].Id == settings.ActiveThemeId || themes[i].Name == settings.ActiveThemeId)
                    selThemeIdx = i;
            }
            if (ThemeCombo.Items.Count > 0)
                ThemeCombo.SelectedIndex = selThemeIdx;
            body.Controls.Add(ThemeCombo);
            y += 32;

            addLabel("Editor Font Family & Size:", y);
            FontFamilyCombo = new CodeLessDropdown
            {
                Location = new Point(240, y),
                Size = new Size(220, 26),
                DropDownStyle = ComboBoxStyle.DropDown,
                CustomDialogTitle = "Custom Font Family",
                Font = new Font("Segoe UI", 9.0f)
            };
            FontFamilyCombo.ApplyTheme(activeTheme);
            string[] commonFonts = new string[] { "Consolas", "Cascadia Code", "Courier New", "Lucida Console", "Segoe UI Mono" };
            foreach (string f in commonFonts) FontFamilyCombo.Items.Add(f);
            FontFamilyCombo.Text = string.IsNullOrEmpty(settings.EditorFontFamily) ? "Consolas" : settings.EditorFontFamily;
            body.Controls.Add(FontFamilyCombo);

            FontSizeInput = new CodeLessDropdown
            {
                Location = new Point(470, y),
                Size = new Size(95, 26),
                DropDownStyle = ComboBoxStyle.DropDown,
                CustomDialogTitle = "Custom Font Size (pt)",
                Font = new Font("Consolas", 9.2f)
            };
            FontSizeInput.ApplyTheme(activeTheme);
            string[] commonSizes = new string[] { "8.0", "8.5", "9.0", "9.5", "10.0", "10.5", "11.0", "11.5", "12.0", "13.0", "14.0", "16.0", "18.0", "20.0", "24.0" };
            foreach (string sz in commonSizes) FontSizeInput.Items.Add(sz);
            FontSizeInput.Value = (decimal)Math.Max(7.0f, Math.Min(28.0f, settings.EditorFontSize <= 0 ? 10.5f : settings.EditorFontSize));
            body.Controls.Add(FontSizeInput);

            TabSizeInput = new CodeLessDropdown
            {
                Location = new Point(575, y),
                Size = new Size(90, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Consolas", 9.2f)
            };
            TabSizeInput.ApplyTheme(activeTheme);
            foreach (string ts in new string[] { "1", "2", "4", "8" }) TabSizeInput.Items.Add(ts);
            TabSizeInput.Value = Math.Max(1, Math.Min(8, settings.TabSize <= 0 ? 4 : settings.TabSize));
            body.Controls.Add(TabSizeInput);
            y += 32;

            Func<string, bool, int, int, CheckBox> addCheck = (txt, val, left, top) =>
            {
                CheckBox cb = new CheckBox
                {
                    Location = new Point(left, top),
                    Size = new Size(340, 22),
                    Text = txt,
                    Checked = val,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8.8f),
                    ForeColor = activeTheme.FgPrimary,
                    UseMnemonic = false,
                    Cursor = Cursors.Hand
                };
                body.Controls.Add(cb);
                return cb;
            };

            InsertSpacesBox = addCheck("Insert Spaces on Tab", settings.InsertSpaces, 24, y);
            WordWrapBox = addCheck("Editor Word Wrap (Alt+Z)", settings.WordWrap, 380, y);
            y += 24;
            MinimapBox = addCheck("Show Code Minimap on Right Edge", settings.ShowMinimap, 24, y);
            GhostTextBox = addCheck("Enable Inline Tab Completion Suggestions", settings.EnableInlineGhostText, 380, y);
            y += 24;
            AutoSaveBox = addCheck("Auto Save Modified Files Automatically", settings.AutoSave, 24, y);
            AgentApprovalBox = addCheck("Require Approval Before Agent Runs Commands", settings.RequireAgentToolApproval, 380, y);
            y += 24;
            ShowWelcomeBox = addCheck("Show Welcome page on startup", settings.ShowWelcomeOnStartup, 24, y);
            ActivatePythonEnvBox = addCheck("Activate Python environment in new terminals", settings.ActivatePythonEnvironmentInTerminal, 380, y);
            y += 24;
            OfflineModeBox = addCheck("Offline Mode (block all non-local network requests)", settings.OfflineMode, 24, y);
            AutoUpdateCheckBox = addCheck("Check for updates automatically (once a day)", settings.AutoCheckForUpdates, 380, y);
            AutoSaveBox.Visible = false; // superseded by the files.autoSave mode below
            y += 28;
            ParityBlock = ParitySettingsBlockWin.Build(body, settings, activeTheme, ref y);
            addLabel("Excluded Folders (files.exclude):", y);
            ExcludedFoldersInput = new CodeLessDarkInputBox
            {
                Location = new Point(240, y),
                Size = new Size(425, 26),
                Placeholder = ".git, node_modules, bin, obj, dist, build, .venv",
                Text = string.IsNullOrWhiteSpace(settings.ExcludedFolders) ? IdePersistentSettings.DefaultExcludedFoldersList : settings.ExcludedFolders,
                Font = new Font("Consolas", 8.8f)
            };
            ExcludedFoldersInput.ApplyTheme(activeTheme);
            body.Controls.Add(ExcludedFoldersInput);
            y += 32;

            // AI & BYOK SECTION
            addSectionHeader("AI & BYOK MODEL SETTINGS", y);
            y += 26;

            addLabel("AI Provider:", y);
            ProviderCombo = new CodeLessDropdown
            {
                Location = new Point(240, y),
                Size = new Size(250, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.0f)
            };
            ProviderCombo.ApplyTheme(activeTheme);
            foreach (var v in this.allVendors) ProviderCombo.Items.Add(v.VendorName);
            if (ProviderCombo.Items.Count > 0) ProviderCombo.SelectedIndex = 0;
            body.Controls.Add(ProviderCombo);

            TestConnButton = new Button
            {
                Location = new Point(500, y - 1),
                Size = new Size(165, 28),
                Text = "⚡ Test Connection",
                FlatStyle = FlatStyle.Flat,
                BackColor = activeTheme.BgElevated,
                ForeColor = activeTheme.FgPrimary,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            TestConnButton.FlatAppearance.BorderColor = activeTheme.AccentPrimary;
            body.Controls.Add(TestConnButton);
            y += 32;

            addLabel("API Key (Masked):", y);
            ApiKeyInput = new CodeLessDarkInputBox
            {
                Location = new Point(240, y),
                Size = new Size(425, 26),
                Placeholder = "sk-... / sk-ant-... / AIza...",
                UseSystemPasswordChar = true,
                Font = new Font("Consolas", 9.0f)
            };
            ApiKeyInput.ApplyTheme(activeTheme);
            body.Controls.Add(ApiKeyInput);
            y += 32;

            addLabel("OpenAI-Compatible Base URL:", y);
            BaseUrlInput = new CodeLessDarkInputBox
            {
                Location = new Point(240, y),
                Size = new Size(425, 26),
                Placeholder = "https://api.openai.com/v1/chat/completions",
                Font = new Font("Consolas", 8.8f)
            };
            BaseUrlInput.ApplyTheme(activeTheme);
            body.Controls.Add(BaseUrlInput);
            y += 28;

            TestConnStatusLabel = new Label
            {
                Location = new Point(240, y),
                Size = new Size(460, 20),
                Text = "Ready — configure provider API key or custom OpenAI-compatible endpoint.",
                Font = new Font("Segoe UI", 8.3f),
                ForeColor = activeTheme.FgMuted,
                UseMnemonic = false
            };
            body.Controls.Add(TestConnStatusLabel);
            y += 24;

            Action syncVendorFields = () =>
            {
                int idx = ProviderCombo.SelectedIndex;
                if (idx >= 0 && idx < this.allVendors.Count)
                {
                    ByokVendor v = this.allVendors[idx];
                    ApiKeyInput.Text = v.RuntimeApiKey ?? "";
                    BaseUrlInput.Text = v.EndpointUrl ?? "";
                }
            };
            ProviderCombo.SelectedIndexChanged += (s, e) => syncVendorFields();
            syncVendorFields();

            ApiKeyInput.TextChanged += (s, e) =>
            {
                int idx = ProviderCombo.SelectedIndex;
                if (idx >= 0 && idx < this.allVendors.Count)
                    this.allVendors[idx].RuntimeApiKey = ApiKeyInput.Text.Trim();
            };
            BaseUrlInput.TextChanged += (s, e) =>
            {
                int idx = ProviderCombo.SelectedIndex;
                if (idx >= 0 && idx < this.allVendors.Count)
                    this.allVendors[idx].EndpointUrl = BaseUrlInput.Text.Trim();
            };

            TestConnButton.Click += async (s, e) =>
            {
                int idx = ProviderCombo.SelectedIndex;
                if (idx < 0 || idx >= this.allVendors.Count) return;
                ByokVendor v = this.allVendors[idx];
                v.RuntimeApiKey = ApiKeyInput.Text.Trim();
                if (!string.IsNullOrWhiteSpace(BaseUrlInput.Text))
                    v.EndpointUrl = BaseUrlInput.Text.Trim();

                TestConnStatusLabel.ForeColor = activeTheme.WarnAmber;
                TestConnStatusLabel.Text = "Testing connection to " + v.VendorName + "...";
                try
                {
                    LlmHarnessAndAgentEngine eng = this.llmEngine ?? new LlmHarnessAndAgentEngine(this.settings);
                    string reply = await eng.CallByokVendorAsync(v, "You are a connectivity check assistant.", "Reply with the single word OK.", 0.0);
                    if (!string.IsNullOrEmpty(reply) && (reply.StartsWith("[Network") || reply.StartsWith("[HTTP") || reply.StartsWith("[Offline")))
                    {
                        TestConnStatusLabel.ForeColor = activeTheme.DangerCoral;
                        TestConnStatusLabel.Text = "✗ Error: " + reply.Replace("\r", " ").Replace("\n", " ");
                    }
                    else
                    {
                        TestConnStatusLabel.ForeColor = activeTheme.AccentSecondary;
                        TestConnStatusLabel.Text = "✓ Connected (" + v.ActiveModel + "): " + (reply ?? "OK").Trim();
                    }
                }
                catch (Exception ex)
                {
                    TestConnStatusLabel.ForeColor = activeTheme.DangerCoral;
                    TestConnStatusLabel.Text = "✗ Connection failed: " + ex.Message;
                }
            };

            addLabel("Default Models (Agent/Ask/Edit):", y);
            DefaultAgentModelCombo = new CodeLessDropdown { Location = new Point(240, y), Size = new Size(138, 26), DropDownStyle = ComboBoxStyle.DropDown, Font = new Font("Consolas", 8.5f) };
            DefaultAskModelCombo = new CodeLessDropdown { Location = new Point(384, y), Size = new Size(138, 26), DropDownStyle = ComboBoxStyle.DropDown, Font = new Font("Consolas", 8.5f) };
            DefaultEditModelCombo = new CodeLessDropdown { Location = new Point(528, y), Size = new Size(138, 26), DropDownStyle = ComboBoxStyle.DropDown, Font = new Font("Consolas", 8.5f) };
            foreach (var c in new[] { DefaultAgentModelCombo, DefaultAskModelCombo, DefaultEditModelCombo })
            {
                c.ApplyTheme(activeTheme);
                foreach (var v in this.allVendors)
                {
                    if (v.SupportedModels != null)
                        foreach (string m in v.SupportedModels)
                            if (!c.Items.Contains(m)) c.Items.Add(m);
                }
                body.Controls.Add(c);
            }
            DefaultAgentModelCombo.Text = settings.DefaultAgentModel ?? "claude-sonnet-4-6";
            DefaultAskModelCombo.Text = settings.DefaultAskModel ?? "gpt-4o";
            DefaultEditModelCombo.Text = settings.DefaultEditModel ?? "claude-sonnet-4-6";
            y += 32;

            addLabel("Temperature & Privacy:", y);
            TemperatureInput = new CodeLessDropdown
            {
                Location = new Point(240, y),
                Size = new Size(100, 26),
                DropDownStyle = ComboBoxStyle.DropDown,
                Font = new Font("Consolas", 9.0f)
            };
            TemperatureInput.ApplyTheme(activeTheme);
            foreach (string t in new[] { "0.0", "0.1", "0.2", "0.4", "0.7", "1.0" }) TemperatureInput.Items.Add(t);
            TemperatureInput.Value = (decimal)Math.Max(0.0f, Math.Min(2.0f, settings.AiTemperature));
            body.Controls.Add(TemperatureInput);

            PrivacyModeBox = addCheck("Privacy Mode (Zero data retention / local-only indexing)", settings.PrivacyMode, 355, y + 2);
            y += 38;

            Action liveApply = () =>
            {
                SaveSettings();
                if (OnSettingsLiveChanged != null)
                    OnSettingsLiveChanged(settings);
            };
            ThemeCombo.SelectedIndexChanged += (s, e) => liveApply();
            FontFamilyCombo.SelectedIndexChanged += (s, e) => liveApply();
            FontSizeInput.SelectedIndexChanged += (s, e) => liveApply();
            TabSizeInput.SelectedIndexChanged += (s, e) => liveApply();
            InsertSpacesBox.CheckedChanged += (s, e) => liveApply();
            WordWrapBox.CheckedChanged += (s, e) => liveApply();
            MinimapBox.CheckedChanged += (s, e) => liveApply();
            GhostTextBox.CheckedChanged += (s, e) => liveApply();
            AutoSaveBox.CheckedChanged += (s, e) => liveApply();
            AgentApprovalBox.CheckedChanged += (s, e) => liveApply();
            ShowWelcomeBox.CheckedChanged += (s, e) => liveApply();
            OfflineModeBox.CheckedChanged += (s, e) => liveApply();
            AutoUpdateCheckBox.CheckedChanged += (s, e) => liveApply();
            ExcludedFoldersInput.TextChanged += (s, e) => liveApply();
            DefaultAgentModelCombo.SelectedIndexChanged += (s, e) => liveApply();
            DefaultAskModelCombo.SelectedIndexChanged += (s, e) => liveApply();
            DefaultEditModelCombo.SelectedIndexChanged += (s, e) => liveApply();
            TemperatureInput.SelectedIndexChanged += (s, e) => liveApply();
            PrivacyModeBox.CheckedChanged += (s, e) => liveApply();

            Button saveBtn = new Button
            {
                Location = new Point(425, y),
                Size = new Size(130, 32),
                Text = "Save Settings",
                FlatStyle = FlatStyle.Flat,
                BackColor = activeTheme.AccentPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.0f, FontStyle.Regular),
                Cursor = Cursors.Hand
            };
            saveBtn.FlatAppearance.BorderSize = 0;
            saveBtn.Click += (s, e) =>
            {
                SaveSettings();
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            Button cancelBtn = new Button
            {
                Location = new Point(565, y),
                Size = new Size(100, 32),
                Text = "Cancel",
                FlatStyle = FlatStyle.Flat,
                BackColor = activeTheme.BgHover,
                ForeColor = activeTheme.FgPrimary,
                Font = new Font("Segoe UI", 9.0f),
                Cursor = Cursors.Hand
            };
            cancelBtn.FlatAppearance.BorderColor = activeTheme.BorderSubtle;
            bool revertedSettings = false;
            Action revertToSnapshot = () =>
            {
                if (revertedSettings) return;
                revertedSettings = true;
                settings.ActiveThemeId = origThemeId;
                settings.EditorFontFamily = origFontFam;
                settings.EditorFontSize = origFontSize;
                settings.TabSize = origTabSize;
                settings.InsertSpaces = origInsertSpaces;
                settings.WordWrap = origWordWrap;
                settings.ShowMinimap = origMinimap;
                settings.EnableInlineGhostText = origGhost;
                settings.AutoSave = origAutoSave;
                settings.RequireAgentToolApproval = origApproval;
                settings.ShowWelcomeOnStartup = origWelcome;
                settings.ActivatePythonEnvironmentInTerminal = origActivateVenv;
                settings.OfflineMode = origOffline;
                NetworkLedger.SetOfflineMode(origOffline);
                settings.AutoCheckForUpdates = origAutoUpdate;
                settings.ExcludedFolders = origExcluded;
                settings.DefaultAgentModel = origAgentModel;
                settings.DefaultAskModel = origAskModel;
                settings.DefaultEditModel = origEditModel;
                settings.AiTemperature = origTemp;
                settings.PrivacyMode = origPrivacy;
                if (ParityBlock != null) ParityBlock.Revert(settings);
                settings.Save();
                if (OnSettingsLiveChanged != null)
                    OnSettingsLiveChanged(settings);
            };
            cancelBtn.Click += (s, e) =>
            {
                this.DialogResult = DialogResult.Cancel;
                revertToSnapshot();
                this.Close();
            };
            this.FormClosing += (s, e) =>
            {
                if (this.DialogResult != DialogResult.OK)
                    revertToSnapshot();
            };

            body.Controls.Add(saveBtn);
            body.Controls.Add(cancelBtn);
            this.AcceptButton = saveBtn;
            this.CancelButton = cancelBtn;

            Panel titleBar = BuildDarkDialogTitleBar("codeLess — Settings & AI Configuration (Ctrl+,)", activeTheme, false, true);
            this.Controls.Add(body);
            this.Controls.Add(titleBar);
        }

        public void SaveSettings()
        {
            if (ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex < allThemes.Count)
                settings.ActiveThemeId = allThemes[ThemeCombo.SelectedIndex].Id;
            settings.EditorFontFamily = string.IsNullOrWhiteSpace(FontFamilyCombo.Text) ? "Consolas" : FontFamilyCombo.Text.Trim();
            float parsedFont = (float)FontSizeInput.Value;
            settings.EditorFontSize = (parsedFont < 7f || parsedFont > 28f) ? 10.5f : parsedFont;
            int parsedTab = (int)TabSizeInput.Value;
            settings.TabSize = (parsedTab < 1 || parsedTab > 8) ? 4 : parsedTab;
            settings.InsertSpaces = InsertSpacesBox.Checked;
            settings.WordWrap = WordWrapBox.Checked;
            settings.ShowMinimap = MinimapBox.Checked;
            settings.EnableInlineGhostText = GhostTextBox.Checked;
            settings.AutoSave = AutoSaveBox.Checked;
            if (ParityBlock != null) ParityBlock.Apply(settings);
            settings.RequireAgentToolApproval = AgentApprovalBox.Checked;
            if (ShowWelcomeBox != null)
                settings.ShowWelcomeOnStartup = ShowWelcomeBox.Checked;
            if (AutoUpdateCheckBox != null)
                settings.AutoCheckForUpdates = AutoUpdateCheckBox.Checked;
            if (ActivatePythonEnvBox != null)
                settings.ActivatePythonEnvironmentInTerminal = ActivatePythonEnvBox.Checked;
            if (OfflineModeBox != null)
            {
                settings.OfflineMode = OfflineModeBox.Checked;
                NetworkLedger.SetOfflineMode(settings.OfflineMode);
            }
            if (ExcludedFoldersInput != null && !string.IsNullOrWhiteSpace(ExcludedFoldersInput.Text))
                settings.ExcludedFolders = ExcludedFoldersInput.Text.Trim();
            if (DefaultAgentModelCombo != null && !string.IsNullOrWhiteSpace(DefaultAgentModelCombo.Text))
                settings.DefaultAgentModel = DefaultAgentModelCombo.Text.Trim();
            if (DefaultAskModelCombo != null && !string.IsNullOrWhiteSpace(DefaultAskModelCombo.Text))
                settings.DefaultAskModel = DefaultAskModelCombo.Text.Trim();
            if (DefaultEditModelCombo != null && !string.IsNullOrWhiteSpace(DefaultEditModelCombo.Text))
                settings.DefaultEditModel = DefaultEditModelCombo.Text.Trim();
            if (TemperatureInput != null)
                settings.AiTemperature = (float)TemperatureInput.Value;
            if (PrivacyModeBox != null)
                settings.PrivacyMode = PrivacyModeBox.Checked;
            if (allVendors != null && llmEngine != null)
                llmEngine.SyncVendorsToSettings(settings);
            settings.Save();
        }
    }

    // =========================================================================
    // 11. KEYBOARD SHORTCUTS REFERENCE DIALOG
    // =========================================================================
    public class KeyboardShortcutsDialog : CodeLessChromelessForm
    {
        public CodeLessDarkInputBox FilterInput;
        public CodeLessListBox ShortcutsList;
        private List<string> allShortcutRows = new List<string>();

        public KeyboardShortcutsDialog(StudioTheme theme)
        {
            this.Text = "codeLess IDE — Keyboard Shortcuts Reference";
            this.Size = new Size(680, 520);
            this.MinimumSize = new Size(540, 380);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.None;
            this.BackColor = theme.BgSidebar;
            this.ForeColor = theme.FgPrimary;
            this.EnableEdgeResize = true;

            Panel topBar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 8, 12, 8), BackColor = theme.BgActivity };
            FilterInput = new CodeLessDarkInputBox
            {
                Dock = DockStyle.Fill,
                Placeholder = "Filter shortcuts by keybinding or command name (e.g. Ctrl+Shift+T, Rename, Comment)...",
                Font = new Font("Segoe UI", 9.5f)
            };
            FilterInput.ApplyTheme(theme);
            topBar.Controls.Add(FilterInput);

            ShortcutsList = new CodeLessListBox
            {
                Dock = DockStyle.Fill,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                ItemHeight = 26,
                Font = new Font("Consolas", 9.2f)
            };
            ShortcutsList.ApplyTheme(theme);

            string[][] bindings = new string[][]
            {
                // Selection & Caret
                new[] { "Shift+Arrow / Home / End", "Extend Selection by Character / Line Boundary" },
                new[] { "Shift+PageUp / PageDown", "Extend Selection by Viewport Page" },
                new[] { "Ctrl+Left / Ctrl+Right", "Move Cursor by Word Left / Right" },
                new[] { "Ctrl+Shift+Left / Right", "Extend Selection by Word Left / Right" },
                new[] { "Ctrl+A", "Select All" },
                new[] { "Ctrl+L", "Expand Line Selection (Select Current Line)" },
                new[] { "Ctrl+D", "Add Selection to Next Find Match (Multi-Cursor)" },
                new[] { "Ctrl+K Ctrl+D", "Move Last Selection to Next Find Match (Skip Occurrence)" },
                new[] { "Ctrl+U", "Cursor Undo (Revert Last Multi-Cursor Addition)" },
                new[] { "Ctrl+Shift+L", "Select All Occurrences of Current Selection" },
                new[] { "Ctrl+F2", "Change All Occurrences of Current Word" },
                new[] { "Alt+Click", "Insert Additional Cursor at Clicked Position" },
                new[] { "Ctrl+Alt+Up / Down", "Insert Cursor Above / Below" },
                new[] { "Shift+Alt+Drag / Arrow", "Column (Box) Selection" },
                new[] { "Shift+Alt+I", "Insert Cursor at End of Each Line Selected" },
                new[] { "Shift+Alt+Right / Left", "Expand / Shrink AST Smart Selection" },
                new[] { "Escape", "Collapse Multi-Cursor / Dismiss Popups" },
                // Editing
                new[] { "Ctrl+X / Ctrl+C", "Cut / Copy Selection (or Entire Line when Empty)" },
                new[] { "Ctrl+V", "Paste from Clipboard" },
                new[] { "Ctrl+Shift+K", "Delete Entire Active Line" },
                new[] { "Alt+Up / Alt+Down", "Move Active Line Up / Down" },
                new[] { "Shift+Alt+Up / Alt+Down", "Copy (Duplicate) Active Line Up / Down" },
                new[] { "Ctrl+Enter / Ctrl+Shift+Enter", "Insert Blank Line Below / Above" },
                new[] { "Ctrl+] / Ctrl+[", "Indent / Outdent Line or Selection" },
                new[] { "Tab / Shift+Tab", "Indent / Unindent Selection or Line" },
                new[] { "Ctrl+/", "Toggle Line Comment (// or #)" },
                new[] { "Shift+Alt+A", "Toggle Block Comment (/* ... */)" },
                new[] { "Ctrl+Shift+\\", "Jump to Matching Bracket" },
                new[] { "Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z", "Undo / Redo (with Typing Coalescing)" },
                new[] { "Ctrl+Backspace / Ctrl+Delete", "Delete Word Left / Delete Word Right" },
                new[] { "Shift+Alt+F", "Format Document Indentation" },
                new[] { "Ctrl+K Ctrl+F", "Format Selected Lines" },
                new[] { "Ctrl+Space", "Trigger Suggest / IntelliSense Completion Popup" },
                new[] { "Ctrl+Shift+Space", "Trigger Parameter Hints" },
                // Folding
                new[] { "Ctrl+Shift+[ / ]", "Fold / Unfold Current Region" },
                new[] { "Ctrl+K Ctrl+[ / ]", "Fold / Unfold Subregions" },
                new[] { "Ctrl+K Ctrl+0 / Ctrl+K Ctrl+J", "Fold All / Unfold All Regions in Document" },
                // Navigation
                new[] { "Ctrl+P", "Quick Open File by Fuzzy Filename (: Line, @ Symbol)" },
                new[] { "Ctrl+G", "Go to Line Number (:)" },
                new[] { "Ctrl+Shift+O", "Go to Symbol in Editor (@)" },
                new[] { "Ctrl+T", "Show All Symbols in Workspace" },
                new[] { "Ctrl+Shift+P / F1", "Show Command Palette (All IDE Commands)" },
                new[] { "F12 / Ctrl+Click", "Go to Symbol Definition" },
                new[] { "Alt+F12 / Shift+F12", "Peek Definition / Show Symbol References" },
                new[] { "Alt+Left / Alt+Right", "Navigate Back / Forward Cursor History" },
                new[] { "Ctrl+Shift+M", "Toggle Problems Panel" },
                new[] { "F8 / Shift+F8", "Go to Next / Previous Error or Warning" },
                new[] { "Ctrl+Home / Ctrl+End", "Jump to Beginning / End of File" },
                new[] { "Ctrl+Up / Ctrl+Down", "Scroll Editor Line Up / Down Without Moving Caret" },
                new[] { "Alt+PageUp / Alt+PageDown", "Scroll Page Up / Down" },
                new[] { "F2", "Rename Symbol Across Current Buffer" },
                // Search
                new[] { "Ctrl+F / Ctrl+H", "Find / Replace in Active Editor" },
                new[] { "F3 / Shift+F3", "Find Next / Find Previous Occurrence" },
                new[] { "Alt+Enter", "Select All Matches of Find Query" },
                new[] { "Alt+C / Alt+W / Alt+R", "Toggle Match Case / Whole Word / Regex in Find" },
                new[] { "Ctrl+Shift+F / Ctrl+Shift+H", "Search / Replace Across Workspace Files" },
                // Workbench
                new[] { "Ctrl+B", "Toggle Primary Left Sidebar" },
                new[] { "Ctrl+J", "Toggle Bottom Panel Dock" },
                new[] { "Ctrl+`", "Toggle Integrated Terminal" },
                new[] { "Ctrl+Shift+`", "Create New Integrated Terminal Session" },
                new[] { "Ctrl+Shift+5", "Split Integrated Terminal Pane" },
                new[] { "Ctrl+Shift+E / F / G / D / X", "Focus Explorer / Search / Source Control / Run / AI" },
                new[] { "Ctrl+\\", "Split Editor Right" },
                new[] { "Ctrl+1 / Ctrl+2 / Ctrl+3", "Focus First / Second Editor Group" },
                new[] { "Ctrl+W / Ctrl+F4", "Close Active Editor Tab" },
                new[] { "Ctrl+K W", "Close All Open Editor Tabs" },
                new[] { "Ctrl+Shift+T", "Reopen Last Closed Editor Tab" },
                new[] { "Ctrl+Tab / Ctrl+Shift+Tab", "Switch to Next / Previous Open Tab" },
                new[] { "Ctrl+PageDown / Ctrl+PageUp", "Switch Next / Previous Tab (or Terminal when Focused)" },
                new[] { "Alt+1 .. Alt+9", "Switch to Editor Tab 1..9 by Index" },
                new[] { "Ctrl+K Ctrl+S", "Open Keyboard Shortcuts Reference" },
                new[] { "Ctrl+,", "Open IDE Settings & Preferences" },
                new[] { "Ctrl+K Ctrl+T", "Select Color Theme" },
                new[] { "Ctrl+= / Ctrl+- / Ctrl+0", "Zoom In / Zoom Out / Reset Editor Font Zoom" },
                new[] { "Ctrl+K Z / F11", "Toggle Zen Mode / Full Screen" },
                new[] { "Alt+Z", "Toggle Word Wrap" },
                new[] { "Ctrl+N / Ctrl+O / Ctrl+K Ctrl+O", "New File / Open File / Open Folder" },
                new[] { "Ctrl+S / Ctrl+Shift+S / Ctrl+K S", "Save / Save As / Save All Files" },
                new[] { "Ctrl+Shift+N", "New codeLess IDE Window" },
                // AI Copilot & Agent
                new[] { "Ctrl+I", "Inline AI Edit & Diff Preview (Accept / Reject)" },
                new[] { "Ctrl+Alt+I", "Toggle AI Agent / Composer Right Dock" },
                new[] { "Tab / Esc / Ctrl+Right", "Accept Ghost Text / Dismiss / Accept Next Word" }
            };

            foreach (var b in bindings)
            {
                allShortcutRows.Add(string.Format("  {0,-28}  │  {1}", b[0], b[1]));
            }

            Action refreshList = () =>
            {
                string q = (FilterInput.Text ?? "").Trim().ToLowerInvariant();
                ShortcutsList.Items.Clear();
                foreach (string row in allShortcutRows)
                {
                    if (string.IsNullOrEmpty(q) || row.ToLowerInvariant().Contains(q))
                        ShortcutsList.Items.Add(row);
                }
            };

            FilterInput.TextChanged += (s, e) => refreshList();
            FilterInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) this.Close();
            };
            refreshList();

            Panel titleBar = BuildDarkDialogTitleBar("codeLess IDE — Keyboard Shortcuts Reference", theme, false, true);
            this.Controls.Add(ShortcutsList);
            this.Controls.Add(topBar);
            this.Controls.Add(titleBar);
        }
    }

    // =========================================================================
    // 12. ABOUT DIALOG & DEVELOPER TOOLS INSPECTOR
    // =========================================================================
    public class AboutDialog : CodeLessChromelessForm
    {
        public AboutDialog(StudioTheme theme)
        {
            this.Text = "About codeLess";
            this.Size = new Size(460, 290);
            this.EnableEdgeResize = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = theme.BgSidebar;
            this.ForeColor = theme.FgPrimary;

            Panel body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24), BackColor = theme.BgSidebar };
            Process proc = Process.GetCurrentProcess();
            double wsMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            double privMb = proc.PrivateMemorySize64 / (1024.0 * 1024.0);

            Panel logoBox = new Panel
            {
                Location = new Point(22, 12),
                Size = new Size(46, 46),
                BackColor = Color.Transparent,
                AccessibleName = "Savatarr Logo",
                Tag = "brand-logo"
            };
            logoBox.Paint += (s, e) =>
            {
                CodeLessVectorIcons.DrawBrandLogo(e.Graphics, new Rectangle(2, 2, 42, 42), false);
            };

            Label title = new Label
            {
                Location = new Point(74, 12),
                AutoSize = true,
                Text = "codeLess",
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                ForeColor = theme.FgPrimary,
                UseMnemonic = false
            };
            Label subBrand = new Label
            {
                Location = new Point(76, 40),
                AutoSize = true,
                Text = "by Savatarr",
                Font = new Font("Segoe UI", 9.0f),
                ForeColor = theme.FgMuted,
                UseMnemonic = false
            };
            Label info = new Label
            {
                Location = new Point(24, 68),
                Size = new Size(412, 132),
                Text = string.Format(
                    "Version: 3.2.3 (Publisher: Savatarr)\n" +
                    "CLR Runtime: .NET {0} ({1}-bit)\n" +
                    "OS Version: {2}\n" +
                    "Process Working Set: {3:F1} MB (Private: {4:F1} MB)\n" +
                    "Terminal Engine: Windows ConPTY (CreatePseudoConsole) + VT100\n" +
                    "AI Engine: Native Multi-Vendor BYOK Agent / Ask / Edit + Inline Diff",
                    Environment.Version,
                    IntPtr.Size * 8,
                    Environment.OSVersion.VersionString,
                    wsMb,
                    privMb),
                Font = new Font("Consolas", 9.0f),
                ForeColor = theme.FgSecondary,
                UseMnemonic = false
            };

            Button copyBtn = new Button
            {
                Location = new Point(242, 206),
                Size = new Size(88, 30),
                Text = "Copy",
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.BgElevated,
                ForeColor = theme.FgPrimary,
                Font = new Font("Segoe UI", 9.0f),
                Cursor = Cursors.Hand,
                UseMnemonic = false
            };
            copyBtn.FlatAppearance.BorderColor = theme.BorderSubtle;
            copyBtn.Click += (s, e) =>
            {
                try { Clipboard.SetText("codeLess by Savatarr\n" + info.Text); } catch { }
            };

            Button okBtn = new Button
            {
                Location = new Point(340, 206),
                Size = new Size(88, 30),
                Text = "OK",
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.AccentPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.0f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            okBtn.FlatAppearance.BorderSize = 0;
            okBtn.Click += (s, e) => { this.DialogResult = DialogResult.OK; this.Close(); };

            body.Controls.Add(logoBox);
            body.Controls.Add(title);
            body.Controls.Add(subBrand);
            body.Controls.Add(info);
            body.Controls.Add(copyBtn);
            body.Controls.Add(okBtn);
            this.AcceptButton = okBtn;
            this.CancelButton = okBtn;

            Panel titleBar = BuildDarkDialogTitleBar("About codeLess", theme, false, false);
            this.Controls.Add(body);
            this.Controls.Add(titleBar);
        }
    }

    public class DeveloperToolsDialog : CodeLessChromelessForm
    {
        public DeveloperToolsDialog(StudioTheme theme, string workspaceRoot, int indexedChunkCount)
        {
            this.Text = "Developer Tools — Process & Memory Inspector";
            this.Size = new Size(560, 380);
            this.EnableEdgeResize = true;
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = theme.BgSidebar;
            this.ForeColor = theme.FgPrimary;

            CodeLessStreamView box = new CodeLessStreamView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = theme.BgEditor,
                ForeColor = theme.FgPrimary,
                Font = new Font("Consolas", 9.2f)
            };
            box.ApplyTheme(theme);

            Process p = Process.GetCurrentProcess();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== CODELESS DEVELOPER TOOLS & RUNTIME DIAGNOSTICS ===");
            sb.AppendLine("PID:                  " + p.Id);
            sb.AppendLine("Working Set:          " + (p.WorkingSet64 / (1024.0 * 1024.0)).ToString("F2") + " MB");
            sb.AppendLine("Private Memory:       " + (p.PrivateMemorySize64 / (1024.0 * 1024.0)).ToString("F2") + " MB");
            sb.AppendLine("GC Managed Heap:      " + (GC.GetTotalMemory(false) / (1024.0 * 1024.0)).ToString("F2") + " MB");
            sb.AppendLine("Threads:              " + p.Threads.Count);
            sb.AppendLine("Handles:              " + p.HandleCount);
            sb.AppendLine("ConPTY Supported:     " + ConPtyNative.IsConPtySupported());
            sb.AppendLine("Workspace Root:       " + (string.IsNullOrEmpty(workspaceRoot) ? "(None)" : workspaceRoot));
            sb.AppendLine("BM25 Indexed Chunks:  " + indexedChunkCount);
            sb.AppendLine("OS Version:           " + Environment.OSVersion);
            sb.AppendLine("CLR Version:          " + Environment.Version);
            box.Text = sb.ToString();

            Panel titleBar = BuildDarkDialogTitleBar("Developer Tools — Process & Runtime Inspector", theme, false, true);
            this.Controls.Add(box);
            this.Controls.Add(titleBar);
        }
    }
}
