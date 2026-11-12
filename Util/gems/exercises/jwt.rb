require 'jwt'
require 'openssl'

payload = { 'sub' => '1234', 'name' => 'ada', 'iat' => 1_500_000_000,
            'exp' => 4_000_000_000 }
secret = 'a-very-secret-key'

%w[HS256 HS384 HS512].each do |alg|
  token = JWT.encode(payload, secret, alg)
  puts "#{alg} #{token.count('.')} #{token.split('.').first}"
  decoded, header = JWT.decode(token, secret, true, algorithm: alg)
  puts decoded.sort.inspect
  puts header['alg']
end

token = JWT.encode(payload, secret, 'HS256')
begin
  JWT.decode(token, 'wrong', true, algorithm: 'HS256')
rescue JWT::VerificationError => e
  puts e.class
end

expired = JWT.encode(payload.merge('exp' => 1), secret, 'HS256')
begin
  JWT.decode(expired, secret, true, algorithm: 'HS256')
rescue JWT::ExpiredSignature => e
  puts e.class
end

begin
  JWT.decode(token, secret, true, algorithm: 'HS512')
rescue JWT::IncorrectAlgorithm => e
  puts e.class
end

puts JWT.decode(token, nil, false).first['name']

key = OpenSSL::PKey::RSA.generate(2048)
rs = JWT.encode(payload, key, 'RS256')
puts JWT.decode(rs, key.public_key, true, algorithm: 'RS256').first['sub']
begin
  JWT.decode(rs, OpenSSL::PKey::RSA.generate(2048).public_key, true, algorithm: 'RS256')
rescue JWT::VerificationError => e
  puts e.class
end

ec = OpenSSL::PKey::EC.generate('prime256v1')
es = JWT.encode(payload, ec, 'ES256')
puts JWT.decode(es, ec, true, algorithm: 'ES256').first['sub']
