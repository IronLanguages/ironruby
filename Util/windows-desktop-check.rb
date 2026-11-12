# Windows Forms and WPF load, from the Windows Desktop runtime ir was built with.
#
# Run by CI's Windows smoke test and by package-release.sh on a win-* archive before
# it is packed. Without the framework reference in Src/Console/Ruby.Console.csproj
# everything else still works, so nothing else would notice; the 4.0.0-preview1
# archive shipped that way.
require "System.Windows.Forms"
require "PresentationFramework"

[System::Windows::Forms::Form, System::Windows::Window].each do |type|
  clr = type.to_clr_type
  puts "#{clr.full_name}: #{clr.assembly.location}"
end
