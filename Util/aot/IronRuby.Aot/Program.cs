using System;

namespace IronRuby.Aot {
    internal static class Program {
        private static int Main(string[] args) {
            if (args.Length == 0) {
                Console.Error.WriteLine("usage: IronRuby.Aot census file.rb...");
                return 2;
            }
            var rest = args[1..];
            switch (args[0]) {
                case "census": return Census.Run(rest);
                case "compile": return CompileCommand.Run(rest);
                case "startup": return StartupCommand.Run(rest);
                case "selftest": return SelfTest.Run(rest.Length > 0 ? rest[0] : "aot-selftest");
                default:
                    Console.Error.WriteLine("unknown command " + args[0]);
                    return 2;
            }
        }
    }
}
