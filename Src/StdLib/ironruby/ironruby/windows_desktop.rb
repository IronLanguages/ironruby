# Loads an assembly of the Windows Desktop runtime: Windows Forms or WPF.
#
# On .NET Framework these sat in the GAC of every Windows machine, and the shims that
# load them (System.Windows.Forms.rb, PresentationCore.rb, PresentationFramework.rb)
# chose the version by the CLR's. On .NET 5 and later they belong to Microsoft.WindowsDesktop.App,
# a framework of its own that ir has only when it was built for Windows (see
# Src/Console/Ruby.Console.csproj). A missing one used to surface as "cannot load such
# file -- System.Windows.Forms, Version=2.0.0.0, ...", a version number that sent people
# looking for the wrong thing.
module IronRuby
  module WindowsDesktop
    # No Version in the name: .NET 5 and later have exactly one of each assembly, and
    # binding would accept any older number anyway. Culture and PublicKeyToken stay, so
    # that the loader reads the string as an assembly name and not as a file to find.
    def self.require_assembly(name, public_key_token)
      require "#{name}, Culture=neutral, PublicKeyToken=#{public_key_token}"
    rescue LoadError
      reason = System::OperatingSystem.is_windows ?
        "and this IronRuby was built without it (IronRubyWindowsDesktop=false, or not for a win-* RID)" :
        "which exists only on Windows"
      raise LoadError, "#{name} is part of the Windows Desktop runtime (Microsoft.WindowsDesktop.App), #{reason}"
    end
  end
end
