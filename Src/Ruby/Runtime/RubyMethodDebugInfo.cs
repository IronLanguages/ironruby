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
using System.Collections.Generic;
using System.Reflection;

namespace IronRuby.Runtime {
    /// <summary>
    /// The IL offset to source line map of a precompiled startup-library method, which has no PDB.
    /// </summary>
    internal sealed class RubyMethodDebugInfo {
        private readonly List<int>/*!*/ _offsets = new List<int>();
        private readonly List<int>/*!*/ _lines = new List<int>();

        public static bool TryGet(MethodBase/*!*/ method, out RubyMethodDebugInfo info) {
            return IronRuby.Runtime.Precompiled.PrecompiledCode.TryGetDebugInfo(method, out info);
        }

        public void AddMapping(int ilOffset, int line) {
            _offsets.Add(ilOffset);
            _lines.Add(line);
        }

        public int Map(int ilOffset) {
            int index =_offsets.BinarySearch(ilOffset);
            if (index >= 0) {
                return _lines[index];
            }
            index = ~index;
            if (index > 0) {
                return _lines[index - 1];
            }
            if (_lines.Count > 0) {
                return _lines[0];
            }
            return 0;
        }
    }
}
