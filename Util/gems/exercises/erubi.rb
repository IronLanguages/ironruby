require 'erubi'
require 'erubi/capture_block'

src = Erubi::Engine.new("<p><%= name %></p>\n<% 2.times do |i| %><%= i %><% end %>\n<%# comment %>").src
name = 'world'
puts eval(src)

esc = Erubi::Engine.new('<%= "<b>" %>|<%== "<b>" %>', escape: true).src
puts eval(esc)

puts Erubi::Engine.new('<%= 1 %>', bufvar: '@out').src.include?('@out')
puts Erubi::Engine.new('plain text only').src.include?('plain text only')
puts Erubi::Engine.new('<%- x = 1 -%><%= x %>', trim: true).src.length > 0
puts eval(Erubi::Engine.new('<%- x = 1 -%>
<%= x %>', trim: true).src)

puts Erubi.h('<a href="x">&')
puts Erubi::Engine.new('<%= 1 %>').filename.inspect
puts Erubi::Engine.new('<%= 1 %>', filename: 'f.erb').filename
