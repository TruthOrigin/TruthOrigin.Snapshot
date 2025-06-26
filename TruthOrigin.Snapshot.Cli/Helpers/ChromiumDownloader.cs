using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TruthOrigin.Snapshot.Cli.Helpers
{
    public class ChromiumDownloader
    {
        private static readonly HttpClient client = new();
        private static readonly string basePath = AppContext.BaseDirectory;

        public static async Task DownloadPreBundledBrowsers()
        {
            var targets = new[]
{
    new { Rid="win-x64", Platform="Win_x64", Folder="chrome-win" },
    new { Rid="win-x86", Platform="Win", Folder="chrome-win" },

    new { Rid="linux-x64", Platform="Linux_x64", Folder="chrome-linux" },
    new { Rid="linux-x86", Platform="Linux", Folder="chrome-linux" },

    new { Rid="osx-x64", Platform="Mac", Folder="chrome-mac" },
    new { Rid="osx-arm64", Platform="Mac_Arm", Folder="chrome-mac" }
};


            foreach (var target in targets)
            {
                try
                {
                    await DownloadAndExtractAsync(target.Rid, target.Folder, target.Platform);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to process {target.Rid}: {ex.Message}");
                }
            }

            Console.WriteLine("All browser downloads completed.");
        }

        static async Task<string> GetLatestRevisionAsync(string platform)
        {
            string url = $"https://storage.googleapis.com/chromium-browser-snapshots/{platform}/LAST_CHANGE";
            string rev = await client.GetStringAsync(url);
            return rev.Trim();
        }

        static async Task DownloadAndExtractAsync(string rid, string folder, string platform)
        {
            string rev = await GetLatestRevisionAsync(platform);
            string downloadUrl = $"https://storage.googleapis.com/chromium-browser-snapshots/{platform}/{rev}/{folder}.zip";

            // NOTE: We are extracting into native/, not native/chrome-win etc.
            string targetDir = Path.Combine(basePath, "runtimes", rid, "native");
            Directory.CreateDirectory(targetDir);

            string zipPath = Path.Combine(targetDir, $"{folder}-{rev}.zip");

            // Skip if already extracted
            if (Directory.EnumerateFileSystemEntries(targetDir).Any())
            {
                Console.WriteLine($"{rid} already downloaded. Skipping.");
                return;
            }

            Console.WriteLine($"Downloading Chromium rev {rev} for {rid}...");
            byte[] data = await client.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(zipPath, data);

            Console.WriteLine($"Extracting to {targetDir}...");
            ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
            File.Delete(zipPath);

            Console.WriteLine($"Done with {rid}.");
        }
    }
}
