/* ****************************************************************************
 *
 * IronRuby's implementation of the websocket-driver gem's C extension.
 *
 * websocket-driver (faye) is pure Ruby apart from one function: the XOR loop that
 * masks and unmasks a hybi frame's payload, which ext/websocket-driver/websocket_mask.c
 * defines as WebSocket::Mask.mask.  The gem already carries a Ruby fallback
 * (websocket/mask.rb) for when the extension is missing, but its gemspec declares the
 * extension, so the gem never gets as far as installing here.  IronRuby ships the gem's
 * Ruby unchanged under Src/StdLib/ironruby/websocket and answers `require
 * "websocket_mask"` with this, so the driver takes the same branch it takes on CRuby.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.WebSocketDriver {

    [RubyModule("WebSocket")]
    public static class WebSocketOps {

        [RubyModule("Mask")]
        public static class MaskOps {

            /// <summary>
            /// websocket_mask.c's method_websocket_mask: a nil mask, or one that is not exactly
            /// four bytes long, hands the payload back as it is; otherwise the answer is a new
            /// binary string, payload[i] ^ mask[i % 4].  The payload is not modified.
            /// </summary>
            [RubyMethod("mask", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Mask(RubyModule/*!*/ self, [NotNull]MutableString/*!*/ payload, object mask) {
                MutableString maskString = mask as MutableString;
                if (mask == null || maskString == null || maskString.GetByteCount() != 4) {
                    // The C extension reads RSTRING_LEN of whatever it is given; anything that is
                    // not a String is treated like a missing mask rather than crashing.
                    return payload;
                }

                byte[] key = maskString.ToByteArray();
                byte[] data = payload.ToByteArray();
                for (int i = 0; i < data.Length; i++) {
                    data[i] ^= key[i & 3];
                }
                return MutableString.CreateBinary(data);
            }
        }
    }
}
