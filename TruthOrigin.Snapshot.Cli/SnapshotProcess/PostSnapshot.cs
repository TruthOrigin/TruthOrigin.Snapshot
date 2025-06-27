using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TruthOrigin.Snapshot.Cli.SnapshotProcess
{
    internal class PostSnapshot
    {
        public async Task Start(string folderPath, List<string> relativePaths, List<(string url, string html)>? snapshots)
        {
            if (snapshots != null)
            {
                foreach (var snapshot in snapshots)
                {
                    await SaveSnapshot(folderPath, snapshot.url, snapshot.html);
                }
            }

            UpdateHeadersAndRedirects(folderPath, relativePaths);
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
    }
}
