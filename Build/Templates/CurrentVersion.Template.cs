using System;

namespace IronRuby {{
    public static class CurrentVersion {{
        public const int Major = {0};
        public const int Minor = {1};
        public const int Micro = {2};
        public const string ReleaseLevel = "{3}";
        public const int ReleaseSerial = {4};

        public const string ShortReleaseLevel = "{5}";

        public const string Series = "{0}.{1}";
        public const string DisplayVersion = "{6}";
        public const string DisplayName = "IronRuby {6}";

        public const string AssemblyVersion = "{0}.{1}.0.{7}";
        
        public const string AssemblyFileVersion = "{0}.{1}.{2}.{8}";
        public const string AssemblyInformationalVersion = "IronRuby {0}.{1}.{2} {3} {4}";

        public static readonly Version Version = new Version(Major, Minor, Micro);
    }}
}}
