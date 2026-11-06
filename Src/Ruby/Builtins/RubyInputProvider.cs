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

using System.Collections.Generic;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    public sealed class RubyInputProvider {
        private readonly RubyContext/*!*/ _context;

        // $<. ARGF
        private object/*!*/ _singleton;

        // $*, ARGV
        private readonly RubyArray/*!*/ _commandLineArguments;

        // $.
        private int _lastInputLineNumber;

        internal RubyInputProvider(RubyContext/*!*/ context, ICollection<string>/*!*/ arguments, RubyEncoding/*!*/ encoding) {
            Assert.NotNull(context, encoding);
            Assert.NotNullItems(arguments);
            _context = context;

            var args = new RubyArray();
            foreach (var arg in arguments) {
                ExpandArgument(args, arg, encoding);
            }

            _commandLineArguments = args;
            // $. counts the lines read so far, and nothing has been read yet.
            _lastInputLineNumber = 0;
            _singleton = new object();
        }
        
        /// <summary>
        /// ARGV becomes a warm-start request's arguments (Hosting/WarmStart.cs). The array object stays the
        /// same: the prelude's ARGF holds on to it.
        /// </summary>
        internal void ReplaceCommandLineArguments(ICollection<string>/*!*/ arguments, RubyEncoding/*!*/ encoding) {
            _commandLineArguments.Clear();
            foreach (var arg in arguments) {
                ExpandArgument(_commandLineArguments, arg, encoding);
            }
            _lastInputLineNumber = 0;
        }

        public object/*!*/ Singleton {
            get { return _singleton; }
            // set by environment initializer:
            internal set {
                Assert.NotNull(value);
                _singleton = value; 
            }
        }

        public RubyArray/*!*/ CommandLineArguments {
            get { return _commandLineArguments; }
        }

        public int LastInputLineNumber {
            get { return _lastInputLineNumber; }
            set { _lastInputLineNumber = value; }
        }

        /// <summary>
        /// $FILENAME when no ARGF is defined: what ARGF.filename reports before reading, the first
        /// argument, or "-" for standard input.
        /// </summary>
        public MutableString/*!*/ CurrentFileName {
            get {
                if (CommandLineArguments.Count == 0) {
                    return MutableString.CreateAscii("-");
                } else {
                    //TODO: convert any non-string
                    return (MutableString)CommandLineArguments[0];
                }
            }
        }

        private void ExpandArgument(RubyArray/*!*/ args, string/*!*/ arg, RubyEncoding/*!*/ encoding) {
            if (arg.IndexOf('*') != -1 || arg.IndexOf('?') != -1) {
                bool added = false;
                foreach (string path in Glob.GetMatches(_context.DomainManager.Platform, arg, 0)) {
                    args.Add(MutableString.Create(path, encoding));
                    added = true;
                }

                if (!added) {
                    args.Add(MutableString.Create(arg, encoding));
                }
            } else {
                args.Add(MutableString.Create(arg, encoding));
            }
        }
    }
}
