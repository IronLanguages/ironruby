# Cross-checks Time#strftime against CRuby, directive by directive.
#
#   ruby     Util/strftime-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/strftime-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt
#
# Every combination of flags x width x directive is printed with its input, so a
# diff line names exactly which directive and flag combination disagrees.

DIRECTIVES = %w[
  a A b B c C d D e F g G h H I j k l L m M n N p P r R s S t T u U v V w W x X y Y z Z + %
]

FLAGS  = ["", "-", "_", "0", "^", "#", "0^", "_-", "-0", ":", "::"]
WIDTHS = ["", "1", "3", "10"]

TIMES = [
  ["utc",        Time.utc(2001, 2, 3, 4, 5, 6)],
  ["utc-frac",   Time.utc(2001, 2, 3, 4, 5, 6, 123456)],
  ["utc-pm",     Time.utc(2000, 12, 31, 22, 59, 59)],
  ["utc-jan1",   Time.utc(2021, 1, 1, 0, 0, 0)],
  ["utc-leapyr", Time.utc(2020, 2, 29, 12, 0, 0)],
  ["offset",     Time.new(2012, 1, 1, 0, 0, 0, 3660)],
  ["offset-neg", Time.new(2012, 6, 15, 13, 45, 1, -28800)],
  ["epoch",      Time.at(0).utc],
  ["pre-epoch",  Time.utc(1969, 11, 12, 13, 18, 57)],
]

TIMES.each do |label, time|
  DIRECTIVES.each do |directive|
    FLAGS.each do |flag|
      WIDTHS.each do |width|
        format = "%#{flag}#{width}#{directive}"
        begin
          result = time.strftime(format).inspect
        rescue StandardError => e
          result = "#{e.class}: #{e.message}"
        end
        puts "#{label} #{format.inspect} => #{result}"
      end
    end
  end
end
