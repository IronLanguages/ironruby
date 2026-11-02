// Hand-maintained. The legacy build generated this from Build/Templates plus
// CurrentVersion.props; the modern SDK build has no such step.
//
// Major.Minor is the Ruby version IronRuby implements: IronRuby 4.0 is Ruby 4.0, and
// moves to 4.1 when RUBY_VERSION does. Micro counts IronRuby's own releases for that
// Ruby version; it is not CRuby's patch level.
using System;

namespace IronRuby {
    public static class CurrentVersion {
        public const int Major = 4;
        public const int Minor = 0;
        public const int Micro = 3;
        public const string ReleaseLevel = "final";
        public const int ReleaseSerial = 0;

        public const string ShortReleaseLevel = "final";

        public const string Series = "4.0";
        public const string DisplayVersion = "4.0.3";
        public const string DisplayName = "IronRuby 4.0.3";

        // Stays 4.0.0.0 across 4.0.x, so that what was built against one 4.0 release binds
        // to the next; the file version and the display version say which release it is.
        public const string AssemblyVersion = "4.0.0.0";

        public const string AssemblyFileVersion = "4.0.3.0";
        public const string AssemblyInformationalVersion = "IronRuby 4.0.3";

        public static readonly Version Version = new Version(Major, Minor, Micro);
    }
}
