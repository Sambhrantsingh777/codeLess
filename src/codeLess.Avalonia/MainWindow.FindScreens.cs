using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using codeLess.Studio;

namespace codeLess.AvaloniaApp
{
    /// <summary>Screenshot-suite screens for the v3.3.1 fixes: find widget toggles, long-line JSON in a .py file.</summary>
    public partial class MainWindow
    {
        private async Task RunFindAndLongLineScreensAsync(string outputDir)
        {
            string dir = Path.Combine(Path.GetTempPath(), "codeLess_suite_find");
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder("import json\n\n# fixture: a big JSON literal on one line (horizontal scroll)\nCONFIG = {");
            for (int i = 0; i < 80; i++) sb.Append("\"service_" + i + "\": {\"url\": \"https://example.invalid/api/v" + i + "\", \"retries\": " + (i % 5) + ", \"note\": \"it's quoted, with # and // inside\"}, ");
            sb.Append("\"enabled\": True}\n\nTEMPLATE = \"\"\"\n{\"name\": \"demo\", \"items\": [1, 2, 3]}\n\"\"\"\n\ndef load(path):\n    with open(path) as f:\n        return json.load(f)  # parse\n");
            string py = Path.Combine(dir, "settings_blob.py");
            File.WriteAllText(py, sb.ToString());
            try
            {
                var tab = OpenFileInTab(py, preview: false);
                if (tab == null) return;
                PrimaryEditor.Canvas.CompleteBackgroundTokenization(5000);
                tab.ScrollTopLine = 0;
                tab.CursorLine = 3;
                tab.CursorCol = 0;
                tab.ClearSelection();
                // scroll so the left edge of the window starts inside a quoted value
                int at = tab.Lines[3].IndexOf("it's quoted", 1500, StringComparison.Ordinal);
                tab.ScrollLeftPx = Math.Max(0, at - 6) * PrimaryEditor.Canvas.CharWidth;
                PrimaryEditor.Canvas.InvalidateVisual();
                await CaptureScreenshotAsync(outputDir, "r13_long_line_json_py.png");
                tab.ScrollLeftPx = 0;

                PrimaryEditor.ShowFindBar(true);
                PrimaryEditor.SetFindQuery("retries", matchCase: true, wholeWord: true, useRegex: false);
                PrimaryEditor.ReplaceInputBox.Text = "attempts";
                await CaptureScreenshotAsync(outputDir, "r13_find_toggles.png");
                PrimaryEditor.SetFindQuery("retries(", matchCase: false, wholeWord: false, useRegex: true);
                await CaptureScreenshotAsync(outputDir, "r13_find_invalid_regex.png");
                PrimaryEditor.SetFindQuery("", matchCase: false, wholeWord: false, useRegex: false);
                PrimaryEditor.HideFindBar();
            }
            catch (Exception ex) { LogSuiteError(outputDir, ex); }
        }
    }
}
