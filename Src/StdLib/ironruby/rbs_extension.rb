# frozen_string_literal: true
#
# The rbs gem's C extension, provided by IronRuby itself.
#
# rbs (Ruby's type-signature library, which ruby-lsp, steep, rdoc 8 and typeprof load) is
# Ruby over one C extension, and lib/rbs.rb ends with
#
#     require "rbs_extension"
#     require "rbs/parser_aux"
#     require "rbs/location_aux"
#
# This file is what that require finds: no compiler runs when IronRuby installs a gem
# (rubygems/defaults/ironruby.rb), so the installed gem has its Ruby and no
# rbs_extension.so, and the standard library directories come next on the load path.
# Everything else - the AST classes, RBS::Parser's public API, the type checker - is the
# installed gem's own Ruby, loaded as it is on CRuby; IronRuby supplies only what the C
# extension defines:
#
#   * RBS::Parser._parse_type, _parse_method_type, _parse_signature, _parse_type_params,
#     _parse_inline_leading_annotation, _parse_inline_trailing_annotation and _lex
#   * RBS::Location's primitives (rbs/location_aux.rb adds the rest)
#
# Like the extension, they run rbs's own C parser: librbs is the gem's src/ compiled
# unmodified, with a small shim in place of the Ruby C API glue
# (Src/Libraries/Rbs/native, built by Util/build-librbs.sh), and Src/Libraries/Rbs turns
# the AST it produces into the same Ruby objects ext/rbs_extension builds.
#
# The AST is tied to the rbs release the parser came from, so this loads only under that
# release's Ruby; any other version is refused with a LoadError naming both.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Rbs'

librbs = RBS::Parser.__send__(:_ironruby_librbs_version)
if defined?(RBS::VERSION) && RBS::VERSION != librbs
  raise LoadError, "cannot load such file -- rbs_extension (IronRuby provides rbs_extension for " \
                   "rbs #{librbs}; this is rbs #{RBS::VERSION}, use `gem \"rbs\", \"#{librbs}\"`)"
end
