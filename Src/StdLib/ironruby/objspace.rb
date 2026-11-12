# MRI's ext/objspace. Its work is heap walking and a per-object allocation record, neither of
# which the CLR offers, so:
#
# * dump(obj) describes the object it is given faithfully - type, class, size, value, what it
#   references - but the addresses are #object_id values, not real addresses, and MRI's
#   heap-internal fields (shape_id, slot_size, flags) are left out.
# * dump_all and memsize_of_all walk what ObjectSpace knows of instead of the heap: every module
#   and class, plus the strings, arrays, hashes and Ruby objects created since this file was
#   required - requiring it switches that recording on (see ObjectSpace.__track_objects__), which
#   is the one cost the library imposes, and nothing is recorded before it. Objects of other types
#   (a Proc, a Range, a CLR object) are not recorded at all.
# * memsize_of estimates the managed size: one object header plus the payload. It is meant for
#   comparing objects of the same kind, not for adding up a heap.
# * trace_object_allocations records the allocation site of the same four kinds of object, taken
#   from the TracePoint :line hook of the statement that allocated (so the file and line are the
#   statement's, and allocations inside a method of the core library written in Ruby - which has
#   no :line events, as it stands for C code - are reported at the caller's statement). The
#   class path and method id are those of the allocating method, nil in a block or at the top
#   level, as in MRI.

module ObjectSpace
  # The GC generations MRI would report; there is no such thing here, so dump_all's :since is
  # accepted and ignored.
  module_function

  # Dump the contents of a Ruby object as JSON.
  #
  # _output_ can be one of: +:stdout+, +:file+, +:string+, or an IO object.
  def dump(obj, output: :string)
    out = __objspace_output__(output, "rubyobj")
    out << __dump_entry__(obj)
    output == :stdout ? nil : out
  end

  # Dump the objects ObjectSpace knows of as JSON, one per line.
  def dump_all(output: :file, full: false, since: nil, shapes: true)
    out = __objspace_output__(output, "rubyheap")
    __tracked_objects__.each do |obj|
      entry = begin
        __dump_entry__(obj)
      rescue Exception
        nil
      end
      out << entry if entry
    end
    output == :stdout ? nil : out
  end

  # An estimate of the memory the object occupies. 0 for an immediate, as in MRI.
  def memsize_of(obj)
    __memsize_of__(obj)
  end

  # The estimated size of every object ObjectSpace knows of, or of those that are kind_of?
  # +klass+.
  def memsize_of_all(klass = nil)
    klass ? __memsize_of_all__(klass) : __memsize_of_all__
  end

  # The objects directly reachable from +obj+: its class, its instance variables and what a
  # container holds. nil for an immediate.
  def reachable_objects_from(obj)
    __reachable_objects_from__(obj)
  end

  # Not implemented: MRI answers the root set it marks from, which has no CLR counterpart.
  def reachable_objects_from_root
    raise NotImplementedError, "ObjectSpace.reachable_objects_from_root is not supported on IronRuby"
  end

  def trace_object_allocations
    __trace_start__
    begin
      yield
    ensure
      __trace_stop__
    end
  end

  def trace_object_allocations_start
    __trace_start__
    nil
  end

  def trace_object_allocations_stop
    __trace_stop__
    nil
  end

  def trace_object_allocations_clear
    __trace_clear__
    nil
  end

  def allocation_sourcefile(obj)
    info = __allocation_info__(obj)
    info && info[0]
  end

  def allocation_sourceline(obj)
    info = __allocation_info__(obj)
    info && info[1]
  end

  def allocation_class_path(obj)
    info = __allocation_info__(obj)
    info && info[2]
  end

  def allocation_method_id(obj)
    info = __allocation_info__(obj)
    info && info[3]
  end

  def allocation_generation(obj)
    info = __allocation_info__(obj)
    info && info[4]
  end

  class << self
    private

    # :string is a new String, :file and nil a Tempfile, :stdout $stdout, and an IO is used as it
    # is. Everything is appended with #<<, which all three answer with themselves.
    def __objspace_output__(output, basename)
      case output
      when :string
        +''
      when :file, nil
        require 'tempfile'
        Tempfile.create([basename, '.json'])
      when :stdout
        STDOUT
      when IO
        output
      else
        raise ArgumentError, "wrong output option: #{output.inspect}"
      end
    end

    # One dump line, terminated by a newline.
    def __dump_entry__(obj)
      type = __objspace_type__(obj)
      fields = []
      fields << ['address', __quote__(__hex__(obj))] unless %w(NIL TRUE FALSE FIXNUM SYMBOL).include?(type)
      fields << ['type', __quote__(type)]

      klass = begin
        obj.class
      rescue Exception
        nil
      end
      fields << ['class', __quote__(__hex__(klass))] if klass

      case type
      when 'STRING'
        fields << ['bytesize', obj.bytesize.to_s]
        value = __string_value__(obj)
        fields << ['value', __quote__(value)] if value
        fields << ['encoding', __quote__(obj.encoding.name)]
      when 'ARRAY'
        fields << ['length', obj.size.to_s]
      when 'HASH'
        fields << ['size', obj.size.to_s]
      when 'SYMBOL'
        fields << ['value', __quote__(obj.to_s)]
      when 'FIXNUM', 'BIGNUM', 'FLOAT'
        fields << ['value', __quote__(obj.to_s)]
      when 'CLASS', 'MODULE'
        name = obj.name
        fields << ['name', __quote__(name)] if name
      end

      ivars = begin
        obj.instance_variables
      rescue Exception
        []
      end
      fields << ['ivars', ivars.size.to_s] unless ivars.empty?

      references = __references__(obj, klass)
      unless references.empty?
        fields << ['references', '[' + references.map { |r| __quote__(r) }.join(', ') + ']']
      end

      fields << ['memsize', __memsize_of__(obj).to_s]

      '{' + fields.map { |name, value| "#{__quote__(name)}:#{value}" }.join(', ') + "}\n"
    end

    # The addresses of the objects the dumped object points at, leaving out its class (which the
    # dump names on its own) and anything without an address of its own.
    def __references__(obj, klass)
      reachable = __reachable_objects_from__(obj) or return []
      reachable.reject { |r| r.equal?(klass) }.map { |r| __hex__(r) }
    rescue Exception
      []
    end

    # MRI prints a real address; #object_id is the nearest thing here - it tells objects apart and
    # stays the same for the life of the object, which is what a dump is read for.
    def __hex__(obj)
      '0x%016x' % __address_of__(obj)
    end

    # The T_* name MRI dumps, worked out from the class rather than from a heap slot.
    def __objspace_type__(obj)
      case obj
      when nil then 'NIL'
      when true then 'TRUE'
      when false then 'FALSE'
      when Symbol then 'SYMBOL'
      when Integer then obj.bit_length < 62 ? 'FIXNUM' : 'BIGNUM'
      when Float then 'FLOAT'
      when String then 'STRING'
      when Array then 'ARRAY'
      when Hash then 'HASH'
      when Class then 'CLASS'
      when Module then 'MODULE'
      when Regexp then 'REGEXP'
      when Struct then 'STRUCT'
      when IO then 'FILE'
      when Proc, Method, UnboundMethod then 'DATA'
      else 'OBJECT'
      end
    rescue Exception
      'OBJECT'
    end

    # nil for a string that is not valid text: MRI dumps those as binary, which this does not.
    def __string_value__(str)
      value = str.encoding == Encoding::UTF_8 ? str : str.encode(Encoding::UTF_8)
      value.valid_encoding? ? value : nil
    rescue Exception
      nil
    end

    JSON_ESCAPES = {
      "\"" => "\\\"", "\\" => "\\\\", "\b" => "\\b", "\f" => "\\f",
      "\n" => "\\n", "\r" => "\\r", "\t" => "\\t"
    }.freeze
    __hide_constant__ :JSON_ESCAPES

    def __quote__(str)
      escaped = str.to_s.gsub(/["\\\x00-\x1f]/) { |c| JSON_ESCAPES[c] || ('\\u%04x' % c.ord) }
      "\"#{escaped}\""
    end
  end
end

# Remembering objects as they are created is what dump_all and memsize_of_all walk; nothing is
# recorded before this point, so an object created before the library was required is not dumped.
ObjectSpace.send :__track_objects__, true
