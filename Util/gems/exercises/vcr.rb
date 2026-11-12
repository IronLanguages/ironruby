require 'vcr'
require 'webmock'
require 'net/http'
require 'uri'
require 'fileutils'
require 'tmpdir'

dir = File.join(Dir.tmpdir, "vcr-exercise-#{Process.pid}")
FileUtils.rm_rf(dir)
FileUtils.mkdir_p(dir)

VCR.configure do |c|
  c.cassette_library_dir = dir
  c.hook_into :webmock
  c.default_cassette_options = { record: :none, match_requests_on: %i[method uri] }
end

File.write(File.join(dir, 'example.yml'), <<~YAML)
  ---
  http_interactions:
  - request:
      method: get
      uri: https://example.test/one
      body:
        encoding: UTF-8
        string: ''
      headers: {}
    response:
      status:
        code: 200
        message: OK
      headers:
        Content-Type:
        - text/plain
      body:
        encoding: UTF-8
        string: recorded one
      http_version:
    recorded_at: Thu, 02 Jan 2020 03:04:05 GMT
  - request:
      method: post
      uri: https://example.test/two
      body:
        encoding: UTF-8
        string: a=1
      headers: {}
    response:
      status:
        code: 201
        message: Created
      headers: {}
      body:
        encoding: UTF-8
        string: recorded two
      http_version:
    recorded_at: Thu, 02 Jan 2020 03:04:05 GMT
  recorded_with: VCR 6.0.0
YAML

VCR.use_cassette('example') do
  res = Net::HTTP.get_response(URI('https://example.test/one'))
  puts res.code, res.body, res['content-type']

  uri = URI('https://example.test/two')
  http = Net::HTTP.new(uri.host, uri.port)
  http.use_ssl = true
  res = http.post(uri.path, 'a=1')
  puts res.code, res.body

  begin
    Net::HTTP.get(URI('https://example.test/three'))
  rescue VCR::Errors::UnhandledHTTPRequestError => e
    puts e.class
  end
end

cassette = VCR::Cassette.new('example', record: :none)
puts cassette.name
puts cassette.file.end_with?('example.yml')
puts cassette.http_interactions.interactions.size
puts cassette.http_interactions.interactions.map { |i| i.request.uri }.inspect
puts cassette.http_interactions.interactions.first.response.status.code

puts VCR.version.split('.').first
FileUtils.rm_rf(dir)
