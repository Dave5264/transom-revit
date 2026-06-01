using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using Transom.Core;

namespace Transom.Commands;

/// <summary>
///     Checks GitHub for a newer Transom release and, if found, downloads the per-user installer and launches
///     it. The installer (no admin) prompts to close Revit to finish; reopen to load the update.
/// </summary>
[UsedImplicitly]
[Transaction(TransactionMode.Manual)]
public class UpdateCommand : ExternalCommand
{
    public override void Execute()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Transom-Updater");

            var json = http.GetStringAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest")
                .GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var latest = tag.TrimStart('v', 'V');
            if (!Version.TryParse(latest, out var lv) || !Version.TryParse(AppInfo.Version, out var cv))
            {
                TaskDialog.Show("Transom", $"Couldn't compare versions (latest reported '{tag}').");
                return;
            }
            if (lv <= cv)
            {
                TaskDialog.Show("Transom — Updates", $"You're on the latest version (v{AppInfo.Version}).");
                return;
            }

            string url = "", name = "";
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var an = a.GetProperty("name").GetString() ?? "";
                if (an.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && an.IndexOf("SingleUser", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    url = a.GetProperty("browser_download_url").GetString() ?? "";
                    name = an;
                    break;
                }
            }
            if (string.IsNullOrEmpty(url))
            {
                ShowLink($"Update v{latest} is available, but no per-user installer was attached to the release.", AppInfo.ReleasesUrl);
                return;
            }

            var prompt = new TaskDialog("Transom — Update available")
            {
                MainInstruction = $"Update available: v{latest}",
                MainContent = $"You have v{AppInfo.Version}. Download and run the installer now?\n\n" +
                              "The installer needs no admin rights. It will ask you to close Revit to finish — reopen afterwards.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes,
            };
            if (prompt.Show() != TaskDialogResult.Yes) return;

            var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            var path = Path.Combine(Path.GetTempPath(), name);
            File.WriteAllBytes(path, bytes);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            TaskDialog.Show("Transom — Update",
                "The installer has been launched. Close Revit when it prompts you, finish the install, then reopen Revit.");
        }
        catch (Exception ex)
        {
            ShowLink("Couldn't check for updates:\n" + ex.Message, AppInfo.ReleasesUrl);
        }
    }

    private static void ShowLink(string message, string url)
    {
        var td = new TaskDialog("Transom — Updates")
        {
            MainInstruction = message,
            MainContent = "Open the releases page to download manually?",
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
        };
        if (td.Show() == TaskDialogResult.Yes)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* ignore */ }
    }
}
