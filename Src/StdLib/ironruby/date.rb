# MRI implements date as the "date_core" C extension. IronRuby provides the
# core in C# instead (Src/Libraries/Date), the same way it provides Digest,
# Zlib, StringIO, Json and BigDecimal.
#
# The C# side owns the representation (an astronomical Julian Day Number plus a
# calendar-reform "start"), the civil/ordinal/commercial conversions, the
# arithmetic and strftime.  The string *parsing* half is a big pile of regular
# expressions with no numeric content, so it stays in Ruby - see date/format.rb
# in this directory, which shadows the vendored 1.9 one.

# RubyGems' specification.rb contains `class Date; end` so that it can mention
# the constant without loading the date library.  Date is a CLR-backed class
# here and the runtime refuses to attach a constructor to a class that already
# exists as a plain Ruby one, so drop the stub before loading the assembly.
if defined?(::Date) && !::Date.respond_to?(:jd)
  Object.send(:remove_const, :Date)
end
if defined?(::DateTime) && !::DateTime.respond_to?(:jd)
  Object.send(:remove_const, :DateTime)
end

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Date'

class Date

  MONTHNAMES = [nil] + %w(January February March April May June July
                          August September October November December)
  DAYNAMES = %w(Sunday Monday Tuesday Wednesday Thursday Friday Saturday)
  ABBR_MONTHNAMES = [nil] + %w(Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec)
  ABBR_DAYNAMES = %w(Sun Mon Tue Wed Thu Fri Sat)

  [MONTHNAMES, DAYNAMES, ABBR_MONTHNAMES, ABBR_DAYNAMES].each do |xs|
    xs.each{|x| x.freeze unless x.nil?}.freeze
  end

  UNIX_EPOCH_IN_CJD = 2440588

  # Retained for compatibility: modern MRI uses Float::INFINITY for
  # Date::JULIAN / Date::GREGORIAN, but Date::Infinity is still a public class.
  class Infinity < Numeric

    def initialize(d=1) @d = d <=> 0 end

    def d() @d end

    protected :d

    def zero?() false end
    def finite?() false end
    def infinite?() d.nonzero? end
    def nan?() d.zero? end

    def abs() self.class.new end

    def -@ () self.class.new(-d) end
    def +@ () self.class.new(+d) end

    def <=> (other)
      case other
      when Infinity; return d <=> other.d
      when Numeric; return d
      else
        begin
          l, r = other.coerce(self)
          return l <=> r
        rescue NoMethodError
        end
      end
      nil
    end

    def coerce(other)
      case other
      when Infinity; return other, self
      else return -d, d
      end
    end

    def to_f
      return 0 if @d == 0
      @d > 0 ? Float::INFINITY : -Float::INFINITY
    end

  end

  # Time is proleptic Gregorian, so the civil parts have to be read off the
  # Gregorian projection of this date (they differ from #year/#mon/#mday for
  # anything before the calendar reform).
  def to_time
    g = gregorian
    Time.local(g.year, g.mon, g.mday)
  end

end

require 'date/format'

class Date

  class << self

    def __fdoy(y, sg) # :nodoc:
      ordinal(y, 1, sg).jd
    end

    def weeknum_to_jd(y, w, d, f=0, sg=Date::ITALY) # :nodoc:
      a = __fdoy(y, sg) + 6
      (a - ((a - f) + 1) % 7 - 7) + 7 * w + d
    end

    def jd_to_weeknum(jd, f=0, sg=Date::ITALY) # :nodoc:
      y = Date.jd(jd, sg).year
      a = __fdoy(y, sg) + 6
      w, d = (jd - (a - ((a - f) + 1) % 7) + 7).divmod(7)
      [y, w, d]
    end

    def _valid_weeknum?(y, w, d, f, sg=Date::ITALY) # :nodoc:
      d += 7 if d < 0
      if w < 0
        ny, nw, _nd = jd_to_weeknum(weeknum_to_jd(y + 1, 1, f, f, sg) + w * 7, f, sg)
        return nil unless ny == y
        w = nw
      end
      jd = weeknum_to_jd(y, w, d, f, sg)
      return nil unless [y, w, d] == jd_to_weeknum(jd, f, sg)
      jd
    end

    def rewrite_frags(elem) # :nodoc:
      elem ||= {}
      if seconds = elem[:seconds]
        d,   fr = seconds.divmod(86400)
        h,   fr = fr.divmod(3600)
        min, fr = fr.divmod(60)
        s,   fr = fr.divmod(1)
        elem[:jd] = UNIX_EPOCH_IN_CJD + d
        elem[:hour] = h
        elem[:min] = min
        elem[:sec] = s
        elem[:sec_fraction] = fr
        elem.delete(:seconds)
        elem.delete(:offset)
      end
      elem
    end

    def complete_frags(elem) # :nodoc:
      i = 0
      g = [[:time, [:hour, :min, :sec]],
           [nil, [:jd]],
           [:ordinal, [:year, :yday, :hour, :min, :sec]],
           [:civil, [:year, :mon, :mday, :hour, :min, :sec]],
           [:commercial, [:cwyear, :cweek, :cwday, :hour, :min, :sec]],
           [:wday, [:wday, :hour, :min, :sec]],
           [:wnum0, [:year, :wnum0, :wday, :hour, :min, :sec]],
           [:wnum1, [:year, :wnum1, :wday, :hour, :min, :sec]],
           [nil, [:cwyear, :cweek, :wday, :hour, :min, :sec]],
           [nil, [:year, :wnum0, :cwday, :hour, :min, :sec]],
           [nil, [:year, :wnum1, :cwday, :hour, :min, :sec]]].
        collect{|k, a| e = elem.values_at(*a).compact; [k, a, e]}.
        select{|k, a, e| e.size > 0}.
        sort_by{|k, a, e| [e.size, i -= 1]}.last

      d = nil

      if g && g[0] && (g[1].size - g[2].size) != 0
        d ||= Date.today

        case g[0]
        when :ordinal
          elem[:year] ||= d.year
          elem[:yday] ||= 1
        when :civil
          g[1].each do |e|
            break if elem[e]
            elem[e] = d.__send__(e)
          end
          elem[:mon]  ||= 1
          elem[:mday] ||= 1
        when :commercial
          g[1].each do |e|
            break if elem[e]
            elem[e] = d.__send__(e)
          end
          elem[:cweek] ||= 1
          elem[:cwday] ||= 1
        when :wday
          elem[:jd] ||= (d - d.wday + elem[:wday]).jd
        when :wnum0
          g[1].each do |e|
            break if elem[e]
            elem[e] = d.__send__(e)
          end
          elem[:wnum0] ||= 0
          elem[:wday]  ||= 0
        when :wnum1
          g[1].each do |e|
            break if elem[e]
            elem[e] = d.__send__(e)
          end
          elem[:wnum1] ||= 0
          elem[:wday]  ||= 1
        end
      end

      if g && g[0] == :time
        if self <= DateTime
          d ||= Date.today
          elem[:jd] ||= d.jd
        end
      end

      elem[:hour] ||= 0
      elem[:min]  ||= 0
      elem[:sec]  ||= 0
      # 1.9 clamped this to 59; modern MRI rejects a 60th second instead.

      elem
    end

    def valid_date_frags?(elem, sg) # :nodoc:
      v = elem[:jd]
      return v if v && Date.valid_jd?(v, sg)

      y, yd = elem[:year], elem[:yday]
      if y && yd && Date.valid_ordinal?(y, yd, sg)
        return Date.ordinal(y, yd, sg).jd
      end

      y, m, d = elem[:year], elem[:mon], elem[:mday]
      if y && m && d && Date.valid_civil?(y, m, d, sg)
        return Date.civil(y, m, d, sg).jd
      end

      cy, cw, cd = elem[:cwyear], elem[:cweek], elem[:cwday]
      if cd.nil? && elem[:wday]
        cd = elem[:wday].nonzero? || 7
      end
      if cy && cw && cd && Date.valid_commercial?(cy, cw, cd, sg)
        return Date.commercial(cy, cw, cd, sg).jd
      end

      y, w, d = elem[:year], elem[:wnum0], elem[:wday]
      d = elem[:cwday] % 7 if d.nil? && elem[:cwday]
      if y && w && d
        jd = _valid_weeknum?(y, w, d, 0, sg)
        return jd if jd
      end

      y, w, d = elem[:year], elem[:wnum1], elem[:wday]
      d = (d - 1) % 7 if d
      d = (elem[:cwday] - 1) % 7 if d.nil? && elem[:cwday]
      if y && w && d
        jd = _valid_weeknum?(y, w, d, 1, sg)
        return jd if jd
      end

      nil
    end

    def valid_time_frags?(elem) # :nodoc:
      h, min, s = elem[:hour], elem[:min], elem[:sec]
      h ||= 0; min ||= 0; s ||= 0
      return nil unless h.between?(0, 24) && min.between?(0, 59) && s.between?(0, 59)
      [h, min, s]
    end

    def new_by_frags(elem, sg) # :nodoc:
      elem = rewrite_frags(elem)
      elem = complete_frags(elem)
      jdn = valid_date_frags?(elem, sg)
      raise Date::Error, 'invalid date' unless jdn
      jd(jdn, sg)
    end

    # MRI's Date._parse and friends take a String (or anything with #to_str),
    # answer an empty Hash when nothing matched, and raise TypeError otherwise.
    def __coerce_str(str) # :nodoc:
      return str if str.is_a?(String)
      return '' if str.nil? # MRI answers {} for nil, and the callers then raise Date::Error
      if str.respond_to?(:to_str)
        s = str.to_str
        return s if s.is_a?(String)
      end
      raise TypeError, "no implicit conversion of #{str.class} into String"
    end

    %w(_iso8601 _rfc3339 _xmlschema _rfc2822 _httpdate _jisx0301).each do |name|
      alias_method "__raw#{name}", name
      define_method(name) do |str|
        __send__("__raw#{name}", __coerce_str(str)) || {}
      end
    end
    alias_method :_rfc822, :_rfc2822

    alias_method :__raw_parse, :_parse
    def _parse(str, comp=true)
      __raw_parse(__coerce_str(str), comp) || {}
    end

    # Any Numeric is a valid Julian day number.
    def valid_jd?(jd, sg=Date::ITALY)
      jd.is_a?(Numeric)
    end

    def parse(str='-4712-01-01', comp=true, sg=Date::ITALY)
      elem = _parse(str, comp)
      new_by_frags(elem, sg)
    end

    def strptime(str='-4712-01-01', fmt='%F', sg=Date::ITALY)
      elem = _strptime(str, fmt)
      raise Date::Error, 'invalid date' unless elem
      new_by_frags(elem, sg)
    end

    def iso8601(str='-4712-01-01', sg=Date::ITALY)
      new_by_frags(_iso8601(str), sg)
    end

    def rfc3339(str='-4712-01-01T00:00:00+00:00', sg=Date::ITALY)
      new_by_frags(_rfc3339(str), sg)
    end

    def xmlschema(str='-4712-01-01', sg=Date::ITALY)
      new_by_frags(_xmlschema(str), sg)
    end

    def rfc2822(str='Mon, 1 Jan -4712 00:00:00 +0000', sg=Date::ITALY)
      new_by_frags(_rfc2822(str), sg)
    end
    alias_method :rfc822, :rfc2822

    def httpdate(str='Mon, 01 Jan -4712 00:00:00 GMT', sg=Date::ITALY)
      new_by_frags(_httpdate(str), sg)
    end

    def jisx0301(str='-4712-01-01', sg=Date::ITALY)
      new_by_frags(_jisx0301(str), sg)
    end

    private :__fdoy, :weeknum_to_jd, :jd_to_weeknum, :_valid_weeknum?,
            :rewrite_frags, :complete_frags, :valid_date_frags?,
            :valid_time_frags?, :new_by_frags

  end

end

class DateTime < Date

  class << self

    def new_by_frags(elem, sg) # :nodoc:
      elem = rewrite_frags(elem)
      elem = complete_frags(elem)
      jdn = valid_date_frags?(elem, sg)
      tm = valid_time_frags?(elem)
      raise Date::Error, 'invalid date' unless jdn && tm
      h, min, s = tm
      s += (elem[:sec_fraction] || 0)
      of = Rational(elem[:offset] || 0, 86400)
      jd(jdn, h, min, s, of, sg)
    end

    def parse(str='-4712-01-01T00:00:00+00:00', comp=true, sg=Date::ITALY)
      new_by_frags(_parse(str, comp), sg)
    end

    def strptime(str='-4712-01-01T00:00:00+00:00', fmt='%FT%T%z', sg=Date::ITALY)
      elem = _strptime(str, fmt)
      raise Date::Error, 'invalid date' unless elem
      new_by_frags(elem, sg)
    end

    def _strptime(str, fmt='%FT%T%z')
      super(str, fmt)
    end

    def iso8601(str='-4712-01-01T00:00:00+00:00', sg=Date::ITALY)
      new_by_frags(_iso8601(str), sg)
    end

    def rfc3339(str='-4712-01-01T00:00:00+00:00', sg=Date::ITALY)
      new_by_frags(_rfc3339(str), sg)
    end

    def xmlschema(str='-4712-01-01T00:00:00+00:00', sg=Date::ITALY)
      new_by_frags(_xmlschema(str), sg)
    end

    def rfc2822(str='Mon, 1 Jan -4712 00:00:00 +0000', sg=Date::ITALY)
      new_by_frags(_rfc2822(str), sg)
    end
    alias_method :rfc822, :rfc2822

    def httpdate(str='Mon, 01 Jan -4712 00:00:00 GMT', sg=Date::ITALY)
      new_by_frags(_httpdate(str), sg)
    end

    def jisx0301(str='-4712-01-01T00:00:00+00:00', sg=Date::ITALY)
      new_by_frags(_jisx0301(str), sg)
    end

    private :new_by_frags

  end

  # Since Ruby 2.4 the Time keeps the DateTime's own offset (date_core.c's
  # datetime_to_time) rather than being moved into the local zone.
  def to_time
    g = gregorian
    sec = g.sec_fraction.zero? ? g.sec : g.sec + g.sec_fraction
    Time.new(g.year, g.mon, g.mday, g.hour, g.min, sec, (g.offset * 86400).to_i)
  end

end

# The C# side registers each name separately; MRI defines them as real aliases
# and ruby/spec checks `instance_method(:mday) == instance_method(:day)`.
class Date
  alias_method :mday, :day
  alias_method :mon, :month
  alias_method :ctime, :asctime
  alias_method :succ, :next
  alias_method :rfc822, :rfc2822
  alias_method :xmlschema, :iso8601
  # not rfc3339: Date#rfc3339 is a date and a time (2024-02-29T00:00:00+00:00), Date#iso8601 a date
  class << self
    alias_method :valid_date?, :valid_civil?
    alias_method :rfc822, :rfc2822
  end
end

class DateTime
  alias_method :min, :minute
  alias_method :sec, :second
  alias_method :second_fraction, :sec_fraction
  alias_method :xmlschema, :iso8601
  alias_method :rfc3339, :iso8601
  class << self
    alias_method :rfc822, :rfc2822
  end
end

class Time

  # Time's civil parts are proleptic Gregorian, so build the Date in the
  # GREGORIAN calendar and then move the reform point to ITALY: that keeps the
  # Julian day and only reinterprets the civil parts.
  def to_date
    Date.civil(year, mon, mday, Date::GREGORIAN).new_start(Date::ITALY)
  end

  def to_datetime
    # Time#nsec is not implemented by IronRuby's Time yet; usec is.
    ns = respond_to?(:nsec) ? nsec : usec * 1000
    DateTime.civil(year, mon, mday, hour, min,
                   sec + Rational(ns, 1_000_000_000),
                   Rational(utc_offset, 86400),
                   Date::GREGORIAN).new_start(Date::ITALY)
  end

  def to_time
    self
  end

end
