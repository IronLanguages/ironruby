# frozen_string_literal: true
#
# error_highlight, for IronRuby.
#
# The upstream library (Src/StdLib/ruby/4.0/error_highlight) finds the code
# fragment an exception came from through the node CRuby's frame was running:
# RubyVM::AbstractSyntaxTree.node_id_for_backtrace_location gives its id, and
# prism, parsing the file again, gives the node with that id to the Spotter,
# which turns it into the columns to underline.
#
# IronRuby parses with prism too, but its frames carry a file, a line and the
# method's name, not a node id. So the node is looked for on the frame's line,
# in a fresh prism parse of its file, by what the error itself says it is
# about: the name a NameError or NoMethodError carries, the method a
# TypeError's or ArgumentError's frame belongs to (`Integer#+` - the call to
# `+`), or the `raise` that raised it. Only a node that is the one such
# candidate on the line is spotted; when there are two (`a.foo + b.foo` with
# either receiver nil) or none, `spot` answers nil - upstream's own answer for
# a fragment it cannot identify - and the message is left as it is, rather
# than ever pointing at the wrong code. Everything else (the Spotter, the
# formatter, the CoreExt that appends the snippet to #detailed_message) is
# upstream's, unchanged.

require_relative "../ruby/4.0/error_highlight/base"

module ErrorHighlight
  # replaced, not redefined: no "method redefined" warning under -w
  singleton_class.send(:remove_method, :spot)

  def self.spot(obj, **opts)
    case obj
    when Exception
      exc = obj
      loc = opts[:backtrace_location]
      explicit = !loc.nil?
      opts = { point_type: opts.fetch(:point_type, :name) }

      unless loc
        case exc
        when TypeError, ArgumentError
          opts[:point_type] = :args
        end

        locs = exc.backtrace_locations
        return nil unless locs

        loc = locs.first
        return nil unless loc

        opts[:name] = exc.name if NameError === obj
      end

      return nil unless Thread::Backtrace::Location === loc

      node = IronRubyLocator.find(exc, loc, opts[:point_type], opts[:name], explicit)
      Spotter.new(node, **opts).spot

    else
      if defined?(::Prism::Node) && ::Prism::Node === obj
        Spotter.new(obj, **opts).spot
      else
        raise TypeError, "Exception is expected"
      end
    end

  rescue SyntaxError,
         SystemCallError, # file not found or something
         ArgumentError # eval'ed code

    return nil
  end

  module IronRubyLocator # :nodoc:
    CACHE_SIZE = 4
    # every thread whose error message is asked for comes through here
    @trees = {}
    @trees_lock = Thread::Mutex.new

    ARITY_ERROR = /\A(?:wrong number of arguments|missing keyword[s]?|unknown keyword[s]?|no keywords accepted)\b/

    class << self
      def find(exc, loc, point_type, name, explicit)
        path = loc.absolute_path
        return nil unless path

        lineno = loc.lineno
        root = tree(path)
        return nil unless root

        nodes = nodes_on_line(root, lineno)

        if name && !explicit
          # a NameError: the call, the constant or the operator-assignment that names it - or
          # else the call that raised it (`struct[:nope]`), as for any other error
          found = nodes.select { |node| names?(node, name, lineno) }
          return only(found) unless found.empty?
        end

        if explicit && ArgumentError === exc && ARITY_ERROR.match?(exc.message.to_s)
          # CoreExt's "caller:" and "callee:" of an ArgumentError for the arguments a method was
          # given: the backtrace starts with the method's frame - at its definition for a Ruby
          # method, at the call for a library one - and goes on with its caller's, at the call
          locs = exc.backtrace_locations
          method = method_name(locs.first.label)
          return nil unless method
          if loc.equal?(locs.first)
            defs = nodes.select { |node| node.type == :def_node && node.name == method && node.name_loc.start_line == lineno }
            return only(defs) unless defs.empty?
          end
          return only(calls_named(nodes, method, lineno))
        end

        method = method_name(loc.label)
        if method
          # the frame of a method written in C# (`Integer#+`) is reported at the line of
          # its caller: the call to it is on that line
          found = calls_named(nodes, method, lineno)
          return only(found) unless found.empty?
        end

        # a Ruby frame: what was running there when it raised
        only(nodes.select { |node| node.type == :call_node && node.receiver.nil? && (node.name == :raise || node.name == :fail) })
      end

      private

      def only(nodes)
        nodes.size == 1 ? nodes.first : nil
      end

      # "Integer#+", "Kernel.Integer", "block in Foo#bar" -> the method's name
      def method_name(label)
        return nil unless label
        label = label.sub(/\A(?:rescue in |ensure in |(?:block )+(?:\(\d+ levels\) )?in )/, "")
        return nil if label.start_with?("<")
        m = label.match(/[#.]([^#.]+)\z/) || label.match(/\A([^#.<>\s]+)\z/)
        m && m[1].to_sym
      end

      def calls_named(nodes, method, lineno)
        nodes.select { |node| node.type == :call_node && node.name == method && name_line(node) == lineno }
      end

      def name_line(node)
        (node.message_loc || node.location).start_line
      end

      def names?(node, name, lineno)
        case node.type
        when :call_node
          node.name == name && name_line(node) == lineno
        when :call_operator_write_node, :index_operator_write_node, :local_variable_operator_write_node
          node.binary_operator_loc.start_line == lineno &&
            (node.binary_operator == name ||
              (node.type == :call_operator_write_node && (node.read_name == name || node.write_name == name)) ||
              (node.type == :index_operator_write_node && (name == :[] || name == :[]=)))
        when :constant_read_node, :constant_path_node
          node.name == name && node.location.start_line == lineno
        when :constant_path_operator_write_node
          node.target.name == name && node.location.start_line == lineno
        else
          false
        end
      end

      # The nodes whose source covers the line: only those can be what a frame on it ran.
      def nodes_on_line(root, lineno)
        found = []
        stack = [root]
        while (node = stack.pop)
          location = node.location
          next if location.start_line > lineno || location.end_line < lineno
          found << node
          stack.concat(node.compact_child_nodes)
        end
        found
      end

      # The tree of the file, parsed again with prism, as upstream's prism_find does - kept
      # for the few files errors were last raised in, as long as they are unchanged.
      def tree(path)
        stat = File.stat(path)
        key = [stat.mtime, stat.size]
        cached = @trees_lock.synchronize { @trees[path] }
        return cached[1] if cached && cached[0] == key

        # parsed outside the lock: another thread's message need not wait for this file
        require "prism"
        root = Prism.parse_file(path).value
        @trees_lock.synchronize do
          @trees.delete(path)
          @trees.shift if @trees.size >= CACHE_SIZE
          @trees[path] = [key, root]
        end
        root
      end
    end
  end
  private_constant :IronRubyLocator
end

require_relative "../ruby/4.0/error_highlight/core_ext"
