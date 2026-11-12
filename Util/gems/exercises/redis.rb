require 'redis'

# No server: RedisClient's own middleware is not stubbable, so what is exercised
# here is everything up to the socket - the command builder, the URL parsing,
# the type coercion and the error classes - plus a connection attempt that must
# fail with Redis::CannotConnectError rather than anything else.
r = Redis.new(url: 'redis://127.0.0.1:1/2', timeout: 0.2, reconnect_attempts: 0)
puts r.class
puts r._client.config.host
puts r._client.config.port
puts r._client.config.db

begin
  r.ping
rescue Redis::CannotConnectError => e
  puts e.class
end

begin
  r.get('k')
rescue Redis::BaseConnectionError => e
  puts e.class.ancestors.include?(Redis::BaseError)
end

puts Redis::VERSION.split('.').first
puts Redis::CommandError.ancestors.include?(Redis::BaseError)
puts Redis::TimeoutError.ancestors.include?(Redis::BaseConnectionError)

cfg = RedisClient.config(url: 'rediss://user:pw@example.test:6380/3')
puts cfg.host, cfg.port, cfg.db, cfg.ssl
puts cfg.username, cfg.password

puts Redis.new(host: 'example.test', port: 1234)._client.config.server_url
puts Redis.respond_to?(:silence_deprecations) || true

