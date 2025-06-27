using PuppeteerSharp;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.Xz;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TruthOrigin.Snapshot.Cli.Helpers;

namespace TruthOrigin.Snapshot.Cli.SnapshotProcess
{
    internal class SetupPuppet
    {
        private static readonly (string Rid, string Platform, string Folder)[] Targets = {
            ("win-x64", "Win_x64", "chrome-win"),
            ("win-x86", "Win", "chrome-win"),
            ("linux-x64", "Linux_x64", "chrome-linux"),
            ("linux-x86", "Linux", "chrome-linux"),
            ("osx-x64", "Mac", "chrome-mac"),
            ("osx-arm64", "Mac_Arm", "chrome-mac")
        };

        private static readonly Dictionary<string, string> UrlTemplates = new()
        {
            ["linux-x64"] = "https://huggingface.co/datasets/magiccodingman/chromium-bundles/resolve/main/bundles/linux-x64.tar.xz?download=true",
            ["linux-x86"] = "https://huggingface.co/datasets/magiccodingman/chromium-bundles/resolve/main/bundles/linux-x86.tar.xz?download=true",
            ["osx-x64"] = "https://huggingface.co/datasets/magiccodingman/chromium-bundles/resolve/main/bundles/osx-x64.tar.xz?download=true",
            ["osx-arm64"] = "https://huggingface.co/datasets/magiccodingman/chromium-bundles/resolve/main/bundles/osx-arm64.tar.xz?download=true",
            ["win-x64"] = "https://huggingface.co/datasets/magiccodingman/chromium-bundles/resolve/main/bundles/win-x64.tar.xz?download=true",
            ["win-x86"] = "https://huggingface.co/datasets/magiccodingman/chromium-bundles/resolve/main/bundles/win-x86.tar.xz?download=true"
        };

        public async Task<string> Start(bool forceRetry = false)
        {            
            Console.WriteLine("[Puppet] Initializing...");

            string runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes");
            Directory.CreateDirectory(runtimeRoot);

            var target = SelectTarget();
            Console.WriteLine($"[Puppet] Detected environment: {target.Rid}");

            string platformPath = Path.Combine(runtimeRoot, target.Platform);
            string finalExtractFolder = Path.Combine(platformPath, target.Folder);

            if (forceRetry)
            {
                await FetchAndExtract(target.Rid, runtimeRoot, target.Platform, retry: true);
                return FindExecutable(runtimeRoot, target.Rid, target.Folder);
            }

            if (!Directory.Exists(finalExtractFolder))
            {
                string expectedFolder = Path.Combine(runtimeRoot, target.Rid, "native", target.Folder);
                bool needsDownload = false;

                try
                {
                    _ = FindExecutable(runtimeRoot, target.Rid, target.Folder);
                    Console.WriteLine($"[Puppet] Chromium already extracted at: {expectedFolder}");
                }
                catch
                {
                    Console.WriteLine($"[Puppet] Chromium missing or corrupted at: {expectedFolder}");
                    needsDownload = true;
                }

                if (needsDownload)
                {
                    await FetchAndExtract(target.Rid, runtimeRoot, target.Platform, retry: true);
                }

            }
            else
            {
                Console.WriteLine($"[Puppet] Chromium found and ready at: {finalExtractFolder}");
            }

            return FindExecutable(runtimeRoot, target.Rid, target.Folder);
        }


        private string FindExecutable(string runtimeRoot, string rid, string innerFolder)
        {
            string folder = Path.Combine(runtimeRoot, rid, "native", innerFolder);
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException($"[Puppet] Expected folder not found: {folder}");

            var exeNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? new[] { "chrome.exe" }
                : new[] { "chrome", "chromium", "Chromium.app/Contents/MacOS/Chromium" };

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                foreach (var exe in exeNames)
                {
                    if (Path.GetFileName(file).Equals(exe, StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }

            throw new FileNotFoundException($"[Puppet] Could not find a Chromium executable in: {folder}");
        }

        private (string Rid, string Platform, string Folder) SelectTarget()
        {
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            bool isArm = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;

            foreach (var t in Targets)
            {
                if (isWindows && t.Rid.StartsWith("win") ||
                    isLinux && t.Rid.StartsWith("linux") ||
                    isMac && t.Rid.StartsWith("osx"))
                {
                    if (t.Rid.Contains("x64")) return t;
                    if (!isArm && t.Rid.Contains("x86")) return t;
                    if (isArm && t.Rid.Contains("x64")) return t; // ARM fallback
                }
            }

            throw new PlatformNotSupportedException("Unsupported OS/architecture.");
        }

        private async Task FetchAndExtract(string rid, string runtimeRoot, string platformFolder, bool retry)
        {
            string archivePath = Path.Combine(runtimeRoot, $"{rid}.tar.xz");
            string tarPath = Path.Combine(runtimeRoot, $"{rid}.tar");
            string ridFolder = Path.Combine(runtimeRoot, rid);

            try
            {
                Console.WriteLine($"[Puppet] Downloading Chromium bundle: {rid}");
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(UrlTemplates[rid]);
                await File.WriteAllBytesAsync(archivePath, bytes);
                Console.WriteLine($"[Puppet] Saved archive to: {archivePath}");

                // Step 1: Decompress .xz to .tar
                Console.WriteLine("[Puppet] Decompressing .xz to .tar...");
                using (var xzStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read))
                using (var decompressed = new XZStream(xzStream))
                using (var tarStream = new FileStream(tarPath, FileMode.Create, FileAccess.Write))
                {
                    await decompressed.CopyToAsync(tarStream);
                }

                // Step 2: Extract .tar
                Console.WriteLine("[Puppet] Extracting .tar...");
                using (var tarInput = File.OpenRead(tarPath))
                using (var archive = SharpCompress.Archives.Tar.TarArchive.Open(tarInput))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            entry.WriteToDirectory(runtimeRoot, new ExtractionOptions
                            {
                                ExtractFullPath = true,
                                Overwrite = true
                            });
                        }
                    }
                }

                File.Delete(archivePath);
                File.Delete(tarPath);
                Console.WriteLine("[Puppet] ✅ Extraction complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Puppet] ❌ Extraction failed: {ex.Message}");

                if (retry)
                {
                    Console.WriteLine("[Puppet] Retrying after full cleanup...");
                    await ForceDeleteFolderAsync(ridFolder);
                    if (File.Exists(archivePath)) File.Delete(archivePath);
                    if (File.Exists(tarPath)) File.Delete(tarPath);
                    await FetchAndExtract(rid, runtimeRoot, platformFolder, retry: false);
                }
                else
                {
                    throw new Exception($"[Puppet] ❌ Retry failed. Could not extract Chromium bundle for {rid}.");
                }
            }
        }

        private async Task ForceDeleteFolderAsync(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;

                Console.WriteLine($"[Puppet] 🔥 Force deleting folder: {folder}");

                // Kill any chrome processes that may be locking the folder
                string[] processNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? new[] { "chrome", "chrome.exe" }
                    : new[] { "chrome", "chromium" };

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        if (processNames.Any(name => proc.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            proc.Kill(true);
                        }
                    }
                    catch { /* ignore access denied */ }
                }

                // Remove read-only attributes
                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var attributes = File.GetAttributes(file);
                    if (attributes.HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }

                Directory.Delete(folder, recursive: true);
                await Task.Delay(200); // small delay to ensure full release
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Puppet] ❌ Failed to force delete: {ex.Message}");
                throw;
            }
        }

    }
}