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

namespace IronRuby.Builtins {
    [RubyModule("GC")]
    public static class RubyGC {
        [RubyMethod("enable", RubyMethodAttributes.PublicSingleton)]
        public static bool Enable(object self) {
            return false;
        }

        [RubyMethod("disable", RubyMethodAttributes.PublicSingleton)]
        public static bool Disable(object self) {
            return false;
        }

        [RubyMethod("start", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("garbage_collect", RubyMethodAttributes.PublicInstance)]
        public static void GarbageCollect(object self) {
            // MRI runs object finalizers as part of a GC. IronRuby parks them for the main thread
            // (see ObjectSpace._pending), so the caller of GC.start is the one that runs them.
            ObjectSpace.CollectAndRunFinalizers();
        }

        /// <summary>
        /// What GC.stat_heap and ObjectSpace.count_objects estimate from: the bytes in use and the
        /// bytes free (fragmented) in each of the CLR's heaps after the last collection - gen0, gen1,
        /// gen2, the large and the pinned object heap, which MRI's five size pools stand in for.
        /// </summary>
        [RubyMethod("__heap_sizes__", RubyMethodAttributes.PrivateSingleton)]
        public static RubyArray/*!*/ GetHeapSizes(object self) {
            var info = System.GC.GetGCMemoryInfo();
            var generations = info.GenerationInfo;
            var result = new RubyArray(generations.Length);
            for (int i = 0; i < generations.Length; i++) {
                var sizes = new RubyArray(2);
                sizes.Add(Protocols.Normalize(generations[i].SizeAfterBytes));
                sizes.Add(Protocols.Normalize(generations[i].FragmentationAfterBytes));
                result.Add(sizes);
            }
            return result;
        }
    }
}
