# frozen_string_literal: true
#
# MRI's ripper/core.rb is a piece of Ripper's Ruby half. IronRuby's Ripper (ripper.rb, on
# top of prism) defines Ripper.parse, .lex, .tokenize, .sexp, .sexp_raw and
# Ripper::Lexer in one file, so requiring this piece loads all of it.
require 'ripper'
