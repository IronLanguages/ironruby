# frozen_string_literal: true
#
# The zstd-ruby gem's C extension, provided by IronRuby itself.
#
# zstd-ruby 1.5.7.0 is Ruby over one C extension, zstd-ruby/zstdruby, which binds
# the libzstd 1.5.7 it bundles.  Here that extension is C# (Src/Libraries/Zstd)
# over ZstdSharp, a C# port of the same libzstd release: Zstd.compress,
# decompress, the skippable-frame functions, Zstd::CDict and Zstd::DDict, and
# Zstd::StreamingCompress / Zstd::StreamingDecompress.
#
# Only the extension is provided.  The gem is installed as usual (its extension
# is not compiled on IronRuby, see rubygems/defaults/ironruby.rb), and its
# lib/zstd-ruby.rb requires "zstd-ruby/zstdruby", which finds this file: a .rb
# anywhere on $LOAD_PATH wins over the gem's zstdruby.so.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Zstd'

module Zstd
  class StreamingCompress
    # Upstream defines these four as the C functions behind IO#<<, #print,
    # #printf and #puts, which write to their receiver through its #write.
    # Same here, in Ruby: every piece goes through StreamingCompress#write.

    def <<(str)
      write(str)
      self
    end

    def print(*args)
      args = [$_] if args.empty?
      ofs = $,
      args.each_with_index do |arg, i|
        write(ofs) if ofs && i > 0
        write(arg)
      end
      write($\) if $\
      nil
    end

    def printf(*args)
      write(format(*args))
      nil
    end

    def puts(*args)
      if args.empty?
        write("\n")
        return nil
      end
      __ironruby_puts(args, {})
      nil
    end

    private

    def __ironruby_puts(args, seen)
      args.each do |arg|
        if arg.is_a?(String)
          line = arg
        else
          ary = Array.try_convert(arg)
          if ary
            if seen[ary.object_id]
              write("[...]", "\n")
            else
              seen[ary.object_id] = true
              begin
                __ironruby_puts(ary, seen)
              ensure
                seen.delete(ary.object_id)
              end
            end
            next
          end
          line = arg.to_s
          line = Kernel.instance_method(:to_s).bind_call(arg) unless line.is_a?(String)
        end
        if line.empty? || !line.end_with?("\n")
          write(line, "\n")
        else
          write(line)
        end
      end
    end
  end
end
