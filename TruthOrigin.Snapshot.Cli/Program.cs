using System;
using System.Threading.Tasks;

namespace TruthOrigin.Snapshot.Cli
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
#if DEBUG
            string folderPath = @"";
            string? apiKey = null;
            Console.WriteLine($"[Debug] Using hardcoded folder path: {folderPath}");
#else
            var parsedArgs = ParseArgs(args);

            if (parsedArgs.ContainsKey("--help") || parsedArgs.ContainsKey("-h"))
            {
                PrintHelp();
                return 0;
            }

            if (!parsedArgs.TryGetValue("--folder", out string? folderPath) || string.IsNullOrWhiteSpace(folderPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[Error] --folder is required.\n");
                Console.ResetColor();
                PrintHelp();
                return 1;
            }

            parsedArgs.TryGetValue("--key", out string? apiKey);

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                Console.WriteLine($"[Info] API Key provided: {apiKey}");
            }
#endif
            try
            {
                await new SnapshotRun().Start(folderPath!, apiKey);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] Snapshot failed: {ex.Message}");
                Console.ResetColor();
                return 1;
            }

            Console.WriteLine("[Success] Snapshot completed.");
            return 0;
        }

        private static Dictionary<string, string?> ParseArgs(string[] args)
        {
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (arg.StartsWith("--") || arg.StartsWith("-"))
                {
                    string? value = (i + 1 < args.Length && !args[i + 1].StartsWith("-")) ? args[++i] : null;
                    dict[arg] = value;
                }
            }

            return dict;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("SnapshotTool - Static Snapshot Generator for WASM wwwroot folders");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  SnapshotTool --folder <path> [--key <apikey>] [--help]");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  --folder   REQUIRED. The path to the published wwwroot of your WASM project.");
            Console.WriteLine("  --key      OPTIONAL. API key for future authenticated services.");
            Console.WriteLine("  --help     Displays this help screen.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine(@"  SnapshotTool --folder ""C:\Sites\MyApp\wwwroot""");
            Console.WriteLine(@"  SnapshotTool --folder ""C:\Sites\MyApp\wwwroot"" --key abc123xyz");
            Console.WriteLine();
        }
    }
}
