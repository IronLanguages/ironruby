# A small script that prints as it goes - what Util/warm/ttfo.py's time to first output
# measures, next to `-e 1`.
puts "hello from #{RUBY_ENGINE} #{RUBY_VERSION}"
words = %w[alpha beta gamma delta epsilon]
words.each_with_index { |w, i| puts format("%2d %-8s %s", i, w, w.upcase.reverse) }
puts words.group_by(&:size).inspect
puts words.map(&:length).sum
