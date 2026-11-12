# frozen_string_literal: true
#
# The bcrypt gem's C extension, provided by IronRuby itself.
#
# bcrypt 3.1.22 (the password hashing gem Devise and has_secure_password use) is
# Ruby over one C extension, bcrypt_ext: BCrypt::Engine.__bc_crypt and __bc_salt
# on Openwall's crypt_blowfish.  The gem's Ruby is vendored unchanged
# (Src/StdLib/ironruby/bcrypt.rb and bcrypt/); its `require "bcrypt_ext"` finds
# this file, which loads the same two functions written in C#
# (Src/Libraries/BCrypt) from the algorithm - EksBlowfish, the $2a$/$2b$/$2x$/$2y$
# variants and crypt_blowfish's self-test included.
#
# A pinned default gemspec makes RubyGems agree that bcrypt 3.1.22 is installed,
# so `gem "bcrypt"` in a Gemfile resolves here instead of stopping at extconf.rb.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.BCrypt'
