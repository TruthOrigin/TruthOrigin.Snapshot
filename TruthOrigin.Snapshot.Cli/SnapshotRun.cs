using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TruthOrigin.Snapshot.Cli
{
    public class SnapshotRun
    {
        public async Task Start(string folderPath, string? apiKey)
        {
            Console.WriteLine($"[Info] Validating folder: {folderPath}");

            // Validate directory
            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException($"The specified folder does not exist: {folderPath}");

            try
            {
                // Check read/write permission
                string testFile = Path.Combine(folderPath, ".snapshot_permission_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                throw new UnauthorizedAccessException("The application does not have read/write permissions for this folder.", ex);
            }

            Console.WriteLine("[Success] Folder is accessible with read/write permissions.");

            // Check index.html
            string indexPath = Path.Combine(folderPath, "index.html");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("Missing required file: index.html. This may not be a valid published WASM folder.");

            Console.WriteLine("[Success] index.html found.");

            // Check robots.txt
            string robotsPath = Path.Combine(folderPath, "robots.txt");
            if (!File.Exists(robotsPath))
                throw new FileNotFoundException("Missing required file: robots.txt. Snapshot protocol requires this for sitemap discovery.");

            Console.WriteLine("[Success] robots.txt found. Parsing...");

            var sitemapUrls = ParseSitemapsFromRobots(robotsPath);
            if (sitemapUrls.Count == 0)
                throw new Exception("No sitemaps found in robots.txt.");

            Console.WriteLine($"[Info] Found {sitemapUrls.Count} sitemap(s) in robots.txt.");

            // Recursively resolve sitemaps
            var allUrls = new HashSet<string>();
            foreach (var sitemapUrl in sitemapUrls)
            {
                var urls = await ParseSitemapAsync(folderPath, sitemapUrl);
                foreach (var u in urls)
                    allUrls.Add(u);
            }

            if (allUrls.Count == 0)
                throw new Exception("No valid URLs found in any sitemap.");

            Console.WriteLine($"[Success] Total URLs discovered: {allUrls.Count}");

            // Remove domain to convert to relative paths
            var relativePaths = allUrls
                .Select(url => GetRelativePathFromUrl(url))
                .Distinct()
                .ToList();

            Console.WriteLine("[Info] Passing relative URLs to snapshot runner...");
            await new Snapshot().Start(relativePaths, folderPath);
        }

        private List<string> ParseSitemapsFromRobots(string robotsPath)
        {
            var sitemapUrls = new List<string>();

            foreach (var line in File.ReadAllLines(robotsPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
                {
                    var url = trimmed.Substring(8).Trim();
                    if (!string.IsNullOrWhiteSpace(url))
                        sitemapUrls.Add(url);
                }
            }

            return sitemapUrls;
        }

        private async Task<List<string>> ParseSitemapAsync(string folderPath, string sitemapUrl)
        {
            string localPath = GetLocalPathFromUrl(folderPath, sitemapUrl);

            if (!File.Exists(localPath))
                throw new FileNotFoundException($"Sitemap not found at expected local path: {localPath}");

            var result = new List<string>();
            var invalidCasingUrls = new List<string>();
            var trailingSlashUrls = new List<string>();

            try
            {
                using var stream = File.OpenRead(localPath);
                var doc = await XDocument.LoadAsync(stream, LoadOptions.None, default);

                var root = doc.Root;
                if (root == null) return result;

                if (root.Name.LocalName == "sitemapindex")
                {
                    foreach (var sitemap in root.Elements().Where(e => e.Name.LocalName == "sitemap"))
                    {
                        var loc = sitemap.Element(XName.Get("loc", root.Name.NamespaceName))?.Value?.Trim();
                        if (!string.IsNullOrEmpty(loc))
                        {
                            var nested = await ParseSitemapAsync(folderPath, loc);
                            result.AddRange(nested);
                        }
                    }
                }
                else if (root.Name.LocalName == "urlset")
                {
                    foreach (var url in root.Elements().Where(e => e.Name.LocalName == "url"))
                    {
                        var loc = url.Element(XName.Get("loc", root.Name.NamespaceName))?.Value?.Trim();
                        if (!string.IsNullOrEmpty(loc))
                        {
                            var relative = GetRelativePathFromUrl(loc);

                            if (!relative.Equals(relative.ToLowerInvariant()))
                                invalidCasingUrls.Add(loc);

                            if (loc.EndsWith("/"))
                                trailingSlashUrls.Add(loc);

                            result.Add(loc);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to parse sitemap: {localPath}", ex);
            }

            if (invalidCasingUrls.Any())
            {
                var sb = new StringBuilder();
                sb.AppendLine($"❌ Sitemap validation error in `{localPath}`:");

                if (invalidCasingUrls.Any())
                {
                    sb.AppendLine($"- {invalidCasingUrls.Count} URL(s) are not lowercase, which may cause indexing issues:");
                    foreach (var url in invalidCasingUrls)
                        sb.AppendLine($"   - {url}");
                }
                throw new Exception(sb.ToString());
            }

            return result;
        }


        private string GetLocalPathFromUrl(string folderPath, string url)
        {
            try
            {
                Uri uri = new Uri(url, UriKind.RelativeOrAbsolute);
                string localRelativePath = uri.IsAbsoluteUri ? uri.AbsolutePath.TrimStart('/') : uri.ToString().TrimStart('/');
                return Path.Combine(folderPath, localRelativePath.Replace('/', Path.DirectorySeparatorChar));
            }
            catch
            {
                throw new Exception($"Could not parse sitemap path from URL: {url}");
            }
        }

        private string GetRelativePathFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url, UriKind.RelativeOrAbsolute);
                return uri.IsAbsoluteUri ? uri.AbsolutePath.TrimStart('/') : uri.ToString().TrimStart('/');
            }
            catch
            {
                Console.WriteLine($"[Warning] Skipping malformed URL: {url}");
                return string.Empty;
            }
        }      
    }
}