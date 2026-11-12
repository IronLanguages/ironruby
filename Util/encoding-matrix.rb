# Cross-checks the Encoding table against CRuby, entry by entry.
#
#   ruby     Util/encoding-matrix.rb > /tmp/cruby.txt
#   ./ir.sh  Util/encoding-matrix.rb > /tmp/ir.txt
#   diff -u  /tmp/cruby.txt /tmp/ir.txt | grep -c '^-'
#
# Encodings are consulted by String, IO, Regexp and by mspec itself, so a wrong
# entry in this table shows up as failures that look like they belong to some
# other class.  Everything here is printed with a *literal* label so a diff line
# names exactly which encoding disagrees, and no part of the harness depends on
# the code under test (no #inspect of an Encoding, no interpolation of a name
# into a non-ASCII string).
#
# Six sections:
#
#   CONST  Encoding::<NAME> for every constant CRuby defines
#   FIND   Encoding.find(<name>) for every name in CRuby's Encoding.name_list
#   ALIAS  Encoding.aliases - which alias points at which primary name
#   LIST   Encoding.list, one primary encoding per line, sorted
#   NAMES  Encoding.name_list, one name per line, sorted
#   IDENT  pairs that must (or must not) be the same Encoding object
#
# The literal lists below come from CRuby 4.0.6.  They are deliberately frozen
# into the script rather than derived from Encoding.name_list at run time: if
# the list itself is wrong on one side, a derived matrix silently shrinks to
# match instead of reporting the missing rows.

def show(label, key)
  begin
    value = yield
  rescue Exception => e
    value = e.class.to_s
  end
  puts label + " " + key + " => " + value.to_s
end

def enc_facts(e)
  return "nil" if e.nil?
  "name=" + e.name +
    " dummy=" + (e.dummy? ? "true" : "false") +
    " ascii=" + (e.ascii_compatible? ? "true" : "false") +
    " names=" + e.names.sort.join(",")
end

# --------------------------------------------------------------------------
# CONST - every constant CRuby's Encoding defines, in CRuby's spelling.
# --------------------------------------------------------------------------

CONSTANTS = %w[
  ANSI_X3_4_1968 ASCII ASCII_8BIT BIG5 BIG5_HKSCS BIG5_HKSCS_2008 BIG5_UAO
  BINARY Big5 Big5_HKSCS Big5_HKSCS_2008 Big5_UAO CESU_8 CP1250 CP1251 CP1252
  CP1253 CP1254 CP1255 CP1256 CP1257 CP1258 CP437 CP50220 CP50221 CP51932
  CP65000 CP65001 CP720 CP737 CP775 CP850 CP852 CP855 CP857 CP860 CP861 CP862
  CP863 CP864 CP865 CP866 CP869 CP874 CP878 CP932 CP936 CP949 CP950 CP951
  CSWINDOWS31J CsWindows31J EBCDIC_CP_US EMACS_MULE EUCCN EUCJP EUCJP_MS EUCKR
  EUCTW EUC_CN EUC_JISX0213 EUC_JIS_2004 EUC_JP EUC_JP_MS EUC_KR EUC_TW
  Emacs_Mule EucCN EucJP EucJP_ms EucKR EucTW GB12345 GB18030 GB1988 GB2312 GBK
  IBM037 IBM437 IBM720 IBM737 IBM775 IBM850 IBM852 IBM855 IBM857 IBM860 IBM861
  IBM862 IBM863 IBM864 IBM865 IBM866 IBM869 ISO2022_JP ISO2022_JP2 ISO8859_1
  ISO8859_10 ISO8859_11 ISO8859_13 ISO8859_14 ISO8859_15 ISO8859_16 ISO8859_2
  ISO8859_3 ISO8859_4 ISO8859_5 ISO8859_6 ISO8859_7 ISO8859_8 ISO8859_9
  ISO_2022_JP ISO_2022_JP_2 ISO_2022_JP_KDDI ISO_8859_1 ISO_8859_10 ISO_8859_11
  ISO_8859_13 ISO_8859_14 ISO_8859_15 ISO_8859_16 ISO_8859_2 ISO_8859_3
  ISO_8859_4 ISO_8859_5 ISO_8859_6 ISO_8859_7 ISO_8859_8 ISO_8859_9 KOI8_R
  KOI8_U MACCENTEURO MACCROATIAN MACCYRILLIC MACGREEK MACICELAND MACJAPAN
  MACJAPANESE MACROMAN MACROMANIA MACTHAI MACTURKISH MACUKRAINE MacCentEuro
  MacCroatian MacCyrillic MacGreek MacIceland MacJapan MacJapanese MacRoman
  MacRomania MacThai MacTurkish MacUkraine PCK SHIFT_JIS SJIS SJIS_DOCOMO
  SJIS_DoCoMo SJIS_KDDI SJIS_SOFTBANK SJIS_SoftBank STATELESS_ISO_2022_JP
  STATELESS_ISO_2022_JP_KDDI Shift_JIS Stateless_ISO_2022_JP
  Stateless_ISO_2022_JP_KDDI TIS_620 UCS_2BE UCS_4BE UCS_4LE US_ASCII
  UTF8_DOCOMO UTF8_DoCoMo UTF8_KDDI UTF8_MAC UTF8_SOFTBANK UTF8_SoftBank UTF_16
  UTF_16BE UTF_16LE UTF_32 UTF_32BE UTF_32LE UTF_7 UTF_8 UTF_8_HFS UTF_8_MAC
  WINDOWS_1250 WINDOWS_1251 WINDOWS_1252 WINDOWS_1253 WINDOWS_1254 WINDOWS_1255
  WINDOWS_1256 WINDOWS_1257 WINDOWS_1258 WINDOWS_31J WINDOWS_874 Windows_1250
  Windows_1251 Windows_1252 Windows_1253 Windows_1254 Windows_1255 Windows_1256
  Windows_1257 Windows_1258 Windows_31J Windows_874
]

CONSTANTS.each do |c|
  show("CONST", c) do
    e = Encoding.const_get(c)
    e.is_a?(Encoding) ? enc_facts(e) : "not-an-Encoding:" + e.class.to_s
  end
end

# --------------------------------------------------------------------------
# FIND - Encoding.find for every name CRuby's Encoding.name_list reports.
#
# "external", "filesystem", "internal" and "locale" resolve to whatever the
# process was started with, so they are checked for resolvability only.
# --------------------------------------------------------------------------

NAMES = %w[
  646 ANSI_X3.4-1968 ASCII ASCII-8BIT BINARY Big5 Big5-HKSCS Big5-HKSCS:2008
  Big5-UAO CESU-8 CP1250 CP1251 CP1252 CP1253 CP1254 CP1255 CP1256 CP1257
  CP1258 CP437 CP50220 CP50221 CP51932 CP65000 CP65001 CP720 CP737 CP775 CP850
  CP852 CP855 CP857 CP860 CP861 CP862 CP863 CP864 CP865 CP866 CP869 CP874 CP878
  CP932 CP936 CP949 CP950 CP951 EUC-CN EUC-JIS-2004 EUC-JISX0213 EUC-JP EUC-KR
  EUC-TW Emacs-Mule GB12345 GB18030 GB1988 GB2312 GBK IBM037 IBM437 IBM720
  IBM737 IBM775 IBM850 IBM852 IBM855 IBM857 IBM860 IBM861 IBM862 IBM863 IBM864
  IBM865 IBM866 IBM869 ISO-2022-JP ISO-2022-JP-2 ISO-2022-JP-KDDI ISO-8859-1
  ISO-8859-10 ISO-8859-11 ISO-8859-13 ISO-8859-14 ISO-8859-15 ISO-8859-16
  ISO-8859-2 ISO-8859-3 ISO-8859-4 ISO-8859-5 ISO-8859-6 ISO-8859-7 ISO-8859-8
  ISO-8859-9 ISO2022-JP ISO2022-JP2 ISO8859-1 ISO8859-10 ISO8859-11 ISO8859-13
  ISO8859-14 ISO8859-15 ISO8859-16 ISO8859-2 ISO8859-3 ISO8859-4 ISO8859-5
  ISO8859-6 ISO8859-7 ISO8859-8 ISO8859-9 KOI8-R KOI8-U MacJapan MacJapanese
  PCK SJIS SJIS-DoCoMo SJIS-KDDI SJIS-SoftBank Shift_JIS TIS-620 UCS-2BE UCS-4BE
  UCS-4LE US-ASCII UTF-16 UTF-16BE UTF-16LE UTF-32 UTF-32BE UTF-32LE UTF-7 UTF-8
  UTF-8-HFS UTF-8-MAC UTF8-DoCoMo UTF8-KDDI UTF8-MAC UTF8-SoftBank Windows-1250
  Windows-1251 Windows-1252 Windows-1253 Windows-1254 Windows-1255 Windows-1256
  Windows-1257 Windows-1258 Windows-31J Windows-874 csWindows31J ebcdic-cp-us
  euc-jp-ms eucCN eucJP eucJP-ms eucKR eucTW macCentEuro macCroatian macCyrillic
  macGreek macIceland macRoman macRomania macThai macTurkish macUkraine
  stateless-ISO-2022-JP stateless-ISO-2022-JP-KDDI
]

NAMES.each do |n|
  show("FIND", n) { enc_facts(Encoding.find(n)) }
end

%w[external filesystem internal locale].each do |n|
  show("FIND", n) { Encoding.find(n).nil? ? "nil" : "resolves" }
end

# Case insensitivity and a few spellings that are not in name_list but that
# real code passes to Encoding.find.
%w[utf-8 UTF8 Utf-8 binary Binary ascii-8bit us-ascii shift_jis sjis
   utf-16 utf-16le utf-16be utf-32 cesu-8 tis-620 unknown-encoding].each do |n|
  show("FIND", n) { enc_facts(Encoding.find(n)) }
end

# --------------------------------------------------------------------------
# ALIAS - Encoding.aliases.  Printed one pair per line so a diff names the
# alias that is missing or points somewhere else.  "external", "filesystem",
# "internal" and "locale" depend on the environment and are skipped.
# --------------------------------------------------------------------------

ENVIRONMENTAL = %w[external filesystem internal locale]

Encoding.aliases.sort.each do |from, to|
  next if ENVIRONMENTAL.include?(from)
  puts "ALIAS " + from + " => " + to
end

# --------------------------------------------------------------------------
# LIST / NAMES - the table itself.  One entry per line, sorted, so extra and
# missing encodings each cost exactly one diff line.
# --------------------------------------------------------------------------

Encoding.list.map { |e| e.name }.sort.each do |n|
  puts "LIST " + n
end

Encoding.name_list.reject { |n| ENVIRONMENTAL.include?(n) }.sort.each do |n|
  puts "NAMES " + n
end

# Every entry of Encoding.list must be findable under its own name and must
# come back as the same object.
Encoding.list.sort_by { |e| e.name }.each do |e|
  show("ROUNDTRIP", e.name) { Encoding.find(e.name).equal?(e) ? "same" : "different" }
end

# --------------------------------------------------------------------------
# IDENT - encodings that MRI keeps distinct.  UTF-16 and UTF-32 without an
# endianness suffix are dummy BOM encodings, *not* aliases of the LE forms;
# TIS-620 is not Windows-874 (Windows-874 adds the C1 range and the Windows
# punctuation); CESU-8 is not UTF-8.
# --------------------------------------------------------------------------

[
  %w[UTF-16 UTF-16LE], %w[UTF-16 UTF-16BE], %w[UTF-16LE UTF-16BE],
  %w[UTF-32 UTF-32LE], %w[UTF-32 UTF-32BE], %w[UTF-32LE UTF-32BE],
  %w[TIS-620 Windows-874], %w[TIS-620 CP874], %w[Windows-874 CP874],
  %w[CESU-8 UTF-8], %w[US-ASCII ASCII-8BIT], %w[ASCII-8BIT BINARY],
  %w[UCS-2BE UTF-16BE], %w[UCS-4BE UTF-32BE], %w[UCS-4LE UTF-32LE],
  %w[SJIS Shift_JIS], %w[Windows-31J CP932], %w[Shift_JIS Windows-31J],
  %w[ISO-2022-JP CP50220], %w[EUC-JP CP51932], %w[UTF-8 CP65001],
].each do |a, b|
  show("IDENT", a + "/" + b) do
    ea = Encoding.find(a) rescue nil
    eb = Encoding.find(b) rescue nil
    if ea.nil? || eb.nil?
      "unresolvable"
    else
      ea.equal?(eb) ? "same" : "different"
    end
  end
end

# --------------------------------------------------------------------------
# BYTES - what each encoding actually does to a byte string.  A table entry can
# carry the right name and still be backed by the wrong converter, which is how
# TIS-620 resolving to Windows-874 hides.  Printed as hex so the output stays
# diffable.
# --------------------------------------------------------------------------

def hex(s)
  s.each_byte.map { |b| b.to_s(16).rjust(2, "0") }.join
end

SAMPLE_CODEPOINTS = [0x41, 0x7f, 0x80, 0xa0, 0xe9, 0x20ac, 0x0e01, 0x3042, 0x10000]

%w[US-ASCII ASCII-8BIT UTF-8 UTF-16 UTF-16LE UTF-16BE UTF-32 UTF-32LE UTF-32BE
   CESU-8 TIS-620 Windows-874 ISO-8859-1 Shift_JIS EUC-JP].each do |name|
  SAMPLE_CODEPOINTS.each do |cp|
    show("BYTES", name + " U+" + cp.to_s(16).rjust(4, "0")) do
      e = Encoding.find(name)
      hex(cp.chr(e))
    end
  end
end
