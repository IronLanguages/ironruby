require 'active_job'

ActiveJob::Base.logger = Logger.new(IO::NULL)
ActiveJob::Base.queue_adapter = :test

class GreetJob < ActiveJob::Base
  queue_as :greetings

  before_enqueue { |job| job.arguments << 'before' }
  around_perform do |_job, block|
    puts 'around start'
    block.call
    puts 'around end'
  end

  def perform(*args)
    puts "performing #{args.inspect}"
  end
end

class FailJob < ActiveJob::Base
  retry_on ArgumentError, attempts: 2, wait: 0
  def perform
    raise ArgumentError, 'nope'
  end
end

GreetJob.perform_later('world')
adapter = ActiveJob::Base.queue_adapter
puts adapter.enqueued_jobs.size
job = adapter.enqueued_jobs.first
puts job[:job].name
puts job[:queue]
puts job[:args].inspect

GreetJob.perform_now('direct')

puts GreetJob.new.queue_name
puts GreetJob.queue_name_prefix.inspect
serialized = GreetJob.new('a', 1, true).serialize
puts serialized.keys.sort.inspect
puts serialized['arguments'].inspect
puts serialized['job_class']
puts ActiveJob::Base.deserialize(serialized).arguments.inspect

set = GreetJob.set(queue: :other)
puts set.class
puts ActiveJob::VERSION::MAJOR
