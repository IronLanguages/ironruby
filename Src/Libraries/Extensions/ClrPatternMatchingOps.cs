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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Mixed into every CLR class that no library defines (records, classes, structs from C#), so that a hash pattern
    /// matches the object's public readable instance properties and fields: `in {X: 1, y:}`. A key is a member's
    /// CLR name or its Ruby name (the same mangling member calls use: first_name for FirstName). Ruby objects and
    /// the built-in classes are not CLR classes in this sense and do not respond to #deconstruct_keys.
    /// </summary>
    [RubyModule("PatternMatching", DefineIn = typeof(IronRubyOps.Clr), Extends = typeof(ClrPatternMatching), Restrictions = ModuleRestrictions.NoUnderlyingType)]
    public static class ClrPatternMatchingOps {

        private sealed class Member {
            public readonly string/*!*/ RubyName;
            public readonly MemberInfo/*!*/ Info;

            public Member(MemberInfo/*!*/ info) {
                Info = info;
                RubyName = RubyUtils.TryMangleName(info.Name) ?? info.Name;
            }

            public object GetValue(object obj) {
                try {
                    var property = Info as PropertyInfo;
                    return (property != null) ? property.GetValue(obj) : ((FieldInfo)Info).GetValue(obj);
                } catch (TargetInvocationException e) {
                    // the getter's own exception, not reflection's wrapper
                    ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    throw;
                }
            }
        }

        private sealed class MemberTable {
            // in declaration order, the most derived member of a name
            public readonly List<Member>/*!*/ Members = new List<Member>();
            public readonly Dictionary<string, Member>/*!*/ ByClrName = new Dictionary<string, Member>();
        }

        private static readonly ConcurrentDictionary<Type, MemberTable>/*!*/ _memberTables = new ConcurrentDictionary<Type, MemberTable>();

        private static MemberTable/*!*/ GetMemberTable(Type/*!*/ type) {
            return _memberTables.GetOrAdd(type, MakeMemberTable);
        }

        private static MemberTable/*!*/ MakeMemberTable(Type/*!*/ type) {
            var result = new MemberTable();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;

            foreach (var property in type.GetProperties(flags)) {
                Type valueType = property.PropertyType;
                if (property.GetGetMethod() != null && property.GetIndexParameters().Length == 0
                    && !valueType.IsByRef && !valueType.IsByRefLike && !valueType.IsPointer) {
                    Add(result, property);
                }
            }

            foreach (var field in type.GetFields(flags)) {
                if (!field.FieldType.IsByRefLike && !field.FieldType.IsPointer) {
                    Add(result, field);
                }
            }

            return result;
        }

        private static void Add(MemberTable/*!*/ table, MemberInfo/*!*/ info) {
            Member existing;
            if (table.ByClrName.TryGetValue(info.Name, out existing)) {
                // a member hidden by a `new' one in a derived class
                if (!info.DeclaringType.IsSubclassOf(existing.Info.DeclaringType)) {
                    return;
                }
                table.Members.Remove(existing);
            }

            var member = new Member(info);
            table.ByClrName[info.Name] = member;
            table.Members.Add(member);
        }

        private static Member TryFindMember(MemberTable/*!*/ table, string/*!*/ name) {
            Member result;
            if (table.ByClrName.TryGetValue(name, out result)) {
                return result;
            }
            string clrName = RubyUtils.TryUnmangleName(name);
            return (clrName != null && table.ByClrName.TryGetValue(clrName, out result)) ? result : null;
        }

        /// <summary>
        /// An instance of a Ruby subclass of a CLR class is an instance of a type generated for it; the members
        /// are those of the CLR class.
        /// </summary>
        private static Type/*!*/ GetClrType(object/*!*/ obj) {
            Type type = obj.GetType();
            while (type.Assembly.IsDynamic && typeof(IRubyObject).IsAssignableFrom(type) && type.BaseType != null) {
                type = type.BaseType;
            }
            return type;
        }

        /// <summary>
        /// Struct#deconstruct_keys's contract: with nil every member, keyed by its Ruby name; otherwise the requested
        /// keys (Symbols or Strings) that name a member, keyed as requested. A key that names nothing is left out.
        /// </summary>
        [RubyMethod("deconstruct_keys")]
        public static Hash/*!*/ DeconstructKeys(RubyContext/*!*/ context, object/*!*/ self, object keys) {
            var requested = ClrDeconstruction.GetRequestedKeys(context, keys);
            var table = GetMemberTable(GetClrType(self));
            var result = new Hash(context);

            if (requested == null) {
                foreach (var member in table.Members) {
                    RubyUtils.SetHashElement(context, result, context.EncodeIdentifier(member.RubyName),
                        ClrDeconstruction.ToPatternValue(member.GetValue(self)));
                }
                return result;
            }

            foreach (object key in requested) {
                string name = ClrDeconstruction.GetKeyName(key);
                Member member = (name != null) ? TryFindMember(table, name) : null;
                if (member != null) {
                    RubyUtils.SetHashElement(context, result, key, ClrDeconstruction.ToPatternValue(member.GetValue(self)));
                }
            }
            return result;
        }
    }
}
