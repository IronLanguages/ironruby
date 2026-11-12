require 'ruby-progressbar'
require 'stringio'

out = StringIO.new
bar = ProgressBar.create(output: out, length: 40, total: 10,
                         format: '%t |%B| %p%% %c/%C',
                         title: 'work', autofinish: false, throttle_rate: 0)
5.times { bar.increment }
puts bar.progress
puts bar.to_s
bar.progress = 10
puts bar.to_s
bar.finish
puts bar.finished?
puts bar.to_s

b2 = ProgressBar.create(output: StringIO.new, total: nil, throttle_rate: 0)
b2.increment
puts b2.progress
puts b2.total.inspect

b3 = ProgressBar.create(output: StringIO.new, total: 4, throttle_rate: 0,
                        format: '%w%i', progress_mark: '#', remainder_mark: '.',
                        length: 10)
b3.progress = 2
puts b3.to_s

begin
  ProgressBar.create(output: StringIO.new, total: 2).tap { |b| b.progress = 5 }
rescue ProgressBar::InvalidProgressError => e
  puts e.class
end
