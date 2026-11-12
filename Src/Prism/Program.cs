using System;
using System.IO;
using System.Text.Json;

namespace IronRuby.Prism {
    internal static class Program {
        private static int Main(string[] args) {
            if (args.Length == 2 && args[0] == "--run") {
                // execute a Ruby file with prism as the front end
                IronRuby.Runtime.RubyContext.AlternativeParser = PrismAstBridge.Parse;
                var engine = IronRuby.Ruby.CreateEngine();
                engine.CreateScriptSourceFromFile(System.IO.Path.GetFullPath(args[1])).Execute();
                return 0;
            }

            if (args.Length == 2 && args[0] == "--sweep") {
                // bridge-coverage sweep: try mapping every .rb under a directory, tally unsupported nodes
                var files = Directory.GetFiles(args[1], "*.rb", SearchOption.AllDirectories);
                int ok = 0, failed = 0;
                var histogram = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var file in files) {
                    try {
                        PrismAstBridge.ParseText(File.ReadAllText(file), file);
                        ok++;
                    } catch (Exception e) {
                        failed++;
                        string key = e is NotSupportedException ? e.Message.Split('(')[0].Trim() : e.GetType().Name + ": " + e.Message;
                        histogram.TryGetValue(key, out int n);
                        histogram[key] = n + 1;
                    }
                }
                Console.WriteLine($"{ok}/{files.Length} files bridged ({failed} failed)");
                foreach (var kv in System.Linq.Enumerable.OrderByDescending(histogram, kv => kv.Value)) {
                    Console.WriteLine($"{kv.Value,5}  {kv.Key}");
                }
                return 0;
            }

            if (args.Length == 2 && args[0] == "--json") {
                Console.WriteLine(PrismParser.ParseToJson(File.ReadAllText(args[1])));
                return 0;
            }

            string source = args.Length > 0 ? File.ReadAllText(args[0]) : "puts 1 + 2\n[1, 2, 3].each { |x| puts x * 2 }\n";

            byte[] serialized = PrismParser.ParseSerialized(source);
            string magic = System.Text.Encoding.ASCII.GetString(serialized, 0, 5);
            Console.WriteLine($"serialized: {serialized.Length} bytes, magic={magic}, version={serialized[5]}.{serialized[6]}.{serialized[7]}");

            string json = PrismParser.ParseToJson(source);
            using var doc = JsonDocument.Parse(json);
            int nodes = CountNodes(doc.RootElement);
            Console.WriteLine($"json AST: {json.Length} chars, {nodes} nodes, root type = {doc.RootElement.GetProperty("type").GetString()}");
            DumpTypes(doc.RootElement, 0);
            return 0;
        }

        private static int CountNodes(JsonElement element) {
            int count = 0;
            if (element.ValueKind == JsonValueKind.Object) {
                if (element.TryGetProperty("type", out _)) count++;
                foreach (var prop in element.EnumerateObject()) count += CountNodes(prop.Value);
            } else if (element.ValueKind == JsonValueKind.Array) {
                foreach (var item in element.EnumerateArray()) count += CountNodes(item);
            }
            return count;
        }

        private static void DumpTypes(JsonElement element, int depth) {
            if (depth > 6) return;
            if (element.ValueKind == JsonValueKind.Object) {
                if (element.TryGetProperty("type", out var type)) {
                    Console.WriteLine(new string(' ', depth * 2) + type.GetString());
                    depth++;
                }
                foreach (var prop in element.EnumerateObject()) DumpTypes(prop.Value, depth);
            } else if (element.ValueKind == JsonValueKind.Array) {
                foreach (var item in element.EnumerateArray()) DumpTypes(item, depth);
            }
        }
    }
}
