# Loaded with -r into every CRuby test/ file run by `suite.rb --suite mri`.
#
# test/unit creates a LeakChecker before the first test and runs it after
# every test. It looks for leaked Tempfiles and names leaked fds with
# ObjectSpace.each_object, which IronRuby cannot do (each_object covers
# modules and classes, or Ruby-derived objects under -X:ObjectSpace) - so the
# first call raised out of the runner and the whole file never ran. The leak
# report is a diagnostic, not a test result, so it is switched off on both
# interpreters. TruffleRuby's own copy of test/mri trims its harness the same
# way.
#
# CRuby's test/runner.rb loads test/unit before any test file (via
# tool/test/init.rb); a few files reference Test::Unit at the top before
# requiring it themselves, so it is loaded here too. init.rb itself is not
# used: it requires RubyVM-only checkers.

require 'test/unit'
require 'leakchecker'

class LeakChecker
  def initialize; end
  def check(test_name) = false
end
