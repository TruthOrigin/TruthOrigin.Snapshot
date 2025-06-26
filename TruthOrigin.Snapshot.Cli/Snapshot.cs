using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace TruthOrigin.Snapshot.Cli
{
    internal class Snapshot
    {
        public async Task Start(List<string> urls, string folderPath)
        {
            int port = GetAvailablePort();
            string baseUrl = $"http://localhost:{port}";

            var host = new WebHostBuilder()
                .UseKestrel()
                .UseUrls(baseUrl)
                .Configure(app =>
                {
                    var fileProvider = new PhysicalFileProvider(folderPath);

                    app.Use(async (context, next) =>
                    {
                        Console.WriteLine($"[Request] {context.Request.Method} {context.Request.Path}");
                        await next.Invoke();
                    });

                    app.Use(async (context, next) =>
                    {
                        await next();

                        if (context.Response.StatusCode == 404 &&
                            !Path.HasExtension(context.Request.Path) &&
                            !context.Request.Path.Value!.StartsWith("/api"))
                        {
                            Console.WriteLine("[Fallback] Redirecting to /index.html");

                            context.Response.StatusCode = 200;
                            context.Request.Path = "/index.html";
                            await app.Build().Invoke(context);
                        }
                    });

                    app.UseDefaultFiles();
                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = fileProvider,
                        ServeUnknownFileTypes = true,
                        DefaultContentType = "application/octet-stream"
                    });
                })
                .Build();

            Console.WriteLine($"[Server] Hosting static WASM app at: {baseUrl}");
            foreach (var url in urls)
                Console.WriteLine($"[Snapshot] Will access: {baseUrl}/{url}");

            // Start server in background
            var serverTask = host.RunAsync();

            // Start snapshot puppet after small delay to ensure server is ready
            await WaitUntilAvailable(baseUrl);

            var puppet = new SnapshotPuppet();
            await puppet.Start(folderPath, baseUrl, urls);

            // Optionally shut down server after puppet is done
            Console.WriteLine("[Server] Snapshot complete. Shutting down...");
            await host.StopAsync();
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static async Task WaitUntilAvailable(string baseUrl)
        {
            using var http = new HttpClient();

            for (int i = 0; i < 25; i++) // try for ~5 seconds max
            {
                try
                {
                    var response = await http.GetAsync(baseUrl);
                    if ((int)response.StatusCode < 500)
                    {
                        Console.WriteLine("[Server] Confirmed ready.");
                        return;
                    }
                }
                catch
                {
                    // swallow and retry
                }

                await Task.Delay(200);
            }

            throw new Exception("Server did not become available in time.");
        }

    }

}
