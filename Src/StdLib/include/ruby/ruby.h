/*
 * IronRuby does not implement Ruby's C extension API.
 *
 * An extension for IronRuby is a .NET assembly, not a shared object compiled
 * against libruby, and there is no libruby here to compile against.  mkmf,
 * however, refuses to load at all unless RbConfig::CONFIG["rubyhdrdir"] names a
 * directory that holds ruby/ruby.h - it aborts before it has defined anything -
 * so this header exists to be found, and to say what the situation is the
 * moment anything actually tries to include it.
 */

#error "IronRuby has no C extension API: an IronRuby extension is a .NET assembly, not a C shared object"
