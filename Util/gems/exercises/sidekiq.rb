require 'logger'
require 'sidekiq'
require 'sidekiq/testing'

Sidekiq::Testing.fake!
Sidekiq.default_configuration.logger = Logger.new(IO::NULL)

class MailJob
  include Sidekiq::Job
  sidekiq_options queue: 'mail', retry: 3
  def perform(to, subject)
    puts "sent #{subject} to #{to}"
  end
end

class DefaultJob
  include Sidekiq::Job
  def perform(*args)
    puts "default #{args.inspect}"
  end
end

MailJob.perform_async('a@example.test', 'hello')
MailJob.perform_async('b@example.test', 'again')
DefaultJob.perform_async(1, 'two', [3])

puts MailJob.jobs.size
puts MailJob.jobs.first['args'].inspect
puts MailJob.jobs.first['queue']
puts MailJob.jobs.first['retry']
puts MailJob.jobs.first['class']
puts DefaultJob.jobs.first['queue']
puts Sidekiq::Queues['mail'].size

MailJob.drain
puts MailJob.jobs.size

DefaultJob.perform_inline(9)

Sidekiq::Testing.inline!
MailJob.perform_async('c@example.test', 'inline')
puts MailJob.jobs.size
Sidekiq::Testing.fake!

puts MailJob.get_sidekiq_options['queue']
puts MailJob.get_sidekiq_options['retry']
puts Sidekiq::VERSION.split('.').first
puts Sidekiq.default_configuration.class
Sidekiq::Worker.clear_all
puts Sidekiq::Queues['mail'].size
