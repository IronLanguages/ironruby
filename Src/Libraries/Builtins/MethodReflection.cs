using System;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    /// <summary>
    /// What Method#arity, #parameters and #inspect report for a method, where that is not simply what the method is
    /// written with. MRI writes most of core in C, a few hundred methods in Ruby (kernel.rb, numeric.rb, ...); IronRuby
    /// writes core in C# and, for a good part, in its own Ruby prelude. To Ruby code a core method looks the way MRI's
    /// does:
    /// <list type="bullet">
    /// <item>one MRI writes in Ruby has the arity and named parameters of MRI's (InternalMethods.Signatures, generated
    /// from CRuby): Kernel#Integer is -2, [[:req, :arg], [:opt, :base], [:key, :exception]];</item>
    /// <item>one MRI writes in C has the arity of the C function and no parameter names: the number of arguments it
    /// takes if that is fixed ([[:req]] for each), else -1 and [[:rest]]. A library method already reports so (see
    /// RubyLibraryMethodInfo.ComputeArity); a prelude method is made to.</item>
    /// </list>
    /// </summary>
    internal static class MethodReflection {

        // "Kernel#tap", "Dir.glob": how InternalMethods names a method.
        internal static string/*!*/ GetInternalMethodKey(RubyModule/*!*/ owner, string/*!*/ name) {
            var singleton = owner.IsSingletonClass ? ((RubyClass)owner).SingletonClassOf as RubyModule : null;
            return (singleton != null) ? singleton.Name + "." + name : owner.Name + "#" + name;
        }

        /// <summary>
        /// Whether the method is a part of core that IronRuby writes in Ruby: a method or a define_method block in one of
        /// the prelude files.
        /// </summary>
        internal static bool IsPreludeMethod(RubyMemberInfo/*!*/ info) {
            var rubyInfo = info as RubyMethodInfo;
            if (rubyInfo != null) {
                return RubyStackTraceBuilder.IsInternalFile(rubyInfo.Document.FileName);
            }
            var lambdaInfo = info as RubyLambdaMethodInfo;
            return lambdaInfo != null && RubyStackTraceBuilder.IsInternalFile(lambdaInfo.Lambda.Dispatcher.SourcePath);
        }

        // MRI's Ruby signature of a core method it writes in Ruby, or null.
        private static string GetMriSignature(RubyMemberInfo/*!*/ info, string/*!*/ name) {
            RubyModule owner = info.DeclaringModule;
            if (owner == null || owner.Name == null && !owner.IsSingletonClass || !(info is RubyLibraryMethodInfo || IsPreludeMethod(info))) {
                return null;
            }
            var rubyInfo = info as RubyMethodInfo;
            string key = GetInternalMethodKey(owner, rubyInfo != null ? rubyInfo.DefinitionName : info.OriginalName ?? name);
            string signature;
            return InternalMethods.Signatures.TryGetValue(key, out signature) ? signature : null;
        }

        // The arity of the C function of a core method IronRuby writes in Ruby.
        private static int GetCArity(RubyMemberInfo/*!*/ info) {
            int arity = info.GetArity();
            var signature = info.GetParameterSignature();
            return (arity >= 0 && (signature == null || !signature.TakesKeywords && signature.RequiredKeywordNames.Length == 0)) ? arity : -1;
        }

        internal static int GetArity(RubyMemberInfo/*!*/ info, string/*!*/ name) {
            string signature = GetMriSignature(info, name);
            if (signature != null) {
                return Int32.Parse(signature.Substring(0, signature.IndexOf('|')), System.Globalization.CultureInfo.InvariantCulture);
            }
            return IsPreludeMethod(info) ? GetCArity(info) : info.GetArity();
        }

        internal static RubyArray/*!*/ GetParameters(RubyContext/*!*/ context, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            string signature = GetMriSignature(info, name);
            if (signature != null) {
                var result = new RubyArray();
                string parameters = signature.Substring(signature.IndexOf('|') + 1);
                if (parameters.Length > 0) {
                    foreach (string parameter in parameters.Split(',')) {
                        int space = parameter.IndexOf(' ');
                        result.Add(space < 0
                            ? new RubyArray(1) { context.CreateAsciiSymbol(parameter) }
                            : new RubyArray(2) { context.CreateAsciiSymbol(parameter.Substring(0, space)), context.EncodeIdentifier(parameter.Substring(space + 1)) }
                        );
                    }
                }
                return result;
            }

            if (IsPreludeMethod(info)) {
                return GetCParameters(context, GetCArity(info));
            }
            return info.GetRubyParameterArray();
        }

        // [[:req]] for each argument a C function takes, [[:rest]] for one that takes argc/argv.
        private static RubyArray/*!*/ GetCParameters(RubyContext/*!*/ context, int arity) {
            var result = new RubyArray();
            if (arity < 0) {
                result.Add(new RubyArray(1) { context.CreateAsciiSymbol("rest") });
            } else {
                for (int i = 0; i < arity; i++) {
                    result.Add(new RubyArray(1) { context.CreateAsciiSymbol("req") });
                }
            }
            return result;
        }

        /// <summary>
        /// The parameter list Method#inspect prints, if it is one #parameters describes - that of a library method, an
        /// attribute accessor or a core method IronRuby writes in Ruby - or null for one the method's own signature does.
        /// "_" stands for a required parameter with no name.
        /// </summary>
        internal static string FormatParameters(RubyContext/*!*/ context, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            if (info.GetParameterSignature() != null && !IsPreludeMethod(info)) {
                return null;
            }

            var result = new StringBuilder("(");
            foreach (object item in GetParameters(context, info, name)) {
                var parameter = item as RubyArray;
                if (parameter == null || parameter.Count == 0) {
                    continue;
                }
                string kind = parameter[0].ToString();
                string parameterName = parameter.Count > 1 ? parameter[1].ToString() : null;
                string text;
                switch (kind) {
                    case "req": text = parameterName ?? "_"; break;
                    case "opt": text = (parameterName ?? "_") + "=..."; break;
                    case "rest": text = "*" + (parameterName == "*" ? null : parameterName); break;
                    case "keyreq": text = (parameterName ?? "_") + ":"; break;
                    case "key": text = (parameterName ?? "_") + ": ..."; break;
                    case "keyrest": text = "**" + (parameterName == "**" ? null : parameterName); break;
                    case "nokey": text = "**nil"; break;
                    case "block": text = "&" + (parameterName == "&" ? null : parameterName); break;
                    default: continue;
                }
                if (result.Length > 1) {
                    result.Append(", ");
                }
                result.Append(text);
            }
            result.Append(')');
            return result.ToString();
        }
    }
}
