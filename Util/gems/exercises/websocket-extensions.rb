require 'websocket/extensions'

class Deflate
  def initialize; @name = 'permessage-deflate'; end
  def type; 'permessage'; end
  attr_reader :name
  def rsv1; true; end
  def rsv2; false; end
  def rsv3; false; end

  def create_client_session; Session.new; end
  def create_server_session(offers)
    return nil if offers.nil? || offers.empty?
    offers.first && Session.new
  end

  class Session
    def generate_offer; { 'client_max_window_bits' => true }; end
    def activate(_params); true; end
    def generate_offer_from(_offer); {}; end
    def generate_response; {}; end
    def process_incoming_message(message)
      message.data = message.data.to_s.reverse
      message
    end
    def process_outgoing_message(message)
      message.data = message.data.to_s.upcase
      message
    end
    def close; end
  end
end

ext = WebSocket::Extensions.new
ext.add(Deflate.new)
offer = ext.generate_offer
puts offer
ext.activate(offer)

Message = Struct.new(:rsv1, :rsv2, :rsv3, :opcode, :data)
out = ext.process_outgoing_message(Message.new(false, false, false, 1, 'hello'))
puts out.data
inn = ext.process_incoming_message(Message.new(true, false, false, 1, 'olleh'))
puts inn.data

server = WebSocket::Extensions.new
server.add(Deflate.new)
puts server.generate_response(offer)

puts WebSocket::Extensions::Parser.parse_header('a; b=1, c').to_a.inspect
begin
  WebSocket::Extensions::Parser.parse_header('a; b=')
rescue WebSocket::Extensions::Parser::ParseError => e
  puts e.class
end
ext.close
