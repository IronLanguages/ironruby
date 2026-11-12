require 'aws-sdk-core'

# No network: the SDK's own stub_responses mode, plus the signer and the
# credential/endpoint plumbing that every aws-sdk-* gem is built on.
creds = Aws::Credentials.new('AKIDEXAMPLE', 'secret')
puts creds.set?
puts creds.access_key_id

signer = Aws::Sigv4::Signer.new(service: 's3', region: 'us-east-1', credentials: creds)
sig = signer.sign_request(
  http_method: 'GET',
  url: 'https://examplebucket.s3.amazonaws.com/test.txt',
  headers: { 'X-Amz-Date' => '20130524T000000Z' }
)
puts sig.headers['authorization'].sub(/Signature=\h+/, 'Signature=<hex>')
puts sig.headers.keys.sort.inspect
puts sig.content_sha256

client = Aws::STS::Client.new(region: 'us-east-1', credentials: creds, stub_responses: true)
client.stub_responses(:get_caller_identity, account: '111122223333', arn: 'arn:aws:iam::1:user/x', user_id: 'AIDA')
r = client.get_caller_identity
puts r.account, r.arn, r.user_id

client.stub_responses(:get_caller_identity, 'AccessDenied')
begin
  client.get_caller_identity
rescue Aws::STS::Errors::AccessDenied => e
  puts e.class
end

puts Aws::Endpoints.respond_to?(:resolve) || true
puts Aws.config.class
puts Aws::EmptyStructure.ancestors.include?(Struct)
puts Aws::Json.load('{"a":[1,2]}').inspect
puts Aws::Json.dump('a' => 1)
puts Aws::Query::Param.new('k', 'v').to_s
puts Aws::EventStream::Decoder.instance_method(:decode).arity
