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

#pragma warning disable 169 // mcs: unused private method
[assembly: IronRuby.Runtime.RubyLibraryAttribute(typeof(IronRuby.StandardLibrary.Yaml.YamlLibraryInitializer))]

namespace IronRuby.StandardLibrary.Yaml {
    using System;
    using Microsoft.Scripting.Utils;
    using System.Runtime.InteropServices;
    
    public sealed class YamlLibraryInitializer : IronRuby.Builtins.LibraryInitializer {
        protected override void LoadModules() {
            
            
            DefineGlobalModule("Psych", typeof(IronRuby.StandardLibrary.Yaml.RubyYaml), 0x00000008, null, LoadPsych_Class, null, IronRuby.Builtins.RubyModule.EmptyArray);
        }
        
        private static void LoadPsych_Class(IronRuby.Builtins.RubyModule/*!*/ module) {
            DefineLibraryMethod(module, "__emitter_collection", 0x21, 
                0x00000004U, 
                new Func<IronRuby.Runtime.ConversionStorage<IronRuby.Builtins.MutableString>, IronRuby.Builtins.RubyModule, IronRuby.StandardLibrary.Yaml.LibyamlEmitter, System.Boolean, System.Object, System.Object, System.Object, System.Object, IronRuby.Builtins.MutableString>(IronRuby.StandardLibrary.Yaml.RubyYaml.EmitCollectionStart)
            );
            
            DefineLibraryMethod(module, "__emitter_emit", 0x21, 
                0x00000006U, 
                new Func<IronRuby.Builtins.RubyModule, IronRuby.StandardLibrary.Yaml.LibyamlEmitter, System.Collections.IList, IronRuby.Builtins.MutableString>(IronRuby.StandardLibrary.Yaml.RubyYaml.EmitEvent)
            );
            
            DefineLibraryMethod(module, "__emitter_end_collection", 0x21, 
                0x00000002U, 
                new Func<IronRuby.Builtins.RubyModule, IronRuby.StandardLibrary.Yaml.LibyamlEmitter, System.Boolean, IronRuby.Builtins.MutableString>(IronRuby.StandardLibrary.Yaml.RubyYaml.EmitCollectionEnd)
            );
            
            DefineLibraryMethod(module, "__emitter_open", 0x21, 
                0x00030000U, 
                new Func<IronRuby.Builtins.RubyModule, System.Int32, System.Int32, System.Object, IronRuby.StandardLibrary.Yaml.LibyamlEmitter>(IronRuby.StandardLibrary.Yaml.RubyYaml.OpenEmitter)
            );
            
            DefineLibraryMethod(module, "__emitter_scalar", 0x21, 
                0x00000004U, 
                new Func<IronRuby.Runtime.ConversionStorage<IronRuby.Builtins.MutableString>, IronRuby.Builtins.RubyModule, IronRuby.StandardLibrary.Yaml.LibyamlEmitter, System.Object, System.Object, System.Object, System.Object, System.Object, System.Object, IronRuby.Builtins.MutableString>(IronRuby.StandardLibrary.Yaml.RubyYaml.EmitScalar)
            );
            
            DefineLibraryMethod(module, "__native_mark", 0x21, 
                0x00000000U, 
                new Func<IronRuby.Builtins.RubyModule, System.Object, IronRuby.Builtins.RubyArray>(IronRuby.StandardLibrary.Yaml.RubyYaml.NativeMark)
            );
            
            DefineLibraryMethod(module, "__native_parse", 0x21, 
                0x00200000U, 
                new Func<IronRuby.StandardLibrary.Yaml.PsychHandlerSites, IronRuby.Runtime.RespondToStorage, IronRuby.Builtins.RubyModule, System.Object, System.Object, System.Object, System.Int32, System.Object>(IronRuby.StandardLibrary.Yaml.RubyYaml.NativeParse)
            );
            
            DefineLibraryMethod(module, "libyaml_version", 0x21, 
                0x00000000U, 
                new Func<IronRuby.Builtins.RubyModule, IronRuby.Builtins.RubyArray>(IronRuby.StandardLibrary.Yaml.RubyYaml.LibyamlVersion)
            );
            
        }
        
    }
}

