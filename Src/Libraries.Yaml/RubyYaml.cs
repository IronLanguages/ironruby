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

using IronRuby.Runtime;

namespace IronRuby.StandardLibrary.Yaml {

    // The Psych module. Everything Psych does is psych's own Ruby code
    // (Src/StdLib/ironruby/psych.rb and psych/); what this library adds is the part psych.so
    // is on CRuby - the event parser and the emitter - in PsychNative.cs.
    [RubyModule("Psych")]
    public static partial class RubyYaml {
    }
}
