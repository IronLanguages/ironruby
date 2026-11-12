require 'tzinfo'

tz = TZInfo::Timezone.get('Europe/Berlin')
puts tz.identifier
puts tz.canonical_identifier

t = Time.utc(2020, 7, 1, 12, 0, 0)
puts tz.utc_to_local(t).strftime('%Y-%m-%d %H:%M:%S')
puts tz.period_for_utc(t).abbreviation.to_s
puts tz.period_for_utc(t).observed_utc_offset
puts tz.period_for_utc(t).dst?

w = Time.utc(2020, 1, 1, 12, 0, 0)
puts tz.utc_to_local(w).strftime('%Y-%m-%d %H:%M:%S')
puts tz.period_for_utc(w).dst?
puts tz.period_for_utc(w).observed_utc_offset

utc = TZInfo::Timezone.get('UTC')
puts utc.utc_to_local(t).strftime('%Y-%m-%d %H:%M:%S')

ny = TZInfo::Timezone.get('America/New_York')
puts ny.local_to_utc(Time.utc(2020, 7, 1, 8, 0, 0)).strftime('%Y-%m-%d %H:%M:%S')

begin
  TZInfo::Timezone.get('Nowhere/Nothing')
rescue TZInfo::InvalidTimezoneIdentifier => e
  puts e.class
end

puts TZInfo::Timezone.all_identifiers.include?('Asia/Tokyo')
puts TZInfo::Country.get('DE').name
puts TZInfo::Country.get('DE').zone_identifiers.sort.first

tr = tz.transitions_up_to(Time.utc(2021, 1, 1), Time.utc(2020, 1, 1))
puts tr.map { |x| x.at.to_time.utc.strftime('%Y-%m-%d %H:%M') }.inspect
