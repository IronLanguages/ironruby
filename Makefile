# Shortcuts for the commands README.md ("Building") and .github/workflows/ci.yml run.
# The build itself is `dotnet build`; this only saves typing. It needs the DLR checkout at
# ../dlr with Build/dlr-patches applied, and libprism built in ../prism/build (see README.md).
#
#   make                  build ir (Debug, net10.0 and net11.0)
#   make release          the same, Release
#   make test             build and run the IronRuby.Tests suite; fails on any "> FAILED" line
#   make spec             ruby/spec's language suite (needs ./spec and ./mspec checkouts)
#   make clean
#
# TFM=net11.0 builds and runs one target framework only, CONFIG=Release the other
# configuration, DLR=/path/to/dlr/src/core a DLR checkout somewhere other than ../dlr.

CONFIG ?= Debug
TFM ?=
DLR ?=

DOTNET_FLAGS = -c $(CONFIG) $(if $(TFM),-p:IronRubyTargetFrameworks=$(TFM)) $(if $(DLR),-p:DlrSourceDir=$(DLR))
TEST_TFM = $(or $(TFM),net10.0)
TEST_DIR = Src/IronRuby.Tests/bin/$(CONFIG)/$(TEST_TFM)

.PHONY: build release test spec clean

build:
	dotnet build Src/Console/Ruby.Console.csproj $(DOTNET_FLAGS)

release:
	$(MAKE) build CONFIG=Release

test:
	@Util/check-dlr-patches.sh $(or $(DLR),../dlr/src/core) || \
	  echo "make test: WARNING: that DLR lacks Build/dlr-patches; the results are not CI's" >&2
	dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj $(DOTNET_FLAGS)
	cd $(TEST_DIR) && { ./IronRuby.Tests > results.txt 2>&1 || true; }
	@tail -n 2 $(TEST_DIR)/results.txt
	@failures=$$(grep -c '^> FAILED' $(TEST_DIR)/results.txt); \
	  echo "failure lines: $$failures ($(TEST_DIR)/results.txt)"; \
	  [ "$$failures" -eq 0 ] && grep -qE 'PASSED.?$$' $(TEST_DIR)/results.txt

spec: build
	RUBY_EXE="$(CURDIR)/ir.sh" IR_TFM=$(TEST_TFM) IR_CONFIG=$(CONFIG) \
	  ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language

clean:
	dotnet clean Src/IronRuby.Tests/IronRuby.Tests.csproj $(DOTNET_FLAGS)
	dotnet clean Src/Console/Ruby.Console.csproj $(DOTNET_FLAGS)
