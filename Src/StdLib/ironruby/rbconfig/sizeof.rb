require 'rbconfig'

# MRI generates this from the C compiler it was built with. IronRuby has no C
# compiler behind it; these are the C ABI's type sizes for the platform the
# process runs on (the ABI native code reached through P/Invoke uses).
module RbConfig
  windows = RUBY_PLATFORM =~ /mswin|mingw/
  ptr = System::IntPtr.Size
  # LP64 everywhere but 64-bit Windows, which is LLP64
  long = windows ? 4 : ptr
  wchar = windows ? 2 : 4
  # char is unsigned in the ARM ABIs (Apple's excepted)
  unsigned_char = RUBY_PLATFORM =~ /\A(arm|aarch64)-linux/

  sizeof = {
    "int" => 4, "short" => 2, "long" => long, "long long" => 8,
    "off_t" => 8, "void*" => ptr, "float" => 4, "double" => 8, "time_t" => 8,
    "size_t" => ptr, "ptrdiff_t" => ptr,
    "int8_t" => 1, "uint8_t" => 1, "int16_t" => 2, "uint16_t" => 2,
    "int32_t" => 4, "uint32_t" => 4, "int64_t" => 8, "uint64_t" => 8,
    "intptr_t" => ptr, "uintptr_t" => ptr, "intmax_t" => 8,
    "wchar_t" => wchar, "_Bool" => 1,
  }
  sizeof["ssize_t"] = ptr unless windows
  SIZEOF = sizeof

  signed = ->(bytes) { [-(1 << (bytes * 8 - 1)), (1 << (bytes * 8 - 1)) - 1] }
  unsigned_max = ->(bytes) { (1 << (bytes * 8)) - 1 }

  limits = {}
  # An Integer is a System.Int32 or a System.Int64 until arithmetic overflows Int64 into a
  # BigInteger, so Int64 is IronRuby's Fixnum range, as long is JRuby's.
  limits["FIXNUM_MIN"], limits["FIXNUM_MAX"] = signed[8]
  if unsigned_char
    limits["CHAR_MIN"], limits["CHAR_MAX"] = 0, 255
  else
    limits["CHAR_MIN"], limits["CHAR_MAX"] = signed[1]
  end
  limits["SCHAR_MIN"], limits["SCHAR_MAX"] = signed[1]
  limits["UCHAR_MAX"] = unsigned_max[1]
  if windows
    limits["WCHAR_MIN"], limits["WCHAR_MAX"] = 0, unsigned_max[2]
  else
    limits["WCHAR_MIN"], limits["WCHAR_MAX"] = signed[4]
  end
  { "SHRT" => 2, "INT" => 4, "LONG" => long, "LLONG" => 8 }.each do |name, bytes|
    limits["#{name}_MIN"], limits["#{name}_MAX"] = signed[bytes]
    limits["U#{name}_MAX"] = unsigned_max[bytes]
  end
  [1, 2, 4, 8].each do |bytes|
    bits = bytes * 8
    limits["INT#{bits}_MIN"], limits["INT#{bits}_MAX"] = signed[bytes]
    limits["UINT#{bits}_MAX"] = unsigned_max[bytes]
  end
  limits["INTMAX_MIN"], limits["INTMAX_MAX"] = signed[8]
  limits["UINTMAX_MAX"] = unsigned_max[8]
  limits["INTPTR_MIN"], limits["INTPTR_MAX"] = signed[ptr]
  limits["UINTPTR_MAX"] = unsigned_max[ptr]
  limits["PTRDIFF_MIN"], limits["PTRDIFF_MAX"] = signed[ptr]
  limits["SIZE_MAX"] = unsigned_max[ptr]
  limits["SSIZE_MAX"] = signed[ptr][1] unless windows

  # IEEE 754 binary32 / binary64
  limits.update(
    "FLT_RADIX" => 2, "FLT_MANT_DIG" => 24, "DBL_MANT_DIG" => Float::MANT_DIG,
    "FLT_DIG" => 6, "DBL_DIG" => Float::DIG,
    "FLT_MIN_EXP" => -125, "DBL_MIN_EXP" => Float::MIN_EXP,
    "FLT_MIN_10_EXP" => -37, "DBL_MIN_10_EXP" => Float::MIN_10_EXP,
    "FLT_MAX_EXP" => 128, "DBL_MAX_EXP" => Float::MAX_EXP,
    "FLT_MAX_10_EXP" => 38, "DBL_MAX_10_EXP" => Float::MAX_10_EXP,
    "FLT_DECIMAL_DIG" => 9, "DBL_DECIMAL_DIG" => 17,
    "FLT_MAX" => 3.4028234663852886e+38, "DBL_MAX" => Float::MAX,
    "FLT_EPSILON" => 1.1920928955078125e-07, "DBL_EPSILON" => Float::EPSILON,
    "FLT_MIN" => 1.1754943508222875e-38, "DBL_MIN" => Float::MIN,
    "FLT_TRUE_MIN" => 1.401298464324817e-45, "DBL_TRUE_MIN" => 5.0e-324
  )
  LIMITS = limits
end
