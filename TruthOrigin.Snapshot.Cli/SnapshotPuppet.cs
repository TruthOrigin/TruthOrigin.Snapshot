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

namespace TruthOrigin.Snapshot.Cli
{
    internal class SnapshotPuppet
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

        public async Task Start(string folderPath, string baseUrl, List<string> relativePaths)
        {
            Console.WriteLine("[Puppet] Initializing...");

            string runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes");
            Directory.CreateDirectory(runtimeRoot);

            var target = SelectTarget();
            Console.WriteLine($"[Puppet] Detected environment: {target.Rid}");

            string platformPath = Path.Combine(runtimeRoot, target.Platform);
            string finalExtractFolder = Path.Combine(platformPath, target.Folder);

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

            List<(string url, string html)>? snapshots = null;
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    string chromeExe = FindExecutable(runtimeRoot, target.Rid, target.Folder);
                   snapshots = await BeginAsync(folderPath, baseUrl,relativePaths, chromeExe);

                    break;
                }
                catch (Exception ex)
                {
                    if (i == 0)
                    {
                        Console.WriteLine("Failure occurred launching trying to redownload");
                        await FetchAndExtract(target.Rid, runtimeRoot, target.Platform, retry: true);
                    }
                    else
                    {
                        throw ex;
                    }
                }
            }


            if (snapshots != null)
            {
                foreach (var snapshot in snapshots)
                {
                    await SaveSnapshot(folderPath, snapshot.url, snapshot.html);
                }
            }

            UpdateHeadersAndRedirects(folderPath, relativePaths);
        }
        public static string ToUpperCamelCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var result = new StringBuilder();
            bool capitalizeNext = true;

            foreach (char c in input)
            {
                if (c == ' ' || c == '/' || c == '-')
                {
                    result.Append(c);
                    capitalizeNext = true;
                }
                else if (capitalizeNext)
                {
                    result.Append(char.ToUpperInvariant(c));
                    capitalizeNext = false;
                }
                else
                {
                    result.Append(char.ToLowerInvariant(c));
                }
            }

            return result.ToString();
        }


        private void UpdateHeadersAndRedirects(string folderPath, List<string> relativePaths)
        {
            var headersPath = Path.Combine(folderPath, "_headers");
            var redirectsPath = Path.Combine(folderPath, "_redirects");

            var existingHeaders = File.Exists(headersPath)
                ? File.ReadAllLines(headersPath).ToList()
                : new List<string>();

            var existingRedirects = File.Exists(redirectsPath)
                ? File.ReadAllLines(redirectsPath).ToList()
                : new List<string>();

            var newHeaders = new List<string>();
            var newRedirects = new List<string>();

            HashSet<string> headerTargets = existingHeaders
                .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(" "))
                .Select(line => line.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            HashSet<string> redirectSources = existingRedirects
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim())
                .Where(src => !string.IsNullOrWhiteSpace(src))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var normalizedPaths = relativePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && path != "/" && path != "[initial-index]")
                .Select(path => path.Trim('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            void AddHeaderRule(string route)
            {
                newHeaders.Add($"{route}\n  Cache-Control: no-store\n  ETag: \"\"");
            }

            // Always include root and index.html headers
            if (!headerTargets.Contains("/"))
                AddHeaderRule("/");

            if (!headerTargets.Contains("/index.html"))
                AddHeaderRule("/index.html");

            // Group by top-level for wildcard eligibility
            var wildcardGroups = normalizedPaths
                .GroupBy(p => p.Split('/')[0].ToLowerInvariant())
                .Where(g => g.Count() >= 2)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Add wildcard headers
            foreach (var kv in wildcardGroups)
            {
                var rule = $"/{kv.Key}/*";
                if (!headerTargets.Contains(rule))
                    AddHeaderRule(rule);
            }

            // Add specific headers for paths not covered by wildcards
            foreach (var path in normalizedPaths)
            {
                var firstSegment = path.Split('/')[0].ToLowerInvariant();
                if (wildcardGroups.ContainsKey(firstSegment))
                    continue;

                var cleanPath = $"/{path}";
                var withSlash = cleanPath.EndsWith("/") ? cleanPath : cleanPath + "/";

                if (!headerTargets.Contains(cleanPath))
                    AddHeaderRule(cleanPath);

                if (!headerTargets.Contains(withSlash))
                    AddHeaderRule(withSlash);
            }

            // Redirects (skip /index.html)
            foreach (var path in normalizedPaths)
            {
                var lowerPath = $"/{path.ToLowerInvariant()}";
                var upperPath = $"/{ToUpperCamelCase(path)}";

                if (lowerPath == "/index.html")
                    continue;

                var lowerNoSlash = lowerPath.TrimEnd('/');
                var upperNoSlash = upperPath.TrimEnd('/');

                var lowerVariants = new[] { lowerNoSlash, lowerNoSlash + "/" };
                var upperVariants = new[] { upperNoSlash, upperNoSlash + "/" };

                // Redirect upper camel to lower variants
                foreach (var variant in lowerVariants)
                {
                    if (!redirectSources.Contains(upperNoSlash) && !string.Equals(upperNoSlash, variant, StringComparison.OrdinalIgnoreCase))
                    {
                        newRedirects.Add($"{upperNoSlash.PadRight(20)} {variant} 200!");
                    }
                }

                // Redirect lowercase variants to their other slash form (normalize)
                if (!redirectSources.Contains(lowerNoSlash + "/"))
                {
                    newRedirects.Add($"{lowerNoSlash.PadRight(20)} {lowerNoSlash + "/"} 200!");
                }

                if (!redirectSources.Contains(lowerNoSlash))
                {
                    newRedirects.Add($"{(lowerNoSlash + "/").PadRight(20)} {lowerNoSlash} 200!");
                }
            }

            // Write final files
            File.WriteAllLines(headersPath, existingHeaders.Concat(newHeaders).Distinct(StringComparer.OrdinalIgnoreCase));
            File.WriteAllLines(redirectsPath, existingRedirects.Concat(newRedirects).Distinct(StringComparer.OrdinalIgnoreCase));
        }




        private async Task SaveSnapshot(string folderPath, string urlPath, string html)
        {
            // Normalize and check for root or initial special marker
            urlPath = (urlPath ?? "").Trim().Trim('/');
            bool isRoot = string.IsNullOrWhiteSpace(urlPath) || urlPath == "/" || urlPath == "[initial-index]";

            // Determine target folder
            string targetDir = isRoot
                ? Path.Combine(folderPath, "index")
                : Path.Combine(folderPath, Path.Combine(urlPath.Split('/')));

            // Ensure the target directory exists
            Directory.CreateDirectory(targetDir);

            // Build full file path
            string fullFilePath = Path.Combine(targetDir, "index.html");

            // Save HTML to file
            await File.WriteAllTextAsync(fullFilePath, html);

            Console.WriteLine($"[Snapshot] 💾 Saved to: {fullFilePath}");
        }




        private async Task<List<(string url, string html)>> BeginAsync(string folderPath, string baseUrl, List<string> relativePaths, string chromeExe)
        {
            Console.WriteLine($"[Puppet] Launching Chromium executable: {chromeExe}");

            var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                ExecutablePath = chromeExe,
                Headless = false,
            });
            try
            {

                var page = await browser.NewPageAsync();

                var snapshotCompleteSignal = new TaskCompletionSource<bool>();
                var snapshots = new List<(string url, string html)>();
                bool hasStarted = false;

                // Helper to safely combine paths
                string CombinePath(string a, string b) => $"{a.TrimEnd('/')}/{b.TrimStart('/')}";

                // C# callback for snapshot content
                await page.ExposeFunctionAsync("onSnapshotReceivedCSharp", async (string html, string url) =>
                {
                    if (!hasStarted)
                    {
                        Console.WriteLine("[Snapshot] 🟢 WASM app loaded (initial index snapshot)");
                        hasStarted = true;

                        if (relativePaths.Count > 0)
                        {
                            string firstPath = CombinePath("", relativePaths[0]) + "?truthseo-snapshot";
                            Console.WriteLine($"[Snapshot] 🚀 Starting with first path: {firstPath}");
                            await page.EvaluateFunctionAsync("path => window.sendNavigate(path)", firstPath);
                            return;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[Snapshot] 📸 Snapshot for {url}");
                        Console.WriteLine(html.Substring(0, Math.Min(html.Length, 300)) + "\n...[truncated]");
                        snapshots.Add((url, html));
                        await page.EvaluateExpressionAsync("window.snapshotAcknowledgeNext()");
                    }
                });

                // Final snapshot callback
                await page.ExposeFunctionAsync("onAllSnapshotsCompleteCSharp", () =>
                {
                    Console.WriteLine("[Snapshot] ✅ All snapshots complete");
                    snapshotCompleteSignal.TrySetResult(true);
                    return Task.CompletedTask;
                });

                string wasmUrlWithParam = AddSnapshotQueryParam(baseUrl);

                string controllerHtml = $@"
<!DOCTYPE html>
<html>
<head>
  <title>Snapshot Controller</title>
  <script>
    const iframeUrl = '{wasmUrlWithParam}';
    let iframe;
    let urls = [];
    let currentIndex = 0;

    function combinePath(a, b) {{
      return a.replace(/\/+$/, '') + '/' + b.replace(/^\/+/, '');
    }}

    window.sendNavigate = function(path) {{
      iframe.contentWindow.postMessage({{
        type: 'truthseo:navigate',
        targetPath: path
      }}, '*');
    }}

    window.snapshotAcknowledgeNext = function () {{
      currentIndex++;
      if (currentIndex < urls.length) {{
        const path = combinePath('', urls[currentIndex]) + '?truthseo-snapshot';
        console.log('[Proxy] 🧭 Sending next route:', path);
        sendNavigate(path);
      }} else {{
        Promise.resolve(window.onAllSnapshotsComplete?.()).then(() => {{
          console.log('[Proxy] 🎯 Completion acknowledged');
        }});
      }}
    }};

    window.addEventListener('message', (event) => {{
      const data = event.data;
      if (!data?.type) return;

      if (data.type === 'truthseo:snapshot') {{
        const url = urls[currentIndex] || '[initial-index]';
        console.log(`[Proxy] 📸 Received snapshot for: ${{url}}`);
        window.onSnapshotReceived?.(data.html, url);
      }}
    }});

    window.startSnapshotSession = function (pathList) {{
      urls = pathList;
      currentIndex = 0;

      iframe = document.createElement('iframe');
      iframe.src = iframeUrl;
      iframe.style.width = '100%';
      iframe.style.height = '1000px';
      iframe.onload = () => console.log('[Proxy] 📦 iframe loaded');
      document.body.appendChild(iframe);
    }};
  </script>
</head>
<body>
  <h1>Snapshot Controller</h1>
</body>
</html>";

                await page.SetContentAsync(controllerHtml);

                // Bind JS events to C# callbacks
                await page.EvaluateExpressionAsync(@"
        window.onSnapshotReceived = (html, url) => window.onSnapshotReceivedCSharp(html, url);
        window.onAllSnapshotsComplete = () => window.onAllSnapshotsCompleteCSharp();
    ");

                await page.EvaluateFunctionAsync("window.startSnapshotSession", relativePaths);

                Console.WriteLine("[Snapshot] 🚀 Waiting for snapshot completion...");
                await Task.WhenAny(snapshotCompleteSignal.Task, Task.Delay(90000));
                if (!snapshotCompleteSignal.Task.IsCompletedSuccessfully)
                    throw new Exception("Timed out waiting for snapshots to finish");

                Console.WriteLine("[Puppet] ✅ Snapshot sequence fully complete");
                return snapshots;
            }
            finally
            {
                await browser.CloseAsync();
            }
        }

        public static string AddSnapshotQueryParam(string url)
        {
            var uriBuilder = new UriBuilder(url);
            var query = System.Web.HttpUtility.ParseQueryString(uriBuilder.Query);
            query["truthseo-snapshot"] = string.Empty;
            uriBuilder.Query = query.ToString();
            return uriBuilder.ToString();
        }


        private (string Rid, string Platform, string Folder) SelectTarget()
        {
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            bool isArm = RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64;

            foreach (var t in Targets)
            {
                if ((isWindows && t.Rid.StartsWith("win")) ||
                    (isLinux && t.Rid.StartsWith("linux")) ||
                    (isMac && t.Rid.StartsWith("osx")))
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

    }
}