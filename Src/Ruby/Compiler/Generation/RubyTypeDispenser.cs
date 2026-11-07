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
using System.Diagnostics;
using System.Reflection.Emit;
using System.Text;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using Range = IronRuby.Builtins.Range;
using IronRuby.Runtime;
using System.Globalization;

namespace IronRuby.Compiler.Generation {
    /// <summary>
    /// For the ahead-of-time generated subclasses of other assemblies (IronRuby.Libraries); see
    /// RubyTypeDispenser.AddPrebuiltType.
    /// </summary>
    public static class PrebuiltSubclasses {
        public static void Register(Type/*!*/ clsBaseType, Type/*!*/ rubyType) {
            RubyTypeDispenser.AddPrebuiltType(clsBaseType, rubyType);
        }
    }

    internal static class RubyTypeDispenser {

        private static readonly Publisher<TypeDescription/*!*/, Type/*!*/>/*!*/ _newTypes;
        private static readonly Dictionary<Type/*!*/, IList<ITypeFeature/*!*/>/*!*/>/*!*/ _typeFeatures;

        // guarded by _typeFeatures (see TryGetObjectAllocator)
        private static readonly Dictionary<Type/*!*/, Func<RubyClass, object>/*!*/>/*!*/ _objectAllocators = new Dictionary<Type, Func<RubyClass, object>>();

        private static readonly ITypeFeature/*!*/[]/*!*/ _defaultFeatures = new ITypeFeature[] {
            RubyTypeFeature.Instance,
            InterfaceImplFeature.Create(ReflectionUtils.EmptyTypes)
        };

        static RubyTypeDispenser() {
            _typeFeatures = new Dictionary<Type, IList<ITypeFeature>>();
            _newTypes = new Publisher<TypeDescription, Type>();
            AddBuiltinType(typeof(object), typeof(RubyObject), false);
            AddBuiltinType(typeof(MutableString), typeof(MutableString.Subclass), true);
            AddBuiltinType(typeof(Proc), typeof(Proc.Subclass), true);
            AddBuiltinType(typeof(RubyRegex), typeof(RubyRegex.Subclass), true);
            AddBuiltinType(typeof(Range), typeof(Range.Subclass), true);
            AddBuiltinType(typeof(Hash), typeof(Hash.Subclass), true);
            AddBuiltinType(typeof(RubyArray), typeof(RubyArray.Subclass), true);
            AddBuiltinType(typeof(MatchData), typeof(MatchData.Subclass), true);
            AddBuiltinType(typeof(RubyIO), typeof(RubyIO.Subclass), true);

            // Without dynamic code (NativeAOT) CreateType cannot emit: use the subclasses generated
            // ahead of time for the CLR types behind library classes (Util/aot/gen-subclasses.rb).
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {
                AotSubclasses.Register(AddPrebuiltType);
            }
        }

        /// <summary>
        /// Registers <paramref name="rubyType"/>, a CLR subclass of <paramref name="clsBaseType"/> that
        /// implements IRubyObject, as the type of Ruby subclasses of the class backed by the base type.
        /// </summary>
        internal static void AddPrebuiltType(Type/*!*/ clsBaseType, Type/*!*/ rubyType) {
            lock (_typeFeatures) {
                AddBuiltinType(clsBaseType, rubyType, false);
                AddBuiltinType(clsBaseType, rubyType, true);
            }
        }
      
        internal static Type/*!*/ GetOrCreateType(Type/*!*/ baseType, IList<Type/*!*/>/*!*/ interfaces, bool noOverrides) {
            Assert.NotNull(baseType);
            Assert.NotNull(interfaces);

            ITypeFeature[] features;
            if (interfaces.Count == 0) {
                features = _defaultFeatures;
            } else {
                // RubyObject is what a plain Ruby class's instances are, not a base for generated types: one derived from it
                // would implement IRubyObject a second time, over fields of its own, and override its CreateInstance.
                // The type of a subclass that adds interfaces derives from Object, as RubyObject itself does.
                if (baseType == typeof(RubyObject)) {
                    baseType = typeof(object);
                }
                features = new ITypeFeature[] {
                    RubyTypeFeature.Instance,
                    InterfaceImplFeature.Create(interfaces)
                };
            }
            noOverrides |= typeof(IRubyType).IsAssignableFrom(baseType);

            TypeDescription typeInfo = new TypeDescription(baseType, features, noOverrides);
            Type type = _newTypes.GetOrCreateValue(typeInfo,
                () => {
                    if (TypeImplementsFeatures(baseType, features)) {
                        return baseType;
                    }
                    return CreateType(typeInfo);
                });

            Debug.Assert(typeof(IRubyObject).IsAssignableFrom(type));
            return type;
        }

        /// <summary>
        /// The allocator of the instances of <paramref name="type"/> if it is a type generated for Ruby classes that derives
        /// from Object (the type of a Ruby class implementing CLR interfaces - those Comparable and Enumerable imply, say), so that
        /// Class#new can make them without a rule as it makes RubyObjects (see RubyClass.TryGetObjectConstruction); null otherwise.
        /// It calls the constructor taking the class the rule calls (RubyClass.GetAllocatorNewExpression).
        /// </summary>
        internal static Func<RubyClass, object> TryGetObjectAllocator(Type/*!*/ type) {
            if (type.BaseType != typeof(object) || !typeof(IRubyObject).IsAssignableFrom(type)) {
                return null;
            }

            Func<RubyClass, object> allocator;
            lock (_typeFeatures) {
                if (_objectAllocators.TryGetValue(type, out allocator)) {
                    return allocator;
                }
                if (!_typeFeatures.ContainsKey(type)) {
                    return null;
                }
            }

            var ctor = type.GetConstructor(new[] { typeof(RubyClass) });
            if (ctor == null || !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {
                return null;
            }

            var method = new DynamicMethod("Allocate" + type.Name, typeof(object), new[] { typeof(RubyClass) }, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Newobj, ctor);
            il.Emit(OpCodes.Ret);
            allocator = (Func<RubyClass, object>)method.CreateDelegate(typeof(Func<RubyClass, object>));

            lock (_typeFeatures) {
                _objectAllocators[type] = allocator;
            }
            return allocator;
        }

        internal static bool TryGetFeatures(Type/*!*/ type, out IList<ITypeFeature/*!*/> features) {
            lock (_typeFeatures) {
                return _typeFeatures.TryGetValue(type, out features);
            }
        }

        private static bool TypeImplementsFeatures(Type/*!*/ type, IList<ITypeFeature/*!*/>/*!*/ features) {
            IList<ITypeFeature> featuresFound;
            if (TryGetFeatures(type, out featuresFound)) {
                return TypeDescription.FeatureSetsMatch(features, featuresFound);
            }

            foreach (ITypeFeature feature in features) {
                if (!feature.IsImplementedBy(type)) {
                    return false;
                }
            }
            return true;
        }

        private static Type CreateType(TypeDescription/*!*/ typeInfo) {
            Type baseType = typeInfo.BaseType;
            if (baseType.IsSealed) {
                throw new NotSupportedException(
                    String.Format(CultureInfo.InvariantCulture, "Can't inherit from a sealed type {0}.",
                    RubyContext.GetQualifiedNameNoLock(baseType, null, false))
                );
            }

            string typeName = GetName(baseType);
            TypeBuilder tb = Snippets.Shared.DefinePublicType(typeName, baseType);
            Utils.Log(typeName, "TYPE_BUILDER");

            IFeatureBuilder[] features = new IFeatureBuilder[typeInfo.Features.Count];
            RubyTypeEmitter emitter = new RubyTypeEmitter(tb);

            for (int i = 0; i < typeInfo.Features.Count; i++) {
                features[i] = typeInfo.Features[i].MakeBuilder(tb);
            }

            foreach (IFeatureBuilder feature in features) {
                feature.Implement(emitter);
            }

            if (!typeInfo.NoOverrides) {
                emitter.OverrideMethods(baseType);
            }

            Type result = emitter.FinishType();
            lock (_typeFeatures) {
                _typeFeatures.Add(result, typeInfo.Features);
            }
            return result;
        }

        private static string GetName(Type/*!*/ baseType) {
            // DLR appends a counter, so we don't need to
            // TODO: Reflect feature set in the name?
            StringBuilder name = new StringBuilder("IronRuby.Classes.");
            name.Append(baseType.Name);
            return name.ToString();
        }

        private static void AddBuiltinType(Type/*!*/ clsBaseType, Type/*!*/ rubyType, bool noOverrides) {
            AddBuiltinType(clsBaseType, rubyType, _defaultFeatures, noOverrides);
        }

        private static void AddBuiltinType(Type/*!*/ clsBaseType, Type/*!*/ rubyType, ITypeFeature/*!*/[]/*!*/ features, bool noOverrides) {
            _newTypes.GetOrCreateValue(new TypeDescription(clsBaseType, features, noOverrides), () => rubyType);
            _typeFeatures[rubyType] = features;
        }
    }
}
