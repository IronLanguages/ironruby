# frozen_string_literal: true
#
# MRI's openssl gem keeps OpenSSL::X509 in openssl/x509.rb, loaded by openssl.rb. IronRuby's
# openssl.rb defines it itself, so requiring this piece loads all of it.
require 'openssl'
