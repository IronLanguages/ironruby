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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    public sealed class CharacterMap {
        private const char Unmapped = '\0';

        // Null if _image contains full information, i.e.
        // No character maps to Unmapped character and so it can be used to mark unmapped characters in the image.
        private readonly BitArray _map;

        // Null if _map contains full information we need, i.e.
        // we only need to know if a character is mapped or not.
        private readonly char[] _image;

        private readonly int _complement;
        private readonly bool _isComplemental;
        private readonly int _min;

        // width = max - min
        private readonly uint _imageWidth;

        private CharacterMap(BitArray map, char[] image, int complement, bool isComplemental, int min, int max) {
            _map = map;
            _min = min;
            _imageWidth = (uint)(max - min);
            _image = image;
            _complement = complement;
            _isComplemental = isComplemental;
        }

        public int Complement {
            get { return _complement; }
        }

        public bool IsComplemental {
            get { return _isComplemental; }
        }

        public bool HasBitmap {
            get { return _map != null; }
        }

        public bool HasFullMap {
            get { return _complement >= 0 || _image != null; }
        }

        public int TryMap(char c) {
            Debug.Assert(HasFullMap);

            int index = c - _min;
            int complement = _complement;
            if (unchecked((uint)index <= _imageWidth)) {
                int im;

                if (complement < 0) {
                    im = _image[index];
                    if (im != Unmapped) {
                        return im;
                    }
                    if (_map == null) {
                        return -1;
                    }
                    complement = -1;
                } else {
                    im = -1;
                }

                return _map[index] ? im : complement;
            } else {
                return complement;
            }
        }

        public bool IsMapped(char c) {
            Debug.Assert(HasBitmap);
            int index = c - _min;
            return unchecked((uint)index <= _imageWidth) ? _map[index] : false;
        }

        public static CharacterMap/*!*/ Create(MutableString/*!*/ from, MutableString/*!*/ to) {
            Debug.Assert(!from.IsEmpty);

            int fromLength = from.GetCharCount();
            bool complemental = from.StartsWith('^') && fromLength > 1;

            // TODO: kcodings
            // TODO: surrogates
            // TODO: max - min > threshold

            int min, max;
            if (from.DetectByteCharacters()) {
                min = 0;
                max = 255;
            } else {
                min = Int32.MaxValue;
                max = -1;
                for (int i = (complemental ? 1 : 0); i < fromLength; i++) {
                    int c = from.GetChar(i);
                    if (c < min) {
                        min = c;
                    }
                    if (c > max) {
                        max = c;
                    }
                }
            }

            BitArray map;
            char[] image;

            if (complemental || to.IsEmpty) {
                image = null;
                map = MakeBitmap(from, fromLength, complemental, min, max);
            } else {
                map = null;
                image = new char[max - min + 1];

                // no need to initialize the array:
                Debug.Assert(Unmapped == 0);

                bool needMap = false;
                var toEnum = ExpandRanges(to, 0, to.GetCharCount(), true).GetEnumerator();
                foreach (var f in ExpandRanges(from, 0, fromLength, false)) {
                    toEnum.MoveNext();
                    needMap |= (image[f - min] = toEnum.Current) == Unmapped;
                }

                if (needMap) {
                    map = MakeBitmap(from, fromLength, false, min, max);
                }
            }

            return new CharacterMap(map, image, complemental ? to.GetLastChar() : -1, complemental, min, max);
        }

        private static BitArray MakeBitmap(MutableString/*!*/ from, int fromLength, bool complemental, int min, int max) {
            BitArray map = new BitArray(max - min + 1);
            foreach (var f in ExpandRanges(from, (complemental ? 1 : 0), fromLength, false)) {
                map.Set(f - min, true);
            }
            return map;
        }

        /// <summary>
        /// Expands a tr-style selector into the characters it names, following MRI's trnext:
        /// a backslash escapes the next character unless it is the last one, a dash is a range
        /// only when a character follows it, and a descending range is an error rather than an
        /// empty set.
        /// </summary>
        internal static IEnumerable<char>/*!*/ ExpandRanges(MutableString/*!*/ str, int start, int end, bool infinite) {
            char c = '\0';
            int i = start;
            while (i < end) {
                if (str.GetChar(i) == '\\' && i < end - 1) {
                    i++;
                }
                c = str.GetChar(i++);

                if (i < end - 1 && str.GetChar(i) == '-') {
                    i++;
                    char last = str.GetChar(i++);
                    if (c > last) {
                        throw RubyExceptions.CreateArgumentError(
                            String.Format("invalid range \"{0}-{1}\" in string transliteration", c, last)
                        );
                    }
                    for (char expanded = c; ; expanded++) {
                        yield return expanded;
                        if (expanded == last) {
                            break;
                        }
                    }
                    c = last;
                } else {
                    yield return c;
                }
            }

            if (infinite) {
                while (true) {
                    yield return c;
                }
            }
        }
    }

}
