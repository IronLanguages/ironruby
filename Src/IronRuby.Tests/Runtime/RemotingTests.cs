/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using IronRuby.Builtins;
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;
using Microsoft.Scripting.Utils;

namespace IronRuby.Tests {
    public partial class Tests {
        [Options(NoRuntime = true)]
        public void Serialization1() {
            // BinaryFormatter throws PlatformNotSupportedException from .NET 9 on; .NET 8 still
            // has it because this project sets EnableUnsafeBinaryFormatterSerialization.
            if (!BinaryFormatterAvailable) return;

            var encodings = new object[] {
                RubyEncoding.EUCJP,
                RubyEncoding.UTF8,
                RubyEncoding.SJIS,
                RubyEncoding.Ascii,
                RubyEncoding.Binary,
            };

            Assert(ArrayUtils.ValueEquals(encodings, Roundtrip(encodings)));

            var s = MutableString.Create("foo", RubyEncoding.UTF8);
            var rs = Roundtrip(s);
            Assert(s.Equals(rs));
        }

        private static bool BinaryFormatterAvailable {
            get {
                try {
                    new BinaryFormatter().Serialize(new MemoryStream(), 1);
                    return true;
                } catch (NotSupportedException) {
                    // PlatformNotSupportedException (.NET 9+), or NotSupportedException when the
                    // app switch is off
                    return false;
                }
            }
        }

        private T Roundtrip<T>(T obj) {
            var stream = new MemoryStream();
            var f = new BinaryFormatter();
            f.Serialize(stream, obj);
            stream.Seek(0, SeekOrigin.Begin);
            return (T)f.Deserialize(stream);
        }
    }
}
