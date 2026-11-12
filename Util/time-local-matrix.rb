# Cross-checks Time's local zone - utc_offset, dst? and the wall clock - against CRuby,
# for the machine's own zone (TZ unset) across a year, through both daylight saving
# changes of the European and the US rules, and for wall-clock readings in the gaps and
# overlaps.
#
#   ruby     Util/time-local-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/time-local-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt
#
# Run both with TZ unset and on the same machine: the expected values are whatever that
# machine's zone says, so the script is only interesting in a zone that has daylight saving
# time (Europe/Zurich, America/New_York, ...). A zone that IronRuby resolves to a fixed
# offset passes in winter and fails every summer line. Zone names are left out because
# CRuby on Windows reports the C runtime's names, not the POSIX abbreviations.

def fmt(time)
  format("%04d-%02d-%02d %02d:%02d:%02d off=%d dst=%p", time.year, time.month, time.day,
    time.hour, time.min, time.sec, time.utc_offset, time.dst?)
end

def t(label)
  puts "#{label}: #{yield}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end

# Time.now: the offset must be the one in force now, not the zone's standard offset.
now = Time.now
t("now consistent") { (now.utc_offset == Time.at(now.to_i).utc_offset).inspect }
t("now wall clock") { ((now.to_i + now.utc_offset) % 86400 == (now.hour * 3600 + now.min * 60 + now.sec)).inspect }

# Time.at and #localtime for the middle and the 1st of every month.
year = 2026
(1..12).each do |month|
  [1, 15].each do |day|
    instant = Time.utc(year, month, day, 12, 0, 0).to_i
    t("at #{year}-#{month}-#{day} 12:00Z") { fmt(Time.at(instant)) }
    t("localtime #{year}-#{month}-#{day} 12:00Z") { fmt(Time.utc(year, month, day, 12, 0, 0).localtime) }
  end
end

# Every hour across the days of the changes: last Sunday of March and of October (EU),
# second Sunday of March and first Sunday of November (US), in UTC so the labels do not
# depend on the zone.
[[3, 28], [3, 29], [3, 7], [3, 8], [10, 24], [10, 25], [10, 31], [11, 1]].each do |month, day|
  base = Time.utc(year, month, day).to_i
  24.times do |hour|
    t("at #{year}-#{month}-#{day} #{hour}:30Z") { fmt(Time.at(base + hour * 3600 + 1800)) }
  end
end

# Wall-clock constructors, including the readings that do not exist (the clocks went
# forward) and the ones that occur twice (the clocks went back) under either rule.
WALL_CLOCKS = [
  [2026, 1, 15, 12, 0, 0], [2026, 7, 15, 12, 0, 0], [2026, 10, 12, 21, 20, 46],
  [2026, 3, 29, 1, 30, 0], [2026, 3, 29, 2, 30, 0], [2026, 3, 29, 3, 30, 0],
  [2026, 10, 25, 1, 30, 0], [2026, 10, 25, 2, 30, 0], [2026, 10, 25, 3, 30, 0],
  [2026, 3, 8, 1, 30, 0], [2026, 3, 8, 2, 30, 0], [2026, 3, 8, 3, 30, 0],
  [2026, 11, 1, 0, 30, 0], [2026, 11, 1, 1, 30, 0], [2026, 11, 1, 2, 30, 0],
  [1999, 7, 1, 0, 0, 0], [2040, 7, 1, 0, 0, 0],
]
WALL_CLOCKS.each do |args|
  label = args.join(",")
  t("local #{label}") { fmt(Time.local(*args)) }
  t("mktime #{label}") { fmt(Time.mktime(*args)) }
  t("new #{label}") { fmt(Time.new(*args)) }
end

# A local Time keeps its offset through arithmetic that crosses a change.
t("summer - 120 days") { fmt(Time.local(2026, 7, 15, 12) - 120 * 86400) }
t("winter + 180 days") { fmt(Time.local(2026, 1, 15, 12) + 180 * 86400) }
t("to_a summer") { Time.local(2026, 7, 15, 12).to_a.values_at(0..8).inspect }
t("strftime %z summer") { Time.local(2026, 7, 15, 12).strftime("%z") }
t("strftime %z winter") { Time.local(2026, 1, 15, 12).strftime("%z") }
t("inspect summer") { Time.local(2026, 7, 15, 12).inspect.sub(/ [A-Za-z].*\z/, "") }
