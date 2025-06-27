using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using PuppeteerSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TruthOrigin.Snapshot.Cli.SnapshotProcess;

namespace TruthOrigin.Snapshot.Cli
{
    internal class SnapshotRunner
    {
        public async Task Start(string folderPath, string? apiKey, bool headless = true)
        {
            var relativePaths = await new DigestWwwroot().ValidateFolderPath(folderPath);

            (IWebHost Host, string BaseUrl) result = await new LaunchServerHost().Start(relativePaths, folderPath, headless);

            await RunSnapshotProcess(folderPath, apiKey, relativePaths, result.BaseUrl, headless);

            // Optionally shut down server after puppet is done
            Console.WriteLine("[Server] Snapshot complete. Shutting down...");
            await result.Host.StopAsync();
        }

        private async Task RunSnapshotProcess(string folderPath, string? apiKey, 
            List<string> relativePaths, string baseUrl,
            bool headless = true)
        {
            string chromeExe = "";
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    chromeExe = await new SetupPuppet().Start();
                    await new RunWebsiteSnapshots().Start(folderPath, baseUrl, relativePaths, chromeExe, headless);
                }
                catch (Exception ex)
                {
                    if (i == 0)
                    {
                        /*
                         * Sometimes things get stuck for a variety of reasons. The most reliable 
                         * brue force fix is to just wipe and retry and it solves 99% of scenarios.
                         */
                        Console.WriteLine("Failure occurred launching trying to redownload");
                        chromeExe = await new SetupPuppet().Start(true);
                    }
                    else
                    {
                        throw ex;
                    }
                }
            }
        }
    }
}
