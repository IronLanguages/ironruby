require 'rainbow'

Rainbow.enabled = true
puts Rainbow('x').red.inspect
puts Rainbow('x').bright.blue.underline.inspect
puts Rainbow('x').background(:yellow).inspect
puts Rainbow('x').color(:aqua).inspect
puts Rainbow('x').color(1, 2, 3).inspect
puts Rainbow('x').color('#ff8000').inspect
puts Rainbow('x').bold.italic.inverse.faint.strike.inspect
puts Rainbow('x').blink.hide.inspect
puts Rainbow('already ' + Rainbow('inner').red + ' outer').green.inspect

Rainbow.enabled = false
puts Rainbow('x').red.inspect
puts Rainbow('x').red.length

Rainbow.enabled = true
puts Rainbow::X11ColorNames::NAMES[:aqua].inspect
puts Rainbow::Color.build(:foreground, [:red]).codes.inspect
puts Rainbow::Presenter.instance_methods(false).map(&:to_s).sort.first(5).inspect
