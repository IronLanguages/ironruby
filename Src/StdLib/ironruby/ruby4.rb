# Ruby 4 compatibility layer for IronRuby's 1.9-era core library.
# Pure Ruby, loaded from gem_prelude. Pattern-matching support classes plus
# widely-used core methods added between Ruby 2.0 and 4.0.

module Kernel
  # X.try_convert asks for a conversion and then insists on the result: nil when the
  # object has no such method or answers nil, the result when it is of the right kind,
  # and a TypeError naming both classes otherwise. The wording is the one ruby/spec
  # asks of an implementation other than CRuby's - "into", where CRuby still says "to".
  private def __ir_try_convert__(obj, method, klass, name)
    return obj if klass === obj
    return nil unless __ir_responds_to__(obj, method)
    converted = obj.__send__(method)
    return nil if converted.nil?
    return converted if klass === converted
    ::Kernel.raise(::TypeError,
      "can't convert #{obj.class} to #{name} (#{obj.class}##{method} gives #{__ir_conversion_result_name__(converted)})")
  end

  # __ir_responds_to__(obj, method) is KernelOps.RespondsTo: it asks the class obj has
  # without making obj a singleton class to ask through.

  # The class of an object that may not answer #class: a BasicObject has none, and MRI's
  # conversion messages name its class all the same.
  private def __ir_class_of__(obj)
    ::Kernel.instance_method(:class).bind(obj).call
  end
end

class NoMatchingPatternError < StandardError; end
class NoMatchingPatternError
  # What a failed `=>` or `case/in` says after the subject, formatted as MRI formats
  # it; the prism bridge records the reason of the test that failed last.
  def self.__reason__(kind, *a)
    case kind
    when :eqq then "#{a[0].inspect} === #{a[1].inspect} does not return true"
    when :length then "#{a[0].inspect} length mismatch (given #{a[1]}, expected #{a[2]}#{a[3] ? '+' : ''})"
    when :deconstruct then "#{a[0].inspect} does not respond to #deconstruct"
    when :deconstruct_keys then "#{a[0].inspect} does not respond to #deconstruct_keys"
    when :empty then "#{a[0].inspect} is not empty"
    when :rest then "rest of #{a[0].reject { |k, _| a[1].include?(k) }.inspect} is not empty"
    when :find then "#{a[0].inspect} does not match to find pattern"
    when :guard then "guard clause does not return true"
    end
  end

  def self.__message__(subject, reason)
    reason ? "#{subject.inspect}: #{reason}" : subject.inspect
  end
end

class NoMatchingPatternKeyError < NoMatchingPatternError
  # KeyError's shape: the pattern's hash and the key it lacked, both optional.
  def initialize(message = nil, matchee: (no_matchee = true; nil), key: (no_key = true; nil))
    message.nil? ? super() : super(message)
    @__matchee__ = matchee unless no_matchee
    @__key__ = key unless no_key
  end

  def matchee
    ::Kernel.raise ::ArgumentError, "no matchee is available" unless defined?(@__matchee__)
    @__matchee__
  end

  def key
    ::Kernel.raise ::ArgumentError, "no key is available" unless defined?(@__key__)
    @__key__
  end

  # What a `case/in` or `=>` raises when the last thing that failed was a hash pattern's key.
  def self.__missing_key__(matchee, key)
    new("#{matchee.inspect}: key not found: #{key.inspect}", matchee: matchee, key: key)
  end
end
class FrozenError < RuntimeError; end unless defined?(FrozenError)

module Kernel
  # #yield_self is an alias of #then (MRI's <internal:kernel>), so the two are one
  # entry, and the original name - the one an Enumerator and Method#name report - is :then.
  # `defined?(yield)', as MRI writes it, rather than block_given?, which is a call: the
  # method then runs without a scope when it is given a block (MethodDefinition.IsBlockGuard).
  def then
    return to_enum(:then) { 1 } unless defined?(yield)
    yield self
  end unless method_defined?(:then)
  alias :yield_self :then unless method_defined?(:yield_self)

  # Written in Ruby, as MRI's is (<internal:kernel>): its frame is reported with its caller's
  # location, and it has a source_location.
  def tap
    yield(self)
    self
  end

  def itself
    self
  end unless method_defined?(:itself)

  # The Method object for a method defined only on this object. Kernel#method
  # would happily answer one inherited from the class.
  def singleton_method(name)
    name = name.to_sym if name.respond_to?(:to_sym)
    unless name.is_a?(::Symbol)
      ::Kernel.raise(::TypeError, "#{name.inspect} is not a symbol nor a string")
    end
    klass = (singleton_class rescue nil)
    # nil, true and false answer their own class here rather than a singleton class of
    # their own, so `def (nil).foo' defines an ordinary NilClass method and there is no
    # singleton method to hand back.
    # The method has to come from the singleton class or a module mixed into it (#20620),
    # not from the object's class. instance_methods(false) here would list the class's
    # methods too, the way obj.methods(false) does.
    defined = klass && klass.singleton_class? &&
      (klass.ancestors - klass.superclass.ancestors).include?(
        (klass.instance_method(name).owner rescue nil))
    unless defined
      # MRI names a class or module here by its #name, anything else by its #inspect.
      receiver = (::Module === self && self.name) || inspect
      ::Kernel.raise(::NameError, "undefined singleton method '#{name}' for '#{receiver}'")
    end
    method(name)
  end unless method_defined?(:singleton_method)
end

# ruby2_keywords (2.7) marks a method so that a trailing Hash which arrived as keyword
# arguments keeps saying so while it sits in the method's rest parameter, and becomes keyword
# arguments again when that rest parameter is splatted onward. The mark itself is kept by the
# runtime, on the method's body, so that every alias of the method shares it; what is here is
# the argument checking and the warning.
class Module
  def ruby2_keywords(*names)
    names.each do |name|
      unless name.is_a?(::Symbol) || name.is_a?(::String)
        ::Kernel.raise(::TypeError, "#{name.inspect} is not a symbol nor a string")
      end
      sym = name.to_sym
      # NameError, not NoMethodError, and it names the missing method.
      unless method_defined?(sym) || private_method_defined?(sym)
        ::Kernel.raise(::NameError,
          "undefined method '#{sym}' for #{::Class === self ? 'class' : 'module'} '#{self.name || inspect}'")
      end
      # The flag only means anything for a method of the shape `def m(*args)`.
      # MRI warns and skips otherwise; the wording is rb_warn's, verbatim.
      unless __ir_mark_ruby2_keywords__(sym)
        ::Kernel.warn("Skipping set of ruby2_keywords flag for #{sym} " \
                      "(method accepts keywords or post arguments or method does not accept argument splat)")
      end
    end
    nil
  end unless private_method_defined?(:ruby2_keywords) || method_defined?(:ruby2_keywords)
  private :ruby2_keywords rescue nil
end

module Kernel
  # MRI unified Fixnum and Bignum into Integer in 2.4; IronRuby still has the
  # split classes, so messages that name a class have to hide it.
  def __ir_message_type_name__(value)
    name = value.class.to_s
    (name == "Fixnum" || name == "Bignum") ? "Integer" : name
  end
  private :__ir_message_type_name__

  # What a failed conversion calls the thing the converter answered. MRI's
  # rb_builtin_class_name names the three singleton values by themselves rather than by
  # their class here - "gives nil", not "gives NilClass" - which is not what it does in
  # messages that name the argument's type.
  def __ir_conversion_result_name__(value)
    case value
    when ::NilClass then "nil"
    when ::TrueClass then "true"
    when ::FalseClass then "false"
    else __ir_message_type_name__(value)
    end
  end
  private :__ir_conversion_result_name__
end

module Kernel
  private

  def require_relative(path)
    # caller(0), not caller: this method's own frame is reported with its caller's file and
    # line, and where the CLR stack cannot be walked (AOT-compiled browser-wasm) the frame of a
    # method the DLR has compiled is missing altogether - caller(1) would then skip the caller.
    caller_path = caller(0).first.split(/:\d/, 2).first
    # Relative to where the file really is: through a symlink, the link's target, and for a
    # file run by a relative path, where it was before any chdir.
    if !File.file?(caller_path) || File.symlink?(caller_path)
      location = __source_location_of__(caller_path)
    end
    # MRI's require_relative is rb_f_require_relative, which goes straight into the
    # loader; it never re-dispatches through the caller's #require. Sending :require to
    # self would pick up any singleton or module #require that happens to be in scope --
    # Bundler.require(*groups), for one, which Bundler.self_manager would then invoke
    # instead of loading bundler/self_manager.rb. Bind Kernel#require (which RubyGems
    # replaces, so gem activation still happens) rather than calling self.require.
    ::Kernel.instance_method(:require).bind(self).
      call(File.expand_path(path, File.dirname(location || caller_path)))
  end unless private_method_defined?(:require_relative)

  # Like #require, this is both a private instance method and a public singleton
  # method on Kernel -- Kernel.require_relative(path) is a documented call.
  module_function :require_relative
  private :require_relative
end

class Ractor
  class << self
    private

    # CRuby 4.0's ractor.rb: called when the first Ractor is created. A non-main Ractor's
    # #require goes to Ractor._require, which has the main Ractor load the file - ahead of
    # RubyGems' Kernel#require, whose own state a non-main Ractor may not touch.
    def _activated
      Kernel.prepend Module.new { |m|
        m.set_temporary_name '<RactorRequire>'

        def require(feature)
          if Ractor.main?
            super
          else
            Ractor._require feature
          end
        end
      }
    end
  end
end

module Enumerable
  def filter_map(&block)
    return to_enum(:filter_map) { size if respond_to?(:size) } unless block
    result = []
    # Like #map, the yielded values reach the block as they were yielded:
    # `yield 1, 2` is 1 to a one-parameter block, not [1, 2].
    each { |*values| v = block.call(*values); result << v if v }
    result
  end unless method_defined?(:filter_map)

  # The optional Hash counts into it instead of into a fresh one, which also
  # means the frozen check happens before the first element is looked at.
  def tally(counts = nil)
    # ::NilClass === and ::Hash ===, not #nil? and #is_a?: a BasicObject answers neither,
    # and has to be the TypeError MRI's rb_to_hash_type makes of it.
    if ::NilClass === counts
      return __list_tally__ if __plain_list__?
      result = {}
      each { |*values| item = __enum_item__(values); __tally_store__(result, item, (result[item] || 0) + 1) }
      return result
    end

    hash = ::Hash === counts ? counts : Hash.try_convert(counts)
    raise TypeError, "no implicit conversion of #{__ir_class_of__(counts)} into Hash" if hash.nil?
    raise FrozenError.new("can't modify frozen Hash: #{hash.inspect}", receiver: hash) if hash.frozen?
    each do |*values|
      item = __enum_item__(values)
      # #fetch rather than #[] so that a default value or default proc on the
      # given Hash cannot be mistaken for a count that is already there.
      n = hash.fetch(item, 0)
      unless n.is_a?(Integer)
        raise TypeError, "wrong argument type #{n.class} (expected Integer)"
      end
      # stored as it is, as MRI's tally_up does: a String is not copied and frozen
      __tally_store__(hash, item, n + 1)
    end
    hash
  end unless method_defined?(:tally)

  # MRI does not simply fold with +: once a Float turns up it switches to
  # Kahan-Babuska compensated summation, which is why ([0.1] * 10).sum is
  # exactly 1.0 there and 0.9999999999999999 under a naive inject. Non-numeric
  # elements (String, Array) keep the plain fold.
  def sum(init = 0)
    acc = init
    compensation = 0.0
    compensating = false
    each do |*values|
      x = __enum_item__(values)
      x = yield(x) if block_given?
      unless compensating
        unless x.is_a?(Float) && (acc.is_a?(Integer) || acc.is_a?(Float) || acc.is_a?(Rational))
          acc = acc + x
          next
        end
        compensating = true
        acc = acc.to_f
      end
      unless x.is_a?(Integer) || x.is_a?(Float) || x.is_a?(Rational)
        # back out of float mode: settle the compensation before leaving it behind
        acc += compensation
        compensation = 0.0
        compensating = false
        acc = acc + x
        next
      end
      x = x.to_f

      # Nothing infinite can be compensated: (1.0 - Infinity) + Infinity is NaN, which is
      # how a single Infinity in the list used to take the whole sum with it. A NaN stays,
      # an infinity swallows the sum unless the sum is the other infinity, and a finite
      # number adds nothing to an infinite sum.
      if acc.nan?
        next
      elsif x.nan?
        acc = x
        compensation = 0.0
        next
      elsif x.infinite?
        acc = (acc.infinite? == -x.infinite?) ? (0.0 / 0.0) : x
        compensation = 0.0
        next
      elsif acc.infinite?
        next
      end

      t = acc + x
      compensation += acc.abs >= x.abs ? (acc - t) + x : (x - t) + acc
      acc = t
    end
    compensating ? acc + compensation : acc
  end unless method_defined?(:sum)

  alias :filter :select unless method_defined?(:filter)

  # A `yield` with several values is one element, packed into an Array; a yield
  # with none is nil. Everything below goes through this so that
  # `def each; yield 1, 2; end` behaves the way the C# side now does.
  def __enum_item__(values)
    case values.size
    when 0 then nil
    when 1 then values[0]
    else values
    end
  end
  private :__enum_item__

  # Extra arguments are passed straight through to #each, which is how a
  # receiver whose #each takes parameters gets to see them.
  def each_entry(*args, &block)
    return to_enum(:each_entry, *args) unless block
    each(*args) { |*values| block.call(__enum_item__(values)) }
    self
  end unless method_defined?(:each_entry)

  unless method_defined?(:__each_with_index_without_args__)
    alias :__each_with_index_without_args__ :each_with_index

    def each_with_index(*args, &block)
      return to_enum(:each_with_index, *args) { size if respond_to?(:size) } unless block
      return __each_with_index_without_args__(&block) if args.empty?
      index = 0
      each(*args) do |*values|
        block.call(__enum_item__(values), index)
        index += 1
      end
      self
    end
  end
  # Enumerator#next walks [...].each_with_index with a cursor while this is the method.
  ::Enumerator.__iter_cursor_method__(self, :each_with_index)

  # Ruby 2.5 gave the predicates an optional pattern, matched with #===.
  # The built-ins only know the block form, and because they are defined on
  # Enumerable they shadow rather than extend, so wrap them.
  unless method_defined?(:__all_without_pattern__?)
    alias :__all_without_pattern__? :all?
    alias :__any_without_pattern__? :any?
    alias :__none_without_pattern__? :none?
    alias :__one_without_pattern__? :one?

    def __check_pattern_args__(args, block = nil)
      if args.size > 1
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
      end
      # The pattern wins and the block is never called, which is worth saying out loud.
      # MRI names the line that called #all? and friends. The prelude's frames stand in a
      # backtrace as a C method's do, at their caller's line, and this helper's not at all:
      # #all? is the first frame above #warn and its caller the second. (uplevel 2 went past
      # the end of a top-level script's backtrace, and the warning had no location.)
      if block && !args.empty? && !$VERBOSE.nil?
        warn "given block not used", uplevel: 1
      end
    end
    private :__check_pattern_args__

    def all?(*args, &block)
      __check_pattern_args__(args, block)
      return __all_without_pattern__?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return false unless pattern === __enum_item__(values) }
      true
    end

    def any?(*args, &block)
      __check_pattern_args__(args, block)
      return __any_without_pattern__?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return true if pattern === __enum_item__(values) }
      false
    end

    def none?(*args, &block)
      __check_pattern_args__(args, block)
      return __none_without_pattern__?(&block) if args.empty?
      pattern = args[0]
      each { |*values| return false if pattern === __enum_item__(values) }
      true
    end

    def one?(*args, &block)
      __check_pattern_args__(args, block)
      return __one_without_pattern__?(&block) if args.empty?
      pattern = args[0]
      found = false
      each do |*values|
        next unless pattern === __enum_item__(values)
        return false if found
        found = true
      end
      found
    end
  end

  # The method-name form of #inject reports a bad name the way MRI does - by
  # inspecting the object rather than by naming its class - and takes at most
  # two arguments. A block alongside a method name is never called.
  unless method_defined?(:__inject_without_name_check__)
    alias :__inject_without_name_check__ :inject

    # inject and reduce are one C function in MRI, which a backtrace names by
    # the method called - so each is a method here, not an alias.
    def __enum_inject__(args, block)
      if args.size > 2 || (args.empty? && !block)
        raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)"
      end
      if args.size == 2 || (args.size == 1 && !block)
        name = args.last
        unless name.is_a?(Symbol) || name.is_a?(String)
          # A name that is neither is still given the chance to be a String.
          name = name.respond_to?(:to_str) ? name.to_str : nil
          unless name.is_a?(String)
            raise TypeError, "#{args.last.inspect} is not a symbol nor a string"
          end
          args = args.dup
          args[-1] = name
        end
        if block && !$VERBOSE.nil?
          warn "given block not used", uplevel: 1
        end
      end
      __inject_without_name_check__(*args, &block)
    end
    private :__enum_inject__

    def inject(*args, &block)
      __enum_inject__(args, block)
    end

    def reduce(*args, &block)
      __enum_inject__(args, block)
    end
    __ir_same_method__(:reduce, :inject)
  end

  # min/max also grew an `n` form.
  unless method_defined?(:__min_without_count__)
    alias :__min_without_count__ :min
    alias :__max_without_count__ :max

    def min(*args, &block)
      n = args.empty? ? nil : __count_arg__(args[0])
      return __min_without_count__(&block) if n.nil?
      sorted = block ? to_a.sort(&block) : to_a.sort
      sorted.first(n)
    end

    def max(*args, &block)
      n = args.empty? ? nil : __count_arg__(args[0])
      return __max_without_count__(&block) if n.nil?
      sorted = block ? to_a.sort(&block) : to_a.sort
      sorted.reverse.first(n)
    end
  end

  def group_by(&block)
    return to_enum(:group_by) unless block
    # an Array is grouped in C#, see Enumerable.ListGroupBy
    return __list_group_by__(&block) if __plain_list__?
    result = {}
    each { |*values| item = __enum_item__(values); (result[yield(item)] ||= []) << item }
    result
  end unless method_defined?(:group_by)

  def each_with_object(memo)
    return to_enum(:each_with_object, memo) unless block_given?
    each { |*values| yield(__enum_item__(values), memo) }
    memo
  end unless method_defined?(:each_with_object)

  # Ruby 2.2 added the `n` form: the n smallest/largest, as an Array.
  def min_by(*args, &block)
    if args.size > 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    return to_enum(:min_by, *args) { size if respond_to?(:size) } unless block
    args = [] if args.size == 1 && args[0].nil?
    if args.empty?
      __extreme_by__(-1, &block)
    else
      __sort_by_key__(block).first(__count_arg__(args[0]))
    end
  end unless method_defined?(:min_by)

  # The first element whose key compares best: a later equal key does not replace it, a
  # nil key is a key like any other (nil <=> nil is 0), and a key that will not compare -
  # NaN, say - is the "comparison of X with Y failed" MRI raises from rb_cmpint.
  def __extreme_by__(direction, &block)
    best = nil
    best_key = nil
    found = false
    each do |*values|
      item = __enum_item__(values)
      key = block.call(item)
      if found
        c = (key <=> best_key)
        if c.nil?
          raise ArgumentError, "comparison of #{key.class} with #{::Comparable.__cmp_operand__(best_key)} failed"
        end
        next unless (direction < 0) ? c < 0 : c > 0
      end
      found = true
      best_key = key
      best = item
    end
    best
  end
  private :__extreme_by__

  def max_by(*args, &block)
    if args.size > 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    return to_enum(:max_by, *args) { size if respond_to?(:size) } unless block
    args = [] if args.size == 1 && args[0].nil?
    if args.empty?
      __extreme_by__(1, &block)
    else
      __sort_by_key__(block).reverse.first(__count_arg__(args[0]))
    end
  end unless method_defined?(:max_by)

  # nil is not a count: `max(nil)` is the plain no-argument form, not an error.
  def __count_arg__(n)
    return nil if n.nil?
    n = n.to_int unless n.is_a?(Integer)
    raise ArgumentError, "negative size (#{n})" if n < 0
    n
  end
  private :__count_arg__

  # Sorts by the block's key, keeping input order for equal keys so that
  # `min_by(n)` and `max_by(n)` agree with MRI on ties.
  def __sort_by_key__(block)
    decorated = []
    index = 0
    each do |*values|
      item = __enum_item__(values)
      decorated << [block.call(item), index, item]
      index += 1
    end
    decorated.sort { |a, b| c = (a[0] <=> b[0]); c == 0 ? (a[1] <=> b[1]) : c }.map { |triple| triple[2] }
  end
  private :__sort_by_key__

  def minmax_by(&block)
    return to_enum(:minmax_by) { size if respond_to?(:size) } unless block
    [min_by(&block), max_by(&block)]
  end unless method_defined?(:minmax_by)

  # flat_map and collect_concat are one C function in MRI, which a backtrace names by
  # the method called - so each is a method here, not an alias.
  def __enum_flat_map__(name)
    return to_enum(name) { size if respond_to?(:size) } unless block_given?
    result = []
    each do |*values|
      # Like #map, #flat_map hands the yielded values straight to the block
      # rather than the packed item: `yield 1, 2` reaches `{ |a| }` as 1.
      mapped = yield(*values)
      array = if mapped.is_a?(Array)
                mapped
              elsif mapped.respond_to?(:to_ary)
                converted = mapped.to_ary
                # An element that answers #to_ary has promised an Array; nil
                # means "not one after all", anything else is a broken promise.
                unless converted.nil? || converted.is_a?(Array)
                  raise TypeError, "can't convert #{mapped.class} to Array " \
                                   "(#{mapped.class}#to_ary gives #{__ir_conversion_result_name__(converted)})"
                end
                converted
              end
      array.is_a?(Array) ? result.concat(array) : result << mapped
    end
    result
  end
  private :__enum_flat_map__

  unless method_defined?(:flat_map)
    def flat_map(&block)
      __enum_flat_map__(:flat_map, &block)
    end
  end

  unless method_defined?(:collect_concat)
    def collect_concat(&block)
      __enum_flat_map__(:collect_concat, &block)
    end
    __ir_same_method__(:collect_concat, :flat_map)
  end

  def uniq
    seen = {}
    result = []
    each do |*values|
      item = __enum_item__(values)
      key = block_given? ? yield(item) : item
      next if seen.key?(key)
      seen[key] = true
      result << item
    end
    result
  end unless method_defined?(:uniq)

  def compact
    result = []
    each { |*values| item = __enum_item__(values); result << item unless item.nil? }
    result
  end unless method_defined?(:compact)

  def to_h(*args)
    result = {}
    each(*args) do |*values|
      pair = block_given? ? yield(*values) : __enum_item__(values)
      array = pair.respond_to?(:to_ary) ? pair.to_ary : pair
      unless array.is_a?(Array)
        raise TypeError, "wrong element type #{pair.class} (expected array)"
      end
      unless array.size == 2
        raise ArgumentError, "element has wrong array length (expected 2, was #{array.size})"
      end
      result[array[0]] = array[1]
    end
    result
  end unless method_defined?(:to_h)

  # The whole slicing family answers a lazy Enumerator, never an Array: the
  # receiver may be endless, and code that chains .lazy or .first onto one of
  # these must not force the source. Each of them therefore does its work
  # inside an Enumerator block and yields groups as they are completed. None of
  # them can say how many groups there will be without running, so the
  # Enumerator has no size.
  def chunk_while(&block)
    # MRI builds a Proc out of the block up front, so the missing-block error
    # is the one #to_proc gives rather than a "no block given" LocalJumpError.
    raise ArgumentError, "tried to create Proc object without a block" unless block
    source = self
    Enumerator.new do |yielder|
      chunk = nil
      previous = nil
      source.each do |*values|
        item = __enum_item__(values)
        if chunk.nil?
          chunk = [item]
        elsif block.call(previous, item)
          chunk << item
        else
          yielder.yield(chunk)
          chunk = [item]
        end
        previous = item
      end
      yielder.yield(chunk) if chunk
    end
  end unless method_defined?(:chunk_while)

  def slice_when(&block)
    raise ArgumentError, "tried to create Proc object without a block" unless block
    chunk_while { |a, b| !block.call(a, b) }
  end unless method_defined?(:slice_when)

  # Exactly one of a pattern and a block, and MRI reports a wrong count against
  # whichever form was chosen: with a block the method takes no arguments.
  def __slice_args__(both_message, args, block)
    if block
      unless args.empty?
        raise ArgumentError, both_message || "wrong number of arguments (given #{args.size}, expected 0)"
      end
    elsif args.size != 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1)"
    end
  end
  private :__slice_args__

  # A new group begins at every element the pattern or block accepts; the first
  # element always starts one, however it answers.
  def slice_before(*args, &block)
    __slice_args__(nil, args, block)
    pattern = args[0]
    source = self
    Enumerator.new do |yielder|
      group = nil
      source.each do |*values|
        item = __enum_item__(values)
        starts = block ? block.call(item) : (pattern === item)
        if group.nil?
          group = [item]
        elsif starts
          yielder.yield(group)
          group = [item]
        else
          group << item
        end
      end
      yielder.yield(group) if group
    end
  end unless method_defined?(:slice_before)

  def slice_after(*args, &block)
    # MRI words the both-given case differently in #slice_after than it does
    # in #slice_before.
    __slice_args__("both pattern and block are given", args, block)
    pattern = args[0]
    source = self
    Enumerator.new do |yielder|
      group = []
      source.each do |*values|
        item = __enum_item__(values)
        group << item
        if block ? block.call(item) : (pattern === item)
          yielder.yield(group)
          group = []
        end
      end
      yielder.yield(group) unless group.empty?
    end
  end unless method_defined?(:slice_after)

  # the arguments go to #each, as Enumerable#to_a's do
  def reverse_each(*args, &block)
    return to_enum(:reverse_each, *args) { size if respond_to?(:size) } unless block
    to_a(*args).reverse_each(&block)
    self
  end unless method_defined?(:reverse_each)

  # The block's value groups adjacent elements, but three of its answers are
  # not keys at all: nil and :_separator drop the element and end the run,
  # :_alone puts the element in a group of its own, and every other Symbol
  # starting with an underscore is reserved and rejected.
  def chunk(&block)
    return to_enum(:chunk) { size if respond_to?(:size) } unless block
    source = self
    Enumerator.new do |yielder|
      key = nil
      chunk = nil
      source.each do |*values|
        item = __enum_item__(values)
        k = block.call(item)
        if k.nil? || k == :_separator
          yielder.yield([key, chunk]) if chunk
          key = nil
          chunk = nil
        elsif k == :_alone
          yielder.yield([key, chunk]) if chunk
          yielder.yield([:_alone, [item]])
          key = nil
          chunk = nil
        elsif k.is_a?(Symbol) && k.to_s.start_with?("_")
          raise RuntimeError, "symbols beginning with an underscore are reserved"
        elsif chunk && k == key
          chunk << item
        else
          yielder.yield([key, chunk]) if chunk
          key = k
          chunk = [item]
        end
      end
      yielder.yield([key, chunk]) if chunk
    end
  end unless method_defined?(:chunk)

  # #sort_by has to walk the receiver exactly once - the block may be expensive
  # or have side effects - which the decorate/sort/undecorate helper does.
  unless method_defined?(:__sort_by_without_enumerator__)
    alias :__sort_by_without_enumerator__ :sort_by

    def sort_by(&block)
      return to_enum(:sort_by) { size if respond_to?(:size) } unless block
      __sort_by_key__(block)
    end
  end

  # Neither of these can know how long the answer will be without running the
  # block, so their Enumerators report no size even when the receiver has one.
  unless method_defined?(:__take_while_without_enumerator__)
    alias :__take_while_without_enumerator__ :take_while
    alias :__drop_while_without_enumerator__ :drop_while

    def take_while(&block)
      return to_enum(:take_while) { nil } unless block
      __take_while_without_enumerator__(&block)
    end

    def drop_while(&block)
      return to_enum(:drop_while) { nil } unless block
      __drop_while_without_enumerator__(&block)
    end
  end

  # Both return the receiver when they are given a block (they returned nil),
  # and both reject a slice size that cannot produce any slice at all.
  unless method_defined?(:__each_cons_without_self__)
    alias :__each_cons_without_self__ :each_cons
    alias :__each_slice_without_self__ :each_slice

    def __slice_size__(n, message)
      size = n
      size = size.to_int if !size.is_a?(Integer) && size.respond_to?(:to_int)
      raise ArgumentError, message if size.is_a?(Integer) && size <= 0
      # the converted size, so that #to_int is called once, as in MRI
      size.is_a?(Integer) ? size : n
    end
    private :__slice_size__

    def each_cons(n, &block)
      n = __slice_size__(n, "invalid size")
      return __each_cons_without_self__(n) unless block
      __each_cons_without_self__(n, &block)
      self
    end

    def each_slice(n, &block)
      n = __slice_size__(n, "invalid slice size")
      return __each_slice_without_self__(n) unless block
      __each_slice_without_self__(n, &block)
      self
    end
  end

  # An argument that is not an Array is taken by #to_ary if it has one and
  # otherwise iterated with #each - a Range or a lazy Enumerator zips fine.
  # Only something that can do neither is an error.
  unless method_defined?(:__zip_without_conversion__)
    alias :__zip_without_conversion__ :zip

    # An Integer Range whose #each is the built-in one yields begin, begin + 1, ... so the
    # element a row wants is worked out from its index. Going through Enumerator#next for
    # every element cost ~15 us each: (1..100).zip(1..100) was ~40x MRI.
    ZipIntegerRange__ = ::Struct.new(:first, :last) do
      def at(index)
        value = first + index
        last.nil? || value <= last ? value : nil
      end
    end
    RANGE_EACH__ = ::Range.instance_method(:each)
    # the readers the indexed path asks; Range#each itself uses the stored endpoints, so a
    # Range that overrides one of them - (1..3).dup can - has to be enumerated instead
    RANGE_READERS__ = [:begin, :end, :exclude_end?].map { |name| [name, ::Range.instance_method(name)] }.freeze
    HASH_EACH__ = ::Hash.instance_method(:each)
    STRUCT_EACH__ = ::Struct.instance_method(:each)
    __hide_constant__ :ZipIntegerRange__, :RANGE_EACH__, :RANGE_READERS__, :HASH_EACH__, :STRUCT_EACH__

    def __zip_builtin_range__(other)
      other.instance_of?(::Range) && other.method(:each).unbind == RANGE_EACH__ &&
        RANGE_READERS__.all? { |name, method| other.method(name).unbind == method }
    end
    private :__zip_builtin_range__

    # An Array, a ZipIntegerRange__, or the argument itself, to be enumerated.
    def __zip_source__(other)
      return other if other.is_a?(Array)
      if other.respond_to?(:to_ary)
        converted = other.to_ary
        return converted if converted.is_a?(Array)
      end
      if __zip_builtin_range__(other) &&
          ::Integer === other.begin && (other.end.nil? || ::Integer === other.end)
        last = other.end
        last -= 1 if last && other.exclude_end?
        return ZipIntegerRange__.new(other.begin, last)
      end
      unless other.respond_to?(:each)
        raise TypeError, "wrong argument type #{__ir_message_type_name__(other)} (must respond to :each)"
      end
      other
    end
    private :__zip_source__

    # Whether obj's #each is a built-in one that runs no Ruby code of anyone else's, never
    # raises and yields one value at a time: a Hash, a Struct, a Set, or an Integer Range
    # (an argument one is indexed instead, see ZipIntegerRange__; this is for a receiver).
    # When it is, nothing can tell when its elements were read relative to the receiver's.
    # Not a String Range: MRI's walks it with a dynamically dispatched String#succ.
    def __zip_quiet_each__(obj)
      case obj
      when ::Hash then obj.method(:each).unbind == HASH_EACH__
      when ::Struct then obj.method(:each).unbind == STRUCT_EACH__
      when ::Range
        __zip_builtin_range__(obj) && ::Integer === obj.begin && (obj.end.nil? || ::Integer === obj.end)
      else
        # Set is autoloaded, and its #each is the prelude's (which has no source location)
        # unless someone redefined it
        !::Object.autoload?(:Set) && ::Set === obj &&
          (each = obj.method(:each)).owner.equal?(::Set) && each.source_location.nil?
      end
    end
    private :__zip_quiet_each__

    # MRI's zip_i reads a non-Array argument with Enumerator#next, interleaved with the
    # receiver's #each - and lazily, so an endless argument works. #next costs a fiber
    # switch per element here (~90 us). Without a block, when the receiver and every
    # argument have a quiet #each (see __zip_quiet_each__), the order the elements are read
    # in cannot show: the receiver is read first, then each argument with #each, stopping at
    # the receiver's length - exactly the elements #next would have asked for, and never
    # starting an argument for an empty receiver. One custom #each is already too many: a
    # receiver's can change an argument between its yields, and an argument's, cut short
    # with break, runs its ensure clauses (and raises from them, or lets a StopIteration it
    # raises escape) where #next would have left it suspended.
    def __zip_in_turn__(sources)
      rows = []
      each { |*values| rows << [values.size <= 1 ? values[0] : values] }
      count = rows.size
      sources.each do |source|
        index = 0
        if source.is_a?(Array)
          while index < count
            rows[index] << source[index]
            index += 1
          end
        elsif ZipIntegerRange__ === source
          while index < count
            rows[index] << source.at(index)
            index += 1
          end
        elsif count == 0
          # MRI asks an argument for nothing when the receiver yields nothing
        else
          source.each do |value|
            rows[index] << value
            index += 1
            break if index == count
          end
        end
        while index < count
          rows[index] << nil
          index += 1
        end
      end
      rows
    end
    private :__zip_in_turn__

    def zip(*others, &block)
      sources = others.map { |other| __zip_source__(other) }
      enumerated = sources.reject { |source| source.is_a?(Array) || ZipIntegerRange__ === source }
      if !block && !enumerated.empty? && __zip_quiet_each__(self) &&
         enumerated.all? { |source| __zip_quiet_each__(source) }
        return __zip_in_turn__(sources)
      end
      unless enumerated.empty?
        sources = sources.map do |source|
          source.is_a?(Array) || ZipIntegerRange__ === source ? source : source.to_enum(:each)
        end
      end
      result = block ? nil : []
      index = 0
      each do |*values|
        row = [__enum_item__(values)]
        sources.each do |source|
          row << if source.is_a?(Array)
                   source[index]
                 elsif ZipIntegerRange__ === source
                   source.at(index)
                 else
                   begin
                     source.next
                   rescue StopIteration
                     nil
                   end
                 end
        end
        index += 1
        block ? block.call(row) : result << row
      end
      result
    end
  end
end

class Range
  # An Integer range is summed in closed form (MRI's range_sum): (1..2**70).sum does not
  # walk 2**70 elements. Anything else, and the block form, goes through Enumerable#sum.
  def sum(init = 0, &block)
    from = self.begin
    to = self.end
    if !block && from.is_a?(::Integer) && to.is_a?(::Integer)
      to -= 1 if exclude_end?
      return init if to < from
      return init + (from + to) * (to - from + 1) / 2
    end
    super
  end

  # The number of elements the range iterates (MRI's range_size). MRI 3.4 only
  # counts a range that begins at an Integer; a Float beginning cannot be walked
  # at all (1.0..2.0 has no elements to count), and a beginning that has #succ can
  # be walked but not counted in advance, so it answers nil rather than guessing.
  # Range#size is C# (RangeOps.Size), which answers an Integer..Integer, an
  # endless Integer range and a nil or Float beginning itself, and hands the rest
  # here.
  def __size__
    from = self.begin
    to = self.end
    if from.is_a?(::Integer)
      if ::Integer === to
        # the common case, counted directly
        n = to - from + (exclude_end? ? 0 : 1)
        return n < 0 ? 0 : n
      end
      if to.is_a?(::Numeric)
        return ::Enumerator::ArithmeticSequence.__interval_step_size__(from, to, 1, exclude_end?)
      end
      return ::Float::INFINITY if to.nil?
    elsif from.nil?
      raise TypeError, "can't iterate from NilClass"
    elsif !from.respond_to?(:succ)
      raise TypeError, "can't iterate from #{from.class}"
    end
    nil
  end
  private :__size__

  # Walking a range backwards needs a finite end to start from. An Integer end
  # can be counted down from forever, which is how a beginless range works;
  # anything else has to be materialised first.
  def reverse_each(&block)
    unless block
      range = self
      enum = ::Enumerator.new { |y| range.reverse_each { |x| y << x } }
      enum.__set_size__(lambda { range.__send__(:__reverse_each_size__) })
      return enum.__set_inspect__(self, :reverse_each)
    end
    to = self.end
    raise TypeError, "can't iterate from NilClass" if to.nil?
    from = self.begin
    if from.nil?
      raise TypeError, "can't iterate from NilClass" unless to.is_a?(::Integer)
      i = exclude_end? ? to - 1 : to
      loop do
        block.call(i)
        i -= 1
      end
      return self
    end
    to_a.reverse_each(&block)
    self
  end

  # Matches MRI's range_reverse_each_size, quirks included: the class it names
  # when it refuses is the end's unless the beginning is the unwalkable one.
  def __reverse_each_size__
    from = self.begin
    to = self.end
    raise TypeError, "can't iterate from NilClass" if to.nil?
    if from.is_a?(::Integer)
      size
    elsif from.nil?
      return ::Float::INFINITY if to.is_a?(::Integer)
      raise TypeError, "can't iterate from #{to.class}"
    elsif to.is_a?(::Integer)
      raise TypeError, "can't iterate from Integer"
    elsif from.respond_to?(:succ)
      nil
    else
      raise TypeError, "can't iterate from #{from.class}"
    end
  end
  private :__reverse_each_size__

  # #first(n) and #last(n): the C# #first and #last (RangeOps) answer the form without a
  # count and hand the count here - an Enumerable#first(n) they would otherwise hide, so
  # `(0..Float::INFINITY).first(3)` was an ArgumentError.
  unless private_method_defined?(:__first_n__)
    def __first_n__(count)
      raise RangeError, "cannot get the first element of beginless range" if self.begin.nil?
      n = __to_count__(count)
      raise ArgumentError, "negative array size (or size too big)" if n < 0
      b = self.begin
      e = self.end
      # An Integer range is counted rather than walked: the first n of b..e are b..b+n-1,
      # cut off at the end (an endless range has none).
      if ::Integer === b && (e.nil? || ::Integer === e) && instance_of?(::Range)
        last_value = b + n - 1
        unless e.nil?
          top = exclude_end? ? e - 1 : e
          last_value = top if top < last_value
        end
        return __int_run__(b, last_value)
      end
      result = []
      # MRI's first_i stops at the first element even for none, so #each is still asked
      # (a Float range is "can't iterate from Float" for first(0) too)
      each do |item|
        break if n == 0
        result << item
        break if result.size == n
      end
      result
    end

    def __last_n__(count)
      raise RangeError, "cannot get the last element of endless range" if self.end.nil?
      n = __to_count__(count)
      raise ArgumentError, "negative array size" if n < 0
      b = self.begin
      e = self.end
      # MRI's rb_int_range_last: the last n of an Integer range, without building all of it
      # first (to_a.last(n) made (1..10**6).last(3) a million-element Array). MRI takes this
      # path unless #each has been redefined (rb_method_basic_definition_p): a subclass with
      # an #each of its own gets to_a.last(n).
      # (..5).last(2) too: a beginless Integer range counts down from its end.
      if (b.nil? || ::Integer === b) && ::Integer === e && (instance_of?(::Range) || method(:each).owner.equal?(::Range))
        top = exclude_end? ? e - 1 : e
        first_value = top - n + 1
        first_value = b if !b.nil? && first_value < b
        return __int_run__(first_value, top)
      end
      to_a.last(n)
    end

    # [from, from + 1, ..., to]: a loop for a few, a Range#to_a (C#) for many
    def __int_run__(from, to)
      return [] if to < from
      return (from..to).to_a if to - from >= 32
      result = []
      i = from
      while i <= to
        result << i
        i += 1
      end
      result
    end
    private :__int_run__
    private :__first_n__, :__last_n__

    # #first and #last take a count, not an arbitrary object: anything that is
    # not an Integer has to offer #to_int and have it answer one.
    def __to_count__(n)
      return n if n.is_a?(::Integer)
      unless n.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{n.nil? ? "nil" : n.class} into Integer"
      end
      converted = n.to_int
      unless converted.is_a?(::Integer)
        raise TypeError, "can't convert #{n.class} to Integer (#{n.class}#to_int gives #{__ir_conversion_result_name__(converted)})"
      end
      converted
    end
    private :__to_count__
  end
end

module Kernel
  private

  # shared tail of Array#dig / Hash#dig / Struct#dig: CRuby raises TypeError,
  # not NoMethodError, when an intermediate element has no #dig
  def __dig_step__(value, rest)
    return value if rest.empty? || value.nil?
    unless value.respond_to?(:dig)
      raise TypeError, "#{value.class} does not have #dig method"
    end
    value.dig(*rest)
  end

  # shared by Hash#to_h and Struct#to_h: validate the [key, value] pair a block
  # returned. Only #to_ary is honoured, never #to_a.
  def __to_h_pair__(pair)
    unless pair.is_a?(Array)
      pair = pair.to_ary if pair.respond_to?(:to_ary)
    end
    unless pair.is_a?(Array)
      # rb_builtin_class_name: nil, true and false by value
      name = case pair when nil then "nil" when true then "true" when false then "false" else pair.class end
      raise TypeError, "wrong element type #{name} (expected array)"
    end
    unless pair.size == 2
      raise ArgumentError, "element has wrong array length (expected 2, was #{pair.size})"
    end
    pair
  end
end

class Array
  # #dig indexes with an Integer and nothing else: unlike #[] it does not take a
  # Range or an arithmetic sequence, because there would be no way to keep
  # digging into the several elements that would answer.
  def dig(key, *rest)
    __dig_step__(self[__array_to_int__(key)], rest)
  end unless method_defined?(:dig)

  # No Array#sum here on purpose: Enumerable#sum already does the compensated
  # summation MRI does, and this was a second, naive copy that Array never
  # reached anyway - method_defined? saw the included Enumerable#sum and skipped it.

  # a[(0..).step(2)]: the sequence describes a stretch of the array and a stride
  # through it. The stretch is normalized like a Range, except that a stride of
  # more than one element makes the normalization strict - a stretch that does
  # not fit is a RangeError rather than a silent clamp - and a negative stride
  # reads the same stretch from its far end.
  def __arithmetic_slice__(sequence)
    unless sequence.is_a?(::Enumerator::ArithmeticSequence)
      raise TypeError, "no implicit conversion of #{sequence.class} into Integer"
    end

    step = sequence.step
    step = 1 if step.nil?
    step = step.to_int unless step.is_a?(::Integer)
    raise ArgumentError, "step can't be 0" if step == 0

    first = sequence.begin
    last = sequence.end
    exclude_end = sequence.exclude_end?
    if step < 0
      # Reading backwards swaps the ends, and the exclusion travels with the
      # element it excluded: it was the last one, it is now the first. The
      # arithmetic is done on the index as written, before it is normalized, so
      # that (0...-1) starts at 0 rather than at the element before the last.
      first, last = last, first
      if exclude_end && !first.nil?
        first += 1
        exclude_end = false
      end
    end

    count = size
    strict = step > 1 || step < -1

    begin_index = first.nil? ? 0 : first.to_int
    begin_index += count if begin_index < 0
    if begin_index < 0 || begin_index > count
      raise RangeError, "#{sequence.inspect} out of range" if strict
      return nil
    end

    if last.nil?
      end_index = count
    else
      end_index = last.to_int
      end_index += count if end_index < 0
      end_index += 1 unless exclude_end
      end_index = count if end_index > count && !strict
    end

    length = end_index - begin_index
    length = 0 if length < 0
    raise RangeError, "#{sequence.inspect} out of range" if strict && length > count
    # Whatever the stretch says, only elements that are really there come out.
    length = count - begin_index if begin_index + length > count

    result = []
    if step > 0
      i = begin_index
      while i < begin_index + length
        result << self[i]
        i += step
      end
    else
      i = begin_index + length - 1
      while i >= begin_index
        result << self[i]
        i += step
      end
    end
    result
  end

  # Array has its own #sum in MRI rather than inheriting Enumerable's, and code in the
  # wild checks which one it gets. The body is Enumerable's. (#max and #min are Array's
  # own in C#, see IListOps: Enumerable's go through #each and a Proc per element.)
  # An Array of Integers and Floats is summed in C# (ArrayOps.NumericSum), the same way.
  def sum(*args, &block)
    if block.nil? && args.size <= 1
      result = __numeric_sum__(args.empty? ? 0 : args[0])
      return result unless result.nil?
    end
    super
  end

  def intersect?(other)
    !(self & other).empty?
  end unless method_defined?(:intersect?)

  def deconstruct
    self
  end unless method_defined?(:deconstruct)

  # --- methods the 1.8-era core never grew -------------------------------
  #
  # All of these hand back a plain Array, never the receiver's subclass, which
  # is what MRI does and what ruby/spec asserts.

  def rotate(count = 1)
    count = __array_to_int__(count)
    return [].concat(self) if empty?
    count %= size
    [].concat(self[count..-1]).concat(self[0, count])
  end unless method_defined?(:rotate)

  def rotate!(count = 1)
    __array_check_frozen__
    replace(rotate(count))
  end unless method_defined?(:rotate!)

  # The deletions have to be visible if the block raises part way through, so
  # this compacts in place rather than building a new array and swapping it in.
  # select!, filter! and keep_if are one C function in MRI, which a backtrace
  # names by the method called - so each is a method here, not an alias.
  def __array_select_bang__(name, block)
    return to_enum(name) { size } unless block
    __array_check_frozen__
    original = size
    kept = 0
    index = 0
    begin
      while index < size
        element = self[index]
        if block.call(element)
          self[kept] = element
          kept += 1
        end
        index += 1
      end
    ensure
      # Only the examined-and-rejected span goes; anything the block never saw
      # (because it raised) stays where it is.
      slice!(kept, index - kept) if index > kept
    end
    original == size ? nil : self
  end
  private :__array_select_bang__

  def select!(&block)
    __array_select_bang__(:select!, block)
  end unless method_defined?(:select!)

  unless method_defined?(:filter!)
    def filter!(&block)
      __array_select_bang__(:filter!, block)
    end
    __ir_same_method__(:filter!, :select!)
  end

  def keep_if(&block)
    return to_enum(:keep_if) { size } unless block
    __array_select_bang__(:keep_if, block)
    self
  end unless method_defined?(:keep_if)

  def sort_by!(&block)
    return to_enum(:sort_by!) { size } unless block
    __array_check_frozen__
    replace(sort_by(&block))
  end unless method_defined?(:sort_by!)

  # Two modes, as in MRI: find-minimum when the block answers true/false/nil,
  # find-any when it answers a number (negative = look left, 0 = found).
  def bsearch_index(&block)
    return to_enum(:bsearch_index) unless block
    low = 0
    high = size
    satisfied = nil
    while low < high
      middle = low + (high - low) / 2
      answer = block.call(self[middle])
      case answer
      when true
        satisfied = middle
        high = middle
      when false, nil
        low = middle + 1
      when Numeric
        return middle if answer == 0
        if answer < 0
          high = middle
        else
          low = middle + 1
        end
      else
        raise TypeError, "wrong argument type #{answer.class} (must be numeric, true, false or nil)"
      end
    end
    satisfied
  end unless method_defined?(:bsearch_index)

  def bsearch(&block)
    return to_enum(:bsearch) unless block
    index = bsearch_index(&block)
    index && self[index]
  end unless method_defined?(:bsearch)

  # The Enumerator shows the ifnone argument only when one was given (MRI's rb_scan_args).
  def rfind(*args, &block)
    return to_enum(:rfind, *args) unless block
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    ifnone = args[0]
    index = size - 1
    while index >= 0
      # The array may shrink under us; MRI re-reads the size every step.
      index = size - 1 if index >= size
      break if index < 0
      element = self[index]
      return element if block.call(element)
      index -= 1
    end
    ifnone && ifnone.call
  end unless method_defined?(:rfind)

  def difference(*others)
    others.inject([].concat(self)) { |result, other| result - other }
  end unless method_defined?(:difference)

  # #union uniques the receiver even with no arguments; #intersection does not.
  def union(*others)
    others.inject([].concat(self).uniq) { |result, other| result | other }
  end unless method_defined?(:union)

  def intersection(*others)
    others.inject([].concat(self)) { |result, other| result & other }
  end unless method_defined?(:intersection)

  def repeated_permutation(count, &block)
    count = __array_to_int__(count)
    unless block
      return to_enum(:repeated_permutation, count) {
        count < 0 ? 0 : size ** count
      }
    end
    __array_repeat__(count, false, &block)
  end unless method_defined?(:repeated_permutation)

  def repeated_combination(count, &block)
    count = __array_to_int__(count)
    unless block
      return to_enum(:repeated_combination, count) {
        if count < 0
          0
        elsif count == 0
          1
        else
          __array_binomial__(size + count - 1, count)
        end
      }
    end
    __array_repeat__(count, true, &block)
  end unless method_defined?(:repeated_combination)

  def fetch_values(*indexes, &block)
    indexes.map { |index| block ? fetch(index, &block) : fetch(index) }
  end unless method_defined?(:fetch_values)

  # The core #shuffle takes no arguments; MRI's takes a :random generator and
  # must hand back a plain Array.  Defined unconditionally for that reason.
  def shuffle(random: Random)
    result = [].concat(self)
    index = result.size - 1
    while index > 0
      swap = __array_rand_index__(random, index + 1)
      result[index], result[swap] = result[swap], result[index]
      index -= 1
    end
    result
  end

  def shuffle!(random: Random)
    __array_check_frozen__
    replace(shuffle(random: random))
  end

  def sample(*args, random: Random)
    if args.size > 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    if args.empty?
      return nil if empty?
      return self[__array_rand_index__(random, size)]
    end

    # an explicit nil is not "no count": sample(nil) is a TypeError
    count = __array_to_int__(args[0])
    raise ArgumentError, "negative sample number" if count < 0
    total = size
    count = total if count > total

    pool = [].concat(self)
    result = []
    index = 0
    while index < count
      swap = index + __array_rand_index__(random, total - index)
      pool[index], pool[swap] = pool[swap], pool[index]
      result << pool[index]
      index += 1
    end
    result
  end unless method_defined?(:sample)

  # --- helpers -----------------------------------------------------------

  private

  def __array_check_frozen__
    raise FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self) if frozen?
  end

  def __array_to_int__(value)
    return value if value.is_a?(Integer)
    unless value.respond_to?(:to_int)
      if value.nil?
        raise TypeError, "no implicit conversion of nil into Integer"
      end
      raise TypeError, "no implicit conversion of #{value.class} into Integer"
    end
    converted = value.to_int
    unless converted.is_a?(Integer)
      raise TypeError, "can't convert #{value.class} to Integer (#{value.class}#to_int gives #{__ir_conversion_result_name__(converted)})"
    end
    converted
  end

  # MRI's rb_random_ulong_limited: ask the generator, coerce with #to_int, and
  # insist the answer addresses an existing element.
  def __array_rand_index__(random, limit)
    value = random.rand(limit)
    value = value.to_int unless value.is_a?(Integer)
    raise RangeError, "random number too big #{value}" if value < 0 || value >= limit
    value
  end

  def __array_binomial__(n, k)
    return 0 if k > n
    result = 1
    (1..k).each { |i| result = result * (n - k + i) / i }
    result
  end

  # MRI iterates a snapshot, so mutating the receiver from the block does not
  # change the elements that are yielded.
  def __array_repeat__(count, sorted, &block)
    return self if count < 0
    if count == 0
      block.call([])
      return self
    end
    return self if empty?

    snapshot = [].concat(self)
    indexes = Array.new(count, 0)
    total = snapshot.size
    loop do
      block.call(indexes.map { |i| snapshot[i] })

      position = count - 1
      position -= 1 while position >= 0 && indexes[position] == total - 1
      return self if position < 0
      indexes[position] += 1
      if sorted
        (position + 1...count).each { |i| indexes[i] = indexes[position] }
      else
        (position + 1...count).each { |i| indexes[i] = 0 }
      end
    end
  end

  public

  alias :filter :select unless method_defined?(:filter)
  alias :append :push unless method_defined?(:append)
  alias :prepend :unshift unless method_defined?(:prepend)
end

class Hash
  def dig(key, *rest)
    __dig_step__(self[key], rest)
  end unless method_defined?(:dig)

  def transform_values(&block)
    return to_enum(:transform_values) { size } unless block
    result = __result_hash__
    each { |k, v| result[k] = block.call(v) }
    result
  end unless method_defined?(:transform_values)

  def transform_values!(&block)
    return to_enum(:transform_values!) { size } unless block
    raise FrozenError.new("can't modify frozen Hash: #{inspect}", receiver: self) if frozen?
    # Over the hash itself, as MRI's rb_hash_stlike_foreach_with_replace is: the block runs
    # while the hash is being iterated, so #rehash in it is an error.
    each_key { |k| self[k] = block.call(self[k]) }
    self
  end unless method_defined?(:transform_values!)

  # transform_keys(hash = nil) { |key| ... } - the hash argument (3.0) wins over
  # the block for the keys it contains.
  def transform_keys(*args, &block)
    mapping = __key_mapping__(args)
    return to_enum(:transform_keys) { size } if mapping.nil? && block.nil?
    # CRuby drops the compare_by_identity flag here (but keeps it in #transform_values)
    result = {}
    each do |k, v|
      nk = if mapping && mapping.key?(k)
             mapping[k]
           elsif block
             block.call(k)
           else
             k
           end
      result[nk] = v
    end
    result
  end unless method_defined?(:transform_keys)

  def transform_keys!(*args, &block)
    mapping = __key_mapping__(args)
    return to_enum(:transform_keys!) { size } if mapping.nil? && block.nil?
    raise FrozenError.new("can't modify frozen Hash: #{inspect}", receiver: self) if frozen?
    # CRuby semantics: walk a snapshot of the pairs, deleting the old key only
    # if it has not already been produced as a new key (so `break` leaves the
    # already-processed prefix rewritten and the rest untouched).
    new_keys = {}
    to_a.each do |k, v|
      nk = if mapping && mapping.key?(k)
             mapping[k]
           elsif block
             block.call(k)
           else
             k
           end
      delete(k) unless new_keys.key?(k)
      self[nk] = v
      new_keys[nk] = nil
    end
    self
  end unless method_defined?(:transform_keys!)

  private def __key_mapping__(args)
    if args.size > 1
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    return nil if args.empty?
    mapping = args[0]
    return mapping if mapping.is_a?(Hash)
    mapping = mapping.to_hash if mapping.respond_to?(:to_hash)
    return mapping if mapping.is_a?(Hash)
    raise TypeError, "no implicit conversion of #{args[0].nil? ? 'nil' : args[0].class} into Hash"
  end

  def deconstruct_keys(keys)
    self
  end unless method_defined?(:deconstruct_keys)

  # unlike #reject, #compact keeps the default value and the default proc
  def compact
    result = dup
    result.delete_if { |_, v| v.nil? }
    result
    # instance_methods(false) rather than method_defined?: Hash includes Enumerable,
    # whose #compact is defined by this point and returns an Array of pairs, so
    # method_defined? is true and Hash would be left returning the wrong class.
  end unless instance_methods(false).include?(:compact)

  alias :filter :select unless method_defined?(:filter)
end

class String
  # The library String#initialize takes the string to copy and no keywords; String.new's
  # encoding: and capacity: are the constructor's. MRI's rb_str_init takes them in
  # #initialize too, as a subclass's super or a call through #send does: the keywords and
  # the argument are checked first, then a frozen receiver is a FrozenError, and the string
  # is emptied when a capacity comes with nothing to copy (capacity is only a size hint).
  alias_method :__ir_initialize__, :initialize
  private :__ir_initialize__

  def initialize(*args, **opts)
    return __ir_initialize__(*args) if opts.empty?
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    unknown = opts.keys - [:encoding, :capacity]
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError,
        "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.map(&:inspect).join(', ')}")
    end
    encoding = opts[:encoding]
    encoding = ::Encoding.find(encoding) unless encoding.nil? || encoding.is_a?(::Encoding)
    # NUM2LONG: a #to_int that answers something else is a TypeError, and a bignum does not fit.
    capacity = opts[:capacity]
    unless capacity.nil?
      capacity = ::Math.__send__(:__int__, capacity)
      unless ::Integer === capacity
        # the one #to_int call's answer decides: asking again could get a different one
        name = __ir_type_name__(opts[:capacity])
        ::Kernel.raise(::TypeError,
          "can't convert #{name} to Integer (#{name}#to_int gives #{capacity.class})")
      end
      unless capacity >= -0x8000_0000_0000_0000 && capacity <= 0x7fff_ffff_ffff_ffff
        ::Kernel.raise(::RangeError, "bignum too big to convert into 'long'")
      end
    end
    # The conversion String.new makes without keywords (rb_check_string_type): #to_str is
    # called whether it is private or the object a BasicObject.
    unless args.empty? || ::String === args[0]
      converted = ::String.try_convert(args[0])
      if converted.nil?
        ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(args[0])} into String")
      end
      args = [converted]
    end
    ::Kernel.raise(::FrozenError.new("can't modify frozen String: #{inspect}", receiver: self)) if frozen?
    # Only a capacity empties a string that is given nothing to copy: encoding: alone, or
    # capacity: nil, leaves the bytes and changes the encoding.
    if !args.empty?
      __ir_initialize__(args[0])
    elsif !capacity.nil?
      clear
    end
    force_encoding(encoding) if encoding
    self
  end

  # Both take a String or something with #to_str, and both answer a copy when
  # there is nothing to remove - including for an empty affix, where
  # "self[0...-0]" would otherwise answer "".
  def __affix__(affix)
    return affix if affix.is_a?(::String)
    converted = affix.respond_to?(:to_str) ? affix.to_str : nil
    unless converted.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(affix)} into String")
    end
    converted
  end
  private :__affix__

  def delete_prefix(prefix)
    prefix = __affix__(prefix)
    (!prefix.empty? && start_with?(prefix)) ? self[prefix.length..-1] : dup
  end unless method_defined?(:delete_prefix)

  # MRI's deleted_suffix_length removes nothing for a suffix with broken bytes - where
  # deleted_prefix_length still compares them, only skipping the encoding check.
  def delete_suffix(suffix)
    suffix = __affix__(suffix)
    return dup unless suffix.valid_encoding?
    (!suffix.empty? && end_with?(suffix)) ? self[0...-suffix.length] : dup
  end unless method_defined?(:delete_suffix)

end

module Comparable
  # rb_cmperr: the right operand of a failed comparison is spelled by inspect when it
  # is an immediate (nil, true, false, Integer, Float, Symbol) and by class otherwise.
  def self.__cmp_operand__(x)
    case x
    when nil, true, false, ::Integer, ::Float, ::Symbol then x.inspect
    else x.class
    end
  end

  # Comparable#clamp is C# (Src/Libraries/Builtins/Comparable.cs).
end

module Math
  # MRI raises Math::DomainError out of Math.sqrt/log/... and Integer#digits.
  class DomainError < StandardError; end unless const_defined?(:DomainError)

end

module Kernel
  private

  # support for find patterns (`in [*pre, x, *post]`): returns the first index
  # at which the probe block matches, or nil
  def __pm_find_index__(arr, count)
    max = arr.length - count
    i = 0
    while i <= max
      return i if yield(i)
      i += 1
    end
    nil
  end
end

# IronRuby's own version, without the pre-release suffix: 4.0.0 for 4.0.0-preview1.
# IronRuby's major.minor is the Ruby version it implements (CurrentVersion.cs), so this
# agrees with RUBY_VERSION up to IronRuby's own release counter, as it does in CRuby.
RUBY_ENGINE_VERSION = IRONRUBY_VERSION[/\A\d+\.\d+\.\d+/].freeze unless defined?(RUBY_ENGINE_VERSION)
RUBY_COPYRIGHT = "ironruby - Apache License, Version 2.0".freeze unless defined?(RUBY_COPYRIGHT)

# Ruby 4.0 collects the RUBY_* constants into a module. Every member must be the
# very same object as its RUBY_* counterpart -- ruby/spec asserts identity with
# #equal?, not equality -- so these alias rather than re-create.
module Ruby
  VERSION = RUBY_VERSION
  PATCHLEVEL = RUBY_PATCHLEVEL
  COPYRIGHT = RUBY_COPYRIGHT
  DESCRIPTION = RUBY_DESCRIPTION
  ENGINE = RUBY_ENGINE
  ENGINE_VERSION = RUBY_ENGINE_VERSION
  PLATFORM = RUBY_PLATFORM
  RELEASE_DATE = RUBY_RELEASE_DATE
  REVISION = RUBY_REVISION
end unless defined?(Ruby)

class IO
  # What a blocking read or write raises once IO#timeout runs out (3.2); RubyIO creates it by name.
  class TimeoutError < IOError; end unless const_defined?(:TimeoutError, false)

  # The library method behind this one takes its options as keywords ([Keywords]): a Hash
  # in the last positional slot is an argument too many.
  alias :__ir_initialize__ :initialize
  private :__ir_initialize__

  def initialize(*args, **opts)
    if args.size > 2
      ::Kernel.raise(::ArgumentError,
                     "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    __ir_initialize__(*args, **opts)
  end

  class << self
    alias :__ir_new__ :new

    # IO.open is the form that takes a block; IO.new ignores one and says so.
    def new(*args, **opts, &block)
      if block
        ::Kernel.warn("warning: #{self}::new() does not take block; use #{self}::open() instead")
      end
      opts.empty? ? __ir_new__(*args) : __ir_new__(*args, **opts)
    end
  end
end

class File
  alias :__ir_initialize__ :initialize
  private :__ir_initialize__

  # File takes a permission argument between the mode and the options.
  def initialize(*args, **opts)
    if args.size > 3
      ::Kernel.raise(::ArgumentError,
                     "wrong number of arguments (given #{args.size}, expected 1..3)")
    end
    __ir_initialize__(*args, **opts)
  end
end

class IO
  # autoclose is tracked but not acted on: IronRuby closes descriptors it owns
  # through the CLR stream, and never closes one handed to it from outside.
  def autoclose?
    ::Kernel.raise(::IOError, "closed stream") if closed?
    defined?(@__autoclose) ? @__autoclose : true
  end unless method_defined?(:autoclose?)

  def autoclose=(value)
    ::Kernel.raise(::IOError, "closed stream") if closed?
    @__autoclose = !!value
  end unless method_defined?(:autoclose=)
end

class File

  # File.size, File.size? and File.directory? take an IO, or anything that
  # converts to one with #to_io, as well as a path; the C# implementations only
  # know about paths.
  class << self
    [:size, :size?, :directory?].each do |name|
      path_only = instance_method(name)
      define_method(name) do |target|
        unless target.is_a?(String) || target.respond_to?(:to_path)
          io = target.respond_to?(:to_io) ? target.to_io : (target.is_a?(IO) ? target : nil)
          if io
            stat = io.stat
            case name
            when :directory? then return stat.directory?
            when :size?      then return stat.size == 0 ? nil : stat.size
            else                  return stat.size
            end
          end
        end
        path_only.bind(self).call(target)
      end
    end
  end

  # IO.write/File.write and friends arrived in 1.9; the C# IO library only has
  # the instance-level operations.  offset nil means truncate, an integer seeks
  # first and leaves the rest of the file intact, matching IO.write.
  def self.write(name, string, offset = nil, **opts)
    open_args = opts[:open_args]
    perm = nil
    if open_args
      # :open_args is the whole argument list for the open, and it displaces every
      # other option - including the mode, which is then read-only if it is absent.
      extra = open_args.find { |a| a.is_a?(::Hash) } || {}
      mode = open_args.find { |a| a.is_a?(::String) || a.is_a?(::Integer) } || extra[:mode] || "r"
      encoding = extra[:encoding]
      if extra[:binmode] && mode.is_a?(::String) && !mode.include?("b")
        mode = mode.sub(/\A([^:]*)/) { "#{$1}b" }
      end
    else
      mode = opts[:mode]
      encoding = opts[:encoding]
      # the permissions a file that has to be created gets, as File.open's third argument
      perm = opts[:perm]
      if encoding && mode.is_a?(::String) && mode.include?(":")
        ::Kernel.raise(::ArgumentError, "encoding specified twice")
      end
      # No mode and an offset: write into the file where it is, creating it if it is
      # not there, but leaving the rest of it alone. No mode string says that.
      mode ||= offset ? (::File::WRONLY | ::File::CREAT) : "w"
      if opts[:binmode] && mode.is_a?(::String) && !mode.include?("b")
        mode = mode.sub(/\A([^:]*)/) { "#{$1}b" }
      end
    end

    open(name, mode, *(perm ? [perm] : [])) do |io|
      io.set_encoding(encoding) if encoding
      io.seek(offset) if offset
      io.write(string)
    end
  end unless respond_to?(:write)

  def self.binwrite(name, string, offset = nil, **opts)
    # binwrite is write in binary mode; it keeps every other option it was given,
    # including the rule that an offset means "do not truncate".
    write(name, string, offset, **opts, binmode: true)
  end unless respond_to?(:binwrite)

  def self.binread(name, length = nil, offset = 0)
    open(name, "rb") do |io|
      # a negative offset is seek's EINVAL, as in MRI
      io.seek(offset) if offset && offset != 0
      length ? io.read(length) : io.read
    end
  end unless respond_to?(:binread)

end

class IO
  def self.write(name, string, offset = nil, **opts)
    File.write(name, string, offset, **opts)
  end unless respond_to?(:write)

  def self.binwrite(name, string, offset = nil, **opts)
    File.binwrite(name, string, offset, **opts)
  end unless respond_to?(:binwrite)

  def self.binread(name, length = nil, offset = 0)
    File.binread(name, length, offset)
  end unless respond_to?(:binread)

  # Ruby 2.3 gave the non-blocking IO primitives an `exception: false`
  # keyword: instead of raising IO::WaitReadable / IO::WaitWritable / EOFError
  # they return :wait_readable / :wait_writable / nil.  net/protocol drives its
  # read buffer through this form.
  unless method_defined?(:__read_nonblock_raising__)
    alias :__read_nonblock_raising__ :read_nonblock

    def read_nonblock(len, buf = nil, exception: true)
      unless exception == true || exception == false
        raise ArgumentError, "expected true or false as exception: #{exception.inspect}"
      end
      # A socket can say whether the read would block without being asked to try; the
      # exceptionless form then answers without raising and rescuing an EAGAIN.
      if !exception && len.to_int > 0 && respond_to?(:__ir_readable_now?, true) && !__ir_readable_now?
        return :wait_readable
      end
      begin
        result = buf.nil? ? __read_nonblock_raising__(len) : __read_nonblock_raising__(len, buf)
      rescue IO::WaitReadable, Errno::EAGAIN => e
        return :wait_readable unless exception
        # MRI raises IO::EAGAINWaitReadable, an Errno::EAGAIN that is also a WaitReadable.
        e.extend(IO::WaitReadable) unless e.is_a?(IO::WaitReadable)
        raise e
      rescue EOFError
        raise if exception
        return nil
      end
      result
    end
  end

  unless method_defined?(:__write_nonblock_raising__)
    alias :__write_nonblock_raising__ :write_nonblock

    def write_nonblock(buf, exception: true)
      unless exception == true || exception == false
        raise ArgumentError, "expected true or false as exception: #{exception.inspect}"
      end
      begin
        result = __write_nonblock_raising__(buf)
      rescue IO::WaitWritable, Errno::EAGAIN => e
        return :wait_writable unless exception
        e.extend(IO::WaitWritable) unless e.is_a?(IO::WaitWritable)
        raise e
      end
      result
    end
  end
end

module Kernel
  private

  def __dir__
    file = caller(0).first   # see require_relative
    file = file.split(/:\d/, 2).first if file
    # Only a real file has a directory. Code that came from a string - an eval, the console -
    # names itself "(eval)" or the like, and MRI answers nil for those rather than pointing at
    # wherever the process happens to be.
    return nil if file.nil? || file.empty? || file.start_with?("(")
    # A file run by a relative path is still where it was after a chdir.
    location = __source_location_of__(file)
    return File.dirname(location) if location && !File.file?(file)
    # An eval told a file name that is not a real file answers that name's directory as given.
    return File.dirname(file) unless File.file?(file)
    File.dirname(File.expand_path(file))
  end unless private_method_defined?(:__dir__)

  # Kernel#readline and #readlines read ARGF, as #gets does.
  def readline(*args)
    ::ARGF.readline(*args)
  end unless private_method_defined?(:readline)

  def readlines(*args)
    ::ARGF.readlines(*args)
  end unless private_method_defined?(:readlines)

  module_function :__dir__, :readline, :readlines
end

class Hash
  # a fresh result hash for the non-mutating combinators, carrying the
  # compare_by_identity flag over the way CRuby does
  private def __result_hash__
    compare_by_identity? ? {}.compare_by_identity : {}
  end
end

class Module
  begin
    public :remove_class_variable
  rescue NameError
  end
end

module Process
  def self.last_status
    $?
  end unless respond_to?(:last_status)
end

class IO
  # What IO.popen adds to the IO it hands back: the child's pid, and a #close that reaps
  # the child so that $? describes it. A module rather than singleton methods, because a
  # singleton `def io.close` has no `super` to reach the library's IO#close with.
  module PopenChild
    def pid
      ::Kernel.raise(::IOError, "closed stream") if closed?
      @__popen_pid__
    end

    def close
      result = super
      begin
        Process.waitpid(@__popen_pid__)
      rescue SystemCallError
      end
      result
    end
  end
  __hide_constant__ :PopenChild
  def self.__popen_child__ = PopenChild
  private_class_method :__popen_child__
end

class << IO
  # IO.pipe used to be a queue between two threads of this process, whose "descriptors"
  # were indices into IronRuby's own table. Nothing outside the process could be handed
  # one, which is the whole point of a pipe as soon as there is a child to talk to, so
  # it is pipe(2) now. The block form is here too; the method it replaces took no block
  # and quietly ignored one.
  def pipe(*args)
    read, write = Process.__os_pipe__
    unless equal?(::IO)
      # A subclass's pipe is made of instances of it, made the way #new would make them but
      # without calling it: allocate, then #initialize(fd, O_RDONLY / O_WRONLY).
      read, write = [[read, 0], [write, 1]].map do |io, flags|
        fd = io.fileno
        io.autoclose = false
        end_io = allocate
        end_io.__send__(:initialize, fd, flags)
        end_io
      end
    end
    external, internal = args.reject { |a| a.respond_to?(:to_hash) }
    read.set_encoding(external, internal) if external
    # MRI hands back a write end that is already sync, so a write reaches the reader
    # without a flush. The read end is an ordinary buffered stream.
    write.sync = true

    return [read, write] unless block_given?
    begin
      yield(read, write)
    ensure
      write.close unless write.closed?
      read.close unless read.closed?
    end
  end

  # IO.popen on top of Process.spawn and a real pipe. The method it replaces started the
  # child through System.Diagnostics.Process, which meant the redirection options had to
  # be lowered onto the shell command line, there was no way to hand the child anything
  # but the three standard streams, #pid did not exist, and $? was filled in when the
  # child *started* - so every caller that looked at the exit status of a popen child,
  # ruby_exe in mspec above all, was reading a status of zero no matter what happened.
  def popen(*args, &block)
    ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 0, expected 1..2)") if args.empty?
    env = nil
    if !args.empty? && !args.first.is_a?(String) && !args.first.is_a?(Array) &&
       args.first.respond_to?(:to_hash)
      env = args.shift
    end

    options = {}
    if args.size > 1 && !args.last.is_a?(String) && args.last.respond_to?(:to_hash)
      options = args.pop.to_hash.dup
    end

    command = args.shift
    # A trailing Hash inside the command array is exec options, not an argument.
    if command.is_a?(Array) && command.size > 1 && command.last.is_a?(Hash)
      command = command.dup
      options = command.pop.to_hash.merge(options)
    end
    mode = args.shift
    mode = "r" if mode.nil?
    mode = mode.to_str if !mode.is_a?(String) && mode.respond_to?(:to_str)
    mode_encodings = mode.to_s[/:(.*)\z/, 1]
    mode = mode.to_s.sub(/:.*\z/, "")

    # The encoding options belong to the IO this hands back, not to the child; spawn
    # rejects anything it does not know.
    external = options.delete(:external_encoding) || options.delete(:encoding)
    internal = options.delete(:internal_encoding)
    if mode_encodings
      external ||= mode_encodings.split(":", 2)[0]
      internal ||= mode_encodings.split(":", 2)[1]
    end
    if external.is_a?(String) && external.include?(":")
      external, internal = external.split(":", 2)
    end

    if command == "-" || (command.is_a?(Array) && command.first == "-")
      raise NotImplementedError, "fork() function is unimplemented on this machine"
    end

    readable = mode.include?("r") || mode.include?("+")
    writable = mode.include?("w") || mode.include?("a") || mode.include?("+")

    parent_read = child_write = parent_write = child_read = nil
    if readable
      parent_read, child_write = Process.__os_pipe__
      options[:out] = child_write
    end
    if writable
      child_read, parent_write = Process.__os_pipe__
      options[:in] = child_read
    end

    spawn_args = []
    spawn_args << env if env
    command.is_a?(Array) ? spawn_args.concat(command) : spawn_args << command
    spawn_args << options

    begin
      pid = Process.spawn(*spawn_args)
    rescue Exception
      [parent_read, child_write, parent_write, child_read].each { |io| io.close if io }
      raise
    end

    # The child owns its ends now; holding them open here would keep a read from ever
    # seeing end-of-file.
    child_write.close if child_write
    child_read.close if child_read

    io = if readable && writable
           Process.__duplex_io__(parent_read, parent_write)
         else
           readable ? parent_read : parent_write
         end

    if external || internal
      # An internal encoding on its own still transcodes, from the default external one.
      io.set_encoding(external || Encoding.default_external, internal)
    end

    # IO.popen answers an instance of whatever class it was called on, so MyIO.popen gives a
    # MyIO. The pipe end is an IO; hand its descriptor over to one of the right class and let
    # the original go without closing the descriptor under it.
    if self != ::IO && !(readable && writable)
      fd = io.fileno
      io.autoclose = false
      io = new(fd, mode)
      if external || internal
        io.set_encoding(external || Encoding.default_external, internal)
      end
    end

    io.__send__(:__ir_set_hidden_ivar__, :@__popen_pid__, pid)
    io.extend(::IO.__send__(:__popen_child__))
    # As for IO.pipe, MRI's popen IO is sync: what is written goes to the child rather
    # than sitting in a buffer until the IO is closed.
    io.sync = true

    return io unless block

    begin
      block.call(io)
    ensure
      io.close unless io.closed?
    end
  end
end

# --------------------------------------------------------------------------
# Object#remove_instance_variable became public in 1.9
# --------------------------------------------------------------------------

class Object
  begin
    public :remove_instance_variable
  rescue NameError
  end
end

# --------------------------------------------------------------------------
# TRUE / FALSE / NIL were removed in Ruby 3.0
# --------------------------------------------------------------------------

[:TRUE, :FALSE, :NIL].each do |__name__|
  begin
    Object.send(:remove_const, __name__) if Object.const_defined?(__name__)
  rescue NameError
  end
end

# --------------------------------------------------------------------------
# The Warning module (2.0 for #warn, 2.7 for the category switches)
# --------------------------------------------------------------------------

module Warning
  # Warning[] and Warning[]= are implemented in C# (WarningOps): the runtime reads
  # the categories itself - the chilled string literal warning is raised from
  # inside MutableString's mutation guard - and -W:category has to be able to set
  # them before any Ruby code runs.

  # Warning#warn is implemented in C# (WarningOps) too: it is the single hook every
  # warning - Kernel#warn, runtime warnings and parser warnings alike - funnels
  # through, and the first of those are emitted while this very file is being parsed.
  # `extend self' is what makes Warning.warn find it while keeping Method#owner
  # == Warning, as CRuby has it.

  extend self
end

# --------------------------------------------------------------------------
# $LOAD_PATH.resolve_feature_path (2.5)
# --------------------------------------------------------------------------

if defined?($LOAD_PATH) && $LOAD_PATH.is_a?(Array) &&
   !$LOAD_PATH.respond_to?(:resolve_feature_path)
  def $LOAD_PATH.resolve_feature_path(feature)
    feature = feature.to_str if !feature.is_a?(String) && feature.respond_to?(:to_str)
    raise TypeError, "no implicit conversion into String" unless feature.is_a?(String)

    rb_exts = ['.rb']
    so_exts = ['.so', '.dll', '.dylib', '.bundle']

    check = lambda do |path|
      so_exts.each do |ext|
        return [:so, path] if path.end_with?(ext) && File.file?(path)
      end
      return [:rb, path] if path.end_with?('.rb') && File.file?(path)
      rb_exts.each do |ext|
        return [:rb, path + ext] if File.file?(path + ext)
      end
      so_exts.each do |ext|
        return [:so, path + ext] if File.file?(path + ext)
      end
      nil
    end

    if feature =~ /\A(?:[\/~]|\.\.?\/|[a-zA-Z]:[\/\\])/
      return check.call(File.expand_path(feature))
    end

    each do |dir|
      next unless dir.is_a?(String)
      found = check.call(File.expand_path(feature, dir))
      return found if found
    end
    nil
  end
end

# --------------------------------------------------------------------------
# Fiber
#
# .NET has no coroutines, so every Fiber is backed by a dedicated thread plus a
# one-slot mailbox. Thread.new marks CLR threads IsBackground, so an abandoned
# fiber can never keep the process alive. Exactly one fiber of a group ever
# runs: switching means "post to the target's mailbox, then block on my own".
# The mailbox is a C# FiberMailbox (Src/Libraries/Builtins/ThreadOps.Fiber.cs),
# which spins for a few microseconds before it falls back to a Monitor wait, so
# a quick resume / Fiber.yield round trip does not cost two kernel wake-ups.
#
# resume / Fiber.yield / transfer / kill / raise / storage / scheduler follow
# MRI, including the rule that a fiber which was entered by #transfer returns,
# when it terminates, to the deepest fiber of the root's resume chain.
#
# A killed fiber unwinds with Kernel#throw rather than an exception, so ensure
# clauses run but "rescue Exception" does not see it - what MRI guarantees.
#
# Worker frames retain State, not the public handle. Once an unreferenced handle
# is collected, FiberCore wakes and unwinds its worker without running Ruby ensure
# bodies. A handle reachable through the fiber's own captured locals or other
# program references still needs explicit completion or kill.
# --------------------------------------------------------------------------

class FiberError < StandardError; end unless defined?(FiberError)

unless defined?(Fiber)
  class Fiber
    # Kernel#throw tag used to unwind a killed fiber.
    KILL_TAG = :__ironruby_fiber_kill__

    # Shared with ThreadOps.FiberCore.FiberMessage.
    MSG_RESUME = 0
    MSG_TRANSFER = 1
    MSG_YIELD = 2
    MSG_RETURN = 3
    MSG_RAISE = 4
    MSG_ERROR = 5
    MSG_KILL = 6
    __hide_constant__ :MSG_RESUME, :MSG_TRANSFER, :MSG_YIELD, :MSG_RETURN,
                     :MSG_RAISE, :MSG_ERROR, :MSG_KILL

    SCHEDULER_METHODS = [:block, :unblock, :kernel_sleep, :io_wait]
    __hide_constant__ :KILL_TAG, :SCHEDULER_METHODS

    # ---- helpers ---------------------------------------------------------

    def self.__err__(cls, msg)
      ::Kernel.raise(cls, msg)
    end

    # A C# FiberMailbox (Src/Libraries/Builtins/ThreadOps.Fiber.cs): a one-slot handoff
    # whose take spins briefly before it blocks.
    def self.__mailbox__
      ::Thread.__fiber_mailbox__
    end

    # Fiber[] and Fiber[]= take a Symbol, or anything with #to_str (but not #to_sym), which
    # is converted: rb_check_id, with its message for anything else.
    def self.__check_key__(key)
      return key if key.is_a?(::Symbol)
      if key.respond_to?(:to_str)
        str = key.to_str
        return str.to_sym if str.is_a?(::String)
      end
      __err__(::TypeError, "#{key.inspect} is not a symbol nor a string")
    end

    # Exactly Kernel#raise's argument handling (including cause:) but returning the
    # exception instead of throwing it, so that it can be handed to the target fiber.
    # The cause is resolved here, i.e. in the calling fiber's context, which is what
    # MRI 4.0 does.
    def self.__make_exception__(args)
      __build_exception__(*args)
    end

    def self.__init_storage__(storage, parent)
      case storage
      when true
        inherited = parent.__storage_raw__
        inherited ? inherited.dup : nil
      when nil
        nil
      when ::Hash
        __err__(::FrozenError, "storage must not be frozen") if storage.frozen?
        # fiber_storage_validate: the keys of a whole storage hash must be Symbols already
        storage.each_key do |k|
          __err__(::TypeError, "wrong argument type #{k.class} (expected Symbol)") unless k.is_a?(::Symbol)
        end
        storage.dup
      else
        __err__(::TypeError, "storage must be a hash")
      end
    end

    # ---- the root fiber of a thread --------------------------------------

    # Thread.__fiber_current__ is a thread-static copy of Thread.current[:__ir_fiber_current__]
    # (a fiber never leaves its CLR thread); __fiber_current_set__ writes both.
    def self.current
      ::Thread.__fiber_current__ || __root__
    end

    def self.__root__
      t = Thread.current
      f = t[:__ir_fiber_current__]
      unless f
        f = t[:__ir_fiber_root__]
        unless f
          f = allocate
          f.__init_root__
          # A thread's root fiber starts with the storage of the fiber that created the
          # thread; Thread.new leaves that fiber's State (not its handle) here for us.
          parent = t[:__ir_fiber_parent__]
          if parent
            t[:__ir_fiber_parent__] = nil
            f.__state__.__inherit_storage__(parent)
          end
          t[:__ir_fiber_root__] = f
        end
      end
      ::Thread.__fiber_current_set__(f)
      f
    end

    def __init_root__
      @__state__ = State.allocate
      @__state__.__init_root__(self)
      self
    end

    def initialize(blocking: false, storage: true, &block)
      # what fiber_t_alloc raises; the handle is registered with its FiberCore only once
      Fiber.__err__(::RuntimeError, "cannot initialize twice") if @__state__
      # positional: keywords cost a Hash and a binding of their own on every Fiber.new
      @__state__ = State.new(self, blocking, storage, &block)
    end

    def initialize_copy(other)
      # MRI copies a Fiber as an uninitialized handle, never as another owner of
      # the same execution stack or weak-handle registration.
      @__state__ = nil
      self
    end
    private :initialize_copy

    def __state__
      @__state__ || Fiber.__err__(::FiberError, "uninitialized fiber")
    end
    def __storage_raw__; __state__.__storage_raw__; end
    def alive?; __state__.alive?; end
    def blocking?; __state__.blocking?; end
    def inspect; __state__.inspect; end
    alias to_s inspect
    def storage; __state__.storage; end
    def storage=(value)
      ::Kernel.warn("Fiber#storage= is experimental and may be removed in the future!",
                    uplevel: 1, category: :experimental)
      __state__.storage = value
    end
    # Keywords given to resume/transfer reach the fiber's block as keywords (and come back
    # from Fiber.yield as a plain Hash: FiberMessage.Value drops the ruby2_keywords mark).
    # MRI checks for interrupts as a fiber switch returns, and again as the method returns
    # (`leave`) - inside whatever `rescue` surrounds the call. FiberCore#switch checks before
    # the Ruby frames of the switch have returned, and a return is no safe point here, so a
    # Thread#raise that came in on the way back was raised at the caller's next safe point:
    # the next iteration of `n.times do ... rescue ... end`, outside the rescue meant for it.
    # The entry of __switched__ is a safe point in the caller's frame.
    ruby2_keywords def resume(*args); Fiber.__switched__(__state__.resume(*args)); end
    ruby2_keywords def transfer(*args); Fiber.__switched__(__state__.transfer(*args)); end
    def kill; __state__.kill; self; end
    ruby2_keywords def raise(*args); __state__.raise(*args); end

    def self.yield(*args); __switched__(State.yield(*args)); end
    def self.__switched__(result); result; end
    def self.blocking?; State.blocking?; end
    def self.blocking(&block); State.blocking(&block); end
    def self.[](key); State[key]; end
    def self.[]=(key, value); State[key] = value; end
    def self.scheduler; State.scheduler; end
    def self.set_scheduler(scheduler); State.set_scheduler(scheduler); end
    def self.current_scheduler; State.current_scheduler; end
    def self.schedule(*args, &block); State.schedule(*args, &block); end
    # Fiber.schedule(blocking: false) { } hands its keywords on to the scheduler's #fiber
    singleton_class.send(:ruby2_keywords, :schedule)

    # This object, rather than the public handle, is retained by the worker stack.
    class State
      def self.current
        ::Thread.__fiber_state__ || Fiber.current.__state__
      end

      def __init_root__(handle)
        @core        = ::Thread.__fiber_core__(handle, Thread.current, true)
        @core.state  = self
        @alive       = true
        @root        = true
        @blocking    = true
        @prev        = nil
        @resuming    = nil
        @transferred = false
        @killing     = false
        @yielded     = false
        @storage     = nil
        @location    = nil
        @scheduler   = nil
        @thread      = Thread.current
        @root_fiber  = self
        self
      end

      # ---- construction ----------------------------------------------------

      def initialize(handle, blocking, storage, &block)
        Fiber.__err__(::ArgumentError, "tried to create Proc object without a block") unless block
        cur          = State.current
        @block       = block
        @blocking    = blocking ? true : false
        @storage     = Fiber.__init_storage__(storage, cur)
        @core        = ::Thread.__fiber_core__(handle, cur.__root_fiber__.__thread__, false)
        @core.state  = self
        @alive       = true
        @root        = false
        @prev        = nil
        @resuming    = nil
        @transferred = false
        @killing     = false
        @yielded     = false
        @thread      = nil
        @scheduler   = nil
        @root_fiber  = cur.__root_fiber__
        # "path:line" of the block, made when #inspect first wants it: making it here was a
        # quarter of what Fiber.new cost.
        @location    = nil
      end

      # ---- internal accessors (used across fibers of the same group) --------

      def __root_fiber__; @root_fiber; end
      def __thread__; @thread; end
      def __storage_raw__; @storage; end

      def __inherit_storage__(parent)
        raw = parent.__storage_raw__
        @storage = raw && raw.dup
        self
      end
      def __resuming__; @resuming; end
      # Somebody resumed us and waits: we are in the root fiber's resume chain.
      def __resumed__; !@prev.nil?; end
      def __set_resuming__(f); @resuming = f; end
      def __core__; @core; end
      def __handle__; @core.handle; end
      def __set_blocking__(b); @blocking = b; end
      def __set_transferred__; @transferred = true; end
      def __post__(kind, payload = nil); ::Thread.__fiber_core_post__(@core, kind, payload); end
      def __scheduler_get__; @scheduler; end
      def __scheduler_set__(s); @scheduler = s; end

      def __storage_store__(key, value)
        # MRI deletes the entry rather than storing a nil
        if value.nil?
          @storage.delete(key) if @storage
        else
          @storage ||= {}
          @storage[key] = value
        end
        value
      end

      # ---- public API ------------------------------------------------------

      def alive?
        @alive
      end

      def blocking?
        @blocking
      end

      def inspect
        state = case @core.status
                when "created"    then "created"
                when "suspended"  then "suspended"
                when "terminated" then "terminated"
                else                  "resumed"
                end
        id = "%016x" % (__handle__.object_id.abs << 1)
        location = @location
        if location.nil?
          loc = @block && (@block.source_location rescue nil)
          location = @location = loc ? "#{loc[0]}:#{loc[1]}" : false
        end
        location ? "#<Fiber:0x#{id} #{location} (#{state})>" : "#<Fiber:0x#{id} (#{state})>"
      end

      alias :to_s :inspect

      def storage
        unless State.current.equal?(self)
          Fiber.__err__(::ArgumentError, "Fiber storage can only be accessed from the Fiber it belongs to")
        end
        # nil until something is stored, as in MRI
        @storage && @storage.dup
      end

      def storage=(hash)
        unless State.current.equal?(self)
          Fiber.__err__(::ArgumentError, "Fiber storage can only be accessed from the Fiber it belongs to")
        end
        @storage = Fiber.__init_storage__(hash, self)
        hash
      end

      # A fiber may only be driven from the thread that owns its group; otherwise
      # the handoff would post to a mailbox nobody is waiting on and deadlock.
      def __check_thread__(cur)
        unless cur.__root_fiber__.equal?(@root_fiber)
          Fiber.__err__(::FiberError, "fiber called across threads")
        end
      end

      ruby2_keywords def resume(*args)
        cur = State.current
        Fiber.__err__(::FiberError, "attempt to resume the current fiber") if cur.equal?(self)
        __check_thread__(cur)
        Fiber.__err__(::FiberError, "attempt to resume a terminated fiber") unless @alive
        Fiber.__err__(::FiberError, "attempt to resume a resumed fiber (double resume)") if @prev
        Fiber.__err__(::FiberError, "attempt to resume a resuming fiber") if @resuming
        # suspended by #transfer rather than Fiber.yield: only a transfer can go back to it
        Fiber.__err__(::FiberError, "attempt to resume a transferring fiber") if @thread && !@yielded
        held = ::Thread.__fiber_hold__
        begin
          __start__
          @prev = cur
          cur.__set_resuming__(self)
          msg = ::Thread.__fiber_switch__(cur.__core__, @core, MSG_RESUME, args, false, held)
        rescue ::Exception
          ::Thread.__fiber_unhold__(held)
          ::Kernel.raise
        end
        cur.__act__(msg)
      end

      ruby2_keywords def transfer(*args)
        cur = State.current
        return nil if cur.equal?(self)
        __check_thread__(cur)
        Fiber.__err__(::FiberError, "attempt to transfer to a resuming fiber") if @resuming
        Fiber.__err__(::FiberError, "attempt to transfer to a yielding fiber") if @yielded
        Fiber.__err__(::FiberError, "dead fiber called") unless @alive
        resumed = cur.__resumed__
        held = ::Thread.__fiber_hold__
        begin
          __start__
          cur.__set_transferred__
          msg = ::Thread.__fiber_switch__(cur.__core__, @core, MSG_TRANSFER, args, resumed, held)
        rescue ::Exception
          ::Thread.__fiber_unhold__(held)
          ::Kernel.raise
        end
        cur.__act__(msg)
      end

      def kill
        cur = State.current
        return self unless @alive
        @killing = true

        if cur.equal?(self)
          throw(KILL_TAG) unless @root
          return self
        end

        __check_thread__(cur)

        unless @thread
          # never entered: there is no stack to unwind and no ensure to run
          @alive = false
          @core.terminate
          return self
        end

        # An ancestor that is busy resuming somebody cannot be unwound now; the
        # throw happens as soon as control comes back to it (MRI does the same).
        return self if @resuming

        cur.__act__(__adopt_and_switch__(cur, MSG_KILL, nil))
        self
      end

      # Makes `cur` the fiber control returns to when we finish - unless somebody
      # is already waiting for us, in which case that fiber keeps the claim and
      # `cur` is the one that gets suspended for good.
      def __adopt__(cur)
        if @prev.nil?
          @prev = cur
          cur.__set_resuming__(self)
        end
      end

      # The arguments are handed straight to Kernel#raise, `cause:' included, so the trailing
      # keyword hash has to stay keywords across the rest parameter.
      ruby2_keywords def raise(*args)
        cur = State.current
        exc = Fiber.__make_exception__(args)
        ::Kernel.raise(exc) if cur.equal?(self)
        __check_thread__(cur)
        Fiber.__err__(::FiberError, "attempt to resume a terminated fiber") unless @alive
        Fiber.__err__(::FiberError, "cannot raise exception on unborn fiber") unless @thread
        cur.__act__(__adopt_and_switch__(cur, MSG_RAISE, exc))
      end

      def __adopt_and_switch__(cur, kind, payload)
        held = ::Thread.__fiber_hold__
        begin
          __adopt__(cur)
          ::Thread.__fiber_switch__(cur.__core__, @core, kind, payload, false, held)
        rescue ::Exception
          ::Thread.__fiber_unhold__(held)
          ::Kernel.raise
        end
      end

      # ---- class methods ---------------------------------------------------

      def self.yield(*args)
        current.__yield__(args)
      end

      def self.blocking?
        current.blocking? ? 1 : false
      end

      def self.blocking
        f = current
        was = f.blocking?
        f.__set_blocking__(true)
        begin
          yield f.__handle__
        ensure
          f.__set_blocking__(was)
        end
      end

      def self.[](key)
        k = Fiber.__check_key__(key)
        s = current.__storage_raw__
        s ? s[k] : nil
      end

      def self.[]=(key, value)
        current.__storage_store__(Fiber.__check_key__(key), value)
        value
      end

      def self.scheduler
        current.__root_fiber__.__scheduler_get__
      end

      def self.set_scheduler(scheduler)
        unless scheduler.nil?
          SCHEDULER_METHODS.each do |m|
            unless scheduler.respond_to?(m)
              Fiber.__err__(::ArgumentError, "Scheduler must implement ##{m}")
            end
          end
          # rb_fiber_scheduler_set (3.4): optional, but MRI says so when it is missing
          unless scheduler.respond_to?(:fiber_interrupt)
            ::Kernel.warn("Scheduler should implement #fiber_interrupt", uplevel: 2)
          end
        end
        current.__root_fiber__.__scheduler_set__(scheduler)
        scheduler
      end

      def self.current_scheduler
        current.blocking? ? nil : scheduler
      end

      def self.schedule(*args, &block)
        s = scheduler
        Fiber.__err__(::RuntimeError, "No scheduler is available!") unless s
        s.fiber(*args, &block)
      end
      singleton_class.send(:ruby2_keywords, :schedule)

      # ---- the switch ------------------------------------------------------

      # Every switch hands control from `cur` to `target` and blocks until control comes back:
      #
      #   held = ::Thread.__fiber_hold__
      #   begin
      #     bookkeeping
      #     msg = ::Thread.__fiber_switch__(cur's core, target's core, kind, payload, cur resumed?, held)
      #   rescue ::Exception
      #     ::Thread.__fiber_unhold__(held)
      #     ::Kernel.raise
      #   end
      #
      # The bookkeeping records who resumed whom and starts the target's thread (#__start__). It
      # runs with interrupts held, so that a Thread#raise cannot leave it half done; the switch
      # ends the hold just before it hands control over, and the rescue should anything fail
      # before that (ThreadOps.HoldFiberSwitch) - re-raising with ::Kernel.raise, since a bare
      # `raise` here is State#raise, Fiber#raise. The checks come before the hold, and #__start__
      # (a thread that cannot be had) before the rest of the bookkeeping. Not a block passed to
      # the switch: the closure cost most of what a switch's Ruby side costs.

      # A fiber runs on a thread of its own: one whose last fiber has finished if one is
      # parked (Thread.__fiber_reuse__, ThreadOps.Fiber.cs), a new one otherwise. A thread
      # goes back to the pool when its fiber is done (Thread.__fiber_park__), clean of that
      # fiber's locals, and ends when nobody wants it for a while.
      def __start__
        return self if @thread
        fiber = self
        @thread = ::Thread.__fiber_reuse__(fiber) || Thread.new do
          while fiber
            fiber.__core__.run(fiber)
            fiber = nil
            fiber = ::Thread.__fiber_park__
          end
        end
        # Known before the first switch, which makes this fiber the running one for
        # Thread#raise as it posts (FiberCore#attach).
        @core.attach(@thread)
        self
      end

      # Takes the next message and makes this CLR thread the one that runs on behalf of the
      # group's owner (Thread.__set_fiber_owner__).
      def __await__
        @core.await
      end

      # Acts on a message that just arrived for the fiber we are running on.
      def __act__(msg)
        throw(KILL_TAG) if @killing && !@root
        case msg.kind
        when MSG_KILL
          @killing = true
          throw(KILL_TAG) unless @root
          nil
        when MSG_RAISE, MSG_ERROR
          ::Kernel.raise(msg.payload)
        when MSG_RESUME, MSG_TRANSFER
          msg.value
        else
          msg.payload
        end
      end

      def __yield__(args)
        throw(KILL_TAG) if @killing
        target = @prev
        if @root || target.nil?
          Fiber.__err__(::FiberError, "attempt to yield on a not resumed fiber")
        end
        held = ::Thread.__fiber_hold__
        begin
          @prev = nil
          target.__set_resuming__(nil)
          @yielded = true
          msg = ::Thread.__fiber_switch__(@core, target.__core__, MSG_YIELD, args, false, held)
        rescue ::Exception
          ::Thread.__fiber_unhold__(held)
          # also when the switch raises a Thread#raise as control comes back: the fiber runs
          @yielded = false
          ::Kernel.raise
        end
        @yielded = false
        __act__(msg)
      end

      # Runs on the fiber's own thread.
      #
      # Everything but the fiber's own block runs with interrupts deferred. Thread#raise is
      # delivered at every method entry and loop back edge, and this is the machinery that
      # MRI has in C: an exception landing between the `rescue' and __finish__ would skip the
      # hand-back and reach the resumer as "unexpected return" (Mutex#lock's "does not raise
      # deadlock if a fiber's attempt to lock was interrupted" spec). One that arrives while
      # the fiber is finishing goes, with control, to the fiber __finish__ hands back to
      # (FiberCore#post), which raises it as its switch returns - MRI's thread interrupt
      # reaching whichever fiber runs next.
      def __run__
        msg = __await__
        result = nil
        error = nil
        handed_back = false
        ::Thread.handle_interrupt(::Object => :never) do
          begin
            catch(KILL_TAG) do
              begin
                ::Thread.handle_interrupt(::Object => :immediate) do
                  case msg.kind
                  when MSG_RESUME, MSG_TRANSFER
                    result = @block.call(*msg.payload)
                  else
                    __act__(msg)
                  end
                end
              rescue ::Exception => e
                error = e
              end
            end
            __finish__(error ? MSG_ERROR : MSG_RETURN, error ? error : result)
            handed_back = true
          ensure
            unless handed_back
              # `return` or `break` out of the fiber block unwinds with something
              # `rescue Exception` cannot see. Hand control back anyway - dropping
              # it here would park the resuming fiber forever.
              begin
                __finish__(MSG_ERROR, ::LocalJumpError.new("unexpected return"))
              rescue ::Exception
              end
            end
          end
        end
      end

      # Runs on the fiber's own thread, as the last thing it does.
      def __finish__(kind, payload)
        @alive = false
        @core.terminate
        # The program has ended and this fiber was killed with its thread: the
        # fiber that resumed it is suspended, and stays that way (as in MRI).
        return if ::Thread.__terminating__
        # What the end of this thread does for the mutexes the fiber still holds,
        # done before the fiber we hand control to can run and lock.
        ::Thread.__fiber_release_locks__
        target = @prev
        if target
          @prev = nil
          target.__set_resuming__(nil)
        else
          # Entered by #transfer: MRI returns to the deepest fiber of the root
          # fiber's resume chain, not to the root itself.
          target = @root_fiber
          while (nested = target.__resuming__)
            target = nested
          end
        end
        target.__post__(kind, payload)
      end
    end
    __hide_constant__ :State
  end
end

# Kernel#Complex and Kernel#Rational never learned to read a String - they went
# straight for #real and #numerator on it - so every `Complex("1+2i")` in the
# specs was a NoMethodError. Both now follow complex.c's nucomp_convert and
# rational.c's nurat_convert; the string grammar lives on Complex/Rational (see
# "MRI's numeric string scanners" further down this file).
module Kernel
  # rb_opts_exception_p. Only the two singletons are accepted - anything else,
  # including nil, is an ArgumentError naming the offending value.
  def __opts_exception__(value)
    return true if value.equal?(true)
    return false if value.equal?(false)
    ::Kernel.raise(::ArgumentError, "expected true or false as exception: #{value.inspect}")
  end
  private :__opts_exception__
  module_function :__opts_exception__
end

# Rational and Complex are Ruby ports of MRI 4.0's rational.c and complex.c, with the
# methods those files define on Integer, Float, Numeric, String and Kernel.
require File.expand_path("ironruby/rational.rb", File.dirname(__FILE__))
require File.expand_path("ironruby/complex.rb", File.dirname(__FILE__))

# A non-blocking fiber does not sleep on the thread: it hands the wait to the
# fiber scheduler, which is free to run something else in the meantime. Without
# this, `sleep` with no duration inside a non-blocking fiber blocks the whole
# process for ever - spec/core/kernel/sleep_spec.rb stops dead there.
module Kernel
  if private_method_defined?(:sleep) || method_defined?(:sleep)
    alias :__ir_sleep__ :sleep
    private :__ir_sleep__

    def sleep(*args)
      scheduler = ::Fiber.current_scheduler
      if scheduler && scheduler.respond_to?(:kernel_sleep)
        return scheduler.kernel_sleep(*args)
      end
      # MRI's rb_time_interval: an Integer or Float goes straight through, a Rational or
      # anything else that answers #divmod is split into seconds and a fraction.
      # A nil duration (3.3) is the same as none: sleep until woken.
      args = [] if args.size == 1 && args[0].nil?
      if args.size == 1
        d = args[0]
        if !d.is_a?(::Integer) && !d.is_a?(::Float)
          unless d.respond_to?(:divmod)
            ::Kernel.raise(::TypeError, "can't convert #{d.class} into time interval")
          end
          whole, fraction = d.divmod(1)
          d = whole.to_i + fraction.to_f
          args = [d]
        end
        ::Kernel.raise(::ArgumentError, "time interval must not be negative") if d < 0
      end
      __ir_sleep__(*args)
    end
    module_function :sleep
  end
end

# --- constants and core methods the 1.9 snapshot predates -------------------

class Float
  INFINITY = 1.0 / 0.0 unless const_defined?(:INFINITY)
  NAN = 0.0 / 0.0 unless const_defined?(:NAN)
end

# Numeric's generic arithmetic is written in terms of the operators the subclass
# supplies, and ruby/spec checks exactly which ones are called and in what order.
# IronRuby's came from the 1.8 complex.rb or from the CLR numeric conversions,
# so they went looking for a Float where MRI asks the object itself. Integer and
# Float define their own, so what changes here is what a Numeric subclass
# written in Ruby gets.
class Numeric
  # MRI's num_sadded: numbers are values and take no singleton methods, so the
  # one just defined is removed again.
  def singleton_method_added(name)
    singleton_class.__send__(:remove_method, name)
    ::Kernel.raise(::TypeError, "can't define singleton method \"#{name}\" for #{self.class}")
  end

  def div(other)
    ::Kernel.raise(::ZeroDivisionError, "divided by 0") if other == 0
    (self / other).floor
  end

  def modulo(other)
    self - other * div(other)
  end
  alias :% :modulo

  def divmod(other)
    [div(other), modulo(other)]
  end

  # The modulo moved back across zero when the two point in opposite
  # directions: remainder keeps the sign of the dividend where modulo keeps the
  # sign of the divisor. The order the operands are asked about their sign is
  # the order MRI asks in, which is what the specs watch.
  def remainder(other)
    x = self
    unless other.is_a?(::Numeric)
      x, other = other.coerce(self)
    end

    z = x % other
    if z != 0 && ((x < 0 && other > 0) || (x > 0 && other < 0))
      z - other
    else
      z
    end
  end

  # A number is already what a copy of it would be - Rational and Complex included, which
  # MRI counts among its special objects too.
  def clone(freeze: nil)
    unless freeze.nil? || freeze == true || freeze == false
      ::Kernel.raise(::ArgumentError, "unexpected value for freeze: #{freeze.class}")
    end
    ::Kernel.raise(::ArgumentError, "can't unfreeze #{self.class}") if freeze == false
    self
  end

  def dup
    self
  end
end

# nil, true and false are special objects too (MRI's special_object_p): they cannot be
# cloned unfrozen either. The library #clone answered them for freeze: false.
[::NilClass, ::TrueClass, ::FalseClass].each do |klass|
  klass.class_eval do
    def clone(freeze: nil)
      unless freeze.nil? || freeze == true || freeze == false
        ::Kernel.raise(::ArgumentError, "unexpected value for freeze: #{freeze.class}")
      end
      ::Kernel.raise(::ArgumentError, "can't unfreeze #{self.class}") if freeze == false
      self
    end
  end
end

module Kernel
  # The built-in warn takes exactly one message and no keywords; 2.5 added
  # multiple messages plus uplevel:, and 3.0 added category:.
  if private_method_defined?(:warn) && !private_method_defined?(:__ir_warn__)
    alias :__ir_warn__ :warn
    private :__ir_warn__

    def warn(*messages, uplevel: nil, category: nil)
      # The category gate runs before the $VERBOSE gate and before the message check,
      # matching warning.rb's Kernel#warn: it is the Ruby half of rb_warn_m.
      # (category: and uplevel: are the only keywords, as in warning.rb)
      unless category.nil?
        unless ::Symbol === category
          unless category.respond_to?(:to_sym)
            raise ::TypeError, "no implicit conversion of #{category.class} into Symbol"
          end
          category = category.to_sym
        end
        return nil unless ::Warning[category]
      end
      return nil if messages.empty?
      return nil if $VERBOSE.nil?

      unless uplevel.nil?
        unless ::Integer === uplevel
          unless uplevel.respond_to?(:to_int)
            raise ::TypeError, "no implicit conversion of #{uplevel.class} into Integer"
          end
          uplevel = uplevel.to_int
        end
        raise ::ArgumentError, "negative level (#{uplevel})" if uplevel < 0
        # IronRuby's Kernel#caller only takes the start argument, and its entries carry a
        # trailing ":in `method'" that MRI's uplevel prefix does not.
        # As RubyGems' own Kernel#warn does, frames of its #require don't count: a warning
        # from a required file names the line that required it, not custom_require.rb.
        # Frames of <internal: code (an eval that names itself so) don't count either.
        frames = (caller(1) || []).reject { |frame| frame.start_with?("<internal:") }
        index = 0
        index += 1 while frames[index] && frames[index].include?("/rubygems/custom_require.rb:")
        uplevel.times do
          index += 1
          index += 1 while frames[index] && frames[index].include?("/rubygems/custom_require.rb:")
        end
        location = frames[index]
        location = location.sub(/:in [`'].*\z/, '') if location
        # MRI prefixes "warning: " even when the level is past the end of the backtrace.
        prefix = location ? "#{location}: warning: " : "warning: "
      end

      # Array arguments are expanded, one element per line (MRI joins with the record
      # separator). Recurse over real Arrays only - probing #to_ary would disturb mocks.
      flattened = []
      expand = ->(list) { list.each { |m| ::Array === m ? expand.call(m) : flattened << m } }
      expand.call(messages)
      text = flattened.map { |m| s = m.to_s; s.end_with?("\n") ? s : s + "\n" }.join
      return nil if text.empty?
      text = "#{prefix}#{text}" if prefix

      # MRI's `exc == rb_mWarning' escape hatch: a Warning#warn override that calls super
      # lands back here, and routing on would recurse forever.
      if equal?(::Warning)
        # Straight to $stderr, as rb_write_error_str does: the built-in warn would route
        # back through Warning.warn, which is the override this hatch exists to bypass.
        $stderr.write(text)
        return nil
      end

      # rb_warning_warn_arity: an override that takes exactly one argument gets no keywords.
      if ::Warning.method(:warn).arity == 1
        ::Warning.warn(text)
      else
        ::Warning.warn(text, category: category)
      end
      nil
    end
    module_function :warn
  end
end

class String
  # The name MRI puts in a "no implicit conversion of X into Y" message: the
  # three singletons by value, everything else by class - with IronRuby's
  # Fixnum/Bignum split hidden, because MRI only knows Integer.
  def __ir_type_name__(value)
    case value
    when nil then "nil"
    when true then "true"
    when false then "false"
    else
      name = value.class.to_s
      (name == "Fixnum" || name == "Bignum") ? "Integer" : name
    end
  end
  private :__ir_type_name__

  # ------------------------------------------------------------------
  # Byte-offset searching.
  #
  # A byte offset is not a character offset, and MRI insists that the one you
  # hand it lands on a character boundary.  The search itself has to run in the
  # receiver's real encoding - forcing a binary copy, which is what #byteindex
  # used to do, makes a UTF-8 Regexp needle raise Encoding::CompatibilityError -
  # so the offset is converted to a character offset, the ordinary search runs,
  # and the answer is converted back.
  # ------------------------------------------------------------------

  # Byte offset of the start of every character, plus one final entry for the
  # end of the string.  The Nth entry is the byte offset of character N.
  def __ir_char_starts__
    starts = [0]
    at = 0
    each_char { |ch| at += ch.bytesize; starts << at }
    starts
  end
  private :__ir_char_starts__

  def __ir_byte_search__(reverse, needle, offset)
    # When every character is one byte wide the byte offsets and the character
    # offsets coincide, so the (linear) character table is not needed.
    size = bytesize
    simple = (size == length)
    starts = simple ? nil : __ir_char_starts__

    offset += size if offset < 0
    return [nil, nil] if offset < 0
    if offset > size
      # #index gives up past the end; #rindex clamps to it.
      return [nil, nil] unless reverse
      offset = size
    end

    if simple
      char_offset = offset
    else
      char_offset = starts.index(offset)
      raise ::IndexError, "offset #{offset} does not land on character boundary" if char_offset.nil?
    end

    found = reverse ? rindex(needle, char_offset) : index(needle, char_offset)
    # The match goes back to the C# #byteindex/#byterindex, which store it as
    # the caller's $~.
    return [nil, $~] if found.nil?
    [simple ? found : starts[found], $~]
  end
  private :__ir_byte_search__

  # A byte search needle is a Regexp or something String-convertible; unlike
  # #index it never accepts an Integer codepoint.
  def __ir_byte_needle__(needle)
    return needle if needle.is_a?(::Regexp) || needle.is_a?(::String)
    if needle.respond_to?(:to_str)
      converted = needle.to_str
      return converted if converted.is_a?(::String)
    end
    ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(needle)} into String")
  end
  private :__ir_byte_needle__

  def __ir_byte_offset__(value)
    return value if value.is_a?(::Integer)
    unless value.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion from #{__ir_type_name__(value)} to integer")
    end
    converted = value.to_int
    unless converted.is_a?(::Integer)
      ::Kernel.raise(::TypeError, "can't convert #{__ir_type_name__(value)} to Integer")
    end
    converted
  end
  private :__ir_byte_offset__

  # #byteindex, #byterindex, #partition and #rpartition are C# methods (they
  # set the caller's $~) that forward their arguments here and receive
  # [result, match] back.
  def __ir_byteindex__(args)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    needle, offset = args
    offset = 0 if args.size < 2
    __ir_byte_search__(false, __ir_byte_needle__(needle), __ir_byte_offset__(offset))
  end
  private :__ir_byteindex__

  # MRI's default offset is the end of the string, not -1: "hello".byterindex("")
  # answers 5. An explicitly passed nil is still a TypeError.
  def __ir_byterindex__(args)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    needle = __ir_byte_needle__(args[0])
    offset = args.size < 2 ? bytesize : __ir_byte_offset__(args[1])
    __ir_byte_search__(true, needle, offset)
  end
  private :__ir_byterindex__

  # Replaces a byte range in place. Every index here is a byte index, so the
  # work is done on a binary copy and tagged back afterwards, like bytesplice.
  def bytesplice(*args)
    # Three shapes: (index, length, str), (range, str) and the five argument
    # (index, length, str, str_index, str_length), which splices only part of the
    # replacement. Four arguments is not one of them, even with a Range first.
    case args.size
    when 2
      index_args, str, sub_args = args[0, 1], args[1], nil
    when 3
      # (range, str, str_range) when the first argument is a Range, otherwise
      # (index, length, str).
      if args[0].is_a?(::Range)
        index_args, str, sub_args = args[0, 1], args[1], args[2, 1]
      else
        index_args, str, sub_args = args[0, 2], args[2], nil
      end
    when 5
      index_args, str, sub_args = args[0, 2], args[2], args[3, 2]
    when 4
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 4, expected 2, 3, or 5)")
    else
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 2..5)")
    end

    unless str.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(str)} into String")
    end

    if index_args.size == 1
      range = index_args[0]
      unless range.is_a?(::Range)
        ::Kernel.raise(::TypeError, "wrong argument type #{__ir_type_name__(range)} (expected Range)")
      end
      index, length = __byte_range__(range)
    else
      index = ::Kernel.Integer(index_args[0])
      length = ::Kernel.Integer(index_args[1])
      # A negative length is rejected before the index is even bounds checked.
      ::Kernel.raise(::IndexError, "negative length #{length}") if length < 0
      index += bytesize if index < 0
      ::Kernel.raise(::IndexError, "index #{index_args[0]} out of string") if index < 0 || index > bytesize
    end

    if sub_args && sub_args.size == 1
      sub_range = sub_args[0]
      unless sub_range.is_a?(::Range)
        ::Kernel.raise(::TypeError, "wrong argument type #{__ir_type_name__(sub_range)} (expected Range)")
      end
      sub_index, sub_length = str.__send__(:__byte_range__, sub_range)
      str.__send__(:__require_byte_boundary__, sub_index)
      str.__send__(:__require_byte_boundary__, sub_index + sub_length)
      str = str.byteslice(sub_index, sub_length) || str[0, 0]
    elsif sub_args
      sub_index = ::Kernel.Integer(sub_args[0])
      sub_length = ::Kernel.Integer(sub_args[1])
      ::Kernel.raise(::IndexError, "negative length #{sub_length}") if sub_length < 0
      sub_index += str.bytesize if sub_index < 0
      if sub_index < 0 || sub_index > str.bytesize
        ::Kernel.raise(::IndexError, "index #{sub_args[0]} out of string")
      end
      str.__send__(:__require_byte_boundary__, sub_index)
      str.__send__(:__require_byte_boundary__, sub_index + sub_length)
      str = str.byteslice(sub_index, sub_length) || str[0, 0]
    end

    length = bytesize - index if index + length > bytesize

    # Both ends of the replaced range have to sit on a character boundary.
    __require_byte_boundary__(index)
    __require_byte_boundary__(index + length)

    # The result's encoding is the compatible one: an ASCII-only operand gives
    # way to the other side.
    target_encoding = encoding
    if str.encoding != encoding
      if ascii_only? && !str.ascii_only?
        target_encoding = str.encoding
      elsif !str.ascii_only?
        ::Kernel.raise(::Encoding::CompatibilityError,
          "incompatible character encodings: #{encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : encoding} and #{str.encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : str.encoding}")
      end
    end

    # in place, in C# (MutableStringOps.ByteSpliceInPlace)
    __ir_bytesplice__(index, length, str, target_encoding)
  end unless method_defined?(:bytesplice)

  # MRI refuses a byte offset that falls inside a character.
  def __require_byte_boundary__(offset)
    return if offset <= 0 || offset >= bytesize || bytesize == length
    return if __ir_char_starts__.include?(offset)
    ::Kernel.raise(::IndexError, "offset #{offset} does not land on character boundary")
  end
  private :__require_byte_boundary__

  def __byte_range__(range)
    size = bytesize
    first = range.begin
    first = 0 if first.nil?
    first = ::Kernel.Integer(first)
    first += size if first < 0
    ::Kernel.raise(::RangeError, "#{range} out of range") if first < 0 || first > size
    last = range.end
    if last.nil?
      length = size - first
    else
      last = ::Kernel.Integer(last)
      last += size if last < 0
      last -= 1 if range.exclude_end?
      length = last - first + 1
      length = 0 if length < 0
    end
    [first, length]
  end
  private :__byte_range__

  # Appends the bytes of each argument without any encoding negotiation: an
  # Integer contributes one byte, a String contributes its bytes as they are.
  def append_as_bytes(*objects)
    objects.each do |o|
      bytes =
        case o
        when ::Integer then [o & 0xff].pack("C")
        when ::String then o
        else ::Kernel.raise(::TypeError, "wrong argument type #{o.class} (expected String or Integer)")
        end
      piece = bytes.dup
      piece.force_encoding(::Encoding::BINARY) if piece.respond_to?(:force_encoding)
      binary = dup
      binary.force_encoding(::Encoding::BINARY) if binary.respond_to?(:force_encoding)
      binary << piece
      binary.force_encoding(encoding) if binary.respond_to?(:force_encoding)
      replace(binary)
    end
    self
  end unless method_defined?(:append_as_bytes)

  # MRI takes a String or a Regexp here and nothing else - Kernel.String would happily
  # accept anything with a #to_s - and the empty pieces carry the receiver's encoding.
  def __ir_separator__(pattern)
    return pattern if pattern.is_a?(::Regexp) || pattern.is_a?(::String)
    if pattern.respond_to?(:to_str)
      converted = pattern.to_str
      return converted if converted.is_a?(::String)
    end
    ::Kernel.raise(::TypeError,
      "wrong argument type #{pattern.nil? ? 'nil' : pattern.class} (expected Regexp)")
  end
  private :__ir_separator__

  def __ir_partition__(pattern)
    pattern = __ir_separator__(pattern)
    empty = self[0, 0]
    if pattern.is_a?(::Regexp)
      m = pattern.match(self)
      return [[self[0..-1], empty, empty], nil] unless m
      [[m.pre_match, m[0], m.post_match], m]
    else
      i = index(pattern)
      return [[self[0..-1], empty, empty], nil] unless i
      [[self[0, i], pattern.dup, self[(i + pattern.length)..-1]], nil]
    end
  end
  private :__ir_partition__

  def __ir_rpartition__(pattern)
    pattern = __ir_separator__(pattern)
    empty = self[0, 0]
    if pattern.is_a?(::Regexp)
      # MRI searches backwards from the end, like #rindex.
      start = rindex(pattern)
      m = $~
      return [[empty, empty, self[0..-1]], nil] unless start
      [[self[0, start], m[0], m.post_match], m]
    else
      i = rindex(pattern)
      return [[empty, empty, self[0..-1]], nil] unless i
      [[self[0, i], pattern.dup, self[(i + pattern.length)..-1]], nil]
    end
  end
  private :__ir_rpartition__

  def prepend(*others)
    others = others.map do |o|
      # #to_str only: MRI will not call #to_s to find something to prepend.
      converted = o.is_a?(::String) ? o : ::String.try_convert(o)
      converted || ::Kernel.raise(::TypeError,
        "no implicit conversion of #{o.nil? ? 'nil' : o.class} into String")
    end
    # MRI's rb_str_update(str, 0, 0, ...), as #insert: shifting the content in place instead of
    # building a copy and #replace-ing self with it.
    insert(0, others.size == 1 ? others[0] : others.join(""))
  end unless method_defined?(:prepend)

  # The frozen check happens before the affix is even looked at: MRI raises
  # FrozenError even when nothing would have been removed.
  def delete_prefix!(prefix)
    prefix = __affix__(prefix)
    __ir_require_unfrozen__
    result = delete_prefix(prefix)
    result == self ? nil : replace(result)
  end unless method_defined?(:delete_prefix!)

  def delete_suffix!(suffix)
    suffix = __affix__(suffix)
    __ir_require_unfrozen__
    result = delete_suffix(suffix)
    result == self ? nil : replace(result)
  end unless method_defined?(:delete_suffix!)

  def __ir_require_unfrozen__
    return unless frozen?
    ::Kernel.raise(::FrozenError.new("can't modify frozen String: #{inspect}", receiver: self))
  end
  private :__ir_require_unfrozen__

  # 3.4's name for -@. Spelled out rather than aliased: String#-@ is itself
  # defined further down this file.

  # The inverse of #dump. Anything that is not something #dump could have
  # produced is a RuntimeError, which is what MRI raises here.
  def undump
    s = self
    forced = nil
    if (m = /\A(".*")(?:\.dup)?\.force_encoding\("([^"]+)"\)\z/m.match(s))
      s = m[1]
      forced = m[2]
    end
    if s.start_with?('"') && !s.end_with?('"') && s.count('"') < 2
      ::Kernel.raise(::RuntimeError, "unterminated dumped string")
    end
    unless s.start_with?('"') && s.end_with?('"') && s.length >= 2
      ::Kernel.raise(::RuntimeError, "invalid dumped string; not wrapped with '\"' nor '\"...\".force_encoding(\"...\")' form")
    end
    if forced && !__known_encoding__(forced)
      ::Kernel.raise(::RuntimeError, "dumped string has unknown encoding name")
    end
    body = s[1...-1]
    ::Kernel.raise(::RuntimeError, "invalid dumped string") if body.nil?
    # The dumped form is plain ASCII; a NUL or a non-ASCII character in it means
    # it did not come from #dump.
    body.each_char do |ch|
      if ch == "\0"
        ::Kernel.raise(::RuntimeError, "string contains null byte")
      elsif ch.ord > 0x7f
        ::Kernel.raise(::RuntimeError, "non-ASCII character detected")
      end
    end
    out = +""
    unicode_escape = false
    hex_escape = false # a \x escape of a non-ASCII byte
    i = 0
    while i < body.length
      c = body[i]
      if c == '"'
        ::Kernel.raise(::RuntimeError, "invalid dumped string")
      elsif c == "\\"
        i += 1
        e = body[i]
        ::Kernel.raise(::RuntimeError, "invalid dumped string") if e.nil?
        case e
        when "n" then out << "\n"
        when "t" then out << "\t"
        when "r" then out << "\r"
        when "f" then out << "\f"
        when "v" then out << "\v"
        when "b" then out << "\b"
        when "a" then out << "\a"
        when "e" then out << "\e"
        when "s" then out << " "
        when "\\" then out << "\\"
        when '"' then out << '"'
        when "#" then out << "#"
        when "0" then out << "\0"
        when "x"
          hex = body[(i + 1), 2]
          ::Kernel.raise(::RuntimeError, "invalid hex escape") unless hex =~ /\A[0-9a-fA-F]{2}\z/
          if hex.to_i(16) > 0x7f
            ::Kernel.raise(::RuntimeError, "hex escape and Unicode escape are mixed") if unicode_escape
            hex_escape = true
          end
          out << hex.to_i(16).chr
          i += 2
        when "u"
          ::Kernel.raise(::RuntimeError, "hex escape and Unicode escape are mixed") if hex_escape
          unicode_escape = true
          if body[i + 1] == "{"
            close = body.index("}", i + 1)
            ::Kernel.raise(::RuntimeError, "invalid Unicode escape") unless close
            body[(i + 2)...close].split(" ").each { |cp| out << __undump_cp__(cp) }
            i = close
          else
            cp = body[(i + 1), 4]
            ::Kernel.raise(::RuntimeError, "invalid Unicode escape") unless cp =~ /\A[0-9a-fA-F]{4}\z/
            out << __undump_cp__(cp)
            i += 4
          end
        else
          # MRI passes an escape it does not recognise through untouched.
          out << "\\" << e
        end
      else
        out << c
      end
      i += 1
    end
    # #dump writes \u escapes only for a Unicode string it does not need to re-encode,
    # so the two forms never meet in its output.
    if forced && unicode_escape
      ::Kernel.raise(::RuntimeError, "dumped string contained Unicode escape but used force_encoding")
    end
    # Without a .force_encoding(...) suffix the answer carries the receiver's
    # own encoding - or UTF-8 once a \u escape has been read, as MRI's
    # undump_after_backslash switches it to.
    if out.respond_to?(:force_encoding)
      out.force_encoding(forced || (unicode_escape ? ::Encoding::UTF_8 : encoding))
    end
    out
  end unless method_defined?(:undump)

  def __known_encoding__(name)
    ::Encoding.find(name)
    true
  rescue ::ArgumentError
    false
  end
  private :__known_encoding__

  def __undump_cp__(hex)
    ::Kernel.raise(::RuntimeError, "invalid Unicode escape") unless hex =~ /\A[0-9a-fA-F]+\z/
    cp = hex.to_i(16)
    ::Kernel.raise(::RuntimeError, "invalid Unicode codepoint") if cp > 0x10ffff
    # pack("U") hands back an ASCII-8BIT string here; the bytes are UTF-8.
    ch = [cp].pack("U")
    ch.force_encoding(::Encoding::UTF_8) if ch.respond_to?(:force_encoding)
    ch
  end
  private :__undump_cp__

  # Replaces every byte that is not part of a valid character with the given
  # replacement (the encoding's own replacement character by default).
  def scrub(replacement = nil, &block)
    # String, not the receiver's class: since Ruby 3.0 only #dup, #clone and #+@ carry a
    # String subclass over to the result.
    return __ir_plain_copy__ if valid_encoding?
    default = __ir_scrub_default__
    replacement = __ir_scrub_replacement__(replacement) unless replacement.nil?
    __ir_check_scrub_replacement__(replacement) if replacement
    out = +""
    out.force_encoding(encoding) if out.respond_to?(:force_encoding)
    # A block that changes the receiver raises "string modified", as MRI's str_mod_check does
    # after each yield. The bytes are read from a copy taken first, so a change can never be
    # copied into the result (or leave a slice past the end) before the check sees it.
    if block
      change_count = __ir_change_count__
      source = __ir_plain_copy__
      call = lambda do |bad|
        result = block.call(bad)
        __ir_require_unchanged__(change_count)
        __ir_check_scrub_replacement__(__ir_scrub_replacement__(result))
      end
    else
      source = self
    end
    # The other multibyte encodings (EUC-JP, GB18030, Big5, ...) get the runs of bytes to
    # replace from C#, which finds them in the bytes the way MRI does: in GB18030 "\xA1\x39"
    # is one run although "\x39" alone is a valid character (see MutableString.GetScrubRuns).
    runs = source.__send__(:__ir_scrub_runs__)
    if runs
      pos = 0
      size = source.bytesize
      runs.each_slice(2) do |offset, length|
        out << source.byteslice(pos, offset - pos) if offset > pos
        out << (block ? call.call(source.byteslice(offset, length)) : (replacement || default))
        pos = offset + length
      end
      out << source.byteslice(pos, size - pos) if pos < size
      return out
    end
    # A run of bad bytes that is the start of a character which simply ran out of input -
    # a truncated one at the end of the string - is one replacement, not one per byte, so
    # the bad characters are collected before being handed over.
    pending = nil
    flush = lambda do
      next if pending.nil?
      out << (block ? call.call(pending) : (replacement || default))
      pending = nil
    end
    source.each_char do |ch|
      if ch.valid_encoding?
        flush.call
        out << ch
      elsif pending && __ir_starts_character__(pending + ch)
        pending += ch
      else
        flush.call
        pending = ch
      end
    end
    flush.call
    out
  end unless method_defined?(:scrub)

  # The replacement character of the encoding: U+FFFD where there is one, "?" elsewhere.
  def __ir_scrub_default__
    case encoding
    when ::Encoding::UTF_8 then "�"
    when ::Encoding::UTF_16LE, ::Encoding::UTF_16BE, ::Encoding::UTF_32LE, ::Encoding::UTF_32BE
      "�".encode(encoding)
    else
      "?".dup.force_encoding(encoding)
    end
  end
  private :__ir_scrub_default__

  # A replacement has to be valid itself and usable in this string's encoding.
  def __ir_check_scrub_replacement__(replacement)
    unless replacement.valid_encoding?
      ::Kernel.raise(::ArgumentError, "replacement must be valid byte sequence '#{replacement.inspect}'")
    end
    if ::Encoding.compatible?(self, replacement).nil? && replacement.encoding != encoding
      ::Kernel.raise(::Encoding::CompatibilityError,
                     "incompatible character encodings: #{encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : encoding} and #{replacement.encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : replacement.encoding}")
    end
    replacement
  end
  private :__ir_check_scrub_replacement__

  # True when the bytes could be the beginning of a character in this encoding that ran
  # out of input, as opposed to something that can never begin one. A run of bad bytes
  # is one replacement only while it stays such a beginning: "\xE3\x81" is one, but
  # "\xFF\xFE" and "\xED\xA0\x80" (an encoded surrogate) are one per byte.
  def __ir_starts_character__(bytes)
    return false if bytes.empty?
    lead = bytes.getbyte(0)
    case encoding
    when ::Encoding::UTF_8
      expected = if lead >= 0xF0 && lead <= 0xF4 then 4
                 elsif lead >= 0xE0 && lead <= 0xEF then 3
                 elsif lead >= 0xC2 && lead <= 0xDF then 2
                 else 0
                 end
      return false if expected == 0 || bytes.bytesize >= expected
      (1...bytes.bytesize).all? do |i|
        b = bytes.getbyte(i)
        next false unless (b & 0xC0) == 0x80
        next true unless i == 1
        case lead
        when 0xE0 then b >= 0xA0
        when 0xED then b <= 0x9F
        when 0xF0 then b >= 0x90
        when 0xF4 then b <= 0x8F
        else true
        end
      end
    when ::Encoding::UTF_16LE, ::Encoding::UTF_16BE, ::Encoding::UTF_32LE, ::Encoding::UTF_32BE
      # a code unit, or the first half of a surrogate pair, that ran out of input
      bytes.bytesize <= 4
    else
      false
    end
  end
  private :__ir_starts_character__

  # Like #to_str conversion of an argument: MRI's StringValue, no #to_s.
  def __ir_scrub_replacement__(replacement)
    return replacement if replacement.is_a?(::String)
    ::String.try_convert(replacement) ||
      ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(replacement)} into String")
  end
  private :__ir_scrub_replacement__

  # A valid receiver is left alone - MRI does not even check it is frozen.
  def scrub!(replacement = nil, &block)
    return self if valid_encoding?
    replace(scrub(replacement, &block))
  end unless method_defined?(:scrub!)

  # ---- strip selectors (4.0) ---------------------------------------------
  #
  # `strip("a-c")` came back as a wrong-number-of-arguments error. (The case
  # mapping options - upcase(:ascii) and friends - used to be shimmed here too;
  # they are built in now, tables and all: see UnicodeCaseMapping.)

  # A character is stripped when it is in every one of the given sets, which is
  # exactly what String#count answers for a one-character string.
  def __selected__(ch, selectors)
    ch.count(*selectors) > 0
  end
  private :__selected__

  alias :__ir_strip__ :strip
  alias :__ir_lstrip__ :lstrip
  alias :__ir_rstrip__ :rstrip
  private :__ir_strip__, :__ir_lstrip__, :__ir_rstrip__

  # #count is asked once per character, so the selectors are converted up front:
  # MRI calls #to_str on each argument exactly once however long the receiver is.
  def __strip_selectors__(selectors)
    selectors.map do |selector|
      next selector if selector.is_a?(::String)
      converted = selector.respond_to?(:to_str) ? selector.to_str : nil
      unless converted.is_a?(::String)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(selector)} into String")
      end
      converted
    end
  end
  private :__strip_selectors__

  def lstrip(*selectors)
    return __ir_lstrip__ if selectors.empty?
    selectors = __strip_selectors__(selectors)
    # An invalid selector is an error even when the receiver is empty.
    "".count(*selectors) if empty?
    i = 0
    i += 1 while i < length && __selected__(self[i, 1], selectors)
    self[i..-1] || self[0, 0]
  end

  def rstrip(*selectors)
    return __ir_rstrip__ if selectors.empty?
    selectors = __strip_selectors__(selectors)
    "".count(*selectors) if empty?
    i = length
    i -= 1 while i > 0 && __selected__(self[i - 1, 1], selectors)
    self[0, i]
  end

  def strip(*selectors)
    return __ir_strip__ if selectors.empty?
    selectors = __strip_selectors__(selectors)
    lstrip(*selectors).rstrip(*selectors)
  end

  [:strip, :lstrip, :rstrip].each do |name|
    bang = :"#{name}!"
    next unless method_defined?(bang)
    plain_bang = :"__ir_#{name}_bang__"
    alias_method plain_bang, bang
    private plain_bang
    define_method(bang) do |*selectors|
      return __send__(plain_bang) if selectors.empty?
      # The frozen check comes before the work: MRI raises even when the
      # selectors would leave the string alone.
      if frozen?
        ::Kernel.raise(::FrozenError.new("can't modify frozen String: #{inspect}", receiver: self))
      end
      result = __send__(name, *selectors)
      result == self ? nil : replace(result)
    end
  end

  # ---- Unicode normalisation and grapheme clusters ------------------------
  #
  # Both are handed to the CLR, which has the Unicode tables: normalisation to
  # System.String#Normalize and segmentation to StringInfo's text elements,
  # which are grapheme clusters by another name.

  NORMALIZATION_FORMS__ = {
    nfc: :FormC, nfd: :FormD, nfkc: :FormKC, nfkd: :FormKD
  }
  __hide_constant__ :NORMALIZATION_FORMS__

  def __normalization_form__(form)
    name = NORMALIZATION_FORMS__[form]
    ::Kernel.raise(::ArgumentError, "Invalid normalization form #{form}.") unless name
    ::System::Text::NormalizationForm.__send__(name)
  end
  private :__normalization_form__

  # The other encodings of Unicode, which MRI's unicode_normalize reads through UTF-8.
  TRANSCODED_UNICODE_ENCODINGS__ = [
    ::Encoding::UTF_16BE, ::Encoding::UTF_16LE, ::Encoding::UTF_32BE, ::Encoding::UTF_32LE, ::Encoding::GB18030
  ]
  __hide_constant__ :TRANSCODED_UNICODE_ENCODINGS__

  def __require_unicode__
    unless [::Encoding::UTF_8, ::Encoding::US_ASCII].include?(encoding) || TRANSCODED_UNICODE_ENCODINGS__.include?(encoding)
      ::Kernel.raise(::Encoding::CompatibilityError, "Unicode Normalization not appropriate for #{encoding}")
    end
    unless valid_encoding?
      ::Kernel.raise(::ArgumentError, "invalid byte sequence in #{encoding}")
    end
  end
  private :__require_unicode__

  def unicode_normalize(form = :nfc)
    __require_unicode__
    if TRANSCODED_UNICODE_ENCODINGS__.include?(encoding)
      return encode(::Encoding::UTF_8).unicode_normalize(form).encode(encoding)
    end
    result = to_clr_string.Normalize(__normalization_form__(form)).to_s
    result.force_encoding(encoding) if result.respond_to?(:force_encoding)
    result
  end unless method_defined?(:unicode_normalize)

  def unicode_normalize!(form = :nfc)
    replace(unicode_normalize(form))
  end unless method_defined?(:unicode_normalize!)

  def unicode_normalized?(form = :nfc)
    __require_unicode__
    return encode(::Encoding::UTF_8).unicode_normalized?(form) if TRANSCODED_UNICODE_ENCODINGS__.include?(encoding)
    to_clr_string.IsNormalized(__normalization_form__(form))
  end unless method_defined?(:unicode_normalized?)

  def each_grapheme_cluster(&block)
    return to_enum(:each_grapheme_cluster) { __ir_grapheme_clusters__.size } unless block
    clusters = __ir_grapheme_clusters__
    clusters.each { |g| block.call(g) }
    self
  end unless method_defined?(:each_grapheme_cluster)

  # MRI still yields when a block is passed, answering self rather than the array.
  def grapheme_clusters(&block)
    return __ir_grapheme_clusters__ unless block
    __ir_grapheme_clusters__.each { |g| block.call(g) }
    self
  end unless method_defined?(:grapheme_clusters)

  UNICODE_ENCODINGS__ = [
    ::Encoding::UTF_8, ::Encoding::US_ASCII, ::Encoding::UTF_16LE, ::Encoding::UTF_16BE,
    ::Encoding::UTF_32LE, ::Encoding::UTF_32BE
  ]
  __hide_constant__ :UNICODE_ENCODINGS__

  # Segmentation is the CLR's, which speaks UTF-16. Anything that is not a
  # Unicode encoding - BINARY, the dummy encodings, the legacy code pages -
  # has no clusters to find, so MRI's answer there is one element per
  # character.
  def __ir_grapheme_clusters__
    return chars unless valid_encoding? && UNICODE_ENCODINGS__.include?(encoding)
    source = (encoding == ::Encoding::UTF_8 || encoding == ::Encoding::US_ASCII) ? self : encode(::Encoding::UTF_8)
    result = []
    e = ::System::Globalization::StringInfo.GetTextElementEnumerator(source.to_clr_string)
    while e.MoveNext
      piece = e.GetTextElement.to_s
      piece.force_encoding(::Encoding::UTF_8) if piece.respond_to?(:force_encoding)
      piece = piece.encode(encoding) unless source.equal?(self)
      result << piece
    end
    result
  end
  private :__ir_grapheme_clusters__

end

# MRI defines the module at startup and fills it in from unicode_normalize/normalize.rb the
# first time a string is normalized; IronRuby normalizes with the CLR, so it stays empty
# unless that file is required.
module UnicodeNormalize
end

# Numeric#step: MRI's num_step, which the builtin only ever implemented as the
# two-positional-argument block form. Missing were the keyword form
# (`1.step(by: 2, to: 7)`), the endless form (`1.step`), the
# Enumerator::ArithmeticSequence return value, and the float/infinity corners.
class Numeric
  # MRI num_step_extract_args: positional (to, step) merged with the `to:` and
  # `by:` keywords, which collide rather than override.
  def __step_extract_args__(args, kw)
    if args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..2)")
    end
    to = args.size >= 1 ? args[0] : nil
    by = args.size >= 2 ? args[1] : nil
    unless kw.nil? || kw.empty?
      unknown = kw.keys - [:to, :by]
      unless unknown.empty?
        ::Kernel.raise(::ArgumentError, "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.map(&:inspect).join(', ')}")
      end
      if kw.key?(:to)
        ::Kernel.raise(::ArgumentError, "to is given twice") if args.size > 0
        to = kw[:to]
      end
      if kw.key?(:by)
        ::Kernel.raise(::ArgumentError, "step is given twice") if args.size > 1
        by = kw[:by]
      end
    end
    [to, by]
  end
  private :__step_extract_args__

  # MRI num_step_scan_args. The `unit < 0` is not incidental: it is what turns
  # a String step into ArgumentError("comparison of String with 0 failed"),
  # which is the error the specs ask for.
  def __step_scan_args__(args, kw)
    to, unit = __step_extract_args__(args, kw)
    unit = 1 if unit.nil?
    ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
    # Ordering it against 0 is what rejects a step that cannot be compared at all:
    # a String raises ArgumentError("comparison of String with 0 failed") here. The
    # result is deliberately discarded - assigned so that -w does not flag it.
    _ = unit < 0
    [to, unit]
  end
  private :__step_scan_args__

  # Numeric#step. Numeric#step (Numeric.cs) loops over Floats itself, Integer#step
  # (Integer.cs) over Fixnums, and both come here with everything else: the positional
  # arguments as an Array, the keywords as a Hash or nil.
  def __ir_step_slow__(args, kw, &block)
    kw ||= {}
    unless block
      to, unit = __step_extract_args__(args, kw)
      unit = 1 if unit.nil?
      ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
      if (to.nil? || to.is_a?(::Numeric)) && unit.is_a?(::Numeric)
        return ::Enumerator::ArithmeticSequence.__build__(self, to, unit, false, self, [:num, args, kw])
      end
      recv = self
      e = ::Enumerator.new { |y| recv.step(*args, **kw) { |v| y << v } }
      e.__set_inspect__(recv, :step, kw.empty? ? args : args + [kw])
      e.__set_size__(lambda do
        t, u = recv.__send__(:__step_scan_args__, args, kw)
        t = (u < 0 ? -::Float::INFINITY : ::Float::INFINITY) if t.nil?
        ::Enumerator::ArithmeticSequence.__interval_step_size__(recv, t, u, false)
      end)
      return e
    end

    to, unit = __step_scan_args__(args, kw)
    ::Enumerator::ArithmeticSequence.__step_each__(self, to, unit, false, &block)
    self
  end
  private :__ir_step_slow__
end

class Numeric
  def fdiv(other)
    to_f / other
  end unless method_defined?(:fdiv)

  alias :magnitude :abs unless method_defined?(:magnitude)

  # #imag and #imaginary are one method in MRI, and so are Float#quo and Float#fdiv.
  alias :imaginary :imag if method_defined?(:imag)
end

# Float has its own #fdiv (one method with #quo) and sign predicates in MRI; Integer has
# Numeric's.
class Float
  def fdiv(other)
    self / other
  end
  alias :quo :fdiv

  def positive?
    self > 0
  end

  def negative?
    self < 0
  end
end

class Float
  # The neighbouring representable Floats. The CLR walks the IEEE bit pattern
  # for us; MRI's next_float of +Infinity is +Infinity, where BitIncrement
  # answers NaN, so the ends are special-cased.
  def next_float
    return ::Float::NAN if nan?
    return self if self == ::Float::INFINITY
    ::System::Math.BitIncrement(self)
  end unless method_defined?(:next_float)

  def prev_float
    return ::Float::NAN if nan?
    return self if self == -::Float::INFINITY
    ::System::Math.BitDecrement(self)
  end unless method_defined?(:prev_float)

end

class Symbol
  include ::Comparable unless ancestors.include?(::Comparable)

  def casecmp?(other)
    return nil unless other.is_a?(::Symbol)
    to_s.casecmp?(other.to_s)
  end unless method_defined?(:casecmp?)

end

# The complex-number half of Numeric.  IronRuby's Complex has these, but the
# real numerics never got them, so anything written against the Numeric
# protocol (Matrix, Vector, rationalisation code) breaks on a plain Integer.
class Numeric
  def real?
    true
  end unless method_defined?(:real?)

  def real
    self
  end unless method_defined?(:real)

  def imaginary
    0
  end unless method_defined?(:imaginary)
  alias :imag :imaginary unless method_defined?(:imag)

  def conjugate
    self
  end unless method_defined?(:conjugate)
  alias :conj :conjugate unless method_defined?(:conj)

  def finite?
    true
  end unless method_defined?(:finite?)

  def infinite?
    nil
  end unless method_defined?(:infinite?)

  def positive?
    self > 0
  end unless method_defined?(:positive?)

  def negative?
    self < 0
  end unless method_defined?(:negative?)

end

class KeyError < IndexError; end unless defined?(KeyError)
class StopIteration < IndexError; end unless defined?(StopIteration)
class UncaughtThrowError < ArgumentError; end unless defined?(UncaughtThrowError)

class UncaughtThrowError
  # UncaughtThrowError.new(tag, value, message = nil): the message is a format
  # string for the tag, as MRI's uncaught_throw_to_s applies it (nil included,
  # which is a TypeError there).
  def to_s
    return super unless __ir_formats_tag__
    ::Kernel.format(__ir_has_message__ ? super : nil, tag)
  end
end
class ClosedQueueError < StopIteration; end unless defined?(ClosedQueueError)

class << Dir
  # Dir.home / Dir.children / Dir.each_child / Dir.empty? postdate the 1.9 core.
  def home(user = nil)
    if user.nil?
      dir = ENV['HOME']
      if dir.nil? && File::ALT_SEPARATOR
        # MRI's rb_w32_home_dir: HOME, then USERPROFILE, then HOMEDRIVE + HOMEPATH, then the
        # profile folder Windows reports.
        dir = ENV['USERPROFILE']
        dir ||= ENV['HOMEDRIVE'] + ENV['HOMEPATH'] if ENV['HOMEDRIVE'] && ENV['HOMEPATH']
        dir ||= System::Environment.GetFolderPath(System::Environment::SpecialFolder.UserProfile).to_s
        dir = nil if dir.empty?
      end
      unless dir
        require 'etc'
        pw = (Etc.getpwuid(Process.uid) rescue nil)
        dir = pw && pw.dir
      end
      unless dir
        # MRI names the uid the user database had no entry for; Windows has no user database.
        raise ArgumentError, File::ALT_SEPARATOR ? "couldn't find HOME environment -- expanding '~'" :
          "couldn't find home for uid '#{Process.uid}'"
      end
      # MRI on Windows: the home directory is written with forward slashes, like every path it
      # makes, whatever HOME holds ("C:\Users\x" from Git Bash, for one). ENV keeps its value.
      return File::ALT_SEPARATOR ? dir.tr("\\", "/") : dir.dup
    end

    raise TypeError, "no implicit conversion of #{user.class} into String" unless user.is_a?(String)
    require 'etc'
    pw = (Etc.getpwnam(user) rescue nil)
    raise ArgumentError, "user #{user} doesn't exist" unless pw
    pw.dir.dup
  end unless respond_to?(:home)

  # encoding: is their only option, a keyword: Dir.children("a", "b") is "given 2, expected 1"
  def children(path, **opts)
    entries(path, **opts) - %w[. ..]
  end unless respond_to?(:children)

  # MRI's Dir.each_child / Dir.foreach enumerators do not know their size.
  def each_child(path, *args, **opts, &block)
    return to_enum(:each_child, path, *args, **opts) { nil } unless block
    children(path, *args, **opts).each(&block)
    nil
  end

  unless respond_to?(:__clr_foreach__, true)
    alias :__clr_foreach__ :foreach
    private :__clr_foreach__

    def foreach(path, *args, **opts, &block)
      return to_enum(:foreach, path, *args, **opts) { nil } unless block
      __clr_foreach__(path, *args, **opts, &block)
      nil
    end
  end

  def empty?(path)
    # File.stat rather than File.directory? so that a missing path is an ENOENT
    return false unless File.stat(path).directory?
    entries(path).size <= 2
  end unless respond_to?(:empty?)
end

# The instance side of Dir is a subset of the singleton side; MRI gives it #children,
# #each_child and #chdir, and an Enumerator from #each when no block is given.
class Dir
  unless method_defined?(:__clr_each__)
    alias :__clr_each__ :each
    private :__clr_each__

    def each(&block)
      return to_enum(:each) { nil } unless block
      __clr_each__(&block)
      self
    end
  end

  def children
    entries - %w[. ..]
  end unless method_defined?(:children)

  def each_child(&block)
    return to_enum(:each_child) { nil } unless block
    children.each(&block)
    self
  end unless method_defined?(:each_child)

  # MRI changes to the open directory itself (fchdir), not to its path, where there are
  # descriptors to do it with.
  def chdir(&block)
    File::ALT_SEPARATOR ? Dir.chdir(path, &block) : Dir.fchdir(fileno, &block)
  end unless method_defined?(:chdir)
end

module Errno
  # errno => [name, strerror] for this platform, and errno => class. Both are made from
  # IronRuby::Runtime::RubyErrno (C#), which the CLR-backed classes read their messages
  # from too, so a number and its text are the ones MRI has on the same platform. This
  # binds the names the C# side registers no class for, gives the C#-registered classes
  # their Errno constant -- they are CLR-backed and carry no number, so without the
  # constant Errno::ENOENT::Errno would resolve up to the Errno module itself -- and
  # supplies the default message for SystemCallError#initialize below. Each class keeps
  # its strerror text in a hidden @__message__ (RubyExceptionData reads it too). MRI has
  # no such constants, so the tables are hidden ivars of the module, read through
  # Errno.__errno_table__ and Errno.__errno_classes__.
  @__errno_table__ = {}
  @__errno_classes__ = {}
  class << self
    def __errno_table__ = @__errno_table__
    def __errno_classes__ = @__errno_classes__
  end

  # Every class below is a new receiver, so a call like klass.const_set(...) would bind
  # a rule of its own for each class - about 150 of them, for calls made once. Module's
  # own methods called through bind_call share one rule. Nothing can have redefined
  # them on these classes yet: this runs before any program code.
  const_set_on = ::Module.instance_method(:const_set)
  const_defined_on = ::Module.instance_method(:const_defined?)
  ivar_set_on = ::Kernel.instance_method(:__ir_set_hidden_ivar__)

  missing = []
  __ir_errno_table__.each do |name, errno, message|
    if errno.nil?
      missing << name
      next
    end
    if (klass = @__errno_classes__[errno])
      # The second name for a number is the first name's class: MRI's EWOULDBLOCK *is*
      # EAGAIN where the numbers agree (and EDEADLOCK is EDEADLK, EOPNOTSUPP is ENOTSUP).
      remove_const(name) if const_defined?(name, false)
      const_set(name, klass)
      next
    end
    @__errno_table__[errno] = [name, message]
    if const_defined?(name, false)
      klass = const_get(name)
      # const_defined? without the second argument would find the enclosing Errno
      # module through the lexical scope, so restrict the lookup to the class.
      const_set_on.bind_call(klass, :Errno, errno) unless const_defined_on.bind_call(klass, :Errno, false)
      ivar_set_on.bind_call(klass, :@__message__, message)
    else
      klass = Class.new(SystemCallError)
      const_set(name, klass)
      # A class made just now has no constants of its own, so there is nothing to ask it
      # first - and asking is not free: every class is a new receiver for these call
      # sites, so each question binds a rule of its own. So would const_set; constant
      # assignment is no method call, and binds nothing.
      klass::Errno = errno
      ivar_set_on.bind_call(klass, :@__message__, message)
    end
    @__errno_classes__[errno] = klass
  end

  # A name this platform has no such error for (EAUTH on Linux) is NOERROR, as in MRI.
  missing.each { |name| const_set(name, NOERROR) unless const_defined?(name, false) }

  @__errno_table__.freeze
  @__errno_classes__.freeze
end

# SystemCallError's constructor understood exactly one argument: either a message
# or an errno, never both, never a location, and it never picked the matching
# Errno subclass. MRI's shape is
#
#   SystemCallError.new(errno)
#   SystemCallError.new(message, errno)
#   SystemCallError.new(message, errno, location)
#   Errno::EINVAL.new(message = nil, location = nil)
#
# and the message is "<strerror> @ <location> - <message>", with each part dropped
# when absent.
class SystemCallError
  # Both of these walk up to the Errno class the subclass came from and stop at
  # SystemCallError, which has neither constant.  const_defined? with inherit
  # left on would not do: on a class it also searches Object, where it would
  # find the Errno *module* and answer that.  A subclass of an Errno class is a
  # real thing - IO::EAGAINWaitReadable is one - and it describes itself the way
  # its parent does rather than as an unknown error.
  def self.__errno_of__(klass)
    while klass && klass < ::SystemCallError
      return klass.const_get(:Errno) if klass.const_defined?(:Errno, false)
      klass = klass.superclass
    end
    nil
  end

  # MRI's strerror(errno): an Errno class keeps its own text (IO::EAGAINWaitReadable
  # inherits it), and any other subclass whose Errno names a known number - including
  # one that sets Errno itself - says what strerror says for it.
  def self.__default_message__(klass, errno)
    while klass && klass < ::SystemCallError
      message = klass.__send__(:__ir_hidden_ivar__, :@__message__)
      return message if message
      klass = klass.superclass
    end
    return "unknown error" if errno.nil?
    entry = ::Errno.__errno_table__[errno]
    entry ? entry[1] : "Unknown error #{errno}"
  end

  # MRI converts the errno with Integer(), so a Float or an exactly-real Complex
  # is truncated and a String is a TypeError.
  def self.__coerce_errno__(errno)
    return nil if errno.nil?
    return errno if errno.is_a?(::Integer)
    if errno.is_a?(::Float)
      ::Kernel.raise(::FloatDomainError, errno.to_s) if errno.nan? || errno.infinite?
      return errno.truncate
    end
    if defined?(::Complex) && errno.is_a?(::Complex)
      unless errno.imaginary == 0
        ::Kernel.raise(::RangeError, "can't convert #{errno} into Integer")
      end
      return __coerce_errno__(errno.real)
    end
    unless errno.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{errno.class} into Integer")
    end
    errno.to_int
  end

  def self.__coerce_message__(message)
    return nil if message.nil?
    return message if message.is_a?(::String)
    unless message.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{message.class} into String")
    end
    message.to_str
  end

  # The location is shown only with a message, as in MRI.
  def self.__build_message__(default, message, location)
    text = default.dup
    unless message.nil?
      text << " @ " << location.to_s unless location.nil?
      text << " - " << message
    end
    text
  end

  def self.new(*args)
    if equal?(::SystemCallError) && !args.empty?
      if args.size == 1 && !args[0].is_a?(::String) && !args[0].respond_to?(:to_str)
        message, errno = nil, __coerce_errno__(args[0])
      else
        message, errno = __coerce_message__(args[0]), __coerce_errno__(args[1])
      end
      # A known errno names a subclass; an unknown one stays a plain
      # SystemCallError that remembers the number for #errno.
      klass = ::Errno.__errno_classes__[errno]
      return klass.new(message, args[2]) if klass
      return super(message, errno, args[2])
    end
    super
  end

  def initialize(*args)
    klass = self.class
    if klass.equal?(::SystemCallError)
      if args.empty?
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 0, expected 1..3)")
      end
      if args.size == 1 && !args[0].is_a?(::String) && !args[0].respond_to?(:to_str)
        message, location, errno = nil, nil, ::SystemCallError.__coerce_errno__(args[0])
      else
        message = ::SystemCallError.__coerce_message__(args[0])
        location = args[2]
        errno = ::SystemCallError.__coerce_errno__(args[1])
      end
    else
      if args.size > 2
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..2)")
      end
      # MRI reads the number with rb_const_get(klass, :Errno), which a subclass that is
      # not under Errno answers with the Errno *module* - a TypeError, so such a class
      # cannot be instantiated unless it defines Errno itself.
      errno = ::SystemCallError.__errno_of__(klass)
      errno = ::SystemCallError.__coerce_errno__(klass.const_get(:Errno)) if errno.nil?
      message = ::SystemCallError.__coerce_message__(args[0])
      location = args[1]
    end

    @__errno__ = errno
    default = ::SystemCallError.__default_message__(klass, errno)
    super(::SystemCallError.__build_message__(default, message, location))
  end

  def errno
    defined?(@__errno__) ? @__errno__ : super
  end
end

class Array
  # ---- the sixteen Array methods spec/core/array gets NoMethodError for ----

  def __sample_index__(rng, limit)
    value = rng.rand(limit)
    value = value.to_int if value.respond_to?(:to_int) && !value.is_a?(::Integer)
    unless value.is_a?(::Integer)
      ::Kernel.raise(::NoMethodError, "undefined method 'to_int' for #{value.inspect}")
    end
    ::Kernel.raise(::RangeError, "random number too big #{value}") if value < 0 || value >= limit
    value
  end
  private :__sample_index__

  # Walks the n-fold product of the receiver's indices, optionally keeping only
  # the non-decreasing tuples, which is exactly repeated_combination.
  def __repeat__(n, sorted)
    if n == 0
      yield []
      return
    end
    return if empty?
    indices = ::Array.new(n, 0)
    loop do
      yield indices.map { |i| self[i] }
      k = n - 1
      k -= 1 while k >= 0 && indices[k] == size - 1
      return if k < 0
      indices[k] += 1
      ((k + 1)...n).each { |j| indices[j] = sorted ? indices[k] : 0 }
    end
  end
  private :__repeat__

  # MRI's rb_ary_cycle walks the array as it is on each pass, where Enumerable#cycle
  # yields what the first pass saw: `a.cycle { a.clear }` stops after one element
  # instead of cycling forever over a copy.
  def cycle(*args)
    unless block_given?
      enum = super(*args)
      return to_enum(:cycle, *args) { enum.size }
    end
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    n = args[0]
    if n.nil?
      n = -1
    else
      count = n
      n = ::Math.__send__(:__int__, count)
      unless n.is_a?(::Integer)
        ::Kernel.raise(::TypeError,
          "can't convert #{count.class} to Integer (#{count.class}#to_int gives #{__ir_conversion_result_name__(n)})")
      end
      return nil if n <= 0
    end
    while size > 0 && (n < 0 || (n -= 1) >= 0)
      i = 0
      while i < size
        yield self[i]
        i += 1
      end
    end
    nil
  end

  def to_h
    result = {}
    each_with_index do |pair, index|
      pair = yield(pair) if block_given?
      array = pair.respond_to?(:to_ary) ? pair.to_ary : nil
      unless array.is_a?(Array)
        raise TypeError, "wrong element type #{pair.class} at #{index} (expected array)"
      end
      unless array.size == 2
        raise ArgumentError, "wrong array length at #{index} (expected 2, was #{array.size})"
      end
      result[array[0]] = array[1]
    end
    result
  end

  # #uniq and #uniq! compare the block's result, not the element itself; the
  # core versions ignore the block entirely, so they are kept for the no-block
  # case and wrapped here.
  alias :__uniq_without_block__ :uniq
  alias :__uniq_in_place_without_block__ :uniq!

  def uniq(&block)
    return __uniq_without_block__ unless block
    seen = {}
    result = []
    each do |element|
      key = block.call(element)
      next if seen.key?(key)
      seen[key] = true
      result << element
    end
    result
  end

  def uniq!(&block)
    return __uniq_in_place_without_block__ unless block
    # The frozen check comes before the block runs.
    if frozen?
      ::Kernel.raise(::FrozenError.new("can't modify frozen Array: #{inspect}", receiver: self))
    end
    result = uniq(&block)
    return nil if result.size == size
    replace(result)
  end

  private :__uniq_without_block__, :__uniq_in_place_without_block__

  # #zip takes anything that responds to #each, not just arrays: an Enumerator
  # (including an infinite one, which is why this reads lazily and stops at the
  # receiver's size) or any object with #each.
  alias :__zip_arrays__ :zip

  def zip(*others, &block)
    length = size
    converted = others.map do |other|
      if other.is_a?(Array)
        other
      elsif other.respond_to?(:to_ary)
        other.to_ary
      elsif other.respond_to?(:each)
        collected = []
        # several yielded values make one element, as Enumerator#next would pack them
        other.each do |*element|
          break if collected.size >= length
          collected << (element.size <= 1 ? element[0] : element)
        end
        collected
      else
        raise TypeError, "wrong argument type #{__ir_message_type_name__(other)} (must respond to :each)"
      end
    end
    __zip_arrays__(*converted, &block)
  end

  private :__zip_arrays__
end

class String
  # A copy of the receiver as a plain String. Since Ruby 3.0 a method that derives a new
  # string answers with String even when the receiver is a subclass of it; only #dup, #clone
  # and #+@ carry the class over, and String.new here takes no encoding: keyword.
  def __ir_plain_copy__
    out = +""
    out.force_encoding(encoding) if out.respond_to?(:force_encoding)
    out << self
    out
  end
  private :__ir_plain_copy__

  def b
    __ir_plain_copy__.force_encoding(Encoding::BINARY)
  end unless method_defined?(:b)

  # Regexp#match? does the work: it matches from a character position with \A still at
  # the start of the string, and leaves $~ alone. A String pattern is a regexp source.
  def match?(pattern, pos = 0)
    pattern = ::Regexp.new(pattern) if pattern.is_a?(::String)
    unless pattern.is_a?(::Regexp)
      ::Kernel.raise(::TypeError, "wrong argument type #{pattern.nil? ? 'nil' : pattern.class} (expected Regexp)")
    end
    pattern.match?(self, pos)
  end unless method_defined?(:match?)

end

class Hash

  def to_h(&block)
    unless block
      # CRuby returns self for a plain Hash, and for a subclass a plain Hash that
      # keeps the default value / default proc / compare_by_identity flag.
      return self if instance_of?(Hash)
      result = __result_hash__
      each { |k, v| result[k] = v }
      if default_proc
        result.default_proc = default_proc
      else
        result.default = default
      end
      return result
    end
    result = {}
    each { |k, v| pair = __to_h_pair__(block.call(k, v)); result[pair[0]] = pair[1] }
    result
    # instance_methods(false) rather than method_defined?: Hash includes Enumerable,
    # whose #to_h is already defined by this point, so method_defined? is true and Hash
    # would keep a #to_h that builds a new plain Hash from pairs - losing self-identity,
    # the default, the default proc and compare_by_identity, and handing the block one
    # pair array instead of a key and a value.
  end unless instance_methods(false).include?(:to_h)

end

# The CLR collector answers most of what MRI's GC module reports; the rest are
# knobs that have no .NET equivalent and are kept as settings that round-trip.
module GC
  def self.count
    total = 0
    0.upto(::System::GC.max_generation) { |g| total += ::System::GC.collection_count(g) }
    total
  end

  def self.major_count
    ::System::GC.collection_count(::System::GC.max_generation)
  end
  private_class_method :major_count

  def self.__stat_values__
    live = ::System::GC.get_total_memory(false).to_i
    major = major_count
    {
      count: count,
      time: total_time / 1_000_000,
      marking_time: 0,
      sweeping_time: 0,
      minor_gc_count: count - major,
      major_gc_count: major,
      heap_allocated_pages: (live / 16_384) + 1,
      heap_live_slots: live / 40,
      heap_free_slots: 0,
      total_allocated_objects: live / 40,
      total_freed_objects: 0,
      malloc_increase_bytes: 0,
      compact_count: defined?(@compact_count) ? @compact_count : 0
    }
  end

  def self.stat(key_or_hash = nil)
    values = __stat_values__
    case key_or_hash
    when nil then values
    when ::Symbol
      unless values.key?(key_or_hash)
        ::Kernel.raise(::ArgumentError, "unknown key: #{key_or_hash}")
      end
      values[key_or_hash]
    when ::Hash
      values.each { |k, v| key_or_hash[k] = v }
      key_or_hash
    else
      ::Kernel.raise(::TypeError, "non-hash or symbol given")
    end
  end

  # MRI returns whether the collector *was* disabled before the call.
  def self.enable
    was = defined?(@disabled) && @disabled
    @disabled = false
    !!was
  end

  def self.disable
    was = defined?(@disabled) && @disabled
    @disabled = true
    !!was
  end

  def self.start(full_mark: true, immediate_mark: true, immediate_sweep: true)
    ::System::GC.collect
    nil
  end

  def self.total_time
    ::System::GC.get_total_pause_duration.total_milliseconds.to_i * 1_000_000
  rescue ::Exception
    0
  end

  def self.auto_compact
    defined?(@auto_compact) ? @auto_compact : false
  end

  def self.auto_compact=(value)
    @auto_compact = value
  end

  def self.measure_total_time
    defined?(@measure_total_time) ? @measure_total_time : true
  end

  def self.measure_total_time=(value)
    @measure_total_time = value
  end

  def self.stress
    defined?(@stress) ? @stress : false
  end

  def self.stress=(value)
    @stress = value
  end

  # The CLR compacts on its own; asking is a full collection. MRI answers what it moved,
  # which here is nothing it could tell apart.
  def self.compact
    ::System::GC.collect
    @compact_count = (defined?(@compact_count) ? @compact_count : 0) + 1
    latest_compact_info
  end

  # 3.4's pluggable-GC configuration. :implementation is read-only and global;
  # everything else is a per-implementation setting, and IronRuby has exactly one.
  def self.config(options = nil)
    @config ||= { rgengc_allow_full_mark: true }
    case options
    when nil then { implementation: "default" }.merge(@config)
    when ::Hash
      options.each do |key, value|
        if key.to_s.downcase == "implementation"
          ::Kernel.raise(::ArgumentError, 'Attempting to set read-only key "Implementation"')
        end
      end
      options.each { |key, value| @config[key] = value if @config.key?(key) }
      { implementation: "default" }.merge(@config)
    else
      ::Kernel.raise(::ArgumentError, "expecting a Hash, got #{options.class}")
    end
  end

  module Profiler
    def self.enabled?
      defined?(@enabled) ? @enabled : false
    end

    def self.enable
      @enabled = true
      nil
    end

    def self.disable
      @enabled = false
      nil
    end

    def self.clear
      nil
    end

    def self.total_time
      ::GC.total_time / 1_000_000_000.0
    end

    def self.result
      "GC #{::GC.count} invokes.\n"
    end

    def self.report(out = $stdout)
      out.write(result)
      nil
    end
  end
end

# Set and Pathname are core in Ruby 4.0 ("set.rb" and "pathname.so" are in $" from the
# start); here they are autoloaded from their files, by path, and Kernel#Pathname loads
# the class and hands over to the real one it defines. `require "set"` does nothing;
# `require "pathname"` loads pathname.rb (find, rmtree, mktmpdir) as it does in MRI.
autoload :Set, File.expand_path("set.rb", File.dirname(__FILE__)) unless defined?(Set)
unless defined?(Pathname)
  autoload :Pathname, File.expand_path("ironruby/pathname_builtin.rb", File.dirname(__FILE__))
  module Kernel
    def Pathname(path)
      ::Object.const_get(:Pathname) # loads pathname_builtin.rb, which redefines this method
      Pathname(path)
    end
    module_function :Pathname
    private :Pathname
  end
end

# --- Enumerator: the block form and the methods 1.9 never had --------------
# The core class only implements #each. Instances the runtime creates itself
# (`[1,2].each` with no block) must keep working, so #initialize and #each
# only divert when a generator block was supplied.
class Enumerator
  class Yielder
    def initialize(&block)
      ::Kernel.raise(::LocalJumpError, "no block given") unless block
      @block = block
    end

    # keywords given to #yield are the block's keywords (MRI's yielder_yield passes them on)
    ruby2_keywords def yield(*args)
      @block.call(*args)
    end

    # Unlike #yield, #<< answers the yielder so that it can be chained.
    def <<(value)
      @block.call(value)
      self
    end

    ruby2_keywords def call(*args)
      @block.call(*args)
    end

    # MRI's yielder has no instance variables to show.
    def inspect
      to_s
    end

    # Lets a yielder stand in for a block: `Enumerator.new { |y| ary.each(&y) }`.
    def to_proc
      yielder = self
      ::Kernel.proc { |*args| yielder.yield(*args) }
    end
  end unless const_defined?(:Yielder)

  unless private_method_defined?(:__ir_each_impl__)
    # The built-in #initialize lives on Enumerator itself, so redefining it here
    # hides it from `super`, which would find Object#initialize and silently
    # leave the enumerator with no target. __enum_init__ is the way back in.
    def initialize(*args, &block)
      # Ruby 3.0 removed Enumerator.new(obj, meth, *args); only the block form
      # Enumerator.new(size = nil) { |yielder| ... } is left (__enum_init__ is
      # still how the prelude builds one internally).  MRI asks for the block
      # before it counts the arguments.
      unless block
        ::Kernel.raise(::ArgumentError, "tried to create Proc object without a block")
      end
      if args.size > 1
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
      end
      @__generator__ = block
      @__size__ = ::Enumerator.__feasible_size__(args[0]) unless args.empty?
      @__initialized__ = true
      self
    end

    # convert_to_feasible_size_value: nil, a callable and Float::INFINITY stand,
    # anything else has to be an Integer.
    def self.__feasible_size__(value)
      return value if value.nil? || value.respond_to?(:call)
      return value if value.is_a?(::Float) && value == ::Float::INFINITY
      return value if value.is_a?(::Integer)
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{value.class} into Integer")
      end
      value.to_int
    end

    # Ruby 1.9: extra arguments are appended to the ones the enumerator was built
    # with and handed to the underlying method; without a block that produces a
    # new enumerator rather than iterating. #each itself is the built-in one, which takes the plain case (no arguments, no generator) straight to the target method
    # with the caller's frame - String#scan sets $~ there - and hands the rest to this.
    def __ir_each_impl__(*args, &block)
      unless args.empty?
        if @__generator__
          # Enumerator.new { |y, *extra| }: the arguments follow the yielder.  (Such an
          # enumerator still carries an empty built-in target, so this comes first.)
          generator = @__generator__
          size = @__size__
          unless block
            return ::Enumerator.new(size) { |y| generator.call(y, *args) }
              .__set_inspect__(nil, :each, args, "#<Enumerator::Generator:#{'0x%016x' % (generator.object_id * 2)}>")
          end
          return generator.call(Yielder.new(&block), *args)
        end
        target = __enum_target__
        if target
          receiver, method, initial = target
          arguments = initial + args
          return receiver.to_enum(method, *arguments) unless block
          return receiver.__send__(method, *arguments, &block)
        end
      end

      return self unless block
      if @__generator__
        # The generator block's value is what #each answers, and so what a finished
        # #next reports as StopIteration#result.
        @__generator__.call(Yielder.new(&block))
      else
        __each_without_generator__(&block)
      end
    end
    private :__ir_each_impl__
  end

  # nil means "no offset", and anything else has to answer #to_int - a Float
  # offset is truncated rather than counted up in halves.
  def __index_offset__(offset)
    return 0 if offset.nil?
    return offset if offset.is_a?(::Integer)
    unless offset.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{offset.class} into Integer")
    end
    converted = offset.to_int
    unless converted.is_a?(::Integer)
      ::Kernel.raise(::TypeError, "can't convert #{offset.class} to Integer")
    end
    converted
  end
  private :__index_offset__

  def with_index(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    offset = __index_offset__(args[0])
    unless block_given?
      # yield [value, index] pairs, lazily, via the generator form above
      source = self
      n = source.size
      return Enumerator.new(n) { |y|
        i = offset
        source.each { |*a| y.yield(a.size <= 1 ? a.first : a, i); i += 1 }
      }.__set_inspect__(self, :with_index, args)
    end
    i = offset
    each do |*args|
      value = args.size <= 1 ? args.first : args
      result = yield(value, i)
      i += 1
      result
    end
  end unless method_defined?(:with_index)

  # Enumerable's #each_with_index would win here and it answers self; MRI's
  # Enumerator#each_with_index is #with_index(0), which answers whatever the
  # underlying #each answered, and it takes no arguments at all.
  def each_with_index(*args, &block)
    unless args.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0)")
    end
    result = with_index(0, &block)
    result.__set_inspect__(self, :each_with_index) if !block && result.is_a?(::Enumerator)
    result
  end

  def with_object(memo)
    unless block_given?
      source = self
      n = source.size
      return Enumerator.new(n) { |y| source.each { |*a| y.yield(a.size <= 1 ? a.first : a, memo) } }
        .__set_inspect__(self, :with_object, [memo])
    end
    each do |*args|
      yield(args.size <= 1 ? args.first : args, memo)
    end
    memo
  end unless method_defined?(:with_object)
  # Same story as #each_with_index: Enumerable's version would shadow this one,
  # and the specs check that the two really are the same method.
  alias :each_with_object :with_object

  # --- #size -----------------------------------------------------------
  # MRI gives every enumerator a size function: an Integer, a Proc, or nil when
  # the length cannot be known without iterating. Three things supply one here -
  # `Enumerator.new(size) { }`, the block form of `to_enum`, and the descriptor
  # the C# builtins record - and everything else is honestly nil.
  def __set_size__(value)
    @__size__ = value
    self
  end

  def size
    n = @__size__
    return n.call if !n.is_a?(::Numeric) && n.respond_to?(:call)
    return n unless n.nil?
    info = __enum_size_info__
    return __size_from_info__(info[0], info[1], info[2]) if info
    target = __enum_target__
    return __size_from_target__(target[0], target[1], target[2]) if target && !@__generator__
    nil
  end

  # Number of items `source` yields, when that is knowable without iterating.
  def __source_count__(source)
    return nil if source.nil?
    if source.is_a?(::Range) || source.respond_to?(:size)
      n = source.size
      return n if n.is_a?(::Numeric)
      nil
    elsif source.respond_to?(:length)
      n = source.length
      n.is_a?(::Numeric) ? n : nil
    end
  end
  private :__source_count__

  # n * (n-1) * ... * (n-k+1), which is 1 when k is 0.
  def __enum_falling_factorial__(n, k)
    result = 1
    k.times { |i| result *= (n - i) }
    result
  end
  private :__enum_falling_factorial__

  def __size_from_info__(source, op, arg)
    case op
    when :same
      __source_count__(source)
    when :bytesize
      # each_byte counts bytes, which is not what the string's own #size answers.
      source.bytesize
    when :none
      # Knowably unknown: a stream does not say how much of it is left, and MRI's
      # answer for such an enumerator is nil rather than a guess from the source.
      nil
    when :self
      source
    when :slice
      n = __source_count__(source)
      n && arg > 0 ? (n + arg - 1) / arg : nil
    when :cons
      n = __source_count__(source)
      n ? (n - arg + 1 < 0 ? 0 : n - arg + 1) : nil
    when :cycle
      __cycle_size__(__source_count__(source), arg)
    when :combination, :permutation
      # Arithmetic, not iteration: C(n, k) and n!/(n-k)!, and zero when k elements
      # cannot be taken out of n at all.
      n = __source_count__(source)
      if n
        k = arg.nil? ? n : arg
        if k < 0 || k > n
          0
        elsif op == :combination
          __enum_falling_factorial__(n, k) / __enum_falling_factorial__(k, k)
        else
          __enum_falling_factorial__(n, k)
        end
      end
    when :upto
      arg < source ? 0 : arg - source + 1
    when :downto
      source < arg ? 0 : source - arg + 1
    end
  end
  private :__size_from_info__

  # `cycle` repeats forever unless a count is given, and an empty source never
  # yields at all, so its size is 0 rather than infinite.
  def __cycle_size__(count, times)
    return nil if count.nil?
    return 0 if count == 0
    return ::Float::INFINITY if times.nil?
    times <= 0 ? 0 : count * times
  end
  private :__cycle_size__

  # `to_enum(:each_slice, 2)` and friends: the descriptor is the method name.
  SIZE_SAME_METHODS = [
    :each, :each_entry, :each_pair, :each_key, :each_value, :each_index,
    :reverse_each, :map, :collect, :map!, :collect!, :flat_map, :collect_concat,
    :select, :filter, :find_all, :select!, :filter!, :reject, :reject!,
    :keep_if, :delete_if, :sort_by, :min_by, :max_by, :group_by, :partition,
    :each_with_index, :each_with_object, :each_char, :each_byte, :each_codepoint,
    :filter_map
  ].freeze
  __hide_constant__ :SIZE_SAME_METHODS
  # Not in the list, and so of unknown size, as in MRI: #each_line (the lines are
  # not counted until they are read), and the searches - #find_index, #find,
  # #detect, #take_while, #drop_while - which answer one result, not one per element.

  def __size_from_target__(source, meth, args)
    case meth
    when :each_slice
      n = __source_count__(source)
      n && args[0].to_i > 0 ? (n + args[0].to_i - 1) / args[0].to_i : nil
    when :each_cons
      n = __source_count__(source)
      n ? [n - args[0].to_i + 1, 0].max : nil
    when :times
      # a negative count yields nothing
      source.is_a?(::Integer) && source < 0 ? 0 : source
    when :upto
      # String#upto also lands here, and its length is not arithmetic.
      source.is_a?(::Integer) ? __int_step_size__(source, args[0], :>) : nil
    when :downto
      source.is_a?(::Integer) ? __int_step_size__(args[0], source, :<) : nil
    when :cycle
      __cycle_size__(__source_count__(source), args[0])
    else
      SIZE_SAME_METHODS.include?(meth) ? __source_count__(source) : nil
    end
  end
  private :__size_from_target__

  # ruby_num_interval_step_size with a step of 1: the comparison comes first,
  # so a stop Integer cannot compare with ("A", nil) is an ArgumentError; a
  # Float stop counts as ruby_float_step_size does.
  def __int_step_size__(from, to, cmp)
    if from.is_a?(::Float) || to.is_a?(::Float)
      n = (to - from).to_f
      return 0 if n < 0
      return (n + n * ::Float::EPSILON).floor + 1
    end
    stop = cmp == :> ? to : from
    start = cmp == :> ? from : to
    return 0 if start.__send__(cmp, stop)
    (to - from).div(1) + 1
  end
  private :__int_step_size__

  # What #inspect shows for an enumerator the prelude built from a generator
  # (with_index, a lazy chain, produce): the receiver, the method and its
  # arguments, as MRI shows them.  recv_text stands in for recv.inspect.
  def __set_inspect__(recv, meth, args = [], recv_text = nil)
    @__inspect__ = [recv, meth, args, recv_text]
    self
  end

  def __inspect_detail__(recv, meth, args, recv_text = nil)
    text = recv_text || recv.inspect
    return text if meth.nil?
    text = "#{text}:#{meth}"
    return text if args.nil? || args.empty?
    # inspect_enumerator: a non-empty Hash with only Symbol keys at the end is shown as
    # keywords, whether or not it was passed as such, each key by its bare name.
    last = args[-1]
    if ::Hash === last && !last.empty? && last.each_key.all?(::Symbol)
      args = args[0...-1]
      kwds = last
    end
    parts = args.map { |a| a.inspect }
    kwds&.each { |k, v| parts << "#{k.name}: #{v.inspect}" }
    "#{text}(#{parts.join(', ')})"
  end
  private :__inspect_detail__

  def inspect
    if (info = @__inspect__)
      return "#<#{self.class}: #{__inspect_detail__(*info)}>"
    end
    target = (@__generator__ ? nil : __enum_target__)
    unless target
      if @__generator__
        return "#<#{self.class}: #<Enumerator::Generator:#{'0x%016x' % (@__generator__.object_id * 2)}>:each>"
      end
      # A C#-built enumerator over a delegate keeps only its size descriptor,
      # which for these names the method it came from.
      info = __enum_size_info__
      if info && [:cycle, :permutation, :combination].include?(info[1])
        return "#<#{self.class}: #{__inspect_detail__(info[0], info[1], info[2].nil? ? [] : [info[2]])}>"
      end
      return "#<#{self.class}: ...>"
    end
    recv, meth, args = target
    # Enumerator.allocate never got a receiver.
    return "#<#{self.class}: uninitialized>" if recv.nil? && meth == :each && args.empty?
    "#<#{self.class}: #{__inspect_detail__(recv, meth, args)}>"
  end
  alias :to_s :inspect

  # --- external iteration ------------------------------------------------
  # #next, #peek, #next_values, #peek_values and #feed are C# (Enumerator.cs). They walk a
  # built-in method over one of its own receivers - Array#each, Hash#each_pair, Integer#times... -
  # with a cursor, and hand everything else to __iter_slow__ here, which suspends `each` half way
  # through in a fiber: a background thread plus a mailbox here. A native iteration source is
  # detached from the public enumerator, so dropping the latter can collect its fiber handle and
  # release the worker.
  def __iter_fiber__
    @__fiber__ ||= begin
      @__iter_done__ = false
      @__iter_started__ = false
      ::Enumerator.__iter_fiber_for__(__enum_iteration_source__)
    end
  end
  # A class-method scope has no reference to the public enumerator whose @__fiber__
  # owns the handle. Built-in sources are detached snapshots of its iteration target.
  def self.__iter_fiber_for__(source)
    ::Fiber.new do
      result = source.each { |*args| ::Fiber.yield([:y, args]) }
      [:done, result]
    end
  end
  private :__iter_fiber__

  # get_next_values: the end is raised once as such, and from then on as a new
  # StopIteration whose cause is that first one.
  def __iter_stop__
    error = ::StopIteration.new("iteration reached an end")
    error.__set_result__(@__iter_result__)
    first = @__iter_stop_exc__
    unless first
      @__iter_stop_exc__ = error
      ::Kernel.raise(error)
    end
    ::Kernel.raise(error, cause: first)
  end
  private :__iter_stop__

  # Pulls one more set of yielded values out of the fiber, unless one is already
  # waiting because of a #peek.
  def __iter_advance__
    return if @__peeked__
    __iter_stop__ if @__iter_done__
    fiber = __iter_fiber__
    fed = nil
    # A fed value is what the suspended yield returns, so it waits while the
    # fiber has not reached a yield yet.
    fed = __iter_take_feed__ if @__iter_started__
    @__iter_started__ = true
    begin
      tag, payload = fiber.resume(fed)
    rescue ::Exception
      # An exception ends that iteration; the next #next starts a fresh one.
      @__fiber__ = nil
      raise
    end
    if tag == :y
      @__peek__ = payload
      @__peeked__ = true
    else
      @__iter_done__ = true
      @__iter_result__ = payload
      @__fiber__ = nil
      __iter_take_feed__
      __iter_stop__
    end
  end
  private :__iter_advance__

  # The fiber's half of #next (op 0: the values of the next step, taken), #peek
  # (op 1: the same values, left for the next #next; the caller copies them) - and
  # the FiberError of a cursor another thread started (op 2).
  def __iter_slow__(op)
    ::Kernel.raise(::FiberError, "fiber called across threads") if op == 2
    __iter_advance__
    values = @__peek__
    if op == 0
      @__peeked__ = false
      @__peek__ = nil
    end
    values
  end
  private :__iter_slow__

  # MRI refuses to copy an enumerator whose external iteration (#next, #peek) is under
  # way; a fresh or finished one copies as a fresh enumerator.
  alias_method :__enum_initialize_copy__, :initialize_copy
  private :__enum_initialize_copy__

  def initialize_copy(other)
    if other.__send__(:__iter_in_progress__)
      ::Kernel.raise(::TypeError, "can't copy execution context")
    end
    __enum_initialize_copy__(other)
    __iter_forget__
    self
  end
  private :initialize_copy

  def __iter_forget__
    __iter_reset__
    @__fiber__ = nil
    @__iter_done__ = false
    @__iter_result__ = nil
    @__iter_stop_exc__ = nil
    @__iter_started__ = false
    @__peeked__ = false
    @__peek__ = nil
  end
  private :__iter_forget__

  def __iter_in_progress__
    # a built-in method walked by a cursor (Enumerator.cs) is under way just as a fiber is
    cursor = __iter_cursor_in_progress__
    return cursor unless cursor.nil?
    return false if @__iter_done__ || !(@__iter_started__ || @__peeked__)
    fiber = @__fiber__
    !fiber.nil? && fiber.alive?
  end
  private :__iter_in_progress__

  # enumerator_rewind: the receiver's #rewind (rb_check_funcall, which finds a private one
  # too), then the iteration is forgotten.
  def rewind
    if frozen?
      ::Kernel.raise(::FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self))
    end
    target = (@__generator__ ? nil : __enum_target__)
    receiver = target && target[0]
    # Unwind the abandoned fiber, so that its thread exits. That runs the ensure clauses of the
    # receiver's #each, which MRI never runs (it only forgets the fiber): before the receiver's
    # #rewind, so that an ensure cannot undo what #rewind sets up. The price is that the iteration
    # is forgotten even when #rewind raises, where MRI's would go on. An error one of those ensure
    # clauses raises does not stop the receiver's #rewind, and then comes out of this method, where MRI
    # has none to show: it cannot be told from a Thread#raise that reached this thread while the fiber
    # ran, which must not be lost.
    fiber = @__fiber__
    begin
      begin
        fiber.kill if fiber && fiber.alive?
      ensure
        receiver.__send__(:rewind) if receiver && receiver.respond_to?(:rewind, true)
      end
    ensure
      __iter_forget__
    end
    self
  end

  # --- Enumerator::Lazy --------------------------------------------------
  # Every lazy operation is a new Lazy whose generator pulls from the previous
  # one, so nothing runs until the chain is forced and an infinite source is
  # fine. The per-iteration state (counters, seen-sets) lives *inside* the
  # generator block, not in the closure that builds it, so forcing the same
  # lazy twice starts over instead of resuming where the last force stopped.
  class Lazy < Enumerator
    # `Enumerator::Lazy.new(obj, size = nil) { |yielder, *values| ... }`
    def initialize(obj, size = nil, &block)
      unless block
        ::Kernel.raise(::ArgumentError, "tried to call lazy new without a block")
      end
      @__generator__ = lambda { |y, *args| obj.each(*args) { |*values| block.call(y, *values) }; nil }
      @__size__ = size
      __set_inspect__(obj, :each)
      self
    end

    # Arguments to #each (and so to #force and #to_a) reach the source's #each,
    # as in MRI; a chained lazy's generator simply ignores them.
    def each(*args, &block)
      if block && @__deferred__
        # A blockless #chunk: the block given here is the one it was waiting for.
        return @__deferred__.call(block)
      end
      return super(&block) if args.empty?
      generator = @__generator__
      lazy = Lazy.__raw__(@__size__) { |y| generator.call(y, *args) }
      block ? lazy.each(&block) : lazy
    end

    # Enumerator#+ chains eagerly; a lazy receiver stays lazy, as in MRI:
    # (1..).lazy + [1] is an Enumerator::Lazy over an Enumerator::Chain.
    def +(other)
      Chain.new(self, other).lazy
    end

    # Builds a Lazy straight from a generator, bypassing #initialize.
    def self.__raw__(size = nil, &generator)
      lazy = allocate
      lazy.__lazy_init__(size, &generator)
      lazy
    end

    def __lazy_init__(size, &generator)
      @__generator__ = generator
      @__size__ = size
      self
    end

    # A lazy operation's #each answers nil (MRI's lazy_add_method), and so does
    # StopIteration#result at the end of its #next, whatever the source answers.
    def __chain__(size = nil, &generator)
      Lazy.__raw__(size) { |y, *args| generator.call(y, *args); nil }
    end
    private :__chain__

    def __need_block__(name, block)
      return if block
      ::Kernel.raise(::ArgumentError, "tried to call lazy #{name} without a block")
    end
    private :__need_block__

    def lazy
      self
    end

    # Back to an ordinary Enumerator over the same elements.
    def eager
      source = self
      ::Enumerator.new(@__size__) { |y| source.each { |*values| y.yield(*values) } }.__set_inspect__(self, :each)
    end

    def size
      value = @__size__
      return value.call if value.is_a?(::Proc) || value.is_a?(::Method)
      value
    end

    def inspect
      if (info = @__inspect__)
        return "#<#{self.class}: #{__inspect_detail__(*info)}>"
      end
      return "#<#{self.class}: uninitialized>" if @__generator__.nil?
      "#<#{self.class}: ...>"
    end
    alias :to_s :inspect

    def map(&block)
      __need_block__("map", block)
      source = self
      __chain__(size) { |y| source.each { |*values| y << block.call(*values) } }.__set_inspect__(self, __callee__)
    end
    alias :collect :map

    def flat_map(&block)
      __need_block__("flat_map", block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          result = block.call(*values)
          # MRI splices an Array (or anything with #to_ary) and anything that
          # answers both #each and #force (a nested Lazy), and yields anything
          # else whole, so `flat_map { |x| x }` over strings is not flattened.
          array = result.is_a?(::Array) ? result : (result.respond_to?(:to_ary) ? result.to_ary : nil)
          if array.is_a?(::Array)
            array.each { |item| y << item }
          elsif result.respond_to?(:each) && result.respond_to?(:force)
            result.each { |item| y << item }
          else
            y << result
          end
        end
      end.__set_inspect__(self, __callee__)
    end
    alias :collect_concat :flat_map

    def select(&block)
      __need_block__("select", block)
      source = self
      __chain__ { |y| source.each { |*values| item = __value__(values); y << item if block.call(item) } }
        .__set_inspect__(self, __callee__)
    end
    alias :filter :select
    alias :find_all :select

    def filter_map(&block)
      __need_block__("filter_map", block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          result = block.call(*values)
          y << result if result
        end
      end
    end

    def reject(&block)
      __need_block__("reject", block)
      source = self
      __chain__ { |y| source.each { |*values| item = __value__(values); y << item unless block.call(item) } }
    end

    # grep and grep_v are Kernel#__ir_lazy_grep__ / __ir_lazy_grep_v__, which hand this the
    # frame that called them: without a block a Regexp match lands in its $~.
    def __ir_lazy_grep_impl__(pattern, invert, caller_frame, block)
      source = self
      __chain__ do |y|
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          next if __ir_case_match__(pattern, value, block || caller_frame) == invert
          y << (block ? block.call(value) : value)
        end
      end.__set_inspect__(self, invert ? :grep_v : :grep, [pattern])
    end
    private :__ir_lazy_grep_impl__

    alias :grep :__ir_lazy_grep__
    alias :grep_v :__ir_lazy_grep_v__
    public :grep, :grep_v

    def compact
      source = self
      __chain__ do |y|
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          y << value unless value.nil?
        end
      end
    end

    def uniq(&block)
      source = self
      __chain__ do |y|
        seen = {}
        source.each do |*values|
          value = values.size <= 1 ? values[0] : values
          key = block ? block.call(value) : value
          next if seen.key?(key)
          seen[key] = true
          y << value
        end
      end
    end

    def take(n)
      n = __to_int__(n)
      ::Kernel.raise(::ArgumentError, "attempt to take negative size") if n < 0
      current = size
      new_size = current.nil? ? n : (current < n ? current : n)
      return __chain__(0) { |y| } if n == 0
      source = self
      __chain__(new_size) do |y|
        taken = 0
        tag = ::Object.new
        catch(tag) do
          source.each do |*values|
            y << __value__(values)
            taken += 1
            throw(tag) if taken >= n
          end
        end
      end
    end

    def take_while(&block)
      __need_block__("take_while", block)
      source = self
      __chain__ do |y|
        tag = ::Object.new
        catch(tag) do
          source.each do |*values|
            throw(tag) unless block.call(*values)
            y << __value__(values)
          end
        end
      end
    end

    def drop(n)
      n = __to_int__(n)
      ::Kernel.raise(::ArgumentError, "attempt to drop negative size") if n < 0
      current = size
      new_size = current.nil? ? nil : (current < n ? 0 : current - n)
      source = self
      __chain__(new_size) do |y|
        dropped = 0
        source.each do |*values|
          if dropped < n
            dropped += 1
          else
            y << __value__(values)
          end
        end
      end
    end

    def drop_while(&block)
      __need_block__("drop_while", block)
      source = self
      __chain__ do |y|
        dropping = true
        source.each do |*values|
          dropping = false if dropping && !block.call(*values)
          y << __value__(values) unless dropping
        end
      end
    end

    def with_index(offset = 0, &block)
      # nil means "no offset", same as Enumerator#with_index.
      offset = offset.nil? ? 0 : __to_int__(offset)
      source = self
      if block
        # the block sees (value, index); the values themselves go on unchanged
        __chain__(size) do |y|
          i = offset
          source.each do |*values|
            block.call(values.size <= 1 ? values[0] : values, i)
            y.yield(*values)
            i += 1
          end
        end
      else
        __chain__(size) do |y|
          i = offset
          source.each do |*values|
            y << [values.size <= 1 ? values[0] : values, i]
            i += 1
          end
        end
      end
    end

    def each_with_index(&block)
      with_index(0, &block)
    end

    def with_object(memo)
      source = self
      __chain__(size) do |y|
        source.each { |*values| y << [values.size <= 1 ? values[0] : values, memo] }
      end
    end

    # Enumerable's, and eager: it runs the pipeline and answers the memo.
    def each_with_object(memo, &block)
      return to_enum(:each_with_object, memo) { size } unless block
      each { |*values| block.call(values.size <= 1 ? values[0] : values, memo) }
      memo
    end

    def zip(*others, &block)
      # Only the block form falls back to the eager Enumerable#zip. Everything
      # else stays lazy, including arguments that are not Arrays: those are
      # iterated with #each, which is what lets an endless receiver be zipped
      # with a Range and cut short by #first.
      return eager.zip(*others, &block) if block

      sources = others.map do |other|
        if other.is_a?(::Array)
          other
        elsif other.respond_to?(:to_ary) && (converted = other.to_ary).is_a?(::Array)
          converted
        elsif other.respond_to?(:each)
          other.to_enum(:each)
        else
          ::Kernel.raise(::TypeError, "wrong argument type #{__ir_message_type_name__(other)} (must respond to :each)")
        end
      end

      source = self
      __chain__(size) do |y|
        index = 0
        source.each do |*values|
          row = [values.size <= 1 ? values[0] : values]
          sources.each do |other|
            row << if other.is_a?(::Array)
                     other[index]
                   else
                     begin
                       other.next
                     rescue ::StopIteration
                       nil
                     end
                   end
          end
          index += 1
          y << row
        end
      end
    end

    def __value__(values)
      values.size <= 1 ? values[0] : values
    end
    private :__value__

    # The grouping operations have to stay lazy too: inheriting the eager
    # Enumerable versions makes `(1..Float::INFINITY).lazy.chunk_while { }`
    # iterate forever instead of emitting each group as it closes.
    def chunk(&block)
      unless block
        # MRI answers `to_enum(:chunk)`: a Lazy whose #each block becomes the chunk block.
        source = self
        deferred = Lazy.__raw__ { |y| }
        deferred.__send__(:__ir_set_hidden_ivar__, :@__deferred__, lambda { |blk| source.chunk(&blk) })
        return deferred
      end
      source = self
      __chain__ do |y|
        key = nil
        group = nil
        source.each do |*values|
          item = __value__(values)
          k = block.call(item)
          if group && k == key
            group << item
          else
            y << [key, group] if group
            key = k
            group = [item]
          end
        end
        y << [key, group] if group
      end
    end

    def chunk_while(&block)
      __need_block__("chunk_while", block)
      source = self
      __chain__ do |y|
        group = nil
        previous = nil
        source.each do |*values|
          item = __value__(values)
          if group.nil?
            group = [item]
          elsif block.call(previous, item)
            group << item
          else
            y << group
            group = [item]
          end
          previous = item
        end
        y << group if group
      end
    end

    def slice_when(&block)
      __need_block__("slice_when", block)
      chunk_while { |a, b| !block.call(a, b) }
    end

    def slice_before(*args, &block)
      has_pattern = !args.empty?
      pattern = args[0]
      source = self
      __chain__ do |y|
        group = nil
        source.each do |*values|
          item = __value__(values)
          starts = has_pattern ? (pattern === item) : block.call(item)
          if group.nil?
            group = [item]
          elsif starts
            y << group
            group = [item]
          else
            group << item
          end
        end
        y << group if group
      end
    end

    def slice_after(*args, &block)
      has_pattern = !args.empty?
      pattern = args[0]
      source = self
      __chain__ do |y|
        group = []
        source.each do |*values|
          item = __value__(values)
          group << item
          if has_pattern ? (pattern === item) : block.call(item)
            y << group
            group = []
          end
        end
        y << group unless group.empty?
      end
    end

    # The version of a method that Lazy overrides, from whichever ancestor defines it -
    # what `super' would reach. nil when Lazy does not override it at all.
    def __eager_method__(name)
      ancestors = ::Enumerator::Lazy.ancestors
      return nil unless ::Enumerator::Lazy.instance_methods(false).include?(name)
      # Past Lazy itself (and whatever is prepended to it) to the eager definition.
      ancestors[(ancestors.index(::Enumerator::Lazy) + 1)..-1].each do |mod|
        return mod.instance_method(name) if mod.instance_methods(false).include?(name)
      end
      nil
    end
    private :__eager_method__

    # Staying lazy across to_enum is what keeps `lazy.to_enum(:each)` usable on
    # an infinite source.
    #
    # The method is the *eager* one where Lazy has overridden it, which is MRI's
    # lazy_use_super_method table: `lazy.to_enum(:with_index, 10)` has to enumerate pairs,
    # and Lazy#with_index given a block answers a lazy chain that yields the original
    # values and never runs on its own, so nothing would come out of it.
    def to_enum(method = :each, *args, &size_block)
      source = self
      eager = __eager_method__(method)
      Lazy.__raw__(size_block) do |y|
        if eager
          eager.bind(source).call(*args) { |*values| y.yield(*values) }
        else
          source.send(method, *args) { |*values| y.yield(*values) }
        end
      end.__set_inspect__(self, method, args)
    end
    alias :enum_for :to_enum

    def first(n = nil)
      if n.nil?
        result = nil
        tag = ::Object.new
        catch(tag) do
          each do |*values|
            result = values.size <= 1 ? values[0] : values
            throw(tag)
          end
        end
        return result
      end
      n = __to_int__(n)
      ::Kernel.raise(::ArgumentError, "attempt to take negative size") if n < 0
      result = []
      return result if n == 0
      tag = ::Object.new
      catch(tag) do
        each do |*values|
          result << (values.size <= 1 ? values[0] : values)
          throw(tag) if result.size >= n
        end
      end
      result
    end

    def force(*args)
      to_a(*args)
    end

    def to_a(*args)
      result = []
      each(*args) { |*values| result << (values.size <= 1 ? values[0] : values) }
      result
    end

    # Without a block these stay lazy (MRI's lazy_use_super_method); the eager
    # Enumerator is built first so that its argument checks and size still apply.
    def each_slice(n, &block)
      return super(n, &block) if block
      enum = super(n)
      to_enum(:each_slice, n) { enum.size }
    end

    def each_cons(n, &block)
      return super(n, &block) if block
      enum = super(n)
      to_enum(:each_cons, n) { enum.size }
    end

    def cycle(*args, &block)
      return super(*args, &block) if block
      enum = super(*args)
      to_enum(:cycle, *args) { enum.size }
    end

    def __to_int__(value)
      return value if value.is_a?(::Integer)
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{value.class} into Integer")
      end
      value.to_int
    end
    private :__to_int__
  end

  # Each lazy chain method records itself for #inspect:
  # #<Enumerator::Lazy: #<Enumerator::Lazy: 1..3>:map>.  grep and grep_v, which
  # need the caller's frame, record themselves in __ir_lazy_grep_impl__.
  module LazyInspect
    # map, flat_map and select record themselves: their aliases (collect, filter, ...)
    # have to stay the very same method.
    [:reject, :filter_map, :compact, :uniq, :take, :take_while, :drop, :drop_while, :with_index,
     :each_with_index, :with_object, :zip, :chunk, :chunk_while,
     :slice_when, :slice_before, :slice_after, :each_slice, :each_cons, :cycle].each do |name|
      define_method(name) do |*args, &block|
        result = super(*args, &block)
        if result.is_a?(::Enumerator::Lazy) && !result.equal?(self)
          result.__set_inspect__(self, name, name == :with_index && args.empty? ? [0] : args)
        end
        result
      end
    end
  end
  __hide_constant__ :LazyInspect
  Lazy.prepend(LazyInspect)

  # --- Enumerator::Chain -------------------------------------------------
  class Chain < Enumerator
    # MRI undefines external iteration for it (enumerator.c, Init_Enumerator).
    undef_method :next, :next_values, :peek, :peek_values, :feed
    def initialize(*enums)
      @__enums__ = enums
      @__iterated__ = 0
      self
    end

    def each(&block)
      return to_enum(:each) { size } unless block
      # #rewind only rewinds what was actually iterated, so how far the last
      # iteration got is recorded as it goes.
      @__iterated__ = 0
      @__enums__.each do |enum|
        @__iterated__ += 1
        enum.each { |*values| block.call(*values) }
      end
      self
    end

    def size
      total = 0
      @__enums__.each do |enum|
        n = enum.respond_to?(:size) ? enum.size : nil
        return nil unless n.is_a?(::Numeric)
        return n if n == ::Float::INFINITY
        total += n
      end
      total
    end

    def rewind
      iterated = @__iterated__ || 0
      @__enums__.first(iterated).reverse_each { |enum| enum.rewind if enum.respond_to?(:rewind) }
      self
    end

    def inspect
      return "#<Enumerator::Chain: uninitialized>" if @__enums__.nil?
      "#<Enumerator::Chain: #{@__enums__.inspect}>"
    end
    alias :to_s :inspect

    def +(other)
      Chain.new(self, other)
    end
  end

  # --- Enumerator::Product -----------------------------------------------
  # The cartesian product of its arguments, leftmost varying slowest.
  class Product < Enumerator
    # MRI undefines external iteration for it (enumerator.c, Init_Enumerator).
    undef_method :next, :next_values, :peek, :peek_values, :feed
    # MRI accepts anything here -- the arguments are only required to answer
    # #each_entry at the point #each actually walks them, which is what lets
    # Product wrap a plain Object that defines each_entry and nothing else.
    def initialize(*enums)
      __check_frozen__
      @__enums__ = enums
      self
    end

    def initialize_copy(other)
      return self if other.equal?(self)
      __check_frozen__
      unless other.instance_of?(self.class)
        ::Kernel.raise(::TypeError, "initialize_copy should take same class object")
      end
      enums = other.__enums_or_nil__
      ::Kernel.raise(::ArgumentError, "uninitialized product") if enums.nil?
      @__enums__ = enums
      self
    end

    def each(&block)
      return to_enum(:each) { size } unless block
      __product__(0, [], block)
      self
    end

    # Ruby 3.2 specifies #each_entry, not #each: an argument that only answers
    # each_entry has to work, and one that answers neither has to raise NoMethodError
    # rather than a TypeError from the constructor.
    def __product__(index, prefix, block)
      if index == @__enums__.size
        block.call(prefix.dup)
        return
      end
      @__enums__[index].each_entry do |*values|
        prefix.push(values.size <= 1 ? values[0] : values)
        __product__(index + 1, prefix, block)
        prefix.pop
      end
    end
    private :__product__

    protected def __enums_or_nil__
      defined?(@__enums__) ? @__enums__ : nil
    end

    # nil for any size MRI cannot use (missing, non-Integer, NaN); zero short-circuits
    # even when another enumerable has an unknown size, and an infinite size carries.
    def size
      total = 1
      unknown = false
      @__enums__.each do |enum|
        n = enum.respond_to?(:size) ? enum.size : nil
        if n.is_a?(::Integer)
          return 0 if n == 0
          total *= n
        elsif n.is_a?(::Float) && n.infinite?
          total *= n
        else
          unknown = true
        end
      end
      unknown ? nil : total
    end

    def rewind
      @__enums__.each { |enum| enum.rewind if enum.respond_to?(:rewind) }
      self
    end

    def inspect
      enums = __enums_or_nil__
      return "#<#{self.class}: uninitialized>" if enums.nil?

      seen = ::Thread.current[:__enumerator_product_inspect__] ||= []
      return "#<#{self.class}: ...>" if seen.any? { |o| o.equal?(self) }

      seen.push(self)
      begin
        "#<#{self.class}: #{enums.inspect}>"
      ensure
        seen.pop
        ::Thread.current[:__enumerator_product_inspect__] = nil if seen.empty?
      end
    end
    alias :to_s :inspect

    def __check_frozen__
      return unless frozen?
      ::Kernel.raise(::FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self))
    end
    private :__check_frozen__
  end

  def +(other)
    Chain.new(self, other)
  end unless method_defined?(:+)

  def self.product(*enums, **options, &block)
    unless options.empty?
      ::Kernel.raise(::ArgumentError, "unknown keyword#{'s' if options.size > 1}: #{options.keys.map { |k| k.inspect }.join(', ')}")
    end
    product = Product.new(*enums)
    return product unless block
    product.each(&block)
    nil
  end unless respond_to?(:product)

  # Enumerator.produce(initial = nil) { |previous| ... } - an endless sequence
  # built by feeding each value back into the block. StopIteration ends it.
  def self.produce(*args, **options, &block)
    ::Kernel.raise(::ArgumentError, "no block given") unless block
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    unknown = options.keys - [:size]
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError, "unknown keyword#{'s' if unknown.size > 1}: #{unknown.map { |k| k.inspect }.join(', ')}")
    end
    size = options.key?(:size) ? options[:size] : ::Float::INFINITY
    producer = Producer.allocate
    state = [!args.empty?, args[0], block].freeze
    Producer.__send__(:__states__)[producer] = state
    # The enumerator keeps both the producer (as its receiver) and the state alive; the map
    # holds them weakly.
    ::Enumerator.new(size) { |y| producer.__send__(:__each__, state) { |v| y << v } }
                .__set_inspect__(producer, :each)
  end unless respond_to?(:produce)

  # What Enumerator.produce enumerates (MRI's producer.c): #each feeds every value back into
  # the block, until the block raises StopIteration.
  class Producer
    STATES = ::ObjectSpace::WeakMap.new
    __hide_constant__ :STATES

    def self.__states__
      STATES
    end
    private_class_method :__states__

    def each(&block)
      state = STATES[self]
      ::Kernel.raise(::ArgumentError, "uninitialized producer") unless state
      __each__(state, &block)
    end

    private

    def __each__(state)
      has_initial, initial, block = state
      begin
        value = has_initial ? initial : block.call(nil)
      rescue ::StopIteration => e
        # the block may refuse to produce even a first value
        return e.result
      end
      loop do
        yield value
        value = block.call(value)
      end
    end
  end unless const_defined?(:Producer, false)
end

module Enumerable
  # Every Enumerable gets #to_set, not just Array: Hash, Struct, Range and
  # Enumerator are all asked for one by the specs.
  # The first argument is the Set class to build, not a Set constructor
  # argument: `to_set(MySet)` has to answer a MySet. Ruby 4.0 deprecated
  # passing it (prelude.rb warns, uplevel 1).
  def to_set(*args, &block)
    require 'set'
    klass = if args.empty?
      ::Set
    else
      warn "passing arguments to Enumerable#to_set is deprecated", uplevel: 1
      args.shift
    end
    klass.new(self, *args, &block)
  end unless method_defined?(:to_set)

  def chain(*others)
    ::Enumerator::Chain.new(self, *others)
  end unless method_defined?(:chain)
end

module Enumerable
  def lazy
    source = self
    n = (size if respond_to?(:size))
    n = nil unless n.is_a?(::Numeric)
    ::Enumerator::Lazy.__raw__(n) { |y, *args| source.each(*args) { |*values| y.yield(*values) } }
      .__set_inspect__(self, nil)
  end unless method_defined?(:lazy)
end

class StopIteration
  def __set_result__(value)
    @__result__ = value
    self
  end

  def result
    @__result__
  end
end

module Kernel
  # MRI defaults the method to :each and takes an optional block returning the
  # enumerator's #size.
  # The arguments are kept and handed to the method when the enumerator runs, keywords
  # included - `enum_for(:find, *paths, ignore_error: true)' has to reach #find as keywords.
  ruby2_keywords def to_enum(method = :each, *args, &size_block)
    # Not Enumerator.new: Ruby 3.0 removed its (obj, meth, *args) form, and the
    # class-level initializer is what to_enum used to reach through.
    enum = ::Enumerator.allocate
    enum.__send__(:__enum_init__, self, method, args)
    # the size block is called with the enumerator's arguments (MRI: enum_size)
    enum.__set_size__(args.empty? ? size_block : lambda { size_block.call(*args) }) if size_block
    enum
  end
  alias :enum_for :to_enum

  # The C# Kernel#loop does not know about StopIteration, which is what makes
  # `loop { e.next }` terminate instead of blowing up.
  def loop
    return to_enum(:loop) { ::Float::INFINITY } unless block_given?
    begin
      yield while true
    rescue ::StopIteration => stop
      stop.result
    end
  end
end

# String#each_line / String#lines.
#
# The C# implementation is frozen at Ruby 1.8: it has no `chomp:` keyword, its
# paragraph mode (separator "") keeps every newline of the run instead of the
# first two, it accepts separators that are not String-convertible, and it
# raises "string modified" when the block mutates the receiver (checked in 1.8,
# not checked at all from 1.9 on). Everything here is layered on top of the C#
# splitter so encodings and the String-instances-for-subclasses rule keep
# working; only the grouping and the trimming are redone.
class String
  alias :__ir_each_line__ :each_line

  def each_line(*args, chomp: (no_chomp = true; false), &block)
    # the enumerator shows the chomp: given, false and nil too, as MRI's does
    return to_enum(:each_line, *args, **(no_chomp ? {} : { chomp: chomp })) unless block
    lines = __ir_split_lines__(args, chomp)
    lines.each { |line| block.call(line) }
    self
  end

  # MRI's #lines still behaves exactly like #each_line when a block is passed:
  # it yields and answers self rather than the array.
  def lines(*args, chomp: false, &block)
    return each_line(*args, chomp: chomp, &block) if block
    __ir_split_lines__(args, chomp)
  end

  private

  def __ir_split_lines__(args, chomp)
    if args.size > 1
      raise ::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)"
    end
    separator = args.empty? ? $/ : args[0]
    unless separator.nil? || separator.is_a?(::String)
      unless separator.respond_to?(:to_str)
        raise ::TypeError, "no implicit conversion of #{__ir_type_name__(separator)} into String"
      end
      separator = separator.to_str
      unless separator.is_a?(::String)
        raise ::TypeError, "can't convert #{separator.class} to String"
      end
    end

    # An ASCII incompatible encoding (UTF-16, UTF-32): MRI transcodes $/, and
    # paragraph mode's "\n\n", into the receiver's encoding (an encoding with no
    # converter raises Encoding::ConverterNotFoundError), while any other
    # separator has to be compatible with the receiver as it is.
    newline = "\n"
    unless encoding.ascii_compatible? || separator.nil?
      if args.empty? || separator.empty?
        newline = newline.encode(encoding)
        separator = separator.encode(encoding)
      elsif !::Encoding.compatible?(self, separator)
        ::Kernel.raise(::Encoding::CompatibilityError,
          "incompatible character encodings: #{encoding} and #{separator.encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : separator.encoding}")
      end
    end

    return empty? ? [] : [__ir_chomp_line__(__ir_each_line_all__(nil), nil, chomp)] if separator.nil?

    # paragraph mode chomps as it splits
    return __ir_paragraphs__(__ir_each_line_all__(newline), newline, chomp) if separator.empty?
    lines = __ir_each_line_all__(separator)
    return lines unless chomp
    lines.map { |line| __ir_chomp_line__(line, separator, chomp) }
  end

  # Collects the physical lines without letting the 1.8 "string modified"
  # check fire: the block below never touches the receiver.
  def __ir_each_line_all__(separator)
    return dup if separator.nil?
    lines = []
    __ir_each_line__(separator) { |line| lines << line }
    lines
  end

  # Paragraph mode: a paragraph ends at the first blank line after some
  # content, that blank line is kept, and every further blank line of the run
  # is dropped. With chomp: a paragraph that ended so loses its last newline and
  # the blank line, and the last paragraph, which no blank line ended, is left as
  # it is, trailing newline and all (rb_str_enumerate_lines' chomp_rslen).
  def __ir_paragraphs__(physical, newline, chomp = false)
    result = []
    buffer = nil
    skipping = false
    crlf = newline.encoding.ascii_compatible? ? "\r\n" : "\r\n".encode(newline.encoding)
    physical.each do |line|
      blank = (line == newline || line == crlf)
      if skipping
        next if blank
        skipping = false
      end
      if blank && buffer
        if chomp
          result << buffer[0, buffer.length - (buffer.end_with?(crlf) ? crlf.length : newline.length)]
        else
          result << (buffer + line)
        end
        buffer = nil
        skipping = true
      elsif buffer
        buffer = buffer + line
      else
        buffer = line.dup
      end
    end
    result << buffer if buffer
    result
  end

  def __ir_chomp_line__(line, separator, chomp)
    return line unless chomp
    unless line.encoding.ascii_compatible?
      # MRI takes off the separator alone: "b\r\n" in UTF-16 chomps to "b\r"
      return line if separator.nil?
      if separator.empty?
        newline = "\n".encode(line.encoding)
        line = line[0...-1] while line.end_with?(newline)
        return line
      end
      return line.end_with?(separator) ? line[0, line.length - separator.length] : line
    end
    if separator.nil? || separator == "\n"
      line = line[0...-1] if line.end_with?("\n")
      line = line[0...-1] if line.end_with?("\r")
      line
    elsif separator.empty?
      line = line[0...-1] while line.end_with?("\n") || line.end_with?("\r")
      line
    elsif line.end_with?(separator)
      line[0, line.length - separator.length]
    else
      line
    end
  end

  public
end

# Mutex/Queue/SizedQueue/ConditionVariable needed `require "thread"` in 1.9; they
# have been built in since 2.0. Ruby 2.x also re-homed them under Thread: the
# library defines them there (Thread::Mutex is their name, as in MRI), and the
# top-level names are aliases.
require "thread" unless Thread.const_defined?(:Mutex, false)

%i[Mutex Queue SizedQueue ConditionVariable].each do |name|
  next if Object.const_defined?(name, false)
  Object.const_set(name, Thread.const_get(name, false)) if Thread.const_defined?(name, false)
end

# MatchData knew nothing about named groups - m[:name] was a TypeError about
# converting a Symbol into an Integer - and had none of the byte-offset or
# pattern-matching methods. The group names come from the CLR Match through the
# three plain CLR methods added to MatchData.cs; everything else is built on
# top of them and of #string, which is the subject the offsets refer to.
class MatchData
  alias :__ir_index__ :[]

  def [](*args)
    if args.size == 1
      key = args[0]
      if key.is_a?(::Symbol) || key.is_a?(::String)
        name = key.to_s
        unless HasNamedGroup(name)
          ::Kernel.raise(::IndexError, "undefined group name reference: #{name}")
        end
        return nil unless NamedGroupSuccess(name)
        # sliced by the CLR offsets it has, not through String#[], which counts characters
        return GetNamedGroupValue(name)
      end
      # The groups are a plain Array as far as a Range is concerned, down to
      # md[3..1] being [] while md[-30..2] is nil.
      return to_a[key] if key.is_a?(::Range)
    end
    __ir_index__(*args)
  end

  # The pattern's own Regexp#names - except that MRI matched a pattern of no fixed
  # encoding against a non-ASCII subject with a copy compiled for the subject's
  # encoding, and the names come out in that one.
  def names
    re = regexp
    return GetGroupNames().map { |n| n.to_s } unless re
    result = re.names
    unless re.fixed_encoding? || string.ascii_only?
      enc = string.encoding
      result = result.map { |n| n.dup.force_encoding(enc) }
    end
    result
  end unless method_defined?(:names)

  def named_captures(symbolize_names: false)
    result = {}
    names.each do |n|
      result[symbolize_names ? n.to_sym : n] = self[n]
    end
    result
  end unless method_defined?(:named_captures)

  # #deconstruct is not merely equivalent to #captures, it is the same method:
  # MatchData.instance_method(:deconstruct) == MatchData.instance_method(:captures).
  alias :deconstruct :captures unless method_defined?(:deconstruct)

  def deconstruct_keys(keys)
    all = names
    result = {}
    if keys.nil?
      all.each { |n| result[n.to_sym] = self[n] }
      return result
    end
    unless keys.is_a?(::Array)
      ::Kernel.raise(::TypeError, "wrong argument type #{keys.class} (expected Array)")
    end
    # More keys than there are groups cannot all be found, and MRI does not even
    # start looking - it answers {} rather than the prefix that does match.
    return result if keys.size > all.size
    keys.each do |k|
      unless k.is_a?(::Symbol)
        ::Kernel.raise(::TypeError, "wrong argument type #{k.class} (expected Symbol)")
      end
      return result unless all.include?(k.to_s)
      result[k] = self[k.to_s]
    end
    result
  end unless method_defined?(:deconstruct_keys)

  # Unlike #[], #match refuses an index or a name the match does not have.
  def match(n)
    if n.is_a?(::Integer)
      ::Kernel.raise(::IndexError, "index #{n} out of matches") if n < 0 || n >= size
      self[n]
    else
      name = n.to_s
      unless names.include?(name)
        ::Kernel.raise(::IndexError, "undefined group name reference: #{name}")
      end
      self[name]
    end
  end unless method_defined?(:match)

  # Equal matches (same string, pattern and offsets) hash alike.
  def hash
    [string, regexp, self.begin(0), self.end(0), to_a].hash
  end unless method_defined?(:hash) && instance_method(:hash).owner == self

  def match_length(n)
    m = match(n)
    m && m.length
  end unless method_defined?(:match_length)

  alias :__ir_values_at__ :values_at

  # Ranges, names and Integers can be mixed. Array#values_at already has the
  # Range rules - nil fill past the end, RangeError for a start that is negative
  # and out of range, [] for an empty Range - so the numeric part goes to it.
  # A single Integer is MRI's rb_reg_nth_match, as for #[]: counted back from the
  # end it never reaches group 0, so values_at(-1) of a groupless match is nil.
  def values_at(*indexes)
    groups = to_a
    indexes.flat_map do |i|
      (i.is_a?(::Symbol) || i.is_a?(::String) || i.is_a?(::Integer)) ? [self[i]] : groups.values_at(i)
    end
  end
end

# Integer#[] is C# (ClrInteger.Bit, Bits): an Integer bit index, and a Fixnum index and length
# on a Fixnum, are answered there. It hands every other form here, to ports of numeric.c's
# int_aref1 (one argument: a bit, or a Range) and int_aref2 (an offset and a size).
class Integer
  alias_method :__ir_bit__, :[]
  private :__ir_bit__

  # rb_to_int, as rb_fix_aref and rb_big_aref convert the index
  def __bit_position__(value)
    return value if value.is_a?(::Integer)
    if value.is_a?(::Float)
      if value.nan? || value.infinite?
        ::Kernel.raise(::FloatDomainError, value.to_s)
      end
      return value.truncate
    end
    unless value.respond_to?(:to_int)
      # nil, true and false are named by themselves in this one, as MRI names them
      ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_conversion_result_name__(value)} into Integer")
    end
    converted = value.to_int
    unless converted.is_a?(::Integer)
      ::Kernel.raise(::TypeError,
        "can't convert #{value.class} to Integer (#{value.class}#to_int gives #{__ir_conversion_result_name__(converted)})")
    end
    converted
  end
  private :__bit_position__

  # int_aref2: (self >> offset) & ((1 << size) - 1), whose shifts convert the operands as
  # rb_to_int does. A Bignum (rb_big_aref2) gives the same bits, but first wants both, and
  # their sum, in a C long.
  def __ir_int_aref2__(offset, size)
    unless between?(-0x4000000000000000, 0x3fffffffffffffff)
      offset = __bit_position__(offset)
      size = __bit_position__(size)
      [offset, size, offset + size].each do |n|
        unless n.between?(-0x8000000000000000, 0x7fffffffffffffff)
          ::Kernel.raise(::RangeError, "bignum too big to convert into 'long long'")
        end
      end
    end
    (self >> offset) & ((1 << size) - 1)
  end
  private :__ir_int_aref2__

  # rb_big_aref reads an index that is not an Integer with NUM2LONG: a Float out of a long's
  # range, and nil, are errors of their own there.
  def __ir_big_bit_position__(value)
    if value.is_a?(::Float)
      # a long's range, the upper end exclusive: 9223372036854775807.0 is 2.0**63 itself
      if value.nan? || value.infinite? || value < -9223372036854775808.0 || value >= 9223372036854775808.0
        text = value.nan? ? "NaN" : (value.infinite? ? (value > 0 ? "Inf" : "-Inf") : ("%-.10g" % value))
        ::Kernel.raise(::RangeError, "float #{text} out of range of integer")
      end
      return value.truncate
    end
    ::Kernel.raise(::TypeError, "no implicit conversion from nil to integer") if value.nil?
    n = __bit_position__(value)
    unless n.between?(-0x8000000000000000, 0x7fffffffffffffff)
      ::Kernel.raise(::RangeError, "bignum too big to convert into 'long'")
    end
    n
  end
  private :__ir_big_bit_position__

  # one_bit: rb_fix_aref converts the index with rb_to_int, rb_big_aref with NUM2LONG
  def __ir_one_bit__(index)
    fixnum = between?(-0x4000000000000000, 0x3fffffffffffffff)
    __ir_bit__(fixnum ? __bit_position__(index) : __ir_big_bit_position__(index))
  end
  private :__ir_one_bit__

  def __ir_int_aref1__(index)
    unless index.is_a?(::Range)
      # rb_range_values: anything else with #begin, #end and #exclude_end? is read as a range
      unless !index.is_a?(::Enumerator::ArithmeticSequence) &&
             index.respond_to?(:begin) && index.respond_to?(:end) && index.respond_to?(:exclude_end?)
        return __ir_one_bit__(index)
      end
    end
    first = index.begin
    last = index.end
    exclusive = index.exclude_end? ? true : false

    if first.nil?
      # A beginless range asks for every bit below its end; that is only a finite answer
      # when they are all zero. (num_negative_p: an end that is not an Integer is asked `< 0'.)
      negative = if last.is_a?(::Integer)
        last < 0
      elsif last.respond_to?(:<, true)
        last.__send__(:<, 0)
      else
        ::Kernel.raise(::ArgumentError, "comparison of #{last.class} with 0 failed")
      end
      return 0 if negative
      last += 1 unless exclusive
      return 0 if (self & ((1 << last) - 1)) == 0
      ::Kernel.raise(::ArgumentError, "The beginless range for Integer#[] results in infinity")
    end

    # compare_indexes: what will not compare counts as greater
    order = (first <=> last)
    order = if order.nil?
      1
    elsif order.is_a?(::Integer)
      order <=> 0
    else
      # rb_cmpint
      order > 0 ? 1 : (order < 0 ? -1 : 0)
    end
    if !last.nil? && order < 0
      size = last - first
      size += 1 unless exclusive
      return __ir_int_aref2__(first, size)
    elsif order == 0
      return 0 if exclusive
      return __ir_one_bit__(first)
    end
    self >> first
  end
  private :__ir_int_aref1__
end

class Symbol
  def match?(pattern, pos = 0)
    to_s.match?(pattern, pos)
  end unless method_defined?(:match?)

  # A Symbol is already what a copy of it would be, and cannot be thawed.
  def clone(freeze: nil)
    unless freeze.nil? || freeze == true
      ::Kernel.raise(::ArgumentError, "can't unfreeze Symbol")
    end
    self
  end

end

class String
  # Ruby 2.3: -"str" answers a frozen *deduplicated* string, so that -"x" and
  # -"x" are the same object; +"str" answers a mutable one, which is the
  # receiver itself when it is not frozen.

  def -@
    # A subclass instance or a string carrying instance variables is not
    # interned - MRI only shares plain, bare Strings.
    unless instance_of?(::String) && instance_variables.empty?
      return frozen? ? self : dup.freeze
    end
    __ir_fstring__
  end unless method_defined?(:-@)

  alias :dedup :-@

  # The C# #upto is Range#each in disguise, which loses the numeric-sequence
  # case ("8".upto("11")), the exclusive-end argument, the "stop is shorter than
  # self" cut-off and the type and encoding checks.
  alias :__ir_upto__ :upto
  private :__ir_upto__

  def upto(stop, exclusive = (no_exclusive = true; false), &block)
    given = stop
    unless stop.is_a?(::String)
      converted = (!stop.is_a?(::Symbol) && stop.respond_to?(:to_str)) ? stop.to_str : nil
      unless converted.is_a?(::String)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_type_name__(stop)} into String")
      end
      stop = converted
    end

    unless encoding == stop.encoding || ascii_only? && stop.ascii_only?
      ::Kernel.raise(::Encoding::CompatibilityError,
        "incompatible character encodings: #{encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : encoding} and #{stop.encoding == ::Encoding::BINARY ? "BINARY (ASCII-8BIT)" : stop.encoding}")
    end

    # the Enumerator names the arguments as given, as MRI's RETURN_ENUMERATOR does
    return (no_exclusive ? to_enum(:upto, given) : to_enum(:upto, given, exclusive)) unless block

    # A pair of decimal strings counts like Integer#upto, with the receiver's
    # width kept: "08".upto("11") answers "08", "09", "10", "11".
    # (Only ASCII can be decimal digits, and a string with bytes invalid in its encoding
    # cannot be matched against a Regexp at all.)
    if ascii_only? && stop.ascii_only? && self =~ /\A\d+\z/ && stop =~ /\A\d+\z/
      from = to_i
      to = exclusive ? stop.to_i - 1 : stop.to_i
      width = length
      from.upto(to) do |n|
        text = n.to_s
        text = text.rjust(width, "0") if text.length < width
        block.call(text)
      end
      return self
    end

    # Two single ASCII characters walk the codepoints between them: "9".upto("A")
    # answers 9 : ; < = > ? @ A. Any other pair walks #succ (MRI's str_upto_each
    # measures in bytes), so "\u0999".upto("\u9999") ends where #succ outgrows it.
    if bytesize == 1 && stop.bytesize == 1 && ascii_only? && stop.ascii_only?
      from = ord
      to = stop.ord
      to -= 1 if exclusive
      from.upto(to) { |c| block.call(c.chr(encoding)) }
      return self
    end

    # MRI's str_upto_each: the block gets a copy of the receiver and then each
    # #succ, and the next string is worked out before the block runs, so a block that
    # changes the string it was given changes neither the receiver nor the walk. The
    # walk ends at stop.succ, or once a string is longer than stop in bytes.
    n = self <=> stop
    return self if n > 0 || (exclusive && n == 0)
    after_end = stop.succ
    current = ::String.new(self)
    until current == after_end
      following = (exclusive || current != stop) ? current.succ : nil
      block.call(current)
      break if following.nil?
      current = following
      break if exclusive && current == stop
      break if current.bytesize > stop.bytesize || current.bytesize == 0
    end
    self
  end
end

# --- pieces the Ruby 4.0 standard library expects --------------------------

class Enumerator
  # 2.6's arithmetic sequence: what Range#step and Range#% answer, and what
  # Numeric#step answers. It is an Enumerator that also remembers the three
  # numbers it was built from.
  #
  # Iteration and #size follow MRI's numeric.c: ruby_float_step,
  # ruby_float_step_size and ruby_num_interval_step_size. Reimplementing them
  # rather than "value += step until past the end" matters because MRI
  # multiplies rather than accumulates for floats (so 1.0.step(12.7, 1.3) ends
  # at exactly 12.7), and because the infinity and NaN corners have answers a
  # naive loop does not reach.
  class ArithmeticSequence < ::Enumerator
    def begin = @__begin__
    def end = @__end__
    def step = @__step__

    # floor that leaves NaN and Infinity alone, the way C's floor() does.
    # Float#floor raises FloatDomainError for them.
    def self.__safe_floor__(x)
      return x if x.is_a?(::Float) && (x.nan? || x.infinite?)
      x.floor
    end

    # MRI ruby_float_step_size: the number of values yielded, as a Float so
    # that Infinity and NaN survive.
    def self.__float_step_size__(beg, fin, unit, excl)
      n = (fin - beg) / unit
      err = (beg.abs + fin.abs + (fin - beg).abs) / unit.abs * ::Float::EPSILON
      if unit.infinite?
        return (unit > 0 ? beg <= fin : beg >= fin) ? 1.0 : 0.0
      end
      return ::Float::INFINITY if unit == 0
      err = 0.5 if err > 0.5
      if excl
        return 0.0 if n <= 0
        n = n < 1 ? 0.0 : __safe_floor__(n - err).to_f
        d = (n + 1) * unit + beg
        if beg < fin
          n += 1 if d < fin
        elsif beg > fin
          n += 1 if d > fin
        end
      else
        return 0.0 if n < 0
        n = __safe_floor__(n + err).to_f
        d = (n + 1) * unit + beg
        if beg < fin
          n += 1 if d <= fin
        elsif beg > fin
          n += 1 if d >= fin
        end
      end
      n + 1
    end

    # MRI ruby_num_interval_step_size.
    def self.__interval_step_size__(from, to, unit, excl)
      if from.is_a?(::Integer) && to.is_a?(::Integer) && unit.is_a?(::Integer)
        return ::Float::INFINITY if unit == 0
        delta = to - from
        diff = unit
        if diff < 0
          diff = -diff
          delta = -delta
        end
        delta -= 1 if excl
        return 0 if delta < 0
        delta / diff + 1
      elsif from.is_a?(::Float) || to.is_a?(::Float) || unit.is_a?(::Float)
        n = __float_step_size__(from.to_f, to.to_f, unit.to_f, excl)
        return n if n.infinite?
        # MRI converts the double back to an Integer here; NaN raises
        # FloatDomainError out of rb_dbl2big, and so does Float#floor.
        n.floor
      else
        neg = unit < 0
        return ::Float::INFINITY if !neg && !(unit > 0)
        cmp = neg ? :< : :>
        return 0 if from.__send__(cmp, to)
        n = (to - from).div(unit)
        n += 1 unless excl && from + n * unit == to
        n < 0 ? 0 : n
      end
    end

    # MRI ruby_float_step / num_step / range_step iteration, shared.
    def self.__step_each__(from, to, unit, excl, &block)
      desc = unit < 0
      if to.nil?
        inf = true
      elsif to.is_a?(::Float) && to.infinite?
        inf = (to < 0) ? desc : !desc
      else
        inf = false
      end
      inf = true if unit == 0

      if from.is_a?(::Integer) && unit.is_a?(::Integer) && (to.nil? || to.is_a?(::Integer))
        # yield rather than block.call: a Proc call per value was most of the loop's cost
        i = from
        if inf
          loop { yield i; i += unit }
        elsif desc
          while excl ? i > to : i >= to
            yield i
            i += unit
          end
        else
          while excl ? i < to : i <= to
            yield i
            i += unit
          end
        end
        return
      end

      fin = to
      fin = (desc ? -::Float::INFINITY : ::Float::INFINITY) if fin.nil?

      if from.is_a?(::Float) || fin.is_a?(::Float) || unit.is_a?(::Float)
        beg = from.to_f
        stop = fin.to_f
        step = unit.to_f
        n = __float_step_size__(beg, stop, step, excl)
        # the loop is C# (KernelOps.FloatStep)
        ::Kernel.__ir_float_step__(beg, stop, step, n.to_f, &block)
        return
      end

      i = from
      cmp = desc ? :< : :>
      loop do
        break if excl ? (i == fin || i.__send__(cmp, fin)) : i.__send__(cmp, fin)
        yield(i)
        i += unit
      end
    end

    # Only Range#step, Range#% and Numeric#step make one: .new is undefined and .allocate refuses.
    class << self
      alias :__allocate__ :allocate
      private :__allocate__
      undef_method :new

      def allocate
        ::Kernel.raise(::TypeError, "allocator undefined for #{self}")
      end
    end

    def self.__build__(from, to, by, exclude_end, source, inspect_str = nil)
      seq = __allocate__
      seq.__send__(:__arith_init__, from, to, by, exclude_end, source, inspect_str)
      seq
    end

    def __arith_init__(from, to, by, exclude_end, source, inspect_str = nil)
      @__begin__ = from
      @__end__ = to
      @__step__ = by
      @__exclude_end__ = exclude_end
      @__source__ = source
      @__inspect_str__ = inspect_str
      # what Enumerator#initialize(nil) { } would set, without its `*args, &block' call
      seq = self
      @__generator__ = ::Kernel.proc { |y| seq.__send__(:__arith_each__) { |v| y << v } }
      @__initialized__ = true
      self
    end
    private :__arith_init__

    def exclude_end?
      @__exclude_end__
    end

    def first(n = nil)
      return __arith_each__ { |v| return v } if n.nil?
      ::Kernel.raise(::ArgumentError, "attempt to take negative size") if n < 0
      return [] if n == 0
      result = []
      __arith_each__ do |v|
        result << v
        break if result.size >= n
      end
      result
    end

    def each(&block)
      return self unless block
      __arith_each__(&block)
      self
    end

    def __arith_each__(&block)
      ArithmeticSequence.__step_each__(@__begin__, @__end__, @__step__, @__exclude_end__, &block)
      self
    end
    private :__arith_each__

    def to_a
      result = []
      __arith_each__ { |v| result << v }
      result
    end
    alias :entries :to_a
    alias :force :to_a

    def size
      return ::Float::INFINITY if @__end__.nil? && @__step__ != 0
      ArithmeticSequence.__interval_step_size__(@__begin__, @__end__, @__step__, @__exclude_end__)
    end

    def last(n = nil)
      if @__end__.nil?
        ::Kernel.raise(::RangeError, "cannot get the last element of endless arithmetic sequence")
      end
      values = to_a
      n.nil? ? values.last : values.last(n)
    end

    def ==(other)
      other.is_a?(ArithmeticSequence) &&
        self.begin == other.begin && self.end == other.end &&
        step == other.step && exclude_end? == other.exclude_end?
    end
    alias :eql? :==

    def hash
      [self.begin, self.end, step, exclude_end?].hash
    end

    # @__inspect_str__ is the text, or how the sequence was written - worked out only when asked
    # for, since building it was a good part of what making a sequence cost
    def inspect
      form = @__inspect_str__
      case form
      when ::String then form
      when :step then "((#{@__source__.inspect}).step)"
      when ::Array
        case form[0]
        when :step then "((#{@__source__.inspect}).step(#{form[1].inspect}))"
        when :% then "((#{@__source__.inspect}).%(#{form[1].inspect}))"
        else
          # Numeric#step: [:num, args, kw]
          args = form[1]
          kw = form[2]
          shown = args.empty? && (kw.nil? || kw.empty?) ? "" : "(#{(args.map(&:inspect) + (kw || {}).map { |k, v| "#{k}: #{v.inspect}" }).join(', ')})"
          "(#{@__source__.inspect}.step#{shown})"
        end
      else
        "((#{@__source__.inspect}).%(#{@__step__.inspect}))"
      end
    end
    alias :to_s :inspect
  end
end

# 3.2's Data: immutable value objects. The constant did not exist, so every
# file in spec/core/data failed to load.
#
# The shape follows struct.c: Data.define builds a subclass that owns the
# member list, its .new normalises whatever it was handed into a keyword hash
# and then calls #initialize, and Data#initialize is the one place that
# validates the keys and freezes the object. Doing the validation inside .new
# instead - which is what the first version of this file did - meant a
# user-written #initialize was never reached, so neither the `super` idiom nor
# psych's Data.instance_method(:initialize).bind_call trick worked.
class Data
  class << self
    # Only the classes Data.define makes have instances.
    alias_method :__ir_allocate__, :allocate
    def allocate
      ::Kernel.raise(::TypeError, "allocator undefined for Data") if equal?(::Data)
      __ir_allocate__
    end

    def define(*names, &block)
      names = names.map do |m|
        unless m.is_a?(::Symbol) || m.is_a?(::String)
          ::Kernel.raise(::TypeError, "#{m.inspect} is not a symbol nor a string")
        end
        m = m.to_sym
        # a member cannot look like a setter (rb_is_attrset_id: `[]=` is fine)
        name = m.name
        if name.end_with?("=") && name.match?(/\A[[:alpha:]_][[:alnum:]_]*=\z/)
          ::Kernel.raise(::ArgumentError, "invalid data member: #{m}")
        end
        m
      end
      if names.uniq.size != names.size
        duplicate = names.group_by { |m| m }.select { |_, v| v.size > 1 }.keys.first
        ::Kernel.raise(::ArgumentError, "duplicate member: #{duplicate}")
      end
      names.freeze

      klass = ::Class.new(self) do
        names.each do |name|
          define_method(name) { __ir_hidden_ivar__(name) }
        end

        class << self
          def members
            __data_members__.dup
          end

          def new(*args, **kwargs, &block)
            unless args.empty?
              unless kwargs.empty?
                ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size + 1}, expected 0)")
              end
              members = __data_members__
              if args.size > members.size
                expected = members.empty? ? "0" : "0..#{members.size}"
                ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected #{expected})")
              end
              args.each_with_index { |value, i| kwargs[members[i]] = value }
            end
            instance = allocate
            instance.__send__(:initialize, **kwargs, &block)
            instance
          end

          alias :[] :new

          def inspect
            ::Module.instance_method(:to_s).bind(self).call
          end

          # only Data itself defines classes
          undef_method :define
        end
      end
      # klass is a new receiver: see Struct.new
      ::Kernel.instance_method(:__ir_set_hidden_ivar__).bind_call(klass, :data_members, names)
      ::Module.instance_method(:class_eval).bind_call(klass, &block) if block
      klass
    end

    private

    # The member list lives in a hidden ivar (one #instance_variables does not
    # list) on the class Data.define created. A
    # plain `class Foo < Data` never gets one, and neither does Data itself,
    # which is why .members is defined on the generated class rather than here
    # - `Data.respond_to?(:members)` has to stay false.
    def __data_members__
      klass = self
      while klass
        names = klass.__send__(:__ir_hidden_ivar__, :data_members)
        return names if names
        klass = klass.superclass
      end
      ::Kernel.raise(::NoMethodError, "undefined method 'members' for class #{self}")
    end

    # Only a class made by Data.define can be instantiated: Data.new is undefined.
    undef_method :new
  end

  def initialize(*args, **kwargs)
    # frozen is checked before the arguments are (MRI: rb_data_initialize_m)
    if frozen?
      ::Kernel.raise(::FrozenError.new("can't modify frozen #{self.class}: #{inspect}", receiver: self))
    end
    # one positional Hash passes for the keywords, as MRI's rb_scan_args still allows here
    if args.size == 1 && kwargs.empty? && ::Hash === args[0]
      kwargs = args.pop
    end
    unless args.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0)")
    end
    names = __data_members__
    given = {}
    unknown = []
    kwargs.each do |key, value|
      key = __data_key__(key)
      if names.include?(key.to_sym)
        given[key.to_sym] = value
      else
        unknown << key.inspect
      end
    end
    # MRI reports a missing member before an unknown keyword (and a member named
    # twice, as a String and a Symbol, still leaves one missing).
    missing = names.reject { |name| given.key?(name) }
    unless missing.empty?
      ::Kernel.raise(::ArgumentError,
        "missing keyword#{missing.size > 1 ? 's' : ''}: #{missing.map { |n| n.inspect }.join(', ')}")
    end
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError,
        "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.join(', ')}")
    end
    # CRuby keeps the members out of the ivar table: #instance_variables is empty
    given.each { |name, value| __ir_set_hidden_ivar__(name, value) }
    freeze
    nil
  end

  def members
    __data_members__.dup
  end

  # Every copy of a Data is frozen too (MRI's rb_data_initialize_copy), whatever
  # clone(freeze: false) asked for. The members are read off the original directly,
  # not through its readers.
  def initialize_copy(other)
    unless other.class == self.class
      ::Kernel.raise(::TypeError, "initialize_copy should take same class object")
    end
    __data_members__.each do |name|
      __ir_set_hidden_ivar__(name, other.__send__(:__ir_hidden_ivar__, name))
    end
    freeze
    self
  end
  private :initialize_copy

  # The members as stored, not as the reader methods answer: #inspect, #to_h and #==
  # work on a Data whose reader was overridden (struct.c reads the slots directly).
  def __data_value__(name)
    __ir_hidden_ivar__(name)
  end
  private :__data_value__

  def to_h(&block)
    result = {}
    __data_members__.each do |name|
      key, value = name, __data_value__(name)
      if block
        pair = block.call(key, value)
        unless ::Array === pair
          converted = pair.respond_to?(:to_ary) ? pair.to_ary : nil
          unless ::Array === converted
            ::Kernel.raise(::TypeError, "wrong element type #{pair.class} (expected array)")
          end
          pair = converted
        end
        unless pair.size == 2
          ::Kernel.raise(::ArgumentError, "element has wrong array length (expected 2, was #{pair.size})")
        end
        key, value = pair[0], pair[1]
      end
      result[key] = value
    end
    result
  end

  def deconstruct
    __data_members__.map { |name| __data_value__(name) }
  end

  def deconstruct_keys(keys)
    return to_h if keys.nil?
    unless ::Array === keys
      ::Kernel.raise(::TypeError, "wrong argument type #{keys.class} (expected Array or nil)")
    end
    names = __data_members__
    return {} if keys.size > names.size
    result = {}
    keys.each do |key|
      key = __data_key__(key)
      return result unless names.include?(key.to_sym)
      result[key] = __data_value__(key.to_sym)
    end
    result
  end

  def with(*args, **kwargs)
    unless args.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0)")
    end
    return self if kwargs.empty?
    names = __data_members__
    updates = {}
    unknown = []
    kwargs.each do |key, value|
      key = __data_key__(key)
      if names.include?(key.to_sym)
        updates[key.to_sym] = value
      else
        unknown << key.inspect
      end
    end
    unless unknown.empty?
      ::Kernel.raise(::ArgumentError,
        "unknown keyword#{unknown.size > 1 ? 's' : ''}: #{unknown.join(', ')}")
    end
    copy = self.class.allocate
    copy.__send__(:initialize, **to_h.merge(updates))
    copy
  end

  def ==(other)
    return true if equal?(other)
    return false unless other.class == self.class
    __data_paired__(:==, other) do
      __data_members__.all? { |name| __data_value__(name) == other.__send__(:__data_value__, name) }
    end
  end

  def eql?(other)
    return true if equal?(other)
    return false unless other.class == self.class
    __data_paired__(:eql?, other) do
      __data_members__.all? { |name| __data_value__(name).eql?(other.__send__(:__data_value__, name)) }
    end
  end

  def hash
    stack = (::Thread.current[:__data_hash__] ||= [])
    return self.class.hash if stack.any? { |seen| seen.equal?(self) }
    stack.push(self)
    begin
      ([self.class] + deconstruct).hash
    ensure
      stack.pop
    end
  end

  # struct.c prints the class path, not #name: a Data class whose name method
  # has been redefined still inspects under its real path, and a class whose
  # path starts with '#' - anonymous, or nested inside something anonymous -
  # prints no name at all unless it is the recursive placeholder.
  def to_s
    path = ::Module.instance_method(:to_s).bind(self.class).call
    named = !path.start_with?("#")
    stack = (::Thread.current[:__data_inspect__] ||= [])
    return "#<data #{path}:...>" if stack.any? { |seen| seen.equal?(self) }
    stack.push(self)
    begin
      out = +"#<data "
      out << path if named
      __data_members__.each_with_index do |name, i|
        out << (i > 0 ? ", " : (named ? " " : ""))
        # a member that is not a local or constant name shows as its Symbol (:@a=1), as in MRI
        label = name.to_s.match?(/\A[[:alpha:]_][[:alnum:]_]*\z/) ? name.to_s : name.to_sym.inspect
        out << "#{label}=#{__data_value__(name).inspect}"
      end
      out << ">"
    ensure
      stack.pop
    end
  end
  alias :inspect :to_s

  private

  def __data_members__
    self.class.__send__(:__data_members__)
  end

  # Data accepts a String wherever it accepts a Symbol, and asks anything else
  # for #to_str before giving up. The value returned here is the key as the
  # caller wrote it - a String stays a String so that #deconstruct_keys can key
  # its result by it, and so that an unknown-keyword message quotes it.
  def __data_key__(key)
    return key if ::Symbol === key
    return key if ::String === key
    unless key.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "#{key.inspect} is not a symbol nor a string")
    end
    converted = key.to_str
    unless ::String === converted
      ::Kernel.raise(::TypeError,
        "can't convert #{key.class} to String (#{key.class}#to_str gives #{__ir_conversion_result_name__(converted)})")
    end
    converted
  end

  def __data_paired__(tag, other)
    stack = (::Thread.current[:__data_paired__] ||= [])
    pair = [tag, object_id, other.object_id]
    return true if stack.include?(pair)
    stack.push(pair)
    begin
      yield
    ensure
      stack.pop
    end
  end
end unless defined?(Data)

# ENV is Hash-shaped but was missing a third of the shape. Everything here is
# written in terms of the accessors it does have, so it stays in step with the
# real environment rather than a snapshot of it.
class << ENV
  # Names and values go through #to_str, not #to_s: ENV is as strict as Hash about
  # implicit conversion and reports the failure the same way.
  def __env_str__(object)
    return object if object.is_a?(::String)
    unless object.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{object.class} into String")
    end
    converted = object.to_str
    unless converted.is_a?(::String)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{object.class} into String")
    end
    converted
  end
  private :__env_str__

  # setenv(3) rejects a name that is empty or contains '='. The CLR reports that as its
  # own ArgumentException; MRI reports Errno::EINVAL naming the call.
  def __env_check_name__(name)
    if name.empty? || name.include?("=")
      ::Kernel.raise(::Errno::EINVAL, "setenv(#{name})")
    end
    name
  end
  private :__env_check_name__

  unless respond_to?(:__clr_store__, true)
    alias :__clr_store__ :[]=
    alias :__clr_fetch__ :[]
    private :__clr_store__
    private :__clr_fetch__

    def []=(name, value)
      name = __env_str__(name)
      if value.nil?
        # Deleting a name setenv(3) would have rejected is a no-op, not an error.
        __clr_store__(name, nil) unless name.empty? || name.include?("=")
        return nil
      end
      value = __env_str__(value)
      __env_check_name__(name)
      __clr_store__(name, value)
      value
    end

    alias :store :[]=
  end

  def [](name)
    __clr_fetch__(__env_str__(name))
  end

  def fetch(name, *default)
    if default.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{default.size + 1}, expected 1..2)")
    end
    name = __env_str__(name)
    if block_given? && !default.empty?
      ::Kernel.warn("warning: block supersedes default value argument")
    end

    value = __clr_fetch__(name)
    return value unless value.nil?
    return yield(name) if block_given?
    return default[0] unless default.empty?

    ::Kernel.raise(::KeyError.new("key not found: #{name.inspect}", receiver: self, key: name))
  end

  def delete(name)
    name = __env_str__(name)
    value = __clr_fetch__(name)
    if value.nil?
      return block_given? ? yield(name) : nil
    end
    __clr_store__(name, nil)
    value
  end

  def clone(freeze: nil)
    unless freeze.nil? || freeze == true || freeze == false
      ::Kernel.raise(::ArgumentError, "unexpected value for freeze: #{freeze.class}")
    end
    ::Kernel.raise(::TypeError, "Cannot clone ENV, use ENV.to_h to get a copy of ENV as a hash")
  end

  def dup
    ::Kernel.raise(::TypeError, "Cannot dup ENV, use ENV.to_h to get a copy of ENV as a hash")
  end

  # MRI hands back an Enumerator rather than complaining about the missing block,
  # and the Enumerator knows how many pairs it will walk.
  def each(&block)
    return to_enum(__callee__) { size } unless block
    to_hash.each(&block)
    self
  end
  alias :each_pair :each

  def each_key
    return to_enum(:each_key) { size } unless block_given?
    to_hash.each_key { |k| yield k }
    self
  end

  def each_value
    return to_enum(:each_value) { size } unless block_given?
    to_hash.each_value { |v| yield v }
    self
  end

  def delete_if
    return to_enum(:delete_if) { size } unless block_given?
    to_hash.each { |k, v| __clr_store__(k, nil) if yield(k, v) }
    self
  end

  def reject!
    return to_enum(:reject!) { size } unless block_given?
    changed = false
    to_hash.each do |k, v|
      next unless yield(k, v)
      __clr_store__(k, nil)
      changed = true
    end
    changed ? self : nil
  end

  def reject(&block)
    return to_enum(:reject) { size } unless block
    to_hash.reject(&block)
  end

  def select(&block)
    return to_enum(__callee__) { size } unless block
    to_hash.select(&block)
  end
  alias :filter :select

  alias :member? :include?

  # Pairs are applied one at a time and in order, so a bad pair leaves everything
  # before it in place -- which is what MRI's own specs pin down.
  def merge!(*others)
    others.each do |other|
      other.each do |key, value|
        key = __env_str__(key)
        if block_given? && !__clr_fetch__(key).nil?
          value = yield(key, __clr_fetch__(key), value)
        end
        self[key] = value
      end
    end
    self
  end

  alias :update :merge!

  # replace is all-or-nothing: everything is converted and checked before the first
  # variable is touched, so a TypeError halfway through leaves ENV untouched.
  def replace(other)
    unless other.is_a?(::Hash)
      unless other.respond_to?(:to_hash)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{other.class} into Hash")
      end
      other = other.to_hash
    end

    pairs = other.map do |key, value|
      key = __env_str__(key)
      value = __env_str__(value)
      __env_check_name__(key)
      [key, value]
    end

    clear
    pairs.each { |key, value| __clr_store__(key, value) }
    self
  end

  # The C# implementation built a CLR dictionary and threw on a duplicate value.
  def invert
    to_hash.invert
  end

  def keep_if
    return to_enum(:keep_if) unless block_given?
    to_hash.each { |k, v| self[k] = nil unless yield(k, v) }
    self
  end unless respond_to?(:keep_if)

  def select!
    return to_enum(__callee__) unless block_given?
    changed = false
    to_hash.each do |k, v|
      unless yield(k, v)
        self[k] = nil
        changed = true
      end
    end
    changed ? self : nil
  end unless respond_to?(:select!)

  alias :filter! :select! unless respond_to?(:filter!)

  # MRI keeps the objects it was handed as the result keys, not their #to_str.
  def slice(*keys)
    result = {}
    keys.each do |key|
      name = __env_str__(key)
      value = __clr_fetch__(name)
      result[key] = value unless value.nil?
    end
    result
  end

  def except(*keys)
    keys = keys.map { |key| __env_str__(key) }
    to_hash.reject { |k, _| keys.include?(k) }
  end

  def assoc(key)
    key = __env_str__(key)
    value = __clr_fetch__(key)
    value.nil? ? nil : [key, value]
  end

  # rassoc and value? answer nil for an argument they cannot convert, where assoc
  # and the key-taking methods raise. That asymmetry is MRI's.
  def rassoc(value)
    return nil unless value.is_a?(::String) || value.respond_to?(:to_str)
    value = __env_str__(value)
    to_hash.each { |k, v| return [k, v] if v == value }
    nil
  end

  def value?(value)
    return nil unless value.is_a?(::String) || value.respond_to?(:to_str)
    value = __env_str__(value)
    to_hash.each_value { |v| return true if v == value }
    false
  end
  alias :has_value? :value?

  def to_h(&block)
    to_hash.to_h(&block)
  end

  def key(value)
    value = __env_str__(value)
    to_hash.each { |k, v| return k if v == value }
    nil
  end

  def to_set(*args, &block)
    require 'set'
    ::Set.new(to_hash.to_a, *args, &block)
  end unless respond_to?(:to_set)
end

class Method

  def curry(*arity)
    to_proc.curry(*arity)
  end unless method_defined?(:curry)

  def >>(other)
    to_proc >> other
  end unless method_defined?(:>>)

  def <<(other)
    to_proc << other
  end unless method_defined?(:<<)
end

# --- Math ------------------------------------------------------------------
# Math had two independent problems in the same place.
#
# The C# builtins signalled every out-of-domain result as Errno::EDOM (MRI
# raises Math::DomainError), did so for NaN input as well (MRI answers NaN),
# converted arguments with the DefaultProtocol instead of Float()'s rules (MRI
# raises TypeError for anything that is not a Numeric, including a String that
# looks like a number), had no second argument to #log, no pair from #lgamma
# and a #cbrt that was pow(x, 1/3.0) and so NaN for every negative argument.
#
# On top of that, the Complex implementation IronRuby loads is Ruby 1.8's
# complex.rb, which replaces sqrt/exp/log/cos/sin/tan/... with CMath versions
# that answer a Complex. This block runs after complex.rb and takes the module
# back; Complex arithmetic keeps working because complex.rb calls the `!`
# aliases it took before redefining.
#
# Everything is module_function, as in MRI: `include Math; atanh(2)` has to
# raise the same Math::DomainError as `Math.atanh(2)`.
module Math
  class DomainError < StandardError; end unless const_defined?(:DomainError, false)

  alias :__ir_gamma__ :gamma
  alias :__ir_lgamma__ :lgamma
  alias :__ir_erf__ :erf
  alias :__ir_erfc__ :erfc
  module_function :__ir_gamma__, :__ir_lgamma__, :__ir_erf__, :__ir_erfc__

  # MRI's rb_to_float: Numeric only. An object that merely answers #to_f is a
  # TypeError, and so is a String - Float("1") would work but Math.sqrt("1")
  # does not.
  def __flt__(x)
    return x if x.is_a?(::Float)
    if x.is_a?(::Numeric)
      v = x.to_f
      return v if v.is_a?(::Float)
    end
    ::Kernel.raise(::TypeError, "can't convert #{x.nil? ? 'nil' : x.class} into Float")
  end

  def __dom__(name)
    ::Kernel.raise(::Math::DomainError, "Numerical argument is out of domain - #{name}")
  end

  module_function :__flt__, :__dom__
  private_class_method :__flt__, :__dom__

  def __ir_acos__(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("acos") if x < -1.0 || x > 1.0
    ::System::Math.Acos(x)
  end

  def __ir_asin__(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("asin") if x < -1.0 || x > 1.0
    ::System::Math.Asin(x)
  end

  def __ir_atan__(x)
    ::System::Math.Atan(__flt__(x))
  end

  def __ir_atan2__(y, x)
    y = __flt__(y)
    x = __flt__(x)
    return y if y.nan?
    return x if x.nan?
    ::System::Math.Atan2(y, x)
  end

  def acosh(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("acosh") if x < 1.0
    ::System::Math.Acosh(x)
  end

  def __ir_asinh__(x)
    ::System::Math.Asinh(__flt__(x))
  end

  def atanh(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("atanh") if x < -1.0 || x > 1.0
    return ::Float::INFINITY if x == 1.0
    return -::Float::INFINITY if x == -1.0
    ::System::Math.Atanh(x)
  end

  def __ir_cos__(x)
    ::System::Math.Cos(__flt__(x))
  end

  def __ir_sin__(x)
    ::System::Math.Sin(__flt__(x))
  end

  def __ir_tan__(x)
    ::System::Math.Tan(__flt__(x))
  end

  def __ir_cosh__(x)
    ::System::Math.Cosh(__flt__(x))
  end

  def __ir_sinh__(x)
    ::System::Math.Sinh(__flt__(x))
  end

  def __ir_tanh__(x)
    ::System::Math.Tanh(__flt__(x))
  end

  def __ir_exp__(x)
    ::System::Math.Exp(__flt__(x))
  end

  # Rational, Bignum and the rest of what the C# fast path leaves: the C# cbrt takes the
  # Float, with the Newton step MRI adds on glibc.
  def __ir_cbrt__(x)
    cbrt(__flt__(x))
  end

  def __ir_sqrt__(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("sqrt") if x < 0.0
    # sqrt(-0.0) is -0.0 in IEEE but 0.0 in MRI.
    return 0.0 if x == 0.0
    ::System::Math.Sqrt(x)
  end

  def __log__(x, name)
    return x if x.nan?
    __dom__(name) if x < 0.0
    nil
  end
  module_function :__log__
  private_class_method :__log__

  # MRI's math_log_split. Math.log2(2**10001) has to be 10001.0, but the
  # argument does not survive the trip through a double: shift the exponent
  # out first and add it back afterwards.
  def __log_split__(x)
    if x.is_a?(::Integer) && x > 0
      bits = x.bit_length
      if bits > 1024
        n = bits - 53
        return [(x >> n).to_f, n]
      end
    end
    [__flt__(x), 0]
  end
  module_function :__log_split__
  private_class_method :__log_split__

  # Math.log(x) and Math.log(x, nil) are different calls - the second is a
  # TypeError - so the base cannot be an optional parameter defaulting to nil.
  # log, log2 and log10 of a Float or Fixnum that is not negative are C#
  # (RubyMath); these are what the rest goes to.
  def __ir_log__(x, *rest)
    if rest.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{rest.size + 1}, expected 1..2)")
    end
    x, bits = __log_split__(x)
    r = __log__(x, "log")
    return r if r
    v = ::System::Math.Log(x) + bits * ::System::Math.Log(2.0)
    return v if rest.empty?
    b, bbits = __log_split__(rest[0])
    r = __log__(b, "log")
    return r if r
    v / (::System::Math.Log(b) + bbits * ::System::Math.Log(2.0))
  end

  def __ir_log2__(x)
    x, bits = __log_split__(x)
    r = __log__(x, "log2")
    return r if r
    ::System::Math.Log2(x) + bits
  end

  def __ir_log10__(x)
    x, bits = __log_split__(x)
    r = __log__(x, "log10")
    return r if r
    ::System::Math.Log10(x) + bits * ::System::Math.Log10(2.0)
  end

  # exp(x)-1 and log(1+x), accurate near zero, which is the whole point of
  # having them separately from exp and log.
  def expm1(x)
    x = __flt__(x)
    # exp(1e-16) - 1.0 is exactly 0; the identity (u-1)*x/log(u) keeps the
    # significant digits that the subtraction cancels away.
    u = ::System::Math.Exp(x)
    return x if u == 1.0
    return u - 1.0 if u - 1.0 == -1.0 || u.infinite? || u.nan?
    (u - 1.0) * x / ::System::Math.Log(u)
  end

  def log1p(x)
    x = __flt__(x)
    return x if x.nan?
    __dom__("log1p") if x < -1.0
    return -::Float::INFINITY if x == -1.0
    # log(1+x) loses every significant digit for tiny x; the identity
    # x*log1p(u)/u with u = (1+x)-1 keeps them.
    u = 1.0 + x
    return x if u == 1.0
    ::System::Math.Log(u) * x / (u - 1.0)
  end

  def erf(x)
    __ir_erf__(__flt__(x))
  end

  def erfc(x)
    __ir_erfc__(__flt__(x))
  end

  def __ir_hypot__(x, y)
    x = __flt__(x).abs
    y = __flt__(y).abs
    # An infinity wins over a NaN, which is what C's hypot() promises.
    return ::Float::INFINITY if x.infinite? || y.infinite?
    return ::Float::NAN if x.nan? || y.nan?
    x, y = y, x if x < y
    return x if y == 0.0
    # Scaling by the larger operand keeps x*x from overflowing for x ~ 1e300.
    r = y / x
    x * ::System::Math.Sqrt(1.0 + r * r)
  end

  # MRI's NUM2INT: #to_int, never String parsing, and a Float that is not
  # finite is a RangeError rather than a FloatDomainError.
  def __int__(n)
    return n if n.is_a?(::Integer)
    if n.is_a?(::Float)
      if n.nan?
        ::Kernel.raise(::RangeError, "float NaN out of range of integer")
      elsif n.infinite?
        ::Kernel.raise(::RangeError, "float #{n > 0 ? 'Inf' : '-Inf'} out of range of integer")
      end
      return n.to_i
    end
    ::Kernel.raise(::TypeError, "no implicit conversion of nil into Integer") if n.nil?
    unless n.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{n.class} into Integer")
    end
    n.to_int
  end
  module_function :__int__
  private_class_method :__int__

  def ldexp(x, n)
    n = __int__(n)
    x = __flt__(x)
    return x if x == 0.0 || x.nan? || x.infinite?
    return 0.0 * x if n < -2098
    return (x < 0 ? -::Float::INFINITY : ::Float::INFINITY) if n > 2098
    ::System::Math.ScaleB(x, n)
  end

  # MRI answers +infinity at the poles rather than raising, and raises only for
  # -infinity. MathUtils.Gamma already handles the negative non-integers.
  def gamma(x)
    x = __flt__(x)
    return x if x.nan?
    if x == 0.0
      return (1.0 / x) < 0 ? -::Float::INFINITY : ::Float::INFINITY
    end
    if x < 0.0
      __dom__("gamma") if x.infinite? || x == x.floor
    end
    __ir_gamma__(x)
  end

  # lgamma answers the pair [log(|gamma(x)|), sign of gamma(x)].
  def lgamma(x)
    x = __flt__(x)
    return [x, 1] if x.nan?
    __dom__("lgamma") if x.infinite? && x < 0
    return [::Float::INFINITY, 1] if x.infinite?
    if x == 0.0
      return [::Float::INFINITY, (1.0 / x) < 0 ? -1 : 1]
    end
    if x < 0.0
      return [::Float::INFINITY, 1] if x == x.floor
      # The sign of gamma(x) is the sign of sin(pi*x): gamma(1-x) > 0 there, and
      # |gamma(x)| = pi / (|sin(pi*x)| * gamma(1-x)). The magnitude is the C
      # library's lgamma, as MRI's is (lgamma_r), with MathUtils' reflection behind it.
      s = ::System::Math.Sin(::Math::PI * x)
      return [__ir_lgamma__(x), s < 0 ? -1 : 1]
    end
    # MathUtils.LogGamma(1.0) is 4.4e-16 rather than 0.
    return [0.0, 1] if x == 1.0 || x == 2.0
    [__ir_lgamma__(x), 1]
  end

  def frexp(x)
    x = __flt__(x)
    return [x, 0] if x == 0.0 || x.nan? || x.infinite?
    e = ::System::Math.ILogB(x) + 1
    [::System::Math.ScaleB(x, -e), e]
  end

  module_function :frexp
  module_function :acosh, :atanh, :erf, :erfc, :expm1, :gamma,
                  :ldexp, :lgamma, :log, :log10, :log1p, :log2
  module_function :acos, :asin, :atan, :atan2, :cos, :sin, :tan, :cosh, :sinh, :tanh, :asinh, :exp, :cbrt,
                  :sqrt, :hypot
  # The Float/Fixnum fast path of these is C# (RubyMath); their general Ruby half:
  module_function :__ir_acos__, :__ir_asin__, :__ir_atan__, :__ir_atan2__, :__ir_cos__, :__ir_sin__, :__ir_tan__, :__ir_cosh__, :__ir_sinh__, :__ir_tanh__, :__ir_asinh__, :__ir_exp__, :__ir_cbrt__, :__ir_sqrt__, :__ir_hypot__,
                  :__ir_log__, :__ir_log2__, :__ir_log10__
  private_class_method :__ir_acos__, :__ir_asin__, :__ir_atan__, :__ir_atan2__, :__ir_cos__, :__ir_sin__, :__ir_tan__, :__ir_cosh__, :__ir_sinh__, :__ir_tanh__, :__ir_asinh__, :__ir_exp__, :__ir_cbrt__, :__ir_sqrt__, :__ir_hypot__,
                       :__ir_log__, :__ir_log2__, :__ir_log10__
end

# `ruby2_keywords' written at the top level is Object.ruby2_keywords. MRI defines it on
# main's singleton rather than on Kernel, which is where main.private_methods(false) looks.
class << self
  private def ruby2_keywords(*names)
    ::Object.send(:ruby2_keywords, *names)
  end unless private_method_defined?(:ruby2_keywords)
end

module Process
  # Linux wait flags, priority classes and resource limits. ruby/spec only asks that
  # they exist and are Integers, but the numbers are the real ones so that anything
  # passing them to a syscall later gets the right value.
  {
    WNOHANG: 1, WUNTRACED: 2,
  }.each { |name, value| const_set(name, value) unless const_defined?(name) }

  # CRuby on Windows has neither priorities nor resource limits, and portable code asks
  # for the constant before it calls the function: net-http-persistent, which Bundler
  # vendors, calls getrlimit whenever RLIMIT_NOFILE is defined.
  {
    PRIO_PROCESS: 0, PRIO_PGRP: 1, PRIO_USER: 2,
    RLIMIT_CPU: 0, RLIMIT_FSIZE: 1, RLIMIT_DATA: 2, RLIMIT_STACK: 3,
    RLIMIT_CORE: 4, RLIMIT_RSS: 5, RLIMIT_NPROC: 6, RLIMIT_NOFILE: 7,
    RLIMIT_MEMLOCK: 8, RLIMIT_AS: 9, RLIMIT_SIGPENDING: 11,
    RLIMIT_MSGQUEUE: 12, RLIMIT_NICE: 13, RLIMIT_RTPRIO: 14, RLIMIT_RTTIME: 15,
    RLIM_INFINITY: 2**64 - 1, RLIM_SAVED_CUR: 2**64 - 1, RLIM_SAVED_MAX: 2**64 - 1,
  }.each { |name, value| const_set(name, value) unless const_defined?(name) } unless ::File::ALT_SEPARATOR == "\\"

  # NB SignalException#signo / #signm are defined just below this module.
  #
  # Process.exit / .exit! / .abort behave exactly like their Kernel namesakes (MRI
  # defines them as singleton methods only).  Without them Process.exit raised NoMethodError,
  # and in spec/core/process/exit_spec.rb that NoMethodError escaped a thread whose
  # main thread was waiting in a bare sleep for the SystemExit - so the file hung.
  unless respond_to?(:exit)
    def self.exit(*args)
      ::Kernel.exit(*args)
    end

    def self.exit!(*args)
      ::Kernel.exit!(*args)
    end

    def self.abort(*args)
      ::Kernel.abort(*args)
    end
  end

  unless respond_to?(:clock_getres)
    # We have no way to ask the platform, so report the resolution the source we
    # actually use has: Stopwatch for the monotonic clocks, and Time for the rest.
    def clock_getres(clock_id, unit = :float_second)
      seconds = CLOCK_RESOLUTIONS__[clock_id.to_s] || 1.0 / System::Diagnostics::Stopwatch.frequency.to_f
      case unit
      when :float_second then seconds
      when :float_millisecond then seconds * 1_000.0
      when :float_microsecond then seconds * 1_000_000.0
      when :second then seconds.to_i
      when :millisecond then (seconds * 1_000).to_i
      when :microsecond then (seconds * 1_000_000).to_i
      when :nanosecond then (seconds * 1_000_000_000).to_i
      when nil then seconds
      else ::Kernel.raise(::ArgumentError, "unexpected unit: #{unit}")
      end
    end
    module_function :clock_getres
  end

  unless respond_to?(:argv0)
    # MRI hands back the $0 the program started with, and keeps handing it back
    # after $0 is assigned to. setproctitle is what assignment goes through. MRI has no
    # constant for it: a hidden ivar of the module, which RubyContext sets again once the
    # main script is known.
    @__argv0__ = ($0 && $0.dup.freeze) unless defined?(@__argv0__)
    class << self
      def __argv0__ = @__argv0__
    end

    def argv0
      ::Process.__argv0__
    end
    module_function :argv0
  end

  unless respond_to?(:maxgroups)
    def maxgroups
      @__maxgroups__ ||= 65536
    end
    module_function :maxgroups

    # MRI's: a positive Integer (implicitly converted), capped at RB_MAX_GROUPS
    def maxgroups=(value)
      ::Kernel.raise TypeError, "no implicit conversion from nil to integer" if value.nil?
      n = Integer.try_convert(value) || ::Kernel.raise(TypeError, "no implicit conversion of #{value.class} into Integer")
      ::Kernel.raise ArgumentError, "maxgroups #{n} should be positive" if n <= 0
      @__maxgroups__ = n > 65536 ? 65536 : n
    end
    module_function :maxgroups=
  end

  # MRI's conversion of anything that claims to be a pid or an id, including the two
  # different TypeErrors it reports for "no #to_int" and "#to_int gave something else".
  def self.__integer_value__(value)
    return value if value.is_a?(Integer)
    unless value.respond_to?(:to_int)
      raise TypeError, "no implicit conversion of #{value.class} into Integer"
    end
    result = value.to_int
    unless result.is_a?(Integer)
      raise TypeError, "can't convert #{value.class} to Integer (#{value.class}#to_int gives #{__ir_conversion_result_name__(result)})"
    end
    result
  end

  # A user or group name is allowed wherever an id is, and is looked up the way MRI
  # looks it up.
  def self.__user_id__(value)
    return __integer_value__(value) unless value.is_a?(String)
    require "etc"
    entry = begin
              Etc.getpwnam(value)
            rescue StandardError
              nil
            end
    raise ArgumentError, "can't find user for #{value}" unless entry
    entry.uid
  end

  def self.__group_id__(value)
    return __integer_value__(value) unless value.is_a?(String)
    require "etc"
    entry = begin
              Etc.getgrnam(value)
            rescue StandardError
              nil
            end
    raise ArgumentError, "can't find group for #{value}" unless entry
    entry.gid
  end

  # The thread Process.detach answers: it reaps the child, its #value is the exit status, and
  # #pid reads the :pid thread-local MRI puts on it. Only Process.detach makes one.
  class Waiter < ::Thread
    class << self
      alias_method :__create__, :new
      private :__create__
      undef_method :new
    end

    def pid
      self[:pid]
    end
  end unless const_defined?(:Waiter, false)

  unless respond_to?(:detach)
    # A pid that is not ours is not an error here: MRI's detach thread just ends with no status.
    def detach(pid)
      pid = __integer_value__(pid)
      thread = Waiter.__send__(:__create__, pid) do |p|
        begin
          Process.wait2(p)[1]
        rescue SystemCallError
          nil
        end
      end
      thread[:pid] = pid
      thread
    end
    module_function :detach
  end

  unless respond_to?(:warmup)
    def warmup
      true
    end
    module_function :warmup
  end

  # --- spawn and the wait family ------------------------------------------------
  #
  # Process.__spawn__ is posix_spawn(3): an executable, an argv, an environment, a
  # list of file actions and a process group, in exchange for a pid. Everything
  # above that - picking the command apart, converting and checking the arguments,
  # and turning the redirection options into file actions - is here, because it is
  # protocol work that reads far better in Ruby than in C#.
  #
  # Redirections used to be lowered onto a /bin/sh command line. That could never be
  # right: a descriptor a Ruby program hands us is close-on-exec, so `1>&7` in the
  # shell finds nothing there, and a path with a quote in it had to survive being
  # re-quoted. A file action is applied by posix_spawn in the child between fork and
  # exec, which is exactly where MRI does the same work.

  SPAWN_OPTION_KEYS = [
    :unsetenv_others, :close_others, :pgroup, :new_pgroup, :chdir, :umask,
    :in, :out, :err, :rlimit_core, :rlimit_cpu, :rlimit_fsize, :exception,
  ].freeze

  # File action opcodes, shared with the C# side.
  SPAWN_DUP2  = 0
  SPAWN_CLOSE = 1
  SPAWN_OPEN  = 2
  SPAWN_CHDIR = 3

  # open(2) flags; Linux spells them the same on every architecture we run on.
  SPAWN_O_RDONLY = 0
  SPAWN_O_WRONLY = 1
  SPAWN_O_RDWR   = 2
  SPAWN_O_CREAT  = 0o100
  SPAWN_O_TRUNC  = 0o1000
  SPAWN_O_APPEND = 0o2000

  # A one-string command goes to the shell only if it contains one of these, exactly
  # as in MRI's rb_exec_fillarg. Otherwise MRI splits the string itself and execs the
  # result, which is what makes Process.spawn("no-such-command") raise Errno::ENOENT
  # rather than quietly collect the shell's exit status 127.
  SPAWN_SHELL_META = /[*?{}\[\]<>()~&|\\$;'"`\n#]/

  # ...and the same goes for a command whose first word is a shell built-in, which
  # there is no file to exec. `system("exit 29")` has to reach a shell to mean
  # anything at all.
  SPAWN_SHELL_BUILTINS = %w[
    ! . : break case continue do done elif else esac eval exec exit export fi for
    if in readonly return set shift then times trap unset until while
  ].freeze

  # Whether there is a /bin/sh at all. Everything below that says "shell" means cmd.exe
  # on Windows, and the executable search there has to try the PATHEXT extensions: a
  # command named "ruby" is a file named ruby.exe, and File.executable? cannot invent
  # the suffix for it.
  SPAWN_WINDOWS = ::File::ALT_SEPARATOR == "\\"

  # cmd.exe's built-ins, which are no more a file to exec than `exit` is on sh. `echo hi`
  # has to reach the shell or it is an Errno::ENOENT for a command named "echo".
  SPAWN_CMD_BUILTINS = %w[
    assoc break call cd chdir cls color copy date del dir echo endlocal erase exit for
    ftype goto if md mkdir mklink move path pause popd prompt pushd rd rem ren rename
    rmdir set setlocal shift start time title type ver verify vol
  ].freeze
  __hide_constant__ :SPAWN_OPTION_KEYS, :SPAWN_DUP2, :SPAWN_CLOSE, :SPAWN_OPEN, :SPAWN_CHDIR,
    :SPAWN_O_RDONLY, :SPAWN_O_WRONLY, :SPAWN_O_RDWR, :SPAWN_O_CREAT, :SPAWN_O_TRUNC, :SPAWN_O_APPEND,
    :SPAWN_SHELL_META, :SPAWN_SHELL_BUILTINS, :SPAWN_WINDOWS, :SPAWN_CMD_BUILTINS

  # [shell, argv] for running a command line through the system's shell.
  def self.__spawn_shell__(command)
    return ["/bin/sh", ["sh", "-c", command]] unless SPAWN_WINDOWS
    shell = ENV["COMSPEC"] || ENV["ComSpec"] || "C:\\Windows\\system32\\cmd.exe"
    [shell, [shell, "/c", command]]
  end

  # The suffixes an extensionless command name may really have, in PATHEXT's own order -
  # which is the order CreateProcess and cmd.exe try them in. Just [""] off Windows.
  def self.__spawn_path_extensions__
    return [""] unless SPAWN_WINDOWS
    [""] + (ENV["PATHEXT"] || ".COM;.EXE;.BAT;.CMD").split(";").reject(&:empty?)
  end

  def self.__check_spawn_string__(value, what)
    unless value.is_a?(String)
      unless value.respond_to?(:to_str)
        raise TypeError, "no implicit conversion of #{value.class} into String"
      end
      value = value.to_str
    end
    raise ArgumentError, "string contains null byte" if value.include?("\0")
    value
  end

  def self.__spawn_to_io__(value)
    return value if value.is_a?(IO)
    if value.respond_to?(:to_io)
      io = value.to_io
      return io if io.is_a?(IO)
    end
    nil
  end

  # The descriptors a redirection key names. MRI lets the key be a symbol, a
  # descriptor, an IO, or an array of any of those so that one value redirects
  # several descriptors at once.
  def self.__spawn_key_fds__(key)
    case key
    when :in  then [0]
    when :out then [1]
    when :err then [2]
    when Integer then [key]
    when Array then key.map { |part| __spawn_key_fds__(part) }.flatten
    else
      io = __spawn_to_io__(key)
      raise ArgumentError, "wrong exec redirect: #{key.inspect}" if io.nil?
      [io.fileno]
    end
  end

  def self.__spawn_redirect_key?(key)
    case key
    when :in, :out, :err, Integer, Array then true
    else !__spawn_to_io__(key).nil?
    end
  end

  def self.__spawn_open_flags__(mode, fd)
    return mode if mode.is_a?(Integer)
    if mode.nil?
      return fd == 0 ? SPAWN_O_RDONLY : (SPAWN_O_WRONLY | SPAWN_O_CREAT | SPAWN_O_TRUNC)
    end
    mode = mode.to_str if !mode.is_a?(String) && mode.respond_to?(:to_str)
    text = mode.to_s.sub(/:.*\z/, "")
    plus = text.include?("+")
    case text[0]
    when "a" then (plus ? SPAWN_O_RDWR : SPAWN_O_WRONLY) | SPAWN_O_CREAT | SPAWN_O_APPEND
    when "w" then (plus ? SPAWN_O_RDWR : SPAWN_O_WRONLY) | SPAWN_O_CREAT | SPAWN_O_TRUNC
    else          (plus ? SPAWN_O_RDWR : SPAWN_O_RDONLY)
    end
  end

  # Returns the file actions for one redirection. [:child, fd] cannot be resolved
  # here - it names a descriptor of the child as it will be *after* the other
  # redirections - so it is handed back separately to be appended last.
  def self.__spawn_redirect__(fd, value, deferred)
    case value
    when :close
      [[SPAWN_CLOSE, fd]]
    when Integer
      [[SPAWN_DUP2, fd, __spawn_native_fd__(value)]]
    when String, Symbol
      path = __check_spawn_string__(value.to_s, "path name")
      [[SPAWN_OPEN, fd, path, __spawn_open_flags__(nil, fd), 0o644]]
    when Array
      if value[0] == :child
        deferred << [SPAWN_DUP2, fd, __spawn_key_fds__(value[1]).first]
        []
      else
        path = value[0]
        path = path.to_path if !path.is_a?(String) && path.respond_to?(:to_path)
        path = __check_spawn_string__(path, "path name")
        [[SPAWN_OPEN, fd, path, __spawn_open_flags__(value[1], fd), value[2] || 0o644]]
      end
    else
      io = __spawn_to_io__(value)
      raise ArgumentError, "wrong exec redirect: #{value.inspect}" if io.nil?
      [[SPAWN_DUP2, fd, __spawn_native_fd__(io.fileno)]]
    end
  end

  def self.__spawn_native_fd__(fd)
    native = __native_fd__(fd)
    raise ArgumentError, "wrong exec redirect: #{fd.inspect}" if native < 0
    native
  end

  def self.__spawn_actions__(options)
    actions = []
    deferred = []

    if (dir = options[:chdir])
      dir = dir.to_path if !dir.is_a?(String) && dir.respond_to?(:to_path)
      dir = __check_spawn_string__(dir, "path name")
      raise Errno::ENOENT, dir unless File.directory?(dir)
      actions << [SPAWN_CHDIR, dir]
    end

    options.each do |key, value|
      next unless __spawn_redirect_key?(key)
      first = nil
      __spawn_key_fds__(key).each do |fd|
        if first.nil?
          actions.concat(__spawn_redirect__(fd, value, deferred))
          first = fd
        else
          # [:out, :err] => "file" opens the file once and points both descriptors at
          # it; opening it twice would give them separate offsets, so whichever wrote
          # second would overwrite the other.
          actions << [SPAWN_DUP2, fd, first]
        end
      end
    end

    actions.concat(deferred)
    actions
  end

  def self.__spawn_env__(env, unset_others)
    result = unset_others ? {} : ENV.to_hash
    if env
      env.each do |key, value|
        value.nil? ? result.delete(key) : result[key] = value
      end
    end
    result.map { |key, value| "#{key}=#{value}" }
  end

  def self.__resolve_executable__(name, search_path = nil)
    raise Errno::ENOENT, name if name.empty?

    named = name.include?(File::SEPARATOR) || (File::ALT_SEPARATOR && name.include?(File::ALT_SEPARATOR))
    if named
      candidates = [name]
    else
      path = (search_path || ENV["PATH"]).to_s
      candidates = path.split(File::PATH_SEPARATOR).map { |dir| File.join(dir.empty? ? "." : dir, name) }
    end

    # On Windows an extensionless name stands for name.exe / name.bat / ...; the bare
    # name is tried first, as CreateProcess does.
    extensions = __spawn_path_extensions__
    if extensions.size > 1 && File.extname(name).empty?
      candidates = candidates.flat_map { |c| extensions.map { |ext| c + ext } }
    end

    candidates.each do |candidate|
      next unless File.exist?(candidate)
      # A file that cannot be run is a reason to refuse only when the caller named it. A
      # PATH search that turns one up simply keeps looking, and ends in ENOENT if nothing
      # runnable is there - naming the command, not the last unusable file that matched.
      # Windows has no execute bit - what makes a file runnable is its extension, which
      # the PATHEXT pass above has already decided - so only the directory test applies.
      runnable = File.directory?(candidate) ? false : (SPAWN_WINDOWS || File.executable?(candidate))
      unless runnable
        next unless named
        raise Errno::EACCES, candidate
      end
      return candidate
    end
    raise Errno::ENOENT, name
  end

  def self.__parse_spawn_args__(args)
    args = args.dup
    env = nil
    options = {}

    if !args.empty? && args.first.respond_to?(:to_hash) && !args.first.is_a?(String)
      env = args.shift.to_hash
    end
    # The options hash is only recognised behind a command, except that an env hash has
    # already been taken off the front - so `spawn({}, {})` is env plus options and no
    # command, not env plus a command that happens to be a Hash.
    if args.size > 1 || (env && args.size == 1)
      if !args.empty? && args.last.respond_to?(:to_hash) && !args.last.is_a?(String)
        options = args.pop.to_hash
      end
    end
    raise ArgumentError, "wrong number of arguments (given 0, expected 1+)" if args.empty?

    if env
      env = env.each_with_object({}) do |(k, v), h|
        key = __check_spawn_string__(k, "environment name")
        raise ArgumentError, "environment name contains a equal : #{key}" if key.include?("=")
        h[key] = v.nil? ? nil : __check_spawn_string__(v, "environment value")
      end
    end

    options.each_key do |key|
      next if __spawn_redirect_key?(key)
      raise ArgumentError, "wrong exec option" if key.is_a?(String)
      unless SPAWN_OPTION_KEYS.include?(key)
        raise ArgumentError, "wrong exec option symbol: #{key}"
      end
    end

    [env, args, options]
  end

  # [executable, argv] for the command part of a spawn call.
  def self.__spawn_command__(args, env)
    search_path = env && env["PATH"]
    first = args.first
    array_form = first.is_a?(Array) || (!first.is_a?(String) && first.respond_to?(:to_ary))

    if args.size == 1 && !array_form
      command = __check_spawn_string__(first, "command")
      # a command line is bytes to the shell; one that is not valid in its encoding still runs
      if (command.valid_encoding? ? command : command.b) =~ SPAWN_SHELL_META
        return __spawn_shell__(command)
      end
      words = command.split(" ")
      raise Errno::ENOENT, command if words.empty?
      builtins = SPAWN_WINDOWS ? SPAWN_CMD_BUILTINS : SPAWN_SHELL_BUILTINS
      if builtins.include?(SPAWN_WINDOWS ? words.first.downcase : words.first)
        return __spawn_shell__(command)
      end
      return [__resolve_executable__(words.first, search_path), words]
    end

    if array_form
      pair = first.to_ary
      raise ArgumentError, "wrong first argument" unless pair.size == 2
      name  = __check_spawn_string__(pair[0], "command")
      argv0 = __check_spawn_string__(pair[1], "command")
    else
      name = __check_spawn_string__(first, "command")
      argv0 = name
    end

    rest = args[1..-1].map { |a| __check_spawn_string__(a, "string") }
    [__resolve_executable__(name, search_path), [argv0] + rest]
  end

  # Everything posix_spawn needs, plus the options the caller still has to act on.
  def self.__spawn_setup__(args)
    env, command, options = __parse_spawn_args__(args)

    [:unsetenv_others, :close_others, :new_pgroup].each do |key|
      next unless options.key?(key)
      value = options[key]
      unless value.nil? || value == true || value == false
        raise ArgumentError, "expected true or false as #{key}: #{value}"
      end
    end

    pgroup = nil
    if options.key?(:pgroup)
      value = options[:pgroup]
      case value
      when true
        pgroup = 0
      when false, nil
        pgroup = nil
      else
        unless value.is_a?(Integer)
          unless value.respond_to?(:to_int) && !value.is_a?(Symbol)
            raise TypeError, "no implicit conversion of #{value.class} into Integer"
          end
          value = value.to_int
        end
        raise ArgumentError, "negative process group ID : #{value}" if value < 0
        pgroup = value
      end
    end

    file, argv = __spawn_command__(command, env)
    [file, argv, __spawn_env__(env, options[:unsetenv_others]), __spawn_actions__(options), pgroup, options]
  end

  # :umask has no posix_spawn equivalent, and the value is inherited, so set it here
  # and put it back afterwards.
  def self.__with_umask__(mask)
    return yield if mask.nil?
    previous = File.umask(mask.to_int)
    begin
      yield
    ensure
      File.umask(previous)
    end
  end

  def self.spawn(*args)
    file, argv, envp, actions, pgroup, options = __spawn_setup__(args)
    close_others = options[:close_others] ? true : false
    __with_umask__(options[:umask]) do
      result = __spawn__(file, argv, envp, actions, pgroup, close_others)
      # An executable file with no #! line is a shell script to execve(2), which refuses it
      # (ENOEXEC); MRI then runs it with /bin/sh, as a shell would.
      if result == -8 && !SPAWN_WINDOWS && file != "/bin/sh"
        result = __spawn__("/bin/sh", ["sh", file] + argv[1..-1], envp, actions, pgroup, close_others)
      end
      __check__(result, file)
    end
  rescue SystemCallError
    # MRI forks first and only then discovers that the command cannot be run, so the child
    # it already has exits with 127 and $? says so even though spawn itself raises.
    __set_last_status__(__make_status__(-1, 127 << 8))
    raise
  end

  # Process.exec really does replace this process: execve(2) only returns on failure.
  # Anything Ruby still has buffered would be lost with the address space, so it is
  # flushed first.
  def self.exec(*args)
    # MRI takes close-on-exec off every descriptor the options name before it so much as
    # looks at the command, so that one named in a redirection is still usable if the exec
    # turns out to be impossible.
    args.each do |argument|
      next if argument.is_a?(String) || argument.is_a?(Array) || !argument.respond_to?(:to_hash)
      argument.to_hash.each_key do |key|
        next unless __spawn_redirect_key?(key)
        __spawn_key_fds__(key).each { |fd| __set_cloexec__(fd, false) }
      end
    end

    file, argv, envp, actions, _pgroup, options = __spawn_setup__(args)
    File.umask(options[:umask].to_int) if options[:umask]
    [$stdout, $stderr].each do |io|
      begin
        io.flush
      rescue StandardError
      end
    end
    __check__(__exec__(file, argv, envp, actions), file)
  end

  # --- POSIX calls that come back as -errno ---------------------------------------
  #
  # Most of the Errno family is defined in Ruby further up this file, so there is no
  # CLR class for the runtime to throw. Mapping the number here instead gives every
  # one of them - ESRCH, EPERM, EINVAL and the rest - without any C# counterpart.

  def self.__errno_class__(errno)
    @errno_classes ||= Errno.constants.each_with_object({}) do |name, h|
      klass = Errno.const_get(name)
      number = (klass.const_get(:Errno) rescue nil) if klass.is_a?(Class)
      h[number] ||= klass if number.is_a?(Integer)
    end
    @errno_classes[errno] || SystemCallError
  end

  def self.__check__(result, message = nil)
    if result.is_a?(Integer) && result < 0
      klass = __errno_class__(-result)
      # `raise klass, nil` is not the same call as `raise klass`: the two-argument form
      # goes looking for Exception.exception(message) and the runtime cannot pick an
      # overload for a nil argument.
      raise klass, message if message
      raise klass
    end
    result
  end

  # The CLR half of kill hands back -errno rather than throwing, for the same reason.
  class << self
    unless method_defined?(:__clr_kill__) || private_method_defined?(:__clr_kill__)
      alias :__clr_kill__ :kill

      def kill(signal, *pids)
        Process.__check__(__clr_kill__(signal, *pids))
      end
    end
  end

  unless respond_to?(:getrlimit)
    def getrlimit(resource)
      resource = __rlimit_resource__(resource)
      result = __getrlimit__(resource)
      __check__(result) if result.is_a?(Integer)
      result
    end
    module_function :getrlimit

    def setrlimit(resource, soft, hard = nil)
      resource = __rlimit_resource__(resource)
      soft = __rlimit_value__(soft)
      hard = hard.nil? ? soft : __rlimit_value__(hard)
      __check__(__setrlimit__(resource, soft, hard))
      nil
    end
    module_function :setrlimit

    # MRI takes the number, or the constant's name with or without the RLIMIT_ prefix.
    # Anything else is asked for #to_str first and only then for #to_int, which is the order
    # MRI's rlimit_resource_type uses.
    def __rlimit_resource__(resource)
      case resource
      when Integer then resource
      when Symbol, String then __rlimit_by_name__(resource.to_s, resource)
      else
        if resource.respond_to?(:to_str)
          name = resource.to_str
          return __rlimit_by_name__(name, resource) if name.is_a?(String)
        end
        unless resource.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{resource.class} into Integer"
        end
        value = resource.to_int
        unless value.is_a?(Integer)
          raise TypeError, "can't convert #{resource.class} to Integer"
        end
        value
      end
    end
    module_function :__rlimit_resource__

    def __rlimit_by_name__(name, original)
      name = "RLIMIT_#{name}" unless name.start_with?("RLIMIT_")
      unless const_defined?(name)
        raise ArgumentError, "invalid resource name: #{original}"
      end
      const_get(name)
    end
    module_function :__rlimit_by_name__

    # rlim_t is unsigned, so RLIM_INFINITY is 2**64-1 and getrlimit hands it back that way.
    # __setrlimit__ takes it as a signed long and reinterprets the bits, so anything above
    # 2**63-1 has to be folded into the negative half first or the conversion overflows.
    def __rlimit_value__(value)
      unless value.is_a?(Integer)
        unless value.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{value.class} into Integer"
        end
        value = value.to_int
        unless value.is_a?(Integer)
          raise TypeError, "can't convert to Integer"
        end
      end
      # Named inline rather than as constants: Process.constants is part of the API and
      # ruby/spec walks everything matching /\ARLIMIT_/ through getrlimit.
      value > 0x7fff_ffff_ffff_ffff ? value - 0x1_0000_0000_0000_0000 : value
    end
    module_function :__rlimit_value__
  end

  unless respond_to?(:getpgid)
    def getpgid(pid)
      __check__(__getpgid__(pid.to_int))
    end
    module_function :getpgid

    def setpgid(pid, pgid)
      __check__(__setpgid__(pid.to_int, pgid.to_int))
      0
    end
    module_function :setpgid

    def getsid(pid = 0)
      __check__(__getsid__(pid.to_int))
    end
    module_function :getsid

    def setsid
      __check__(__setsid__)
    end
    module_function :setsid
  end

  unless respond_to?(:getpgrp)
    def getpgrp
      __check__(__getpgid__(0))
    end
    module_function :getpgrp

    def setpgrp
      __check__(__setpgid__(0, 0))
      0
    end
    module_function :setpgrp
  end

  # The real and effective ids. MRI takes a name as well as a number for the ones that
  # name a user or a group, and the C# side hands back -errno so that EPERM comes out as
  # Errno::EPERM rather than as a bare failure.
  def self.uid=(value)
    __check__(__setuid__(__user_id__(value)))
    value
  end

  def self.euid=(value)
    __check__(__seteuid__(__user_id__(value)))
    value
  end

  def self.gid=(value)
    __check__(__setgid__(__group_id__(value)))
    value
  end

  def self.egid=(value)
    __check__(__setegid__(__group_id__(value)))
    value
  end

  def self.groups=(list)
    gids = list.to_ary.map { |gid| __group_id__(gid) }
    __check__(__setgroups__(gids))
    list
  end

  def self.initgroups(user, group)
    __check__(__initgroups__(user.to_s, __group_id__(group)))
    groups
  end

  unless respond_to?(:getpriority)
    def getpriority(which, who)
      result = __getpriority__(which.to_int, who.to_int)
      result <= -1000 ? __check__(result + 1000) : result
    end
    module_function :getpriority

    def setpriority(which, who, priority)
      __check__(__setpriority__(which.to_int, who.to_int, priority.to_int))
      0
    end
    module_function :setpriority
  end

  unless const_defined?(:Sys)
    module Sys
      def getuid;   Process.uid;  end
      def geteuid;  Process.__geteuid__; end
      def getgid;   Process.gid;  end
      def getegid;  Process.__getegid__; end
      def issetugid; Process.__issetugid__; end
      module_function :getuid, :geteuid, :getgid, :getegid, :issetugid
    end
  end

  unless const_defined?(:UID)
    module UID
      def rid;  Process.uid; end
      def eid;  Process.euid; end
      def sid_available?; false; end
      module_function :rid, :eid, :sid_available?
    end
  end

  unless const_defined?(:GID)
    module GID
      def rid;  Process.gid; end
      def eid;  Process.egid; end
      def sid_available?; false; end
      module_function :rid, :eid, :sid_available?
    end
  end

  # waitpid(2) itself, so that a child that was killed is reported as killed rather
  # than as having exited with 128 plus the signal number.
  def self.__wait2__(pid, flags)
    result = __waitpid__(__integer_value__(pid), __integer_value__(flags))
    return nil if result.nil?
    __check__(result)
    result
  end

  def self.wait(pid = -1, flags = 0)
    result = __wait2__(pid, flags)
    result && result[0]
  end

  def self.wait2(pid = -1, flags = 0)
    __wait2__(pid, flags)
  end

  # Not four methods that call one another: MRI defines two and aliases them, and a spec
  # compares Process.method(:waitpid) with Process.method(:wait).
  class << self
    alias :waitpid :wait
    alias :waitpid2 :wait2
  end

  def self.waitall
    results = []
    loop do
      begin
        pair = __wait2__(-1, 0)
      rescue SystemCallError
        break
      end
      break if pair.nil?
      results << pair
    end
    results
  end

  # Daemonising means forking, and there is no fork on the CLR. MRI defines the method
  # on platforms that cannot do it too, as the function that raises NotImplementedError,
  # and that function is exactly the case respond_to? answers false for - so portable
  # code asks whether the feature is there rather than whether the name is.
  # On Windows the resource-limit and priority functions are the same kind of stub in
  # MRI; the libc behind ours is not there to call. So are the process-group, session,
  # supplementary-group and set-id ones: win32/Makefile.sub defines none of getpgid,
  # setpgid, getsid, setsid, getpgrp, setpgrp, getgroups, setgroups, initgroups or the
  # setuid/setgid family, and process.c makes each of these methods rb_f_notimplement.
  WINDOWS_NOT_IMPLEMENTED_METHODS__ = [:getrlimit, :setrlimit, :getpriority, :setpriority,
    :getpgid, :setpgid, :getsid, :setsid, :getpgrp, :setpgrp,
    :groups, :groups=, :initgroups, :maxgroups, :maxgroups=,
    :uid=, :gid=, :euid=, :egid=].freeze unless defined?(WINDOWS_NOT_IMPLEMENTED_METHODS__)
  unless defined?(NOT_IMPLEMENTED_METHODS)
    NOT_IMPLEMENTED_METHODS = (::File::ALT_SEPARATOR == "\\" ?
      [:daemon, :fork, :_fork] + WINDOWS_NOT_IMPLEMENTED_METHODS__ : [:daemon, :fork, :_fork]).freeze
  end
  __hide_constant__ :WINDOWS_NOT_IMPLEMENTED_METHODS__, :NOT_IMPLEMENTED_METHODS

  if ::File::ALT_SEPARATOR == "\\"
    WINDOWS_NOT_IMPLEMENTED_METHODS__.each do |name|
      define_method(name) { |*| raise NotImplementedError, "#{name}() function is unimplemented on this machine" }
      module_function name
    end
  end

  unless respond_to?(:daemon)
    def daemon(nochdir = nil, noclose = nil)
      raise NotImplementedError, "daemon() function is unimplemented on this machine"
    end
    module_function :daemon
  end

  # Process.fork and Process._fork exist for the same reason daemon does: MRI defines
  # them everywhere and lets respond_to? be the portable answer. Kernel#fork is private
  # and so was never reachable as Process.fork.
  unless singleton_class.method_defined?(:fork)
    def self.fork(&block)
      raise NotImplementedError, "fork() function is unimplemented on this machine"
    end

    def self._fork
      raise NotImplementedError, "fork() function is unimplemented on this machine"
    end
  end

  # The resolution of the clock we would actually read, per clock. The two named after
  # the calls MRI emulates them with have the resolution of those calls, whatever the
  # platform timer manages.
  CLOCK_RESOLUTIONS__ = {
    "GETTIMEOFDAY_BASED_CLOCK_REALTIME" => 1.0e-6,
    "TIME_BASED_CLOCK_REALTIME" => 1.0,
    "GETRUSAGE_BASED_CLOCK_PROCESS_CPUTIME_ID" => 1.0e-6,
    "CLOCK_BASED_CLOCK_PROCESS_CPUTIME_ID" => 1.0e-6,
    "TIMES_BASED_CLOCK_PROCESS_CPUTIME_ID" => 1.0e-2,
    "TIMES_BASED_CLOCK_MONOTONIC" => 1.0e-2,
    "MACH_ABSOLUTE_TIME_BASED_CLOCK_MONOTONIC" => 1.0e-9,
  }.freeze unless defined?(CLOCK_RESOLUTIONS__)
  # not a clock: kept out of Process.constants, which ruby/spec feeds to clock_gettime one by one
  __hide_constant__ :CLOCK_RESOLUTIONS__

  def self.respond_to?(name, include_all = false)
    return false if NOT_IMPLEMENTED_METHODS.include?(name.to_sym)
    Module.instance_method(:respond_to?).bind(self).call(name, include_all)
  end
end

class Process::Status
  # Process::Status.wait is Process.wait2 without the side effect on $?; it is what a
  # library uses when it must not disturb the status the program is looking at. Having
  # no children at all is not an error here - it answers with a status whose pid is -1.
  def self.wait(pid = -1, flags = 0)
    previous = $?
    begin
      pair = Process.waitpid2(pid, flags)
      pair && pair[1]
    rescue Errno::ECHILD
      Process.__make_status__(-1, 0)
    ensure
      Process.__set_last_status__(previous)
    end
  end unless respond_to?(:wait)
end

module Kernel
  # system, exec, spawn and the backquotes are all Process.spawn underneath; they
  # differ only in what they do with the child once it is running. Doing the argument
  # handling once, in Process, is what gives all four the env hash, the
  # [command, argv0] form and the options hash - the implementations they replace
  # understood only "a string, optionally followed by more strings", so every one of
  # them raised or silently mis-ran anything else.

  def spawn(*args)
    Process.spawn(*args)
  end
  module_function :spawn

  def exec(*args)
    Process.exec(*args)
  end
  module_function :exec

  def system(*args)
    options = (args.size > 1 && args.last.respond_to?(:to_hash) && !args.last.is_a?(String)) ? args.last.to_hash : {}
    exception = options[:exception]
    unless exception.nil? || exception == true || exception == false
      ::Kernel.raise(::ArgumentError, "expected true or false as exception: #{exception.inspect}")
    end

    begin
      pid = Process.spawn(*args)
    rescue SystemCallError
      # MRI reports "could not even start it" as nil rather than as false.
      raise if exception
      return nil
    end

    Process.waitpid(pid)
    status = $?
    return true if status.success?
    if exception
      if status.exitstatus
        raise RuntimeError, "Command failed with status (#{status.exitstatus}): #{args.first}"
      else
        raise RuntimeError, "Command failed with signal #{status.termsig}: #{args.first}"
      end
    end
    false
  end
  module_function :system

  def `(command)
    file, argv, envp, actions, = Process.__spawn_setup__([command])
    Process.__backquote__(file, argv, envp, actions)
  end
  module_function :`
end

# MRI's default SIGINT disposition raises Interrupt on the main thread; left to the
# platform the signal just ends the process. Installing Ruby's default does that.
if ::File::ALT_SEPARATOR.nil? && defined?(::Signal.trap)
  begin
    ::Signal.trap("INT", "DEFAULT")
  rescue ::StandardError, ::NotImplementedError
  end
end

class SignalException
  # MRI's #initialize names a signal, by number or by name, rather than taking a
  # message: a number may be given a message alongside it, a name may not, and
  # anything that is neither is an ArgumentError. The CLR-backed class carries only
  # the message, so the number and the name it was built from are kept here.
  def initialize(signal = (missing = true), message = (no_message = true))
    if missing
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 0, expected 1)")
    end

    case signal
    when ::Integer
      # Any number below the platform's NSIG will do, listed or not ("SIG16"); NSIG is 65 on
      # Linux, 32 on macOS and 23 for the Windows C runtime.
      nsig = ::File::ALT_SEPARATOR ? 23 : ::RUBY_PLATFORM.include?("darwin") ? 32 : 65
      if signal < 0 || signal > nsig
        ::Kernel.raise(::ArgumentError, "invalid signal number (#{signal})")
      end
      name = ::Signal.list.key(signal) || signal
      @__signo__ = signal
      @__signm__ = no_message ? "SIG#{name}" : message.to_s
    when ::String, ::Symbol
      unless no_message
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 2, expected 1)")
      end
      name = signal.to_s.sub(/\ASIG/, "")
      number = ::Signal.list[name]
      unless number
        ::Kernel.raise(::ArgumentError, "unsupported signal 'SIG#{name}'")
      end
      @__signo__ = number
      @__signm__ = "SIG#{name}"
    else
      ::Kernel.raise(::ArgumentError, "bad signal type #{signal.class}")
    end

    super(@__signm__)
  end

  # MRI names the signal both ways round: #signm is "SIGTERM" and #signo is 15.
  # A SignalException the runtime raises itself is built from its message alone,
  # so both still answer from the message when #initialize did not run.
  def signm
    defined?(@__signm__) ? @__signm__ : message
  end

  def signo
    return @__signo__ if defined?(@__signo__)
    ::Signal.list[message.to_s.sub(/\ASIG/, "")]
  end
end

class Interrupt
  # Interrupt is always SIGINT, and unlike its superclass it takes a plain
  # optional message.
  def initialize(message = nil)
    @__signo__ = ::Signal.list["INT"]
    @__signm__ = message.nil? ? "Interrupt" : message.to_s
    __signal_exception_super__(@__signm__)
  end

  private def __signal_exception_super__(message)
    ::Exception.instance_method(:initialize).bind(self).call(message)
  end

  # also for one the runtime raised for a SIGINT without going through #initialize
  def signo
    defined?(@__signo__) ? @__signo__ : ::Signal.list["INT"]
  end
end

# Random (1.9.2) — the runtime only exposes Kernel#rand/srand.
unless defined?(Random)
  class Random
    # MRI's generator is MT19937 (see MersenneTwister.cs), seeded and consumed exactly as
    # random.c does, so a given seed produces the same numbers, bytes and marshal data here.
    @__mt_class__ = System::Type.get_type("IronRuby.Builtins.MersenneTwister, IronRuby.Libraries").to_class
    def self.__mt_class__ = @__mt_class__
    private_class_method :__mt_class__

    def initialize(seed = Random.new_seed)
      # A seed is an Integer however it was given: Random.new(42.5) and Random.new(42)
      # are the same generator, and #seed answers 42 for both.
      unless seed.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{seed.class} into Integer")
      end
      @__seed__ = seed.to_int
      @__mt__ = ::Random.__send__(:__mt_class__).new(@__seed__)
    end

    def initialize_copy(other)
      super
      @__mt__ = other.__send__(:__ir_hidden_ivar__, :@__mt__).copy
      self
    end

    def seed = @__seed__

    # rand_mt_state / rand_mt_left: the state vector as one Integer, and how many words
    # of it are still to be handed out.
    def state
      @__mt__.get_state
    end
    private :state

    def left
      @__mt__.left
    end
    private :left

    def ==(other)
      other.is_a?(::Random) && self.class == other.class &&
        @__mt__.state_equals(other.__send__(:__ir_hidden_ivar__, :@__mt__)) && seed == other.seed
    end

    def rand(limit = nil)
      case limit
      when nil then @__mt__.genrand_real(true)
      when ::Range then __rand_in_range__(limit)
      when ::Float
        unless limit > 0
          ::Kernel.raise(::ArgumentError, "invalid argument - #{limit}")
        end
        @__mt__.genrand_real(true) * limit
      else
        n = limit.to_int
        unless n > 0
          ::Kernel.raise(::ArgumentError, "invalid argument - #{limit}")
        end
        __rand_below__(n)
      end
    end

    # An Integer uniformly in 0...n: limited_rand on n - 1.
    def __rand_below__(n)
      @__mt__.limited_rand(n - 1)
    end
    private :__rand_below__

    # MRI drives this entirely off `end - begin`, which is what makes it work for
    # Time and for any object that answers #-, #+ and #to_int or #to_f. nil comes
    # back for a range that cannot produce a value.
    def __rand_in_range__(range)
      first = range.begin
      last = range.end
      return nil if first.nil? || last.nil?

      span = last - first
      if !span.is_a?(::Float) && span.respond_to?(:to_int)
        n = span.to_int
        n += 1 unless range.exclude_end?
        return nil if n <= 0
        first + __rand_below__(n)
      else
        width = span.to_f
        return nil if width < 0 || (width == 0 && range.exclude_end?)
        # random_real: [0, 1) for an exclusive range, [0, 1] for an inclusive one.
        first + @__mt__.genrand_real(range.exclude_end?) * width
      end
    end
    private :__rand_in_range__

    def bytes(count)
      @__mt__.genrand_bytes(count.to_int).to_a.pack("C*")
    end

    # rand_mt_dump: [state, left, seed], which is what MRI writes and reads back.
    def marshal_dump
      [state, left, @__seed__]
    end
    private :marshal_dump

    def marshal_load(data)
      state, left, seed = data
      @__seed__ = seed
      @__mt__ = ::Random.__send__(:__mt_class__).new(0)
      @__mt__.set_state(state, left)
      self
    end
    private :marshal_load

    # fill_random_seed: 128 bits of entropy.
    def self.new_seed
      Random.urandom(16).unpack("Q<2").then { |lo, hi| (hi << 64) | lo }
    end

    def self.__default__
      @__default__ ||= ::Random.new
    end
    private_class_method :__default__

    def self.rand(limit = nil)
      ::Random.__send__(:__default__).rand(limit)
    end

    def self.bytes(count)
      ::Random.__send__(:__default__).bytes(count)
    end

    def self.seed
      ::Random.__send__(:__default__).seed
    end

    # rand_srand: reseeds the default generator and answers the seed it had.
    def self.srand(number = new_seed)
      previous = ::Random.__send__(:__default__).seed
      ::Random.__send__(:__ir_set_hidden_ivar__, :@__default__, ::Random.new(number))
      previous
    end
  end
end

# nil converts to the zero of every numeric kind and to the empty Hash.
class NilClass
  def to_r
    ::Kernel.Rational(0, 1)
  end unless method_defined?(:to_r)

  # The argument is the precision to approximate to, and there is nothing to
  # approximate; MRI still insists there is at most one of them.
  def rationalize(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    ::Kernel.Rational(0, 1)
  end unless method_defined?(:rationalize)

  def to_c
    ::Kernel.Complex(0, 0)
  end unless method_defined?(:to_c)

  def to_h
    {}
  end unless method_defined?(:to_h)
end

# MRI keeps the BasicObject constant on BasicObject itself, where Object inherits it from;
# IronRuby's runtime defines it on Object, so BasicObject.constants comes up empty.
unless ::BasicObject.const_defined?(:BasicObject, false)
  ::BasicObject.const_set(:BasicObject, ::BasicObject)
end

# There is one nil, one true and one false, and no way to ask for another.
[::NilClass, ::TrueClass, ::FalseClass].each do |klass|
  klass.singleton_class.send(:undef_method, :new) if klass.respond_to?(:new)
end

class Random
  # Real entropy from the OS. /dev/urandom is the same source MRI uses on Unix.
  # Windows has no such file - and reading one that does not exist is how
  # Tempfile, Dir.mktmpdir, SecureRandom and net/http all used to die there - so
  # off Unix this goes to .NET's CSPRNG instead. System.Security.Cryptography is
  # not one of the assemblies the host pre-references, hence the load_assembly.
  unless respond_to?(:urandom)
    if ::File.exist?("/dev/urandom")
      def self.urandom(count)
        File.open("/dev/urandom", "rb") { |f| f.read(count) }
      end
    else
      load_assembly "System.Security.Cryptography"
      def self.urandom(count)
        count = ::Kernel.Integer(count)
        ::System::Security::Cryptography::RandomNumberGenerator.get_bytes(count).to_a.pack("C*")
      end
    end
  end
end

module Kernel
  # Kernel#rand is more forgiving than Random#rand: it takes a Range (1.9.3), it
  # ignores the sign of a numeric limit, and a limit that truncates to 0 -- which
  # includes every Float below 1 -- means "give me a Float" rather than an error.
  # The builtin only ever understood a positive Integer.
  unless private_method_defined?(:__ir_rand__)
    alias :__ir_rand__ :rand
    private :__ir_rand__

    # rb_f_rand draws from the same default generator as Random.rand, which is
    # what Random.srand seeds.
    def rand(limit = nil)
      return ::Random.rand if limit.nil?
      return ::Random.rand(limit) if limit.is_a?(::Range)

      unless limit.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{limit.class} into Integer")
      end
      n = limit.to_int.abs
      n == 0 ? ::Random.rand : ::Random.rand(n)
    end
    module_function :rand

    def srand(*args)
      ::Random.srand(*args)
    end
    module_function :srand
  end
end

module ObjectSpace
  # 3.2's map with weakly-held keys compared by equality rather than identity.
  # Unlike WeakMap (Builtins/WeakMap.cs) the references here are strong, so
  # entries outlive what MRI would collect, which is safe but not weak.
  class WeakKeyMap
    # Keys are looked up by #hash and #eql?, like a Hash, but kept as given: a Hash would store a
    # frozen copy of a String key instead.
    class Key # :nodoc:
      attr_reader :object, :hash

      def initialize(object)
        @object = object
        @hash = object.__send__(:hash)
      end

      def eql?(other)
        @object.equal?(other.object) || @object.eql?(other.object)
      end
    end
    __hide_constant__ :Key

    def initialize
      @__table__ = {}
    end

    def [](key)
      entry = @__table__[Key.new(key)]
      entry && entry[1]
    end

    def []=(key, value)
      # MRI refuses a key it could not hold weakly.
      case key
      when ::Integer, ::Float, ::Symbol, ::TrueClass, ::FalseClass, ::NilClass
        ::Kernel.raise(::ArgumentError, "WeakKeyMap keys must be garbage collectable")
      end
      # an equal key already there is replaced by this one, as in MRI
      k = Key.new(key)
      @__table__.delete(k)
      @__table__[k] = [key, value]
      value
    end

    def delete(key)
      entry = @__table__.delete(Key.new(key))
      if entry
        entry[1]
      elsif block_given?
        yield key
      end
    end

    # The key already in the map that is equal to the one given.
    def getkey(key)
      entry = @__table__[Key.new(key)]
      entry && entry[0]
    end

    def key?(key)
      @__table__.key?(Key.new(key))
    end

    def clear
      @__table__.clear
      self
    end

    def inspect
      "#<ObjectSpace::WeakKeyMap:0x#{(object_id << 1).to_s(16).rjust(16, '0')} size=#{@__table__.size}>"
    end

    # MRI's map is native data Marshal cannot write; this one dumped its table.
    def marshal_dump
      ::Kernel.raise(::TypeError, "no _dump_data is defined for class #{self.class}")
    end
    private :marshal_dump
  end unless const_defined?(:WeakKeyMap)
end

# Class.try_convert (1.9): the conversion protocol, returning nil instead of raising.
class String
  def self.try_convert(obj)
    __ir_try_convert__(obj, :to_str, ::String, "String")
  end unless respond_to?(:try_convert)
end

class Integer
  def self.try_convert(obj)
    __ir_try_convert__(obj, :to_int, ::Integer, "Integer")
  end unless respond_to?(:try_convert)
end

# Binding had nothing but the CLR accessor for its receiver, so all of
# spec/core/binding errored. Kernel#eval already accepts a binding, and every
# one of these can be asked of the binding through it.
class Binding
  def receiver
    self.SelfObject
  end unless method_defined?(:receiver)

  def eval(code, file = nil, line = nil)
    return ::Kernel.eval(code, self, file, line || 1) if file

    # Kernel#eval names an unnamed eval after the place it was written, and that place is
    # this method unless it is named here: the caller of Binding#eval is the one that wrote it.
    at = caller_locations(1, 1)
    at = at && at.first
    return ::Kernel.eval(code, self) unless at
    ::Kernel.eval(code, self, "(eval at #{at.path}:#{at.lineno})", line || 1)
  end unless method_defined?(:eval)

  def local_variables
    eval("local_variables")
  end unless method_defined?(:local_variables)

  # A keyword argument named after a reserved word (`alias:`, `if:`, `class:`) is a local
  # that no source text can name, so neither `defined?(alias)` nor `alias` can be eval'd:
  # those are looked up rather than evaluated.
  KEYWORD_LOCAL_NAMES__ = %w[BEGIN END alias and begin break case class def defined? do else
    elsif end ensure for if in module next not or redo rescue retry return super then undef
    unless until when while yield].freeze # :nodoc:
  __hide_constant__ :KEYWORD_LOCAL_NAMES__

  def local_variable_defined?(name)
    name = __check_lvar_name__(name)
    if KEYWORD_LOCAL_NAMES__.include?(name)
      return eval("local_variables").include?(name.to_sym)
    end
    eval("defined?(#{name}) == 'local-variable'")
  end unless method_defined?(:local_variable_defined?)

  def local_variable_get(name)
    name = __check_lvar_name__(name)
    unless local_variable_defined?(name)
      ::Kernel.raise(::NameError, "local variable '#{name}' is not defined for #{inspect}")
    end
    return __ir_resolve_local__(name) if KEYWORD_LOCAL_NAMES__.include?(name)
    eval(name)
  end unless method_defined?(:local_variable_get)

  # The value cannot be written into the eval'd source, so it is parked in a
  # thread-local and read back out from inside the binding.
  def local_variable_set(name, value)
    name = __check_lvar_name__(name)
    ::Thread.current[:__ir_binding_value__] = value
    eval("#{name} = ::Thread.current[:__ir_binding_value__]")
    value
  end unless method_defined?(:local_variable_set)

  # Only a plain identifier names a local; a global, an instance variable or a
  # special variable such as $~ is a NameError rather than a way in.
  def __check_lvar_name__(name)
    name = __ir_variable_name__(name)
    unless name =~ /\A[_\p{Alpha}][_\p{Alnum}]*\z/
      ::Kernel.raise(::NameError, "wrong local variable name '#{name}' for #{inspect}")
    end
    # Ruby 4.0: _1.._9 are never locals, whether or not the block uses them
    if name =~ /\A_[1-9]\z/
      ::Kernel.raise(::NameError, "numbered parameter '#{name}' is not a local variable")
    end
    name
  end
  private :__check_lvar_name__
end

class IO
  def self.try_convert(obj)
    __ir_try_convert__(obj, :to_io, ::IO, "IO")
  end unless respond_to?(:try_convert)

  # 3.1's IO::Buffer, backed by a String rather than by mapped memory: there is
  # no zero-copy to be had here, so an "external" or "mapped" buffer is the
  # internal kind wearing a different flag. 160 of spec/core/io's errors were
  # this constant not existing at all.
  class Buffer
    include ::Comparable

    PAGE_SIZE = 4096
    DEFAULT_SIZE = 65536
    EXTERNAL = 1
    INTERNAL = 2
    MAPPED = 4
    SHARED = 8
    LOCKED = 32
    PRIVATE = 64
    READONLY = 128
    LITTLE_ENDIAN = 4
    BIG_ENDIAN = 8
    HOST_ENDIAN = LITTLE_ENDIAN
    NETWORK_ENDIAN = BIG_ENDIAN

    class AllocationError < ::RuntimeError; end
    class AccessError < ::RuntimeError; end
    class InvalidatedError < ::RuntimeError; end
    class LockedError < ::RuntimeError; end
    class MaskError < ::ArgumentError; end

    # MRI names nil/true/false by value rather than by class in the TypeErrors
    # rb_builtin_class_name produces.
    # MRI warns the first time a buffer is allocated in the process, and only
    # then: the experimental category is read at that moment and not again
    # (io_buffer_experimental), so code that allocates one with the category off
    # silences the warning for good. Every allocation goes through new or
    # allocate here.
    EXPERIMENTAL_WARNED__ = [false]
    __hide_constant__ :EXPERIMENTAL_WARNED__

    def self.__experimental__
      return if EXPERIMENTAL_WARNED__[0]
      EXPERIMENTAL_WARNED__[0] = true
      return unless ::Warning[:experimental]
      loc = caller_locations(1).find { |l| l.path != __FILE__ }
      ::Kernel.warn("#{"#{loc.path}:#{loc.lineno}: " if loc}warning: IO::Buffer is experimental and both the Ruby and C interface may change in the future!",
                    category: :experimental)
    end

    def self.allocate
      __experimental__
      super
    end

    def self.__class_name__(value)
      case value
      when nil then "nil"
      when true then "true"
      when false then "false"
      else value.class.to_s
      end
    end

    # Sizes and flags go through NUM2SIZET, which refuses anything that is not
    # already an Integer rather than asking it for #to_int.
    def self.__integer_arg__(value)
      ::Kernel.raise(::TypeError, "not an Integer") unless value.is_a?(::Integer)
      if value > 0xffffffffffffffff || value < -0x8000000000000000
        ::Kernel.raise(::RangeError, "bignum too big to convert into 'unsigned long long'")
      end
      value
    end

    # Offsets, on the other hand, are ordinary implicit conversions.
    def self.__offset_arg__(value)
      return value if value.is_a?(::Integer)
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{__class_name__(value)} into Integer")
      end
      value.to_int
    end

    # Under a page malloc is cheaper than mmap, which is the only reason a
    # buffer comes out INTERNAL rather than MAPPED when the flags are left out.
    def self.__flags_for_size__(size)
      size >= PAGE_SIZE ? MAPPED : INTERNAL
    end

    def self.for(string)
      unless string.is_a?(::String)
        unless string.respond_to?(:to_str)
          ::Kernel.raise(::TypeError, "no implicit conversion of #{__class_name__(string)} into String")
        end
        string = string.to_str
      end

      if block_given?
        # The buffer is a window onto the string itself, so the string is locked
        # against modification for as long as the window is open.
        buffer = allocate
        buffer.__take_over__(string, 0, string.bytesize,
                             EXTERNAL | (string.frozen? ? READONLY : 0), string)
        buffer.__lock_source__
        begin
          return yield(buffer)
        ensure
          buffer.free
        end
      end

      # Without a block nothing bounds the window's lifetime, so MRI takes a
      # frozen copy and the buffer over it is read-only.
      backing = string.frozen? ? string : string.dup.freeze
      buffer = allocate
      buffer.__take_over__(backing, 0, backing.bytesize, EXTERNAL | READONLY, backing)
      buffer
    end

    # Yields a buffer of the given size and answers what was written into it.
    def self.string(length)
      ::Kernel.raise(::LocalJumpError, "no block given") unless block_given?
      # The size goes to rb_str_new, whose parameter is a long rather than the
      # size_t the rest of the buffer API takes.
      unless length.is_a?(::Integer)
        ::Kernel.raise(::TypeError, "not an Integer")
      end
      if length > 0x7fffffffffffffff || length < -0x8000000000000000
        ::Kernel.raise(::RangeError, "bignum too big to convert into 'long'")
      end
      if length < 0
        ::Kernel.raise(::ArgumentError, "negative string size (or size too big)")
      end
      string = "\0".b * length
      buffer = allocate
      buffer.__take_over__(string, 0, length, EXTERNAL, string)
      begin
        yield buffer
      ensure
        buffer.free
      end
      string
    end

    def self.map(file, size = nil, offset = 0, flags = 0)
      offset = __offset_arg__(offset)
      ::Kernel.raise(::ArgumentError, "Offset can't be negative!") if offset < 0
      unless size.nil?
        size = __integer_arg__(size)
        ::Kernel.raise(::ArgumentError, "Size can't be negative!") if size < 0
      end
      flags = __integer_arg__(flags)

      file_size = file.size
      if file_size <= 0
        ::Kernel.raise(::ArgumentError, "Invalid negative or zero file size!")
      end
      size = file_size - offset if size.nil?
      ::Kernel.raise(::ArgumentError, "Size can't be zero!") if size == 0
      if size > file_size
        ::Kernel.raise(::ArgumentError, "Size can't be larger than file size!")
      end
      if offset + size > file_size
        ::Kernel.raise(::ArgumentError, "Offset too large!")
      end

      # A shared mapping asks mmap for PROT_WRITE, which the kernel refuses on a
      # descriptor that was not opened for writing; MAP_PRIVATE and a read-only
      # mapping do not need it.
      if (flags & (PRIVATE | READONLY)) == 0 && !file.__writable__?
        ::Kernel.raise(::Errno::EACCES, "io_buffer_map_file:mmap")
      end

      data = file.pread(size, offset)
      flags |= MAPPED
      flags |= EXTERNAL | SHARED if (flags & PRIVATE) == 0
      buffer = allocate
      buffer.__take_over__(data, 0, data.bytesize, flags, nil)
      buffer
    end

    def initialize(size = DEFAULT_SIZE, *flags)
      self.class.__experimental__
      size = self.class.__integer_arg__(size)
      ::Kernel.raise(::ArgumentError, "Size can't be negative!") if size < 0

      if flags.empty?
        flags = self.class.__flags_for_size__(size)
      else
        flags = self.class.__integer_arg__(flags[0])
        ::Kernel.raise(::ArgumentError, "Flags can't be negative!") if flags < 0
      end

      @offset = 0
      @source = nil
      @locked_source = false
      if size == 0
        # Nothing was allocated, so there is no memory for the flags to describe.
        @data = nil
        @size = 0
        @flags = 0
      else
        if (flags & (INTERNAL | MAPPED)) == 0
          ::Kernel.raise(AllocationError, "Could not allocate buffer!")
        end
        @data = "\0".b * size
        @size = size
        @flags = flags
      end
    end

    # The "address" a buffer shows in #to_s. There is no real one here, so the object
    # identity stands in for it - until #transfer hands the memory to another buffer.
    def __address__
      @address ||= object_id << 1
    end

    def __set_address__(value)
      @address = value
    end

    # The allocation a buffer looks into: a String standing in for the memory,
    # plus where in it this buffer's window starts. Slices share the String of
    # the buffer they came from, which is what makes a write through a slice
    # visible through the buffer.
    def __take_over__(data, offset, size, flags, source = nil)
      @data = data
      @offset = offset
      @size = size
      @flags = flags
      @source = source
      @locked_source = false
      self
    end

    def __data__
      @data
    end

    def __offset__
      @offset
    end

    def __size__
      @size
    end
    protected :__data__, :__offset__, :__size__

    def __lock_source__
      if @data && !@data.frozen?
        @data.__locktmp__
        @locked_source = true
      end
      self
    end

    def __adopt_lock__(locked)
      @locked_source = locked
      self
    end

    def __unlock_source__
      if @locked_source
        @locked_source = false
        @data.__unlocktmp__ if @data && !@data.frozen?
      end
    end
    private :__unlock_source__

    # Writes from the buffer go through the lock the buffer itself installed.
    def __unlocked__
      return yield unless @locked_source
      @data.__unlocktmp__
      begin
        yield
      ensure
        @data.__locktmp__
      end
    end
    private :__unlocked__

    def size
      @size
    end

    def empty?
      @size == 0
    end

    # What makes a buffer null is a NULL base pointer, which is what a buffer
    # that never allocated - or that has been freed, resized to nothing or
    # transferred away - has. A zero-length window onto live memory is not null.
    def null?
      @data.nil?
    end

    # A slice stops being usable when the allocation underneath goes away or
    # shrinks out from under it. A buffer that owns its memory is always valid,
    # and so is a freed one: #free drops the association rather than dangling.
    def valid?
      source = @source
      return true if source.nil?
      return false if @data.nil?
      if source.is_a?(::String)
        @data.equal?(source) && (@offset + @size) <= source.bytesize
      else
        other = source.__data__
        !other.nil? && @data.equal?(other) &&
          @offset >= source.__offset__ &&
          (@offset + @size) <= (source.__offset__ + source.__size__)
      end
    end

    def __check__
      unless valid?
        ::Kernel.raise(InvalidatedError, "Buffer has been invalidated!")
      end
    end
    private :__check__

    def external?
      (@flags & EXTERNAL) != 0
    end

    def internal?
      (@flags & INTERNAL) != 0
    end

    def mapped?
      (@flags & MAPPED) != 0
    end

    def shared?
      (@flags & SHARED) != 0
    end

    def private?
      (@flags & PRIVATE) != 0
    end

    def readonly?
      (@flags & READONLY) != 0
    end

    def locked?
      (@flags & LOCKED) != 0
    end

    # A lock stops the buffer itself from moving; reads and writes through it
    # carry on.
    def __check_writable__
      __check__
      ::Kernel.raise(AccessError, "Buffer is not writable!") if readonly?
    end
    private :__check_writable__

    def locked
      __check__
      ::Kernel.raise(LockedError, "Buffer already locked!") if locked?
      @flags |= LOCKED
      begin
        yield self
      ensure
        @flags &= ~LOCKED
      end
    end

    def free
      ::Kernel.raise(LockedError, "Buffer is locked!") if locked?
      __unlock_source__
      @data = nil
      @source = nil
      @offset = 0
      @size = 0
      @flags = 0
      self
    end

    def transfer
      __check__
      if locked?
        ::Kernel.raise(LockedError, "Cannot transfer ownership of locked buffer!")
      end
      other = self.class.allocate
      other.__take_over__(@data, @offset, @size, @flags, @source)
      # The address names the memory, and transfer moves the memory rather than
      # copying it, so it goes across with the rest.
      other.__set_address__(__address__)
      other.__adopt_lock__(@locked_source)
      @locked_source = false
      @data = nil
      @source = nil
      @offset = 0
      @size = 0
      @flags = 0
      other
    end

    def resize(new_size)
      ::Kernel.raise(LockedError, "Cannot resize locked buffer!") if locked?
      __check__
      new_size = self.class.__integer_arg__(new_size)
      ::Kernel.raise(::ArgumentError, "Size can't be negative!") if new_size < 0

      if @data.nil?
        # There is no allocation to keep, so a null buffer allocates afresh the
        # way IO::Buffer.new would have.
        if new_size > 0
          @flags = self.class.__flags_for_size__(new_size)
          @data = "\0".b * new_size
          @offset = 0
          @size = new_size
          @source = nil
        end
        return self
      end

      if external?
        ::Kernel.raise(AccessError, "Cannot resize external buffer!")
      end
      ::Kernel.raise(AccessError, "Buffer is not writable!") if readonly?

      if new_size == 0
        __unlock_source__
        @data = nil
        @source = nil
        @offset = 0
        @size = 0
        @flags = 0
        return self
      end

      current = get_string
      grown = current.byteslice(0, new_size).to_s
      grown = grown + ("\0".b * (new_size - grown.bytesize)) if grown.bytesize < new_size
      @data = grown
      @offset = 0
      @size = new_size
      @source = nil
      self
    end

    def slice(offset = 0, length = nil)
      __check__
      offset = self.class.__offset_arg__(offset)
      ::Kernel.raise(::ArgumentError, "Offset can't be negative!") if offset < 0
      length = @size - offset if length.nil?
      length = self.class.__offset_arg__(length)
      ::Kernel.raise(::ArgumentError, "Length can't be negative!") if length < 0
      if offset + length > @size
        ::Kernel.raise(::ArgumentError, "Specified offset+length is bigger than the buffer size!")
      end
      other = self.class.allocate
      # A slice is a window, not an allocation: it is neither internal nor
      # mapped nor shared, and the only flag it inherits is read-only-ness. Its
      # source is the buffer the memory really belongs to, so that slicing a
      # slice still points back at the root.
      other.__take_over__(@data, @offset + offset, length, @flags & READONLY, @source || self)
      other
    end

    def get_string(offset = 0, length = nil, encoding = ::Encoding::BINARY)
      __check__
      offset = self.class.__offset_arg__(offset)
      length = @size - offset if length.nil?
      length = self.class.__offset_arg__(length)
      if offset < 0 || length < 0 || offset + length > @size
        ::Kernel.raise(::ArgumentError, "Specified offset+length is bigger than the buffer size!")
      end
      if @data.nil?
        result = "".dup
      else
        result = @data.byteslice(@offset + offset, length).to_s
        result = result.dup if result.frozen?
      end
      result.force_encoding(encoding) if result.respond_to?(:force_encoding)
      result
    end
    alias :to_str :get_string

    # The write goes into the allocation in place, so a String-backed buffer and
    # every slice sharing the allocation see it.
    def set_string(string, offset = 0, length = nil, source_offset = 0)
      __check_writable__
      offset = self.class.__offset_arg__(offset)
      source = string.byteslice(source_offset, length || (string.bytesize - source_offset)).to_s
      if offset + source.bytesize > @size
        ::Kernel.raise(::ArgumentError, "Specified offset+length is bigger than the buffer size!")
      end
      unless source.empty?
        piece = source.dup
        piece.force_encoding(::Encoding::BINARY) if piece.respond_to?(:force_encoding)
        at = @offset + offset
        __unlocked__ { @data.bytesplice(at, piece.bytesize, piece) }
      end
      source.bytesize
    end

    def clear(value = 0, offset = 0, length = nil)
      __check_writable__
      length = @size - offset if length.nil?
      set_string([value & 0xff].pack("C") * length, offset)
      self
    end

    def __value_size__(type)
      case type
      when :U8, :S8 then 1
      when :U16, :S16 then 2
      when :U32, :S32, :f32 then 4
      when :U64, :S64, :f64 then 8
      else ::Kernel.raise(::ArgumentError, "Invalid type name!")
      end
    end
    private :__value_size__

    def __directive__(type)
      case type
      when :U8 then "C"
      when :S8 then "c"
      when :U16 then "S>"
      when :S16 then "s>"
      when :U32 then "L>"
      when :S32 then "l>"
      when :U64 then "Q>"
      when :S64 then "q>"
      when :f32 then "g"
      when :f64 then "G"
      else ::Kernel.raise(::ArgumentError, "Invalid type name!")
      end
    end
    private :__directive__

    def get_value(type, offset)
      get_string(offset, __value_size__(type)).unpack(__directive__(type))[0]
    end

    def set_value(type, offset, value)
      set_string([value].pack(__directive__(type)), offset)
    end

    def get_values(types, offset = 0)
      at = offset
      types.map do |type|
        v = get_value(type, at)
        at += __value_size__(type)
        v
      end
    end

    def each(type = :U8, offset = 0, count = nil)
      return ::Enumerator.new { |y| each(type, offset, count) { |i, v| y << [i, v] } } unless block_given?
      step = __value_size__(type)
      at = offset
      n = count || ((@size - offset) / step)
      n.times do
        yield at, get_value(type, at)
        at += step
      end
      self
    end

    def each_byte(offset = 0, count = nil)
      return ::Enumerator.new { |y| each_byte(offset, count) { |i, v| y << [i, v] } } unless block_given?
      each(:U8, offset, count) { |i, v| yield i, v }
      self
    end

    def values(type = :U8, offset = 0, count = nil)
      result = []
      each(type, offset, count) { |_, v| result << v }
      result
    end

    # Offset, sixteen bytes in hex padded out to a fixed width, then the same
    # bytes as text with anything unprintable shown as a dot - MRI's layout.
    def hexdump
      get_string.bytes.each_slice(16).each_with_index.map { |row, i|
        hex = row.map { |b| format("%02x", b) }.join(" ")
        text = row.map { |b| (b >= 0x20 && b < 0x7f) ? b.chr : "." }.join
        format("0x%08x  %-47s %s", i * 16, hex, text)
      }.join("\n")
    end

    def to_s
      parts = []
      parts << "EXTERNAL" if external?
      parts << "INTERNAL" if internal?
      parts << "MAPPED" if mapped?
      parts << "SHARED" if shared?
      parts << "LOCKED" if locked?
      parts << "PRIVATE" if private?
      parts << "READONLY" if readonly?
      parts << "NULL" if null?
      "#<IO::Buffer 0x#{__address__.to_s(16).rjust(16, '0')}+#{@size} #{parts.join(' ')}>"
    end
    # MRI's inspect is the header with the hexdump under it; to_s is the
    # header on its own.
    def inspect
      return to_s if null? || size == 0
      "#{to_s}#{nl_}#{hexdump}"
    end

    def nl_
      "\n"
    end
    private :nl_

    def <=>(other)
      return nil unless other.is_a?(::IO::Buffer)
      get_string <=> other.get_string
    end

    def ==(other)
      other.is_a?(::IO::Buffer) && get_string == other.get_string
    end

    # The copying operators also want a Buffer. The result is the size of the
    # receiver: a shorter mask repeats over it, and a longer one is cut off.
    def __binary_op__(other, op)
      __require_buffer__(other)
      a = get_string
      b = other.get_string
      bytes = a.bytes
      unless b.empty?
        bytes.each_index { |i| bytes[i] = bytes[i].__send__(op, b.getbyte(i % b.bytesize)) & 0xff }
      end
      result = ::IO::Buffer.new(bytes.size)
      result.set_string(bytes.pack("C*"))
      result
    end
    private :__binary_op__

    # The in-place forms. MRI insists on a Buffer for the right-hand side of
    # these, where the copying forms are happy with anything string-shaped, and
    # it works over as many bytes as the two have in common rather than
    # refusing a mismatch.
    def __require_buffer__(other)
      unless other.is_a?(::IO::Buffer)
        ::Kernel.raise(::TypeError,
                       "wrong argument type #{::IO::Buffer.__class_name__(other)} (expected IO::Buffer)")
      end
      other
    end
    private :__require_buffer__

    def __in_place_op__(other, op)
      __check_writable__
      __require_buffer__(other)
      a = get_string
      b = other.get_string
      bytes = a.bytes
      unless b.empty?
        bytes.each_index { |i| bytes[i] = bytes[i].__send__(op, b.getbyte(i % b.bytesize)) & 0xff }
      end
      set_string(bytes.pack("C*"))
      self
    end
    private :__in_place_op__

    def and!(other)
      __in_place_op__(other, :&)
    end

    def or!(other)
      __in_place_op__(other, :|)
    end

    def xor!(other)
      __in_place_op__(other, :^)
    end

    def not!
      __check_writable__
      set_string(get_string.bytes.map { |x| (~x) & 0xff }.pack("C*"))
      self
    end

    # Moving bytes between the buffer and an IO. There is no scatter/gather
    # underneath, so these are a plain read or write of the slice in question.
    # MRI reads and writes as much as the buffer holds - the length argument is
    # a minimum, not a cap - and answers how many bytes moved.
    def read(io, length = nil, offset = 0)
      want = size - offset
      data = io.read(want)
      return nil if data.nil?
      set_string(data, offset)
      data.bytesize
    end

    def write(io, length = nil, offset = 0)
      io.write(get_string(offset, size - offset))
    end

    def pread(io, from, length = nil, offset = 0)
      data = io.pread(size - offset, from)
      set_string(data, offset)
      data.bytesize
    end

    def pwrite(io, from, length = nil, offset = 0)
      io.pwrite(get_string(offset, size - offset), from)
    end

    def copy(source, offset = 0, length = nil, source_offset = 0)
      __require_buffer__(source)
      set_string(source.get_string, offset, length, source_offset)
    end

    def &(other)
      __binary_op__(other, :&)
    end

    def |(other)
      __binary_op__(other, :|)
    end

    def ^(other)
      __binary_op__(other, :^)
    end

    def ~
      bytes = get_string.bytes.map { |x| (~x) & 0xff }
      result = ::IO::Buffer.new(bytes.size)
      result.set_string(bytes.pack("C*"))
      result
    end
  end

  # Same for IO.read: the text form is tagged with the encoding asked for,
  # the length form is bytes. IO.binread stays binary, which is its whole job.
  class << self
    alias :__ir_class_read__ :read
    private :__ir_class_read__

    # IO.read opens the file itself, so mode:, encoding: and :open_args all get
    # their chance; the whole-file form is tagged with the stream's encoding and
    # the length form is bytes, which is MRI's split too.
    def read(*args, **options)
      # MRI's rb_scan_args("13:"); a fourth positional argument it takes, only to hand the three
      # after the name on to open, which takes two
      if args.empty? || args.size > 4
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..4)")
      end
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 3, expected 0..2)") if args.size == 4
      name = args.shift
      options = options.empty? ? nil : options
      length = args[0]
      offset = args[1]
      ::Kernel.raise(::ArgumentError, "negative offset #{offset} given") if offset && offset < 0
      ::Kernel.raise(::ArgumentError, "negative length #{length} given") if length && length < 0

      open_args = options && options[:open_args]
      if open_args
        extra = open_args.find { |a| a.is_a?(::Hash) } || {}
        mode = open_args.find { |a| a.is_a?(::String) || a.is_a?(::Integer) } || extra[:mode] || "r"
        enc = extra[:encoding] || extra[:external_encoding]
      else
        mode = (options && options[:mode]) || "r"
        enc = options && (options[:encoding] || options[:external_encoding])
      end

      internal = nil
      if enc.is_a?(::String) && enc.include?(":")
        # "external:internal" asks for a conversion, same as the mode string.
        enc, internal = enc.split(":", 2)
      end

      # "BOM|<encoding>" means "read the byte-order mark if there is one, and let it
      # name the encoding"; the mode parser underneath does not know the prefix.
      bom = mode.is_a?(::String) && mode =~ /:\s*BOM\|/i
      mode = mode.sub(/BOM\|/i, "") if bom

      result = ::File.open(name, mode) do |io|
        io.set_encoding(enc) if enc
        # "BOM|enc" means the mark names the encoding if there is one and enc does
        # otherwise, so the mark is read past either way - which is not what
        # #set_encoding_by_bom is for: that one refuses once an encoding is set.
        if bom
          from_bom = io.__ir_skip_bom__
          io.set_encoding(from_bom) if from_bom
        end
        io.seek(offset) if offset && offset > 0
        length ? io.read(length) : io.read
      end

      return result if result.nil? || !result.respond_to?(:force_encoding)
      return result if length
      if internal
        result = result.encode(::Encoding.find(internal))
      end
      result
    end
  end

  # read with no length answers text in the stream's external encoding; read
  # with a length answers bytes, and is ASCII-8BIT. The C# read always handed
  # back ASCII-8BIT, so File.read(path, encoding: "utf-8") came out binary.
  alias :__ir_read__ :read
  private :__ir_read__

  def read(*args)
    result = __ir_read__(*args)
    return result if result.nil?
    return result unless args.empty? || args[0].nil?
    return result unless result.respond_to?(:force_encoding)
    enc = (external_encoding rescue nil) || ::Encoding.default_external
    result.force_encoding(enc) if enc
    converted = __transcode__(result)
    return converted unless args.size >= 2 && args[1].equal?(result) && !converted.equal?(result)
    # A caller-supplied buffer is filled in place, so the transcoded text has to
    # go back into it rather than being handed back beside it.
    result.force_encoding(converted.encoding)
    result.replace(converted)
    result
  end

  # set_encoding takes "external:internal" in one string as well as the two
  # separately; the C# one only understood a single encoding name and answered
  # "unknown encoding name - utf-8:ISO-8859-1".
  alias :__ir_set_encoding__ :set_encoding

  # The options are keywords: a Hash passed positionally is a third argument too many.
  def set_encoding(*args, **opts)
    if args.empty? || args.size > 2
      ::Kernel.raise(::ArgumentError,
                     "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    # An encoding name that is not itself ASCII compatible cannot name anything.
    args.first(2).each do |name|
      next unless name.is_a?(::String)
      unless name.encoding.ascii_compatible?
        ::Kernel.raise(::ArgumentError, "invalid name encoding (non ASCII)")
      end
    end
    # Anything string-shaped names an encoding, and one name may carry both sides.
    if args.size >= 1 && !args[0].is_a?(::String) && !args[0].is_a?(::Encoding) &&
       !args[0].nil? && args[0].respond_to?(:to_str)
      args = args.dup
      args[0] = args[0].to_str
    end
    if args.size >= 2 && args[0].nil? && !args[1].nil?
      # MRI reads the pair as two names and trips over the nil on the way.
      ::Kernel.raise(::TypeError, "no implicit conversion of nil into String")
    end
    if args.size >= 2 && args[1] == "-"
      # "-" in the second position is MRI's way of saying "no internal encoding at
      # all", as opposed to "not given", which falls back to Encoding.default_internal.
      # Naming the external encoding twice is how that is said here.
      args = args.dup
      args[1] = args[0]
    end
    # "BOM|<encoding>" asks for the byte-order mark to have the last word; the
    # name underneath is what goes in when there is no mark to read.
    if args.size >= 1 && args[0].is_a?(::String) && args[0] =~ /\ABOM\|/i
      args = args.dup
      args[0] = args[0].sub(/\ABOM\|/i, "")
    end
    __check_set_encoding_options__(opts.empty? ? args : args + [opts])
    if args.size >= 1 && args[0].is_a?(::String) && args[0].include?(":")
      external, internal = args[0].split(":", 2)
      return __ir_set_encoding__(external, internal, **opts)
    end
    __ir_set_encoding__(*args, **opts)
  end

  # MRI validates the pair before it takes it: an encoding that is not ASCII
  # compatible needs either binmode or a conversion to something that is, and the
  # newline decorator only takes the three values it knows.
  def __check_set_encoding_options__(args)
    options = args.last.is_a?(::Hash) ? args.last : nil
    if options
      if options.key?(:newline)
        value = options[:newline]
        unless [:universal, :crlf, :cr, :lf].include?(value)
          # Only a Symbol gets named in the message; anything else is just wrong.
          ::Kernel.raise(::ArgumentError, value.is_a?(::Symbol) ?
            "unexpected value for newline option: #{value}" : "unexpected value for newline option")
        end
      end
      decorator = options.key?(:newline) || options[:universal_newline] ||
                  options[:crlf_newline] || options[:cr_newline]
      if decorator && (@__binmode__ || binmode?)
        ::Kernel.raise(::ArgumentError, "newline decorator with binary mode")
      end
    end

    external = args[0]
    if external.nil?
      # A nil external encoding is Encoding.default_external, which still has to be
      # something this stream can decode.
      external = ::Encoding.default_external
    end
    external = external.to_str if !external.is_a?(::String) && !external.is_a?(::Encoding) && external.respond_to?(:to_str)
    encoding = external.is_a?(::Encoding) ? external : (::Encoding.find(external) rescue nil)
    return if encoding.nil? || encoding.ascii_compatible?

    internal = args[1].is_a?(::Hash) ? nil : args[1]
    return unless internal.nil?
    return if @__binmode__ || binmode?
    # A write-only stream never decodes what it wrote, so it does not need binmode.
    return unless __readable_stream__?
    ::Kernel.raise(::ArgumentError, "ASCII incompatible encoding needs binmode")
  end
  private :__check_set_encoding_options__

  def __readable_stream__?
    lineno
    true
  rescue ::IOError
    false
  end
  private :__readable_stream__?

  # binmode is implemented for a File and throws for everything else - a pipe,
  # a socket, the standard streams. On this platform it has nothing to do but
  # say "these are bytes", so record that and set the external encoding, rather
  # than throwing a CLR exception at anything that is not a File.
  alias :__ir_binmode__ :binmode

  def binmode
    ::Kernel.raise(::IOError, "closed stream") if closed?
    @__binmode__ = true
    begin
      __ir_binmode__
    rescue ::Exception
    end
    # Binary mode is "these bytes are bytes": the external encoding becomes
    # ASCII-8BIT and there is no conversion left to do, so the internal one goes.
    begin
      set_encoding(::Encoding::BINARY)
    rescue ::Exception
    end
    self
  end

  # The conversion options the stream was opened with, reduced to the ones
  # String#encode understands. The hash also carries :mode, :binmode and the
  # rest of IO.open's own options, which #encode would reject.
  DECORATOR_OPTIONS__ = [:universal_newline, :cr_newline, :crlf_newline, :lf_newline, :newline].freeze
  ENCODE_OPTIONS__ = ([:invalid, :undef, :replace, :fallback, :xml] + DECORATOR_OPTIONS__).freeze
  __hide_constant__ :DECORATOR_OPTIONS__, :ENCODE_OPTIONS__

  def __encode_options__
    given = (__conversion_options__ rescue nil)
    return {} unless given.is_a?(::Hash)
    options = {}
    ENCODE_OPTIONS__.each { |key| options[key] = given[key] if given.key?(key) }
    # Binary mode is the absence of conversion, and that includes the newline
    # decorators the stream was opened with.
    DECORATOR_OPTIONS__.each { |key| options.delete(key) } if binmode?
    options
  end
  private :__encode_options__

  # The newline decorators work on the bytes, not on the characters: MRI runs
  # them beside the conversion rather than through it, so they apply to text
  # whose bytes are not valid characters of its own encoding. They are a
  # conversion in their own right, so they happen even when the two encodings
  # are the same; and :universal_newline is a decorator for the way in only.
  #
  # Universal newline is the one decorator for the way in. :crlf_newline,
  # :cr_newline and :lf_newline (newline: :lf, which turns "\r\n" and "\r" into
  # "\n") are for the way out only: MRI reads a stream opened with them as it is.
  def __decorate_newlines__(text, options, reading)
    newline = options[:newline]
    if reading
      if options[:universal_newline] || newline == :universal
        return text.gsub("\r\n", "\n").gsub("\r", "\n")
      end
      return text
    end
    return text.gsub("\n", "\r\n") if options[:crlf_newline] || newline == :crlf
    return text.gsub("\n", "\r") if options[:cr_newline] || newline == :cr
    return text.gsub("\r\n", "\n").gsub("\r", "\n") if options[:lf_newline] || newline == :lf
    text
  end
  private :__decorate_newlines__

  def __decorated__(options, reading)
    newline = options[:newline]
    if reading
      return (options[:universal_newline] || newline == :universal) ? true : false
    end
    (options[:crlf_newline] || options[:cr_newline] || options[:lf_newline] ||
      newline == :crlf || newline == :cr || newline == :lf) ? true : false
  end
  private :__decorated__

  # Whether what is read has its "\r\n" and lone "\r" turned into "\n" - which
  # has to happen before a line is looked for, so the line readers and #getc
  # (IoOps.cs) ask this once a call, and only of a stream with conversion options.
  def __universal_newline__?(_unused = nil)
    __decorated__(__encode_options__, true)
  end
  private :__universal_newline__?

  # A stream opened "r:external:internal" is asking for the bytes to be read as
  # the external encoding and handed back as the internal one. #internal_encoding
  # already answers nil unless there is really a conversion to do - the external
  # side binary, or the two the same, both mean no - so its answer is the whole
  # condition here, once the decorators have had their turn.
  def __transcode__(text)
    return text if text.nil?
    return text unless text.respond_to?(:encode)
    options = __encode_options__
    text = __decorate_newlines__(text, options, true) if __decorated__(options, true)
    target = internal_encoding
    return text if target.nil?
    text.encode(target, **options.reject { |key, _| DECORATOR_OPTIONS__.include?(key) })
  end
  private :__transcode__

  # The mirror of __transcode__ for the way out, which IO#write (IoOps.cs) calls
  # for a String that needs converting. A stream opened "w:external"
  # is asking for what is written to it to be converted into that encoding, and
  # nothing did it, so the bytes of whatever string was handed over went
  # straight to the file whatever its encoding said.
  #
  # Only a stream that was actually told an external encoding converts: MRI
  # leaves an ordinary stream alone even when Encoding.default_external would
  # have something to say, which is what keeps a binary string writable to
  # $stdout.  A byte-string destination converts nothing either, and text that
  # is already in the target encoding, or is ASCII and going somewhere that
  # keeps ASCII where it is, needs no work.
  def __encode_for_write__(text)
    return text unless ::String === text
    options = __encode_options__
    text = __decorate_newlines__(text, options, false) if __decorated__(options, false)
    target = external_encoding
    return text if target.nil? || target == ::Encoding::BINARY || text.encoding == target
    return text if text.ascii_only? && target.ascii_compatible?
    text.encode(target, **options.reject { |key, _| DECORATOR_OPTIONS__.include?(key) })
  end
  private :__encode_for_write__

  # chomp: is a keyword, so a Hash sitting in the last positional slot is a
  # separator argument - a TypeError - and not options. MRI tolerates keywords it
  # does not know here rather than refusing them.
  def __take_chomp__(args, opts)
    if args.size > 2
      ::Kernel.raise(::ArgumentError,
                     "wrong number of arguments (given #{args.size}, expected 0..2)")
    end
    [args, !!opts[:chomp]]
  end
  private :__take_chomp__

  # The line readers do the chomping and the transcoding in the built-ins
  # (IoOps.cs), a line at a time in C# - done here in Ruby it cost #readlines ten
  # times its read. #gets and #readline take chomp: there too, since they set $_
  # for their caller and a Ruby wrapper would set its own instead; #readlines and
  # #each_line only have the keyword taken off here, once a call, and go to the
  # chomping variants of the built-ins when it is given.

  alias :__ir_readlines__ :readlines

  def readlines(*args, **opts)
    args, chomp = __take_chomp__(args, opts)
    chomp ? __readlines_chomp__(*args) : __ir_readlines__(*args)
  end

  alias :__ir_each_line__ :each_line

  def __io_each_line__(name, args, opts, block)
    return to_enum(name, *args, **opts) unless block
    args, chomp = __take_chomp__(args, opts)
    chomp ? __each_line_chomp__(*args, &block) : __ir_each_line__(*args, &block)
  end
  private :__io_each_line__

  def each_line(*args, **opts, &block)
    __io_each_line__(:each_line, args, opts, block)
  end

  # #each is #each_line under another name: one method in MRI, which a spec compares, and
  # which a backtrace names by the name called.
  if method_defined?(:each)
    alias :__ir_each__ :each
    def each(*args, **opts, &block)
      __io_each_line__(:each, args, opts, block)
    end
    __ir_same_method__(:each, :each_line)
  end

  class << self
    alias :__ir_class_readlines__ :readlines

    # IO.readlines and IO.foreach are the instance methods on a stream opened for
    # the occasion, which is how the mode: and encoding options in the trailing
    # hash get a chance to apply - the specs open a file for writing that way and
    # expect the read to fail.
    #
    # Everything in the trailing hash but chomp: is for the open - mode:, the encodings,
    # binmode: and the conversion options - and chomp: is for the read.
    def readlines(*args, **options)
      if args.empty? || args.size > 3
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..3)")
      end
      name = args.shift
      chomp = options.key?(:chomp) ? { chomp: options[:chomp] } : {}
      ::File.open(name, **options.reject { |k, _| k == :chomp }) do |io|
        io.readlines(*args, **chomp)
      end
    end

    # IO.foreach itself is a built-in (IoOps.cs) that calls this with the separator and
    # limit in an Array and the keywords in a Hash, and then sees to its caller's $_.
    def __ir_foreach__(name, args, options, &block)
      options ||= {}
      if args.size > 2
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size + 1}, expected 1..3)")
      end
      unless block
        return to_enum(:foreach, name, *args, **options) { nil }
      end
      chomp = options.key?(:chomp) ? { chomp: options[:chomp] } : {}
      ::File.open(name, **options.reject { |k, _| k == :chomp }) do |io|
        io.each_line(*args, **chomp, &block)
      end
      # MRI's IO.foreach answers nil when it was given a block.
      nil
    end
    private :__ir_foreach__
  end

  # readpartial reads what is there, up to maxlen bytes, and only blocks when
  # nothing is there at all. Nothing here has a non-blocking read underneath, so
  # on a stream that is already open this is a read of at most maxlen bytes -
  # which is what MRI does on a regular file too. The result is bytes, so
  # ASCII-8BIT, and end of file is an EOFError rather than nil.
  # A buffer keeps the encoding it came with: MRI puts the bytes in it and leaves its label
  # alone, so a caller that handed over an ISO-8859-1 string gets one back.
  def __fill_buffer__(outbuf, data)
    encoding = outbuf.encoding
    outbuf.replace(data)
    outbuf.force_encoding(encoding)
    outbuf
  end
  private :__fill_buffer__

  def readpartial(maxlen, outbuf = nil)
    maxlen = ::Kernel.Integer(maxlen)
    ::Kernel.raise(::ArgumentError, "negative length #{maxlen} given") if maxlen < 0
    if maxlen == 0
      # Even a zero-length readpartial looks at the stream, so a closed one is an error.
      ::Kernel.raise(::IOError, "closed stream") if closed?
      result = "".b
      return outbuf ? __fill_buffer__(outbuf, result) : result
    end
    data = __read_available__(maxlen)
    if data.nil? || data.empty?
      __fill_buffer__(outbuf, "".b) if outbuf
      ::Kernel.raise(::EOFError, "end of file reached")
    end
    data.force_encoding(::Encoding::BINARY) if data.respond_to?(:force_encoding)
    outbuf ? __fill_buffer__(outbuf, data) : data
  end unless method_defined?(:readpartial)

  class << self
    # IO.pipe takes the encodings for the read end, and its block form yields the
    # pair, closes both afterwards and answers what the block answered. The
    # built-in took no arguments at all and gave the pair back from the block
    # form instead of the block's value.
    alias :__ir_pipe__ :pipe

    # The options are keywords: IO.pipe(ext, int, {}) is "given 3, expected 0..2".
    def pipe(*args, **options)
      if args.size > 2
        ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..2)")
      end
      options = nil if options.empty?
      external, internal = args
      if external.is_a?(::String) && external.include?(":")
        external, internal = external.split(":", 2)
      end
      if options
        external ||= options[:external_encoding]
        internal ||= options[:internal_encoding]
        if (enc = options[:encoding]) && external.nil?
          external, internal = enc.is_a?(::String) && enc.include?(":") ? enc.split(":", 2) : [enc, internal]
        end
      end

      read_end, write_end = __ir_pipe__
      if external
        internal ? read_end.set_encoding(external, internal) : read_end.set_encoding(external)
      end

      return [read_end, write_end] unless block_given?
      begin
        yield(read_end, write_end)
      ensure
        read_end.close unless read_end.closed?
        write_end.close unless write_end.closed?
      end
    end
  end

  # Reads a byte-order mark, and if there is one, adopts the encoding it names
  # and leaves the stream positioned after it. Answers nil when there is none.
  BOMS__ = [
    ["\xEF\xBB\xBF".b, "UTF-8"],
    ["\x00\x00\xFE\xFF".b, "UTF-32BE"],
    ["\xFF\xFE\x00\x00".b, "UTF-32LE"],
    ["\xFE\xFF".b, "UTF-16BE"],
    ["\xFF\xFE".b, "UTF-16LE"],
  ]
  __hide_constant__ :BOMS__

  def set_encoding_by_bom
    unless binmode?
      ::Kernel.raise(::ArgumentError, "ASCII incompatible encoding needs binmode")
    end
    # The BOM is what names the encoding, so there must be nothing named already.
    if internal_encoding
      ::Kernel.raise(::ArgumentError, "encoding conversion is set")
    end
    external = external_encoding
    if external && external != ::Encoding::BINARY
      ::Kernel.raise(::ArgumentError, "encoding is set to #{external} already")
    end
    enc = __ir_skip_bom__
    set_encoding(enc) if enc
    enc
  end unless method_defined?(:set_encoding_by_bom)

  # Reads past a byte-order mark if the stream starts with one and answers the encoding
  # it names, leaving the stream where it was when there is none. Unlike
  # #set_encoding_by_bom this asks nothing about the encoding already in force: a "BOM|"
  # mode names one for the case where there is no mark, so there always is one.
  def __ir_skip_bom__
    # Nothing to read a mark out of on a write-only stream.
    return nil unless __readable_stream__?
    start = pos
    # +"" rather than "": nil.to_s is a frozen string, and this one is re-tagged below.
    head = +(__ir_read__(4).to_s)
    head.force_encoding(::Encoding::BINARY) if head.respond_to?(:force_encoding)
    match = BOMS__.find { |bytes, _| head.start_with?(bytes) }
    unless match
      seek(start)
      return nil
    end
    seek(start + match[0].bytesize)
    ::Encoding.find(match[1])
  end

  # ---- the byte and character side of IO ---------------------------------

  # seek and sysseek take the whence as a symbol as well as an Integer.
  SEEK_WHENCE__ = { CUR: 1, SET: 0, END: 2, DATA: 3, HOLE: 4 }.freeze
  __hide_constant__ :SEEK_WHENCE__

  def __seek_whence__(whence)
    return whence unless whence.is_a?(::Symbol)
    value = SEEK_WHENCE__[whence]
    ::Kernel.raise(::TypeError, "no implicit conversion of Symbol into Integer") if value.nil?
    value
  end
  private :__seek_whence__

  alias :__ir_seek__ :seek
  private :__ir_seek__

  def seek(amount, whence = 0)
    __ir_seek__(amount, __seek_whence__(whence))
  end

  alias :__ir_sysseek__ :sysseek
  private :__ir_sysseek__

  def sysseek(amount, whence = 0)
    __ir_sysseek__(amount, __seek_whence__(whence))
  end

  # ungetc takes a String as well as a codepoint, and a codepoint is a character
  # in the stream's external encoding rather than a single byte. The built-in
  # (IoOps.cs) pushes a String and an ASCII codepoint itself and hands anything
  # else to this; __ir_ungetc__ pushes one byte.
  def __ungetc_rest__(value)
    if value.is_a?(::Integer)
      enc = (external_encoding rescue nil) || ::Encoding.default_external
      text = (value.chr(enc) rescue value.chr)
    else
      unless value.is_a?(::String)
        unless value.respond_to?(:to_str)
          ::Kernel.raise(::TypeError, "no implicit conversion of #{value.nil? ? "nil" : value.class} into String")
        end
        value = value.to_str
      end
      text = value
    end
    text.bytes.reverse_each { |b| __ir_ungetc__(b) }
    nil
  end
  private :__ungetc_rest__

  # ungetbyte pushes bytes, never characters, so it goes straight to the built-in
  # rather than through #ungetc - and an Integer is taken modulo 256 rather than
  # being out of range.
  def ungetbyte(byte)
    return nil if byte.nil?
    if byte.is_a?(::Integer)
      __ir_ungetc__(byte & 0xff)
    else
      unless byte.is_a?(::String)
        unless byte.respond_to?(:to_str)
          ::Kernel.raise(::TypeError, "no implicit conversion of #{byte.class} into String")
        end
        byte = byte.to_str
      end
      byte.bytes.reverse_each { |b| __ir_ungetc__(b) }
    end
    nil
  end unless method_defined?(:ungetbyte)

  def each_codepoint
    return to_enum(:each_codepoint) { nil } unless block_given?
    each_char { |c| yield c.ord }
    self
  end unless method_defined?(:each_codepoint)

  # ---- descriptor flags ---------------------------------------------------
  #
  # There is no exec here to leak a descriptor into, and no way to unset
  # binmode once set, so these record what they are told and answer it back.

  # Real FD_CLOEXEC, not a remembered flag: whether a child sees the descriptor is the
  # kernel's business, and a spawned child was the only thing that ever asked.
  def close_on_exec=(value)
    flag = value ? true : false
    Process.__set_cloexec__(fileno, flag)
    @__close_on_exec__ = flag
    value
  end

  def close_on_exec?
    actual = Process.__get_cloexec__(fileno)
    return actual unless actual.nil?
    defined?(@__close_on_exec__) && !@__close_on_exec__ ? false : true
  end

  # The built-in answers from the stream mode, which a pipe never gets set
  # because the built-in binmode throws on one. Fall back to the flag that
  # the binmode above records.
  if method_defined?(:binmode?)
    alias :__ir_binmode_p__ :binmode?
    private :__ir_binmode_p__
  end

  def binmode?
    ::Kernel.raise(::IOError, "closed stream") if closed?
    return true if defined?(@__binmode__) && @__binmode__
    respond_to?(:__ir_binmode_p__, true) ? __ir_binmode_p__ : false
  end

  # A hint to the kernel about the access pattern; there is nothing to pass it
  # to here, but MRI still validates the arguments and answers nil.
  # There is no posix_fadvise here, so the advice is checked and then dropped -
  # but the checking is the part callers can observe.
  def advise(advice, offset = 0, len = 0)
    unless advice.is_a?(::Symbol)
      ::Kernel.raise(::TypeError, "advice must be a Symbol")
    end
    unless %i[normal sequential random willneed dontneed noreuse].include?(advice)
      ::Kernel.raise(::NotImplementedError, "Unsupported advice: #{advice.inspect}")
    end
    offset = __advise_offset__(offset)
    len = __advise_offset__(len)
    ::Kernel.raise(::IOError, "closed stream") if closed?
    nil
  end unless method_defined?(:advise)

  # The offset and length go to off_t parameters: they take #to_int and nothing
  # else, and anything that does not fit is out of range rather than convertible.
  def __advise_offset__(value)
    unless value.is_a?(::Integer)
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError,
                       "no implicit conversion of #{value.class} into Integer")
      end
      value = value.to_int
    end
    if value > 0x7fffffffffffffff || value < -0x8000000000000000
      ::Kernel.raise(::RangeError, "bignum too big to convert into 'long long'")
    end
    value
  end
  private :__advise_offset__

  def fdatasync
    fsync
    0
  end unless method_defined?(:fdatasync)

  # IO#path answers the path the stream was opened with - File overrides it with the
  # real one - and the standard streams answer the names MRI gives them. An IO built
  # from a descriptor keeps whatever path: it was told about.
  def path
    case fileno
    when 0 then "<STDIN>"
    when 1 then "<STDOUT>"
    when 2 then "<STDERR>"
    else @__io_path__
    end
  end unless method_defined?(:path)

  alias :to_path :path unless method_defined?(:to_path)

  # Positional read/write, done by saving and restoring the file position since
  # there is no pread/pwrite underneath.
  def __io_to_int__(value)
    return value if value.is_a?(::Integer)
    unless value.respond_to?(:to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{value.nil? ? 'nil' : value.class} into Integer")
    end
    value.to_int
  end
  private :__io_to_int__

  def pread(maxlen, offset, buffer = nil)
    maxlen = __io_to_int__(maxlen)
    offset = __io_to_int__(offset)
    ::Kernel.raise(::ArgumentError, "negative string size (or size too big)") if maxlen < 0
    ::Kernel.raise(::Errno::EINVAL, (path if respond_to?(:path))) if offset < 0
    # A zero-length read touches neither the file nor the buffer, which is why an
    # offset past the end of the file is not an error either.
    return buffer || "".b if maxlen == 0
    if buffer && !buffer.is_a?(::String)
      unless buffer.respond_to?(:to_str)
        ::Kernel.raise(::TypeError, "no implicit conversion of #{buffer.class} into String")
      end
      target = buffer
      buffer = buffer.to_str
    end
    saved = pos
    begin
      seek(offset)
      result = read(maxlen)
      if result.nil?
        # End of file empties the buffer before raising, which is what the caller
        # sees if it kept a reference to it.
        buffer.clear if buffer
        ::Kernel.raise(::EOFError, "end of file reached")
      end
      if buffer
        # The buffer gets the bytes and keeps its own encoding, as in MRI.
        encoding = buffer.encoding
        buffer.replace(result)
        buffer.force_encoding(encoding)
        target || buffer
      else
        result
      end
    ensure
      seek(saved)
    end
  end unless method_defined?(:pread)

  def pwrite(string, offset)
    offset = __io_to_int__(offset)
    saved = pos
    begin
      seek(offset)
      write(string)
    ensure
      seek(saved)
    end
  end unless method_defined?(:pwrite)

end

# caller_locations (2.0) and the Location objects it yields. The runtime only
# offers caller strings, so parse those: "path:lineno:in 'label'".
class Thread
  # A thread's own stack is reachable through Kernel#caller.  Another thread's
  # comes from __native_backtrace__, which reads the frame list that thread
  # already keeps for its own backtraces instead of trying to walk its stack
  # from outside - which .NET Core does not allow.  A thread that has not
  # started, has finished, or has never run Ruby code still answers nil.
  def backtrace(*args)
    if self == ::Thread.current
      # MRI's first entry is the #backtrace frame itself, and a frame for a method
      # implemented natively reports the call site rather than a place in the method.
      # This one is written in Ruby, so that entry goes in by hand, at the caller's line.
      # (Plain calls, not Kernel.send: a send of a literal name is a direct call that
      # leaves no frame of its own, so nothing here may count on one.)
      frames = caller(1)
      unless frames.empty?
        here = caller_locations(1, 1)[0]
        frames = ["#{here.path}:#{here.lineno}:in 'Thread#backtrace'"] + frames
      end
    else
      frames = __native_backtrace__
      # nil is MRI's answer for a thread that has finished.  A thread that is alive but has
      # not reached any Ruby code yet has an empty stack, not no stack - and which of the two
      # a just-created thread is in depends on how quickly it gets scheduled.
      frames = [] if frames.nil? && status
    end
    return nil if frames.nil?
    __slice_stack__(frames, args)
  end unless method_defined?(:backtrace)

  def backtrace_locations(*args)
    if self == ::Thread.current
      frames = caller_locations(1)
      # MRI's first location is the #backtrace_locations frame itself, and a frame for a
      # method implemented natively reports the call site rather than a place in the
      # method.  This one is written in Ruby, so that location goes in by hand, at the
      # caller's line (see #backtrace on why not through Kernel.send).
      unless frames.empty?
        frames = [::Thread::Backtrace::Location.new(frames[0].path, frames[0].lineno, "Thread#backtrace_locations")] + frames
      end
    else
      entries = __native_backtrace__
      return nil if entries.nil?
      frames = entries.map { |e| ::Thread::Backtrace::Location.__parse__(e) }
    end
    __slice_stack__(frames, args)
  end unless method_defined?(:backtrace_locations)

  # Both take (start, length) or a Range, like Kernel#caller.
  def __slice_stack__(frames, args)
    return frames if args.empty?
    if args[0].is_a?(::Range)
      return frames[args[0]]
    end
    start = ::Kernel.Integer(args[0])
    ::Kernel.raise ::ArgumentError, "negative level (#{start})" if start < 0
    return nil if start > frames.size
    frames = frames[start..-1] || []
    if args.size > 1 && !args[1].nil?
      length = ::Kernel.Integer(args[1])
      ::Kernel.raise ::ArgumentError, "negative size (#{length})" if length < 0
      frames.first(length)
    else
      frames
    end
  end
  private :__slice_stack__

  # The core Thread#fetch would have to raise KeyError, and KeyError is one of the
  # exception classes defined in Ruby, above the runtime - so the method lives here.
  def fetch(key, *default, &block)
    if default.size > 1
      ::Kernel.raise ::ArgumentError, "wrong number of arguments (given #{default.size + 1}, expected 1..2)"
    end
    if block && !default.empty?
      ::Kernel.warn "warning: block supersedes default value argument"
    end
    return self[key] if key?(key)
    return block.call(key) if block
    return default[0] unless default.empty?
    ::Kernel.raise ::KeyError.new("key not found: #{key.inspect}", receiver: self, key: key)
  end

  # Starts at the caller of the frame that called it, exactly where a plain
  # caller_locations in that frame would start.  The block is yielded to rather than
  # called through a Proc so that a `break' in it breaks out of this method, which is
  # what MRI's C implementation does and what the specs check.
  def self.each_caller_location(*args, **keywords)
    # MRI's C function takes (start = 1, length = nil) and no keywords
    unless keywords.empty?
      ::Kernel.raise ::ArgumentError, "unknown keyword#{'s' if keywords.size > 1}: #{keywords.keys.map(&:inspect).join(', ')}"
    end
    ::Kernel.raise ::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..2)" if args.size > 2
    start, length = args
    start ||= 1
    ::Kernel.raise ::LocalJumpError, "no block given" unless block_given?
    # Start 1 is two frames up: this method's own and the block's that called it (see
    # Thread#backtrace on why not through Kernel.send).
    locations = length.nil? ? caller_locations(start + 1) : caller_locations(start + 1, length)
    locations&.each { |l| yield l }
    nil
  end unless respond_to?(:each_caller_location)

  def native_thread_id
    return nil unless alive?
    __native_thread_id__
  end unless method_defined?(:native_thread_id)

  def __native_thread_id__
    if self == ::Thread.current
      ::System::Environment.CurrentManagedThreadId
    else
      object_id
    end
  end
  private :__native_thread_id__

  class Backtrace
    # --backtrace-limit=N, or -1 when it was not given.
    def self.limit
      ::Thread.send(:__backtrace_limit__)
    end unless respond_to?(:limit)

    class Location
      attr_reader :path, :lineno, :label

      def initialize(path, lineno, label)
        @path = path
        @lineno = lineno
        @label = label
      end

      # MRI resolves symlinks here, and answers nil when there is no real file behind the
      # frame - code eval'd under a made-up file name, or a <internal:...> frame.
      def absolute_path
        return nil if @path.nil? || @path.start_with?("(") || @path.start_with?("<")
        # where the file was when it ran: it may have been reached through a relative path
        # before a chdir, or through a symlink that is gone now
        location = __source_location_of__(@path)
        return (File.realpath(location) rescue location) if location
        File.realpath(@path) rescue nil
      end

      # MRI's base_label is the bare method name: without the "block in" /
      # "block (N levels) in" prefix a block frame carries, and without the owner that
      # #label has been qualified with since 3.4.  A label that is entirely a <...> form
      # ("<main>", "<module:A>") has no owner part to strip.
      def base_label
        return nil if @label.nil?
        base = @label.sub(/\Ablock (\(\d+ levels\) )?in /, "")
        base.start_with?("<") ? base : base.sub(/\A[^ ]*[#.]/, "")
      end

      # "path:lineno:in 'label'" -> a Location.  Both Kernel#caller_locations and
      # Thread#backtrace_locations have only the string form to work from.
      def self.__parse__(entry)
        if (m = /\A(.*):(\d+)(?::in [`'](.*)')?\z/.match(entry))
          new(m[1], m[2].to_i, m[3])
        else
          new(entry, 0, nil)
        end
      end

      def to_s
        @label ? "#{@path}:#{@lineno}:in '#{@label}'" : "#{@path}:#{@lineno}"
      end

      def inspect; to_s.inspect; end
    end unless const_defined?(:Location)
  end unless const_defined?(:Backtrace)
end

module Kernel
  private

  # Takes everything Kernel#caller takes, shifted by one frame so that the count starts
  # at the caller rather than here. A negative range end is left alone: caller resolves
  # it against the real stack depth, which this frame is not part of.
  def caller_locations(start = 1, length = nil)
    if start.is_a?(Range)
      first = start.begin ? start.begin.to_int : 0
      last = start.end&.to_int
      raise ArgumentError, "negative level (#{first})" if first < 0
      entries = caller(Range.new(first + 1, (last && last >= 0) ? last + 1 : last, start.exclude_end?))
    else
      first = start.to_int
      raise ArgumentError, "negative level (#{first})" if first < 0
      if length.nil?
        entries = caller(first + 1)
      else
        length = length.to_int
        raise ArgumentError, "negative size (#{length})" if length < 0
        entries = caller(first + 1, length)
      end
    end

    return nil if entries.nil?
    entries.map { |entry| Thread::Backtrace::Location.__parse__(entry) }
  end unless private_method_defined?(:caller_locations)
  module_function :caller_locations
end

# Object#=~ went away in 3.2; nil keeps one of its own, so `nil =~ re` still answers nil.
class NilClass
  def =~(other)
    nil
  end unless method_defined?(:=~)
end

# --- Hash: pieces of the 2.x-4.0 surface the 1.9 core never had ------------

class Hash
  private def __hash_operand__(other)
    return other if other.is_a?(Hash)
    if other.respond_to?(:to_hash)
      converted = other.to_hash
      return converted if converted.is_a?(Hash)
    end
    raise TypeError, "no implicit conversion of #{other.nil? ? 'nil' : other.class} into Hash"
  end

  # true if every pair of `sub` is present in `sup` (values compared with ==)
  private def __hash_subset__(sub, sup)
    sub.each { |k, v| return false unless sup.key?(k) && sup[k] == v }
    true
  end

  def <(other)
    other = __hash_operand__(other)
    size < other.size && __hash_subset__(self, other)
  end unless method_defined?(:<)

  def <=(other)
    other = __hash_operand__(other)
    size <= other.size && __hash_subset__(self, other)
  end unless method_defined?(:<=)

  def >(other)
    other = __hash_operand__(other)
    size > other.size && __hash_subset__(other, self)
  end unless method_defined?(:>)

  def >=(other)
    other = __hash_operand__(other)
    size >= other.size && __hash_subset__(other, self)
  end unless method_defined?(:>=)

  # Hash#to_proc (2.3) is a lambda, so its arity is enforced.
  def to_proc
    hash = self
    ->(key) { hash[key] }
  end unless method_defined?(:to_proc)

  def compact!
    reject! { |_, v| v.nil? }
  end unless method_defined?(:compact!)

  def default_proc=(proc)
    raise FrozenError.new("can't modify frozen Hash: #{inspect}", receiver: self) if frozen?
    self.DefaultValue = nil
    if proc.nil?
      self.DefaultProc = nil
      return nil
    end
    unless proc.is_a?(Proc)
      unless proc.respond_to?(:to_proc)
        raise TypeError, "wrong default_proc type #{proc.class} (expected Proc)"
      end
      converted = proc.to_proc
      unless converted.is_a?(Proc)
        raise TypeError, "can't convert #{proc.class} to Proc (#{proc.class}#to_proc gives #{converted.class})"
      end
      proc = converted
    end
    # MRI accepts arity 2 and any negative arity down to -3 (|a, b, *c|).
    if proc.lambda? && proc.arity != 2 && (proc.arity >= 0 || proc.arity < -3)
      raise TypeError, "default_proc takes two arguments (2 for #{proc.arity})"
    end
    self.DefaultProc = proc
    proc
  end unless method_defined?(:default_proc=)

  # #assoc / #rassoc compare with ==, and stop at the first match (which matters
  # for an identity hash holding several equal-but-distinct keys).
  def assoc(key)
    each { |k, v| return [k, v] if k == key }
    nil
  end

  def rassoc(value)
    each { |k, v| return [k, v] if v == value }
    nil
  end

  # #slice and #except are HashOps' (C#): they read the table itself, so an overridden #[]
  # takes no part, without first copying the whole hash.

  # Hash#eql? (and the value comparison Hash#== performs) - compares sizes,
  # then looks each key up with eql?/hash semantics and compares values with
  # #eql?. Pairs already on the comparison stack count as equal so that
  # mutually recursive hashes terminate.
  def eql?(other)
    return true if equal?(other)
    unless other.is_a?(Hash)
      # rb_hash_eql hands an object that converts to a Hash the question the other way round
      return false unless other.respond_to?(:to_hash)
      return other.eql?(self) ? true : false
    end
    return false if size != other.size
    # two non-empty hashes of different kinds (one compares by identity) are never eql
    return false if size != 0 && compare_by_identity? != other.compare_by_identity?
    pairs = (Thread.current[:__hash_eql_pairs__] ||= [])
    entry = [object_id, other.object_id]
    return true if pairs.include?(entry)
    pairs.push(entry)
    begin
      each do |k, v|
        return false unless other.key?(k)
        return false unless v.eql?(other[k])
      end
      true
    ensure
      pairs.pop
    end
  end

  # Hash#hash is the core's (HashOps), and so is Array#hash: see StructuralHash.
end

# Hash.ruby2_keywords_hash and .ruby2_keywords_hash? are the runtime's: the mark is a flag
# on the hash object, set where a ruby2_keywords method's rest parameter is filled.
class << Hash
  # the core's try_convert reports a different error and does not accept a nil
  # result from #to_hash
  def try_convert(object)
    __ir_try_convert__(object, :to_hash, ::Hash, "Hash")
  end

  # The copy is #dup's, so it is of the receiver's own class and carries its instance
  # variables; only the mark itself comes from the runtime.
  def ruby2_keywords_hash(hash)
    unless hash.is_a?(::Hash)
      ::Kernel.raise(::TypeError, "wrong argument type #{hash.class} (expected Hash)")
    end
    __ir_mark_ruby2_keywords_hash__(hash.dup)
  end
end

# KeyError gained #receiver and #key in 2.5; Hash#fetch and friends set them.
class KeyError
  # Either one given, nil included, is answered; one not given is an ArgumentError.
  def initialize(message = nil, receiver: (no_receiver = true; nil), key: (no_key = true; nil))
    @__receiver__ = receiver unless no_receiver
    @__key__ = key unless no_key
    super(message)
  end

  def receiver
    raise ArgumentError, "no receiver is available" unless defined?(@__receiver__)
    @__receiver__
  end

  def key
    raise ArgumentError, "no key is available" unless defined?(@__key__)
    @__key__
  end
end

class Hash
  def fetch_values(*keys, &block)
    keys.map do |k|
      if key?(k)
        self[k]
      elsif block
        block.call(k)
      else
        desc = k.inspect
        desc = "#{desc[0, 62]}..." if desc.length > 65 # rb_hash_fetch's rb_str_ellipsize
        raise KeyError.new("key not found: #{desc}", receiver: self, key: k)
      end
    end
  end
end

# --- Struct: the modern surface -------------------------------------------

class Struct
  def to_h(&block)
    result = {}
    if block
      each_pair { |k, v| pair = __to_h_pair__(block.call(k, v)); result[pair[0]] = pair[1] }
    else
      members.each_with_index { |m, i| result[m] = self[i] }
    end
    result
    # instance_methods(false) rather than method_defined?: Struct includes Enumerable,
    # whose #to_h is already defined by this point, so method_defined? is true and this
    # definition would be skipped - leaving Struct with a #to_h that walks each (the
    # values) instead of each_pair.
  end unless instance_methods(false).include?(:to_h)

  def dig(key, *rest)
    value = begin
      self[key]
    rescue NameError, IndexError
      nil
    end
    __dig_step__(value, rest)
  end unless method_defined?(:dig)

  # index of `name` in members, or nil; mirrors CRuby's struct_pos
  private def __struct_pos__(name)
    if name.is_a?(Symbol) || name.is_a?(String)
      members.index(name.to_sym)
    else
      unless name.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{name.class} into Integer"
      end
      i = name.to_int
      unless i.is_a?(Integer)
        raise TypeError, "can't convert #{name.class} to Integer (#{name.class}#to_int gives #{__ir_conversion_result_name__(i)})"
      end
      i += size if i < 0
      (i >= 0 && i < size) ? i : nil
    end
  end

  def deconstruct_keys(keys)
    return to_h if keys.nil?
    unless keys.is_a?(Array)
      raise TypeError, "wrong argument type #{keys.class} (expected Array or nil)"
    end
    return {} if size < keys.size
    result = {}
    keys.each do |key|
      index = __struct_pos__(key)
      return result if index.nil?
      result[key] = self[index]
    end
    result
  end

  def values_at(*args)
    values = to_a
    count = values.size
    result = []
    args.each do |arg|
      if arg.is_a?(Range)
        first = arg.begin
        first = first.nil? ? 0 : first.to_int
        first += count if first < 0
        raise RangeError, "#{arg} out of range" if first < 0
        last = arg.end
        if last.nil?
          last = count - 1
        else
          last = last.to_int
          last += count if last < 0
          last -= 1 if arg.exclude_end?
        end
        i = first
        while i <= last
          result << (i < count ? values[i] : nil)
          i += 1
        end
      else
        unless arg.respond_to?(:to_int)
          raise TypeError, "no implicit conversion of #{arg.class} into Integer"
        end
        index = arg.to_int
        normalized = index < 0 ? index + count : index
        if normalized < 0
          raise IndexError, "offset #{index} too small for struct(size:#{count})"
        end
        if normalized >= count
          raise IndexError, "offset #{index} too large for struct(size:#{count})"
        end
        result << values[normalized]
      end
    end
    result
  end

  # #each / #each_pair / #select return an Enumerator when no block is given.
  alias :__struct_each__ :each
  def each(&block)
    return to_enum(:each) unless block
    __struct_each__(&block)
  end
  # Enumerator#next walks a struct with a cursor while this is the method.
  ::Enumerator.__iter_cursor_method__(self, :each)

  # each_pair yields one [name, value] array, so that a single-parameter block
  # receives the pair rather than just the name
  def each_pair(&block)
    return to_enum(:each_pair) unless block
    names = self.class.members
    names.each_with_index { |name, i| block.call([name, self[i]]) }
    self
  end

  alias :deconstruct :to_a
end

# Struct.new(..., keyword_init: true) (2.5) and StructClass#keyword_init? (3.1).
class << Struct
  alias :__struct_new__ :new

  def new(*args, **options, &block)
    keyword_init = nil
    # keywords proper; a Hash passed as an argument is a (bad) member name
    unless options.empty?
      unknown = options.keys - [:keyword_init]
      unless unknown.empty?
        raise ArgumentError, "unknown keyword#{'s' if unknown.size > 1}: #{unknown.map(&:inspect).join(', ')}"
      end
      value = options[:keyword_init]
      keyword_init = value.nil? ? value : !!value
    end

    names = args.reject { |a| a.is_a?(String) && a =~ /\A[A-Z]/ }
    seen = {}
    names.each do |name|
      key = name.respond_to?(:to_sym) ? name.to_sym : name
      # a setter name (rb_is_attrset_id) would clash with the member's own writer
      if key.is_a?(Symbol) && key.name.match?(/\A[[:alpha:]_][[:alnum:]_]*=\z/)
        raise ArgumentError, "invalid struct member: #{key}"
      end
      raise ArgumentError, "duplicate member: #{key}" if seen.key?(key)
      seen[key] = true
    end

    klass = args.empty? ? __struct_new__(nil) : __struct_new__(*args)
    # klass is a new receiver: a call like klass.define_singleton_method would bind a rule
    # of its own, every time (hundreds of microseconds). The methods called through
    # bind_call share one rule - and are the built-ins, as in MRI's struct.c.
    ::Kernel.instance_method(:__ir_set_hidden_ivar__).bind_call(klass, :@__keyword_init__, keyword_init)
    define_singleton = ::Kernel.instance_method(:define_singleton_method)
    # only classes created by Struct.new answer #keyword_init?, not every Struct subclass
    define_singleton.bind_call(klass, :keyword_init?) { @__keyword_init__ }
    if keyword_init
      # MRI: #<Class:0x...>(keyword_init: true)
      define_singleton.bind_call(klass, :inspect) { "#{super()}(keyword_init: true)" }
    end
    ::Module.instance_method(:module_eval).bind_call(klass, &block) if block
    klass
  end
end

class Struct
  # Struct#initialize is RubyStructOps.Initialize: it stores one positional value per member
  # itself and hands every other call here, the positional arguments in one array and the
  # keywords, if any, in a Hash - as a `def initialize(*args, **kwargs)' would have received
  # them. __struct_initialize__ is the plain store.
  def __struct_initialize_slow__(args, kwargs)
    kwargs ||= {}
    # self.class.members, not members: a struct may have a member called "members"
    klass = self.class
    names = klass.members
    keyword_init = klass.respond_to?(:keyword_init?) ? klass.keyword_init? : nil

    # The overwhelmingly common call: one positional value per member.
    if kwargs.empty? && !keyword_init && args.size == names.size
      return __struct_initialize__(*args)
    end

    # Since 3.2 a struct built without keyword_init: takes keywords as well as
    # positional values - keywords proper, that is: a Hash passed as an argument,
    # or keywords beside positional values, is a member value (MRI's rb_struct_initialize_m).
    if keyword_init.nil? && !kwargs.empty? && args.empty?
      keyword_init = true
    elsif !keyword_init && !kwargs.empty?
      args << kwargs
      kwargs = {}
    elsif keyword_init && kwargs.empty? && args.size == 1 && args[0].is_a?(::Hash)
      # keyword_init: true also takes the keywords as one Hash argument
      kwargs = args.pop
    end

    if keyword_init
      unless args.empty?
        raise ArgumentError, "wrong number of arguments (given #{args.size + (kwargs.empty? ? 0 : 1)}, expected 0)"
      end
      unknown = kwargs.keys - names
      unless unknown.empty?
        # MRI's wording is plural whatever the count
        raise ArgumentError, "unknown keywords: #{unknown.join(', ')}"
      end
      __struct_initialize__(*names.map { |name| kwargs[name] })
    else
      # pad with nil so that a partial re-initialize clears the remaining members
      values = args.dup
      values.concat([nil] * (names.size - values.size)) if values.size < names.size
      __struct_initialize__(*values)
    end
  end
  private :__struct_initialize_slow__
end

# --------------------------------------------------------------------------
# Exception#detailed_message / #full_message (Ruby 3.2+)
# --------------------------------------------------------------------------
#
# This is the text `ruby` prints for an uncaught exception, exposed as methods
# so that libraries can reuse and override it. Written in Ruby rather than C#
# because it is pure string assembly over #message, #backtrace and #cause, all
# of which already exist.
#
# The escape sequences are MRI's: \e[1m = bold (the message), \e[1;4m = bold
# plus underline (the class name), \e[m = reset. MRI leaves the bold
# unterminated when the class is anonymous and ruby/spec pins that, so the
# missing \e[m below is deliberate.

class Exception
  # The runtime keeps a backtrace as strings, so the Locations an exception answers
  # are read back out of those - one per frame, parsed the way Kernel#caller_locations
  # parses its own. An exception that was never raised has no backtrace and so no
  # locations either, which is MRI's answer as well. The one case where there are real
  # Locations to begin with is `raise Klass, message, caller_locations`: set_backtrace
  # was handed them, so they are kept as they are.
  alias :__core_set_backtrace__ :set_backtrace

  def set_backtrace(value)
    if ::Array === value && !value.empty? &&
       value.all? { |v| ::Thread::Backtrace::Location === v }
      @__backtrace_locations__ = value
      __core_set_backtrace__(value.map(&:to_s))
    else
      # The locations belong to the raise and not to these strings: an exception that
      # was raised keeps the ones it was raised with, and one that never was gets none,
      # however many strings it is given.
      @__backtrace_locations__ = backtrace_locations
      @__backtrace_from_strings__ = true
      __core_set_backtrace__(value)
    end
  end

  def backtrace_locations
    return @__backtrace_locations__ if @__backtrace_locations__
    return nil if defined?(@__backtrace_from_strings__)
    # a #set_backtrace of the exception's own that did not store the raise's backtrace leaves
    # #backtrace nil, and MRI still has the locations of the raise
    entries = backtrace || __raise_backtrace__
    return nil unless entries
    # The array is answered again as it is, rather than parsed afresh: MRI hands back
    # the one array, and a spec pushes onto it and looks again.
    @__backtrace_locations__ = entries.map { |entry| ::Thread::Backtrace::Location.__parse__(entry) }
  end unless method_defined?(:backtrace_locations)

  # Whether an uncaught exception would be printed to a terminal. Decides the
  # default for the `highlight:` option.
  def self.to_tty?
    $stderr.tty?
  rescue StandardError, NotImplementedError
    false
  end

  def detailed_message(highlight: false, **)
    Exception.send(:__check_highlight__, highlight)

    # rb_get_message: what #message answers (a subclass may override it); an
    # exception that has no #message at all has no text.
    text = respond_to?(:message, true) ? message : nil
    text = text.nil? ? "" : text.to_s unless text.is_a?(::String)

    # nil for an anonymous class, in which case MRI leaves the class out (the
    # class's own #name override is not consulted: rb_class_name)
    name = ::Module.instance_method(:name).bind_call(self.class)

    if text.empty?
      # MRI singles out RuntimeError: it is what a bare `raise "..."` produces,
      # so an empty one means nobody ever gave a reason.
      plain = self.class.equal?(::RuntimeError) ? "unhandled exception" : (name || self.class.to_s)
      return highlight ? "\e[1;4m#{plain}\e[m" : plain
    end

    # a multi-line message carries the class on its first line only; a message
    # that ends right after its first line's newline loses that newline
    lines = text.split("\n", -1)
    first = lines.shift
    lines = [] if lines == [""]
    if highlight
      first = name ? "\e[1m#{first} (\e[1;4m#{name}\e[m\e[1m)\e[m" : "\e[1m#{first}"
      lines = lines.map { |line| line.empty? ? line : "\e[1m#{line}\e[m" }
    elsif name
      first = "#{first} (#{name})"
    end
    lines.unshift(first).join("\n")
  end

  def full_message(highlight: Exception.to_tty?, order: :top, **options)
    Exception.send(:__check_highlight__, highlight)
    highlight = false if highlight.nil?
    unless order == :top || order == :bottom
      raise ArgumentError, "expected :top or :bottom as order: #{order.inspect}"
    end

    result = +""
    if order == :bottom
      result << (highlight ? "\e[1mTraceback\e[m (most recent call last):\n" : "Traceback (most recent call last):\n")
    end
    # `order` is ours; every other keyword (plus the resolved highlight) is
    # handed on to #detailed_message, which the user may have overridden.
    # An exception that was never raised has no backtrace; MRI then shows the
    # position of the #full_message call itself.
    here = caller(1, 1)[0]
    fallback = here ? [here.sub(/:in .*\z/, ":in 'full_message'")] : nil
    Exception.send(:__append_full_message__, result, self, highlight, order, options, fallback, [])
    result
  end

  def self.__check_highlight__(highlight)
    # nil stands for false (MRI's rb_bool_expected with nil allowed)
    unless highlight == true || highlight == false || highlight.nil?
      raise ArgumentError, "expected true or false as highlight: #{highlight.inspect}"
    end
  end
  private_class_method :__check_highlight__

  def self.__detailed__(exc, highlight, options)
    detailed = exc.detailed_message(**options, highlight: highlight) if exc.respond_to?(:detailed_message)
    detailed = detailed.to_str if detailed && !detailed.is_a?(::String) && detailed.respond_to?(:to_str)
    unless detailed.is_a?(::String)
      name = exc.class.name || exc.class.to_s
      detailed = highlight ? "\e[1;4m#{name}\e[m" : name
    end
    detailed
  end
  private_class_method :__detailed__

  # `fallback` is the caller of #full_message, which MRI shows when the
  # exception was never raised and so has no backtrace of its own.
  def self.__append_full_message__(out, exc, highlight, order, options, fallback, shown)
    # rb_get_backtrace: whatever #backtrace answers, which has to be nil or an Array of Strings
    backtrace = exc.backtrace
    unless backtrace.nil? || (backtrace.is_a?(::Array) && backtrace.all? { |line| line.is_a?(::String) })
      ::Kernel.raise(::TypeError, "backtrace must be an Array of String or an Array of Thread::Backtrace::Location")
    end
    backtrace = fallback if backtrace.nil? || backtrace.empty?
    backtrace = [] if backtrace.nil?
    head = backtrace[0]
    rest = backtrace[1..-1] || []
    detailed = __detailed__(exc, highlight, options)
    # The cause MRI prints is the one raise recorded, not whatever a #cause
    # override answers; a cause already printed (a cycle) is not printed again.
    cause = exc.is_a?(::Exception) ? ::Exception.instance_method(:cause).bind_call(exc) : nil
    cause = nil unless cause.is_a?(::Exception)
    shown = shown + [exc]
    cause = nil if cause && shown.any? { |e| e.equal?(cause) }

    # --backtrace-limit=N keeps N "from" lines and counts the rest - when there are at
    # least two of them to leave out (MRI's print_backtrace)
    limit = ::Thread::Backtrace.limit
    skipped = 0
    if order == :top && limit >= 0 && rest.size > limit + 1
      skipped = rest.size - limit
      rest = rest[0, limit]
    end

    if order == :top
      out << (head ? "#{head}: #{detailed}\n" : "#{detailed}\n")
      rest.each { |line| out << "\tfrom #{line}\n" }
      out << "\t ... #{skipped} levels...\n" if skipped > 0
      __append_full_message__(out, cause, highlight, order, options, fallback, shown) if cause
    else
      __append_full_message__(out, cause, highlight, order, options, fallback, shown) if cause
      (rest.size - 1).downto(0) { |i| out << "\t#{i + 1}: from #{rest[i]}\n" }
      out << (head ? "#{head}: #{detailed}\n" : "#{detailed}\n")
    end
  end
  private_class_method :__append_full_message__
end

class Range
  # ---- cover?, overlap?, bsearch, % ---------------------------------------
  #
  # 109 of spec/core/range's errors were #cover? alone and 71 were #bsearch;
  # neither existed.

  def __cover_range__(other)
    ob = other.begin
    oe = other.end
    # An empty range is covered by nothing.
    if !ob.nil? && !oe.nil?
      c = (ob <=> oe)
      return false if c.nil?
      return false if c > 0 || (c == 0 && other.exclude_end?)
    end
    return false if ob.nil? && !self.begin.nil?
    return false if oe.nil? && !self.end.nil?
    return false unless ob.nil? || cover?(ob)
    se = self.end
    if se.nil?
      # Two ranges that both run to infinity end at the same place; only the
      # exclusive/inclusive disagreement below can still separate them.
      cmp = oe.nil? ? 0 : 1
    else
      cmp = (se <=> oe)
      return false if cmp.nil?
    end
    # MRI's r_cover_range_p: when the two ranges agree about their end being
    # exclusive the comparison is enough, and when they disagree the inclusive
    # one has to be measured against the other's last element instead.
    if exclude_end? == other.exclude_end?
      cmp >= 0
    elsif exclude_end?
      cmp > 0
    elsif cmp >= 0
      true
    else
      vmax = (other.max rescue nil)
      return false if vmax.nil?
      c = (se <=> vmax)
      !c.nil? && c >= 0
    end
  end
  private :__cover_range__

  # Range#include? is not Range#cover?. For a range of numbers or times - or
  # when the value asked about is itself a number - the two agree and the
  # comparison is enough. A range of Strings answers whether the value turns up
  # in the #succ walk from begin to end, which is why ("a".."z") does not
  # include "cc". Anything else has to be walked, and a range with no beginning
  # or no end cannot be walked at all.
  def __include__(value)
    b = self.begin
    e = self.end
    # Integers all round: the comparison #cover? would make, without the class tests below.
    if ::Integer === value && ::Integer === b && ::Integer === e
      return value >= b && (exclude_end? ? value < e : value <= e)
    end
    # Only the endpoints decide (MRI's range_include_internal): a range of numbers or
    # times compares, and a Range asked about is just a value that does not compare.
    if __linear__(b) || __linear__(e) || __integerish__(b) || __integerish__(e)
      return false if value.is_a?(::Range)
      return cover?(value)
    end
    return __string_include__(b, e, value) if b.is_a?(::String) && e.is_a?(::String)
    # (nil..nil) covers every number and time; nothing else can be placed in it, and a
    # range open at one end only cannot be walked
    return true if b.nil? && e.nil? && __linear__(value)
    if b.nil? || e.nil?
      ::Kernel.raise(::TypeError, "cannot determine inclusion in beginless/endless ranges")
    end
    each { |x| return true if x == value }
    false
  end
  private :__include__

  def __linear__(x)
    x.is_a?(::Numeric) || x.is_a?(::Time)
  end
  private :__linear__

  # MRI's rb_check_to_integer: an endpoint that converts to an Integer is
  # treated as a point on a line even if its class is something else entirely.
  def __integerish__(x)
    return false unless x.respond_to?(:to_int)
    (x.to_int rescue nil).is_a?(::Integer)
  end
  private :__integerish__

  # MRI's rb_str_include_range_p: single ASCII characters compare directly,
  # everything else walks #succ and looks for an equal string.
  def __string_include__(b, e, value)
    unless value.is_a?(::String)
      return false unless value.respond_to?(:to_str)
      value = value.to_str
      unless value.is_a?(::String)
        ::Kernel.raise(::TypeError, "can't convert #{value.class} to String")
      end
    end
    if b.bytesize == 1 && e.bytesize == 1 && b.ascii_only? && e.ascii_only? && value.ascii_only?
      return false unless value.bytesize == 1
      return true if b <= value && value < e
      return !exclude_end? && value == e
    end
    found = false
    b.upto(e) do |s|
      next if exclude_end? && s == e
      if s == value
        found = true
        break
      end
    end
    found
  end
  private :__string_include__

  def __empty_range__(r)
    b, e = r.begin, r.end
    return false if b.nil? || e.nil?
    c = (b <=> e)
    c.nil? || c > 0 || (c == 0 && r.exclude_end?)
  end
  private :__empty_range__

  # Binary search over a numeric range, in MRI's two modes: a block answering
  # true/false finds the smallest element it says true for, a block answering
  # an Integer finds the element it answers 0 for.
  # The built-in handles an Integer range; a Float one it refuses outright,
  # and MRI searches those too - bisecting the interval rather than the
  # integers in it.
  if method_defined?(:bsearch)
    alias :__ir_bsearch__ :bsearch
    private :__ir_bsearch__
  end

  def bsearch(&block)
    b = self.begin
    e = self.end
    integral = (b.nil? || b.is_a?(::Integer)) && (e.nil? || e.is_a?(::Integer))
    numeric = (b.nil? || b.is_a?(::Numeric)) && (e.nil? || e.is_a?(::Numeric))
    unless numeric
      # MRI refuses before it would hand back an enumerator.
      ::Kernel.raise(::TypeError, "can't do binary search for #{(b || e).class}")
    end
    return ::Enumerator.new { |y| each { |x| y << x } } unless block
    if integral
      respond_to?(:__ir_bsearch__, true) ? __ir_bsearch__(&block) : __bsearch_int__(block)
    else
      __bsearch_float__(block)
    end
  end

  # Answers :found, true (go left, remember) or false (go right). MRI accepts any
  # Numeric from the block, not just an Integer, so a block that answers a Float
  # difference - or +/-Float::INFINITY - steers the search rather than raising.
  def __bsearch_test__(block, value)
    r = block.call(value)
    case r
    when true then :satisfied
    when false, nil then :greater
    when ::Integer then r == 0 ? :found : (r < 0 ? :smaller : :greater)
    when ::Numeric
      c = (r <=> 0)
      ::Kernel.raise(::ArgumentError, "comparison of #{r.class} with 0 failed") if c.nil?
      c == 0 ? :found : (c < 0 ? :smaller : :greater)
    else
      ::Kernel.raise(::TypeError, "wrong argument type #{r.class} (must be numeric, true, false or nil)")
    end
  end
  private :__bsearch_test__

  def __bsearch_int__(block)
    low = self.begin
    high = self.end
    high -= 1 if !high.nil? && exclude_end?
    # An endless or beginless range is widened until the answer is bracketed.
    if low.nil? || high.nil?
      span = 1
      if high.nil?
        high = low + span
        while __bsearch_test__(block, high) == :greater
          span *= 2
          high = low + span
        end
      else
        low = high - span
        while __bsearch_test__(block, low) != :greater
          span *= 2
          low = high - span
        end
      end
    end
    result = nil
    while low <= high
      mid = low + (high - low) / 2
      case __bsearch_test__(block, mid)
      when :found then return mid
      when :satisfied then result = mid; high = mid - 1
      when :smaller then high = mid - 1
      else low = mid + 1
      end
    end
    result
  end
  private :__bsearch_int__

  # MRI bisects a Float range over the IEEE bit patterns of the doubles rather
  # than over the interval, so the search lands exactly on a representable value
  # instead of converging near it - the difference between answering -0.2 and
  # answering -0.19999999999999998 - and reaches infinity in 64 steps.
  DOUBLE_MIN_INT64 = -9223372036854775808
  __hide_constant__ :DOUBLE_MIN_INT64 rescue nil

  def __double_as_int64__(d)
    i = ::System::BitConverter.DoubleToInt64Bits(d)
    i < 0 ? (-9223372036854775808 - i) : i
  end
  private :__double_as_int64__

  def __int64_as_double__(i)
    i = -9223372036854775808 - i if i < 0
    ::System::BitConverter.Int64BitsToDouble(i)
  end
  private :__int64_as_double__

  def __bsearch_float__(block)
    b = self.begin
    e = self.end
    low = __double_as_int64__(b.nil? ? -::Float::INFINITY : b.to_f)
    high = __double_as_int64__(e.nil? ? ::Float::INFINITY : e.to_f)
    high += 1 unless exclude_end?
    satisfied = nil
    while low < high
      mid = if (high < 0) == (low < 0)
              low + ((high - low) / 2)
            elsif low < -high
              -((-1 - low - high) / 2 + 1)
            else
              (low + high) / 2
            end
      value = __int64_as_double__(mid)
      case __bsearch_test__(block, value)
      when :found then return value
      when :satisfied then satisfied = value; high = mid
      when :smaller then high = mid
      else low = mid + 1
      end
    end
    # find-minimum mode answers the smallest element the block accepted; find-any
    # mode has already returned, so reaching here means it never found its zero.
    satisfied
  end
  private :__bsearch_float__

  # Range#step as MRI 3.4 rewrote it. A numeric range steps arithmetically,
  # sharing ruby_float_step with Numeric#step so that a Float range lands on
  # its end point rather than drifting; a range whose elements have #succ still
  # walks them when the step is an Integer, which is what keeps ("A".."G")
  # .step(2) answering letters; and anything else - a Time, a String step, an
  # object that only knows #+ and #<=> - advances by asking the current element
  # for `element + step`. A beginless range has nowhere to start.
  #
  # The old version just handed every stepping job to the 1.9 built-in, which
  # knows only #succ, so a Float step, a negative step, a String step and a
  # step given as an object answering #coerce were all wrong or raised.
  #
  # Range#step itself is C# (RangeOps.Step): it loops over a Fixnum range with a positive
  # Fixnum step and hands every other call here, its arguments in an Array.
  def __ir_range_step__(args, &block)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    n = args[0]
    b = self.begin
    e = self.end
    # (a..b).step(n) { } over Integers with a positive Integer step, the common case: the loop
    # directly, as ArithmeticSequence.__step_each__ would run it.
    if block && ::Integer === b && (e.nil? || ::Integer === e)
      unit = args.empty? ? 1 : n
      if ::Integer === unit && unit > 0
        i = b
        if e.nil?
          loop { yield i; i += unit }
        elsif exclude_end?
          while i < e
            yield i
            i += unit
          end
        else
          while i <= e
            yield i
            i += unit
          end
        end
        return self
      end
    end
    # Ruby 3.4: a step that is not a number is not converted up front; iterating adds it
    # with #+ (so an object answering #coerce works), and without a block the Enumerator is
    # built regardless.
    unit = args.empty? ? 1 : n
    numeric = b.is_a?(::Numeric) && (e.nil? || e.is_a?(::Numeric)) && unit.is_a?(::Numeric)

    if b.nil?
      unless e.is_a?(::Numeric) && unit.is_a?(::Numeric)
        ::Kernel.raise(::ArgumentError, "#step for non-numeric beginless ranges is meaningless")
      end
      ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
      if block
        ::Kernel.raise(::ArgumentError, "#step iteration for beginless ranges is meaningless")
      end
      return ::Enumerator::ArithmeticSequence.__build__(b, e, unit, exclude_end?, self, n.nil? ? :step : [:step, n])
    end

    ::Kernel.raise(::ArgumentError, "step can't be 0") if numeric && unit == 0

    unless block
      if numeric
        # inspected the way it was written: ((1..10).step) or ((1..10).step(2))
        return ::Enumerator::ArithmeticSequence.__build__(b, e, unit, exclude_end?, self, n.nil? ? :step : [:step, n])
      end
      range = self
      return ::Enumerator.new { |y| range.step(*args) { |x| y << x } }.__set_inspect__(self, :step, args)
    end

    if numeric
      ::Enumerator::ArithmeticSequence.__step_each__(b, e, unit, exclude_end?, &block)
    elsif unit.is_a?(::Integer) && b.respond_to?(:succ)
      ::Kernel.raise(::ArgumentError, "step can't be 0") if unit == 0
      ::Kernel.raise(::ArgumentError, "step can't be negative") if unit < 0
      i = 0
      each do |x|
        block.call(x) if i % unit == 0
        i += 1
      end
    else
      __step_by_plus__(b, e, unit, &block)
    end
    self
  end
  private :__ir_range_step__

  def %(n)
    seq = step(n)
    seq.__send__(:__ir_set_hidden_ivar__, :@__inspect_str__, [:%, n]) if seq.is_a?(::Enumerator::ArithmeticSequence)
    seq
  end

  # The generic walk: decide which way the range runs, check that adding the
  # step moves that way, then advance with #+ until #<=> says the end has been
  # passed. A step that does not move, or moves against the range, yields
  # nothing instead of looping forever.
  def __step_by_plus__(b, e, unit, &block)
    if e.nil?
      v = b
      loop do
        block.call(v)
        v = v + unit
      end
      return
    end
    dir = (b <=> e)
    return if dir.nil?
    c = (b <=> e)
    return if c.nil? || __step_past_end__(c, dir)
    sdir = (b <=> (b + unit))
    return if sdir.nil? || sdir == 0
    return unless dir == 0 || (dir < 0) == (sdir < 0)
    v = b
    loop do
      block.call(v)
      break if c == 0
      v = v + unit
      c = (v <=> e)
      break if c.nil? || __step_past_end__(c, dir)
    end
  end
  private :__step_by_plus__

  def __step_past_end__(c, dir)
    if dir < 0
      exclude_end? ? c >= 0 : c > 0
    elsif dir > 0
      exclude_end? ? c <= 0 : c < 0
    else
      exclude_end?
    end
  end
  private :__step_past_end__

  # Range#min/#max/#minmax are specialised in MRI: without a block they answer from the
  # endpoints instead of enumerating. IronRuby inherited Enumerable's versions, so
  # (0...2**64).max walked eighteen quintillion integers and never came back.
  #
  # The shape follows MRI's range_max/range_min: only a Numeric begin takes the
  # endpoint shortcut for an exclusive range, which is why ('a'...'f').max is still "e"
  # (enumerated) while (303.20...908.1111).max is a TypeError.

  def __range_count__(n)
    unless n.is_a?(Integer)
      unless n.respond_to?(:to_int)
        raise TypeError, "no implicit conversion of #{n.class} into Integer"
      end
      n = n.to_int
      unless n.is_a?(Integer)
        raise TypeError, "can't convert #{n.class} to Integer"
      end
    end
    raise ArgumentError, "negative size (#{n})" if n < 0
    n
  end
  private :__range_count__

  # MRI's range_max: a block, a count, or an exclusive end that is not a number means
  # Enumerable's walk (which a beginless range cannot do); otherwise the end answers, one
  # less for an exclusive Integer end - and only an Integer begin may exclude it.
  # #min, #max, #include? and #member? are C# (RangeOps): an Integer or Float range without a
  # block or count is answered there, and anything else comes here.
  def __max__(*args, &block)
    e = self.end
    raise RangeError, "cannot get the maximum of endless range" if e.nil?
    raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1)" if args.size > 1
    n = args[0]
    b = self.begin

    if block || n || (exclude_end? && !e.is_a?(Numeric))
      if n
        n = __range_count__(n)
        # An Integer range can answer without walking: (0...2**64).max(2) must not enumerate,
        # and a beginless one (Ruby 4.0) counts down from its end.
        if block.nil? && e.is_a?(Integer) && (b.nil? || b.is_a?(Integer))
          last = exclude_end? ? e - 1 : e
          return Array.new(n) { |i| last - i } if b.nil?
          return [] if b > last
          return Array.new([n, last - b + 1].min) { |i| last - i }
        end
        if b.nil?
          raise TypeError, "cannot get the maximum of beginless range with non Integer end value"
        end
        # Otherwise enumerate, which is also what makes a Float/Array/Time range a TypeError.
        return (block ? entries.sort(&block) : entries.sort).last(n).reverse
      end
      if b.nil?
        raise RangeError, "cannot get the maximum of beginless range with custom comparison method"
      end
      return ::Enumerable.instance_method(:max).bind_call(self, &block)
    end

    c = b.nil? ? -1 : (b <=> e)
    return nil if c.nil? || c > 0

    if exclude_end?
      raise TypeError, "cannot exclude non Integer end value" unless e.is_a?(Integer)
      return nil if c == 0
      # (...1).max is 0 (Ruby 4.0); a begin that is not an Integer cannot exclude the end
      raise TypeError, "cannot exclude end value with non Integer begin value" unless b.nil? || b.is_a?(Integer)
      return e - 1
    end
    e
  end

  def __min__(*args, &block)
    b = self.begin
    raise RangeError, "cannot get the minimum of beginless range" if b.nil?
    raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1)" if args.size > 1
    n = args[0]
    if n
      n = __range_count__(n)
      return (block ? entries.sort(&block) : entries.sort).first(n) if block
      return take(n)
    end
    if block
      if self.end.nil?
        raise RangeError, "cannot get the minimum of endless range with custom comparison method"
      end
      return ::Enumerable.instance_method(:min).bind_call(self, &block)
    end

    e = self.end
    c = e.nil? ? -1 : (b <=> e)
    return nil if c.nil? || c > 0 || (c == 0 && exclude_end?)
    b
  end

  private :__min__, :__max__

  def minmax(&block)
    return super(&block) if block
    [min, max]
  end

  # Enumerable#count would walk an endless range forever. An Integer range is counted
  # without walking it at all, from #size, as CRuby's range_count does.
  def count(*args, &block)
    raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1)" if args.size > 1
    return super if block || !args.empty?
    return Float::INFINITY if self.begin.nil? || self.end.nil?
    if self.begin.is_a?(Integer)
      n = size
      return n unless n.nil?
    end
    super
  end

  def to_set(*args, &block)
    raise RangeError, "cannot convert endless range to a set" if self.end.nil?
    super
  end

  def to_a
    raise RangeError, "cannot convert endless range to an array" if self.end.nil?
    super
  end
  alias :entries :to_a
end

class Fiber
  # MRI answers nil for a fiber that has finished, [] for one not started, and
  # the fiber's own frames otherwise. Only the running fiber's frames can be
  # read here: a suspended fiber's are its thread's, and are not walked.
  def backtrace(*args)
    return nil unless alive?
    return caller(*args) if equal?(Fiber.current)
    []
  end

  def backtrace_locations(*args)
    return nil unless alive?
    return caller_locations(*args) if equal?(Fiber.current)
    []
  end

  # MRI's Fiber is native data Marshal cannot write; this one is a Ruby object, and
  # Marshal.dump went on to fail on the first Proc among its instance variables.
  def marshal_dump
    ::Kernel.raise(::TypeError, "no _dump_data is defined for class #{self.class}")
  end
  private :marshal_dump
end

module GC
  # The CLR does not say why it collected. The keys are MRI's, with the values a
  # collection that nothing in particular triggered.
  LATEST_GC_INFO = {
    major_by: nil, need_major_by: nil, gc_by: :newobj, have_finalizer: false,
    immediate_sweep: false, state: :none, weak_references_count: 0,
    retained_weak_references_count: 0
  }.freeze
  __hide_constant__ :LATEST_GC_INFO

  # gc.c's layout constants and compile options. The object sizes are MRI's on a 64-bit
  # build, the sizes ObjectSpace.memsize_of counts in; the CLR collector is generational
  # and compacting.
  INTERNAL_CONSTANTS = {
    DEBUG: false, BASE_SLOT_SIZE: 40, RBASIC_SIZE: 16, RVALUE_OVERHEAD: 0,
    HEAP_PAGE_OBJ_LIMIT: 1638, HEAP_PAGE_BITMAP_SIZE: 208, HEAP_PAGE_SIZE: 65536,
    HEAP_COUNT: 5, RVARGC_MAX_ALLOCATE_SIZE: 640, RVALUE_OLD_AGE: 3
  }.freeze
  OPTS = ["USE_RGENGC", "GC_COMPACTION_SUPPORTED"].each(&:freeze).freeze

  def self.latest_gc_info(key = nil)
    case key
    when nil then LATEST_GC_INFO.dup
    when Symbol then LATEST_GC_INFO[key]
    when Hash then key.merge!(LATEST_GC_INFO)
    else raise TypeError, "non-hash or symbol given"
    end
  end

  def self.latest_compact_info
    { considered: {}, moved: {}, moved_up: {}, moved_down: {} }
  end

  # MRI checks its heap's invariants; the CLR's are not Ruby's to check.
  def self.verify_internal_consistency
    nil
  end

  def self.verify_compaction_references(toward: nil, double_heap: false, expand_heap: false)
    compact
  end

  # MRI's five size pools (slots of 40 to 640 bytes) stand in for the CLR's five heaps: gen0,
  # gen1, gen2, the large and the pinned object heap. The slot counts are the bytes each heap
  # holds after the last collection divided by the pool's slot size - an estimate, since a
  # CLR heap is not made of slots - and the counters MRI keeps per pool are 0.
  STAT_HEAP_KEYS = %i[slot_size heap_live_slots heap_free_slots heap_final_slots heap_eden_pages
                      heap_eden_slots total_allocated_pages force_major_gc_count
                      force_incremental_marking_finish_count total_allocated_objects
                      total_freed_objects].freeze
  __hide_constant__ :STAT_HEAP_KEYS

  def self.__stat_heap_values__(index, sizes)
    slot_size = 40 << index
    used, free = sizes || [0, 0]
    live = used / slot_size
    free_slots = free / slot_size
    pages = (used + free + 65_535) / 65_536
    {
      slot_size: slot_size, heap_live_slots: live, heap_free_slots: free_slots,
      heap_final_slots: 0, heap_eden_pages: pages, heap_eden_slots: live + free_slots,
      total_allocated_pages: pages, force_major_gc_count: 0,
      force_incremental_marking_finish_count: 0, total_allocated_objects: live,
      total_freed_objects: 0
    }
  end
  private_class_method :__stat_heap_values__

  def self.stat_heap(heap_name = nil, hash_or_key = nil)
    sizes = __heap_sizes__
    if heap_name.nil?
      unless hash_or_key.nil? || hash_or_key.is_a?(Hash)
        raise TypeError, "non-hash given"
      end
      result = hash_or_key || {}
      5.times { |i| result[i] = __stat_heap_values__(i, sizes[i]) }
      return result
    end
    raise TypeError, "heap_name must be nil or an Integer" unless heap_name.is_a?(Integer)
    raise ArgumentError, "size pool index out of range" unless (0...5).cover?(heap_name)

    values = __stat_heap_values__(heap_name, sizes[heap_name])
    case hash_or_key
    when nil then values
    when Symbol
      raise ArgumentError, "unknown key: #{hash_or_key}" unless values.key?(hash_or_key)
      values[hash_or_key]
    when Hash then hash_or_key.merge!(values)
    else raise TypeError, "non-hash or symbol given"
    end
  end
end

module ObjectSpace
  # MRI counts the slots of its heap by type. The CLR cannot walk its heap, so this is an
  # estimate: TOTAL is the bytes in use over MRI's 40-byte slot and FREE the fragmented bytes
  # likewise; classes, modules and symbols are counted exactly; everything else is T_OBJECT.
  # As in MRI, a type with nothing counted is left out.
  def count_objects(result = nil)
    raise TypeError, "non-hash given" unless result.nil? || result.is_a?(Hash)
    sizes = ::GC.__send__(:__heap_sizes__)
    used = sizes.sum { |s| s[0] }
    free = sizes.sum { |s| s[1] }
    classes = modules = 0
    ::ObjectSpace.each_object(Module) { |m| m.is_a?(Class) ? classes += 1 : modules += 1 }
    symbols = Symbol.all_symbols.size
    total = (used + free) / 40
    free_slots = free / 40
    objects = total - free_slots - classes - modules - symbols
    counts = { TOTAL: total, FREE: free_slots, T_OBJECT: objects, T_CLASS: classes,
               T_MODULE: modules, T_SYMBOL: symbols }
    if result
      result.each_key { |k| result[k] = 0 }
    else
      result = {}
    end
    counts.each { |k, v| result[k] = v if v > 0 || k == :TOTAL || k == :FREE }
    result
  end
  module_function :count_objects
end

# An Encoding marshals by name, as in MRI: Marshal.dump(Encoding::UTF_8) loads
# back as the same Encoding object.
class Encoding
  def _dump(limit = nil)
    name
  end

  def self._load(name)
    find(name)
  end
end

# Kernel#open, as MRI's rb_f_open has it since Ruby 4.0: an argument that
# answers #to_open is redirected to it (and closed after the block, like
# File.open), a leading "|" is no longer a subprocess but an ordinary - and
# therefore missing - filename, and options are keyword arguments, so a fourth
# *positional* Hash is an arity error rather than an option set.
module Kernel
  def open(*args, **options, &block)
    if args.empty?
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given 0, expected 1..3)")
    end

    target = args[0]
    rest = args[1..-1]

    if target.respond_to?(:to_open)
      io = options.empty? ? target.to_open(*rest) : target.to_open(*rest, **options)
      return io unless block
      begin
        return block.call(io)
      ensure
        if io.respond_to?(:close) && !(io.respond_to?(:closed?) && io.closed?)
          io.close
        end
      end
    end

    # FilePathValue: File.open would take an Integer as a file descriptor. MRI converts the
    # path before File.open counts the arguments.
    unless target.is_a?(::String) || target.respond_to?(:to_path) || target.respond_to?(:to_str)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{target.nil? ? 'nil' : target.class} into String")
    end

    # Only the file-opening form is limited to (path, mode, permission); #to_open
    # above takes as many arguments as it likes.
    if args.size > 3
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..3)")
    end

    # File.open's mode/permission parameters are typed, so an explicit nil is a
    # conversion error there where MRI treats it as "not given".
    rest.pop while !rest.empty? && rest.last.nil?

    if options.empty?
      ::File.open(target, *rest, &block)
    else
      ::File.open(target, *rest, **options, &block)
    end
  end
  module_function :open
end

# --- a batch of small post-1.9 additions ------------------------------------

module Kernel
  # rb_String: #to_str if there is one, otherwise #to_s - and #to_s not existing, or
  # answering something that is not a String, is a TypeError rather than a NoMethodError
  # or a silently wrong result.
  def String(object)
    return object if ::String === object
    # a BasicObject has neither respond_to? nor to_s: only its #to_s (through method_missing) can help
    basic = !(::Kernel === object)
    if !basic && object.respond_to?(:to_str)
      converted = object.to_str
      return converted if converted.is_a?(::String)
      # rb_check_string_type: nil means "no conversion" and #to_s is tried; anything else is wrong
      unless converted.nil?
        ::Kernel.raise(::TypeError,
          "can't convert #{object.class} to String (#{object.class}#to_str gives #{__ir_conversion_result_name__(converted)})")
      end
    end
    # rb_check_funcall: a user-defined respond_to? can veto the call; otherwise a missing
    # #to_s still goes to method_missing, and only a NoMethodError for it means "cannot".
    class_name = basic ? (class << object; self; end).superclass : (object.nil? ? 'nil' : object.class)
    no_conversion = "can't convert #{class_name} into String"
    custom_respond_to = !basic && !object.method(:respond_to?).owner.equal?(::Kernel)
    ::Kernel.raise(::TypeError, no_conversion) if custom_respond_to && !object.respond_to?(:to_s, true)
    if !basic && ::Kernel.instance_method(:respond_to?).bind_call(object, :to_s, true)
      converted = object.__send__(:to_s)
    else
      begin
        converted = object.__send__(:to_s)
      rescue ::NoMethodError => e
        ::Kernel.raise if e.name != :to_s || !e.receiver.equal?(object)
        ::Kernel.raise(::TypeError, no_conversion)
      end
    end
    unless converted.is_a?(::String)
      ::Kernel.raise(::TypeError,
        "can't convert #{object.class} to String (#{object.class}#to_s gives #{__ir_conversion_result_name__(converted)})")
    end
    converted
  end
  module_function :String

  # rb_f_puts forwards to $stdout.puts, so a redefined or mocked #puts on $stdout is what
  # runs (Kernel#print and #p write to it directly instead). It forwards with rb_funcallv,
  # which does not mind that an object only inherits this private #puts - and then, with
  # $stdout as the receiver, formats the lines itself and hands them to $stdout.write
  # (rb_io_puts): $stdout may be anything with a #write. The C# Kernel#puts below this
  # one does that last part. The identity is BasicObject#equal?'s, not the writer's own, and
  # keywords given to Kernel#puts reach a $stdout.puts that takes them.
  alias_method :__ir_puts_to_stdout__, :puts
  private :__ir_puts_to_stdout__
  ruby2_keywords def puts(*args)
    out = $stdout
    return __ir_puts_to_stdout__(*args) if __ir_equal__(out)
    out.__send__(:puts, *args)
  end
  module_function :puts

  # Kernel#Hash (1.9). Only nil and [] are special-cased; everything else must
  # answer #to_hash with a Hash.
  def Hash(object)
    return {} if object.nil? || object == []
    return object if object.instance_of?(::Hash)
    unless object.respond_to?(:to_hash)
      ::Kernel.raise(::TypeError, "can't convert #{object.class} into Hash")
    end
    converted = object.to_hash
    unless converted.is_a?(::Hash)
      ::Kernel.raise(::TypeError, "can't convert #{object.class} to Hash (#{object.class}#to_hash gives #{__ir_conversion_result_name__(converted)})")
    end
    converted
  end
  module_function :Hash
  private :Hash

  # MRI has these as private instance methods on Kernel as well as public
  # singletons; IronRuby declared several of them singleton-only, and #loop was
  # not private at all.
  private :loop if public_method_defined?(:loop)
end

class LoadError
  # MRI 2.0 records the path that could not be loaded. IronRuby does not thread
  # it through the raise sites yet, so the reader exists and answers nil unless
  # something set it.
  def path
    defined?(@path) ? @path : nil
  end unless method_defined?(:path)
end

class Random
  # 2.0's Random#random_number: rand's behaviour, but a bare call always answers
  # a Float and an out-of-range argument is an ArgumentError rather than nil.
  def random_number(limit = nil)
    limit.nil? ? rand : rand(limit)
  end unless method_defined?(:random_number)

  def self.random_number(limit = nil)
    limit.nil? ? rand : rand(limit)
  end unless respond_to?(:random_number)
end

# CRuby raises a class named for the errno whenever a non-blocking operation
# would have had to wait, so that a rescue can tell "nothing yet" from a real
# error of the same errno; the WaitReadable/WaitWritable modules they include
# are what code rescues when it does not care which. On Linux EWOULDBLOCK and
# EAGAIN are one errno, and these are correspondingly one class under two
# names - which is exactly what ruby/spec checks.
class IO
  class EAGAINWaitReadable < Errno::EAGAIN
    include IO::WaitReadable
  end

  class EAGAINWaitWritable < Errno::EAGAIN
    include IO::WaitWritable
  end

  if Errno::EAGAIN.equal?(Errno::EWOULDBLOCK)
    EWOULDBLOCKWaitReadable = EAGAINWaitReadable
    EWOULDBLOCKWaitWritable = EAGAINWaitWritable
  else
    class EWOULDBLOCKWaitReadable < Errno::EWOULDBLOCK
      include IO::WaitReadable
    end

    class EWOULDBLOCKWaitWritable < Errno::EWOULDBLOCK
      include IO::WaitWritable
    end
  end

  class EINPROGRESSWaitReadable < Errno::EINPROGRESS
    include IO::WaitReadable
  end

  class EINPROGRESSWaitWritable < Errno::EINPROGRESS
    include IO::WaitWritable
  end
end

# MRI's prelude: Kernel#pp is there from the start and loads the pp library the
# first time it is called; the library then replaces it with its own (a module
# function: Kernel.pp is only public from then on).
module Kernel
  private def pp(*objs)
    require "pp"
    objs.each { |obj| ::PP.pp(obj) }
    objs.size <= 1 ? objs.first : objs
  end
end unless Kernel.private_method_defined?(:pp) || Kernel.method_defined?(:pp)

# MRI's prelude again: Binding#irb starts an IRB session on the binding.  The
# library redefines this method when it loads, so the second call below is
# irb's own.
class Binding
  def irb
    require "irb"
    irb
  end
end

require "argf"

# monitor is a builtin in CRuby 4.0: Monitor and MonitorMixin are there before
# anything requires them, and gems reach for ::Monitor without a require of their
# own (concurrent-ruby does). Autoloaded rather than required, so that a program
# that never touches it does not pay for it at startup.
Object.autoload :Monitor, "monitor"
Object.autoload :MonitorMixin, "monitor"

# error_highlight is loaded by CRuby's own startup, so ::ErrorHighlight is there
# for anything that asks - ActiveSupport calls ErrorHighlight.spot for every
# frame it renders on the Rails error page, with no require of its own.  Same
# treatment as Monitor: autoloaded, so a program that never raises does not pay
# for it.  See Src/StdLib/ironruby/error_highlight.rb for what IronRuby's copy
# can and cannot do.
Object.autoload :ErrorHighlight, "error_highlight"

class Encoding
  class << self
    # Encodings are the runtime's own; there is no Encoding.new.
    undef_method :new if method_defined?(:new)
  end

  class Converter
    # Two converters are equal when they would do the same conversion.
    def ==(other)
      return nil unless other.is_a?(::Encoding::Converter)
      convpath == other.convpath
    end
  end
end

class Enumerator
  # MRI's Enumerator::Generator: the block of Enumerator.new, as an Enumerable of its own.
  class Generator
    include ::Enumerable

    def initialize(&block)
      ::Kernel.raise(::ArgumentError, "no block given") unless block
      @__block__ = block
    end

    def each(*args, &block)
      producer = @__block__
      ::Enumerator.new { |y| producer.call(y, *args) }.each(&block)
    end
  end unless const_defined?(:Generator, false)
end

# A Method comes from #method, an UnboundMethod from #instance_method or #unbind.
[::Method, ::UnboundMethod].each do |klass|
  klass.singleton_class.send(:undef_method, :new) if klass.respond_to?(:new)
end

# Ruby::Box (4.0) runs code with classes and loaded files of its own. MRI ships it disabled -
# RUBY_BOX=1 at startup turns it on - and IronRuby does not implement it, so what is here is
# Ruby::Box as a disabled one looks: no box is current, none can be made, and every method is
# in the main box (Method#box is nil).
module Ruby
  class Box < ::Module
    DISABLED = "Ruby Box is disabled. Set RUBY_BOX=1 environment variable to use Ruby::Box."
    __hide_constant__ :DISABLED

    class Entry
    end

    module Loader
    end

    class << self
      def enabled?
        false
      end

      def current
        nil
      end
    end

    def initialize(*)
      ::Kernel.raise(::RuntimeError, DISABLED)
    end
    private :initialize

    def eval(code)
      ::Kernel.raise(::RuntimeError, DISABLED)
    end

    def load(*args)
      ::Kernel.raise(::RuntimeError, DISABLED)
    end

    def require(path)
      ::Kernel.raise(::RuntimeError, DISABLED)
    end

    def require_relative(path)
      ::Kernel.raise(::RuntimeError, DISABLED)
    end

    def load_path
      ::Kernel.raise(::RuntimeError, DISABLED)
    end

    def inspect
      super
    end
  end
end unless defined?(::Ruby::Box)

class Method
  # The Ruby::Box the method was defined in; nil while boxes are disabled.
  def box
    nil
  end unless method_defined?(:box)
end

# MRI defines these with rb_define_module_function, which also gives each a private instance
# method, for a class that includes the module. IronRuby's are singleton methods; the instance
# copy calls the singleton.
{
  ::Process => %i[argv0 clock_getres clock_gettime daemon detach egid egid= euid euid= getpgid
                  getpgrp getpriority getrlimit getsid gid gid= groups groups= initgroups kill
                  maxgroups maxgroups= pid ppid setpgid setpgrp setpriority setproctitle setrlimit
                  setsid times uid uid= wait wait2 waitall waitpid waitpid2 warmup],
  ::Signal => %i[list signame trap],
  ::ObjectSpace => %i[_id2ref count_objects define_finalizer each_object garbage_collect undefine_finalizer],
  ::Marshal => %i[dump],
}.each do |mod, names|
  names.each do |name|
    next if !mod.respond_to?(name) || mod.private_method_defined?(name, false) || mod.method_defined?(name, false)
    # with the arity of the singleton method, as the copy MRI makes has (Process#egid is 0)
    mod.module_eval do
      case mod.method(name).arity
      when 0 then define_method(name) { mod.__send__(name) }
      when 1 then define_method(name) { |a| mod.__send__(name, a) }
      when 2 then define_method(name) { |a, b| mod.__send__(name, a, b) }
      else define_method(name) { |*args, **opts, &block| mod.__send__(name, *args, **opts, &block) }
      end
      private name
    end
  end
end

# Gem compatibility patches (see the file for the policy on adding one).
require "ironruby/gem_compat"
