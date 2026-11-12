# frozen_string_literal: true
#
# The websocket-driver gem's C extension, provided by IronRuby itself.
#
# websocket-driver 0.8.2 is pure Ruby except for ext/websocket-driver/websocket_mask.c,
# which defines WebSocket::Mask.mask - the XOR loop over a frame's payload.  The gem's
# own Ruby (Src/StdLib/ironruby/websocket, vendored unchanged) starts with
#
#     begin
#       require 'websocket_mask'
#     rescue LoadError
#       require 'websocket/mask'
#     end
#
# and this file is what that first require finds: the same function, in C#
# (Src/Libraries/WebSocketDriver/WebSocketMask.cs).  A default gemspec makes RubyGems
# agree that websocket-driver 0.8.2 is installed, so `gem "websocket-driver"` in a
# Gemfile - which faye-websocket, ActionCable and Capybara's drivers all pull in -
# resolves here instead of stopping at extconf.rb.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.WebSocketDriver'
