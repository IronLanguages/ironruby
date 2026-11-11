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

namespace IronRuby.Compiler {
    /// <summary>
    /// The error codes IronRuby reports warnings to an ErrorSink with; the parser itself is prism,
    /// which reports its own diagnostics.
    /// </summary>
    public static class Errors {
        private const int WarningLevel1 = 0x2000;
        private const int WarningLevel2 = 0x3000;

        internal const int RuntimeWarning = WarningLevel1;
        internal const int RuntimeVerboseWarning = WarningLevel2;

        internal static bool IsVerboseWarning(int errorCode) {
            return errorCode >= WarningLevel2;
        }
    }
}
