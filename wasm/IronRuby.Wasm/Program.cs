// The browser-wasm host for IronRuby. Everything the page uses is in Interop.cs ([JSExport]);
// Main only has to exist - the JavaScript side boots the runtime and calls the exports.
public static class Program {
    public static int Main() => 0;
}
