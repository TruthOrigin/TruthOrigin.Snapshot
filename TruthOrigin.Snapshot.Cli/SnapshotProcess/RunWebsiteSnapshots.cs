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

namespace TruthOrigin.Snapshot.Cli.SnapshotProcess
{
    internal class RunWebsiteSnapshots
    {
        public async Task<List<(string url, string html)>?> Start(string folderPath, string baseUrl, 
            List<string> relativePaths, string chromeExe, bool headless)
        {
            List<(string url, string html)>? snapshots = snapshots = 
                await BeginAsync(folderPath, baseUrl, relativePaths, chromeExe, headless);

            return snapshots;         
        }

        private async Task<List<(string url, string html)>> BeginAsync(string folderPath, string baseUrl,
            List<string> relativePaths, string chromeExe, bool headless)
        {
            Console.WriteLine($"[Puppet] Launching Chromium executable: {chromeExe}");

            var browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                ExecutablePath = chromeExe,
                Headless = headless, // or false for debugging
                Args = new[]
    {
       "--no-sandbox",
    "--disable-setuid-sandbox",
    "--disable-cache",
    "--disk-cache-size=0",
    "--disable-application-cache",
    "--disable-offline-load-stale-cache",
    "--disable-gpu-shader-disk-cache",
    "--media-cache-size=0",
    "--disk-cache-dir=/dev/null", // Ignored on Windows
    "--window-size=1200,800"
    }
            });

            try
            {

                var page = await browser.NewPageAsync();
                await page.SetCacheEnabledAsync(false);
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
                            string firstPath = CombinePath("", relativePaths[0]) + "?truth-snapshot";
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
        type: 'truth:navigate',
        targetPath: path
      }}, '*');
    }}

    window.snapshotAcknowledgeNext = function () {{
      currentIndex++;
      if (currentIndex < urls.length) {{
        const path = combinePath('', urls[currentIndex]) + '?truth-snapshot';
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

      if (data.type === 'truth:snapshot') {{
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
            query["truth-snapshot"] = string.Empty;
            uriBuilder.Query = query.ToString();
            return uriBuilder.ToString();
        }
    }
}
