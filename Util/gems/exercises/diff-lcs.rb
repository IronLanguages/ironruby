require 'diff/lcs'
require 'diff/lcs/hunk'

a = %w[a b c d e f]
b = %w[a c d x e f g]

puts Diff::LCS.lcs(a, b).inspect
puts Diff::LCS.diff(a, b).map { |ch| ch.map { |c| [c.action, c.position, c.element] } }.inspect
puts Diff::LCS.sdiff(a, b).map { |c| [c.action, c.old_element, c.new_element] }.inspect
puts Diff::LCS.patch(a, Diff::LCS.diff(a, b)).inspect
puts Diff::LCS.unpatch!(b.dup, Diff::LCS.diff(a, b)).inspect

puts Diff::LCS.lcs('abcdef'.chars, 'acdxef'.chars).join

changes = []
Diff::LCS.traverse_sequences(a, b, Object.new.tap do |cb|
  def cb.match(e) end
  def cb.discard_a(e) end
  def cb.discard_b(e) end
end)
puts changes.inspect

hunk = Diff::LCS::Hunk.new(a, b, Diff::LCS.diff(a, b).first, 3, 0)
puts hunk.diff(:unified)
