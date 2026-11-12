# IO#wait, #wait_readable, #wait_writable and #wait_priority are part of IO itself,
# along with IO::READABLE, IO::PRIORITY and IO::WRITABLE - the same as CRuby, where
# io/wait stopped being a separate extension. The file stays so that `require
# "io/wait"` keeps working; there is nothing left for it to define.
#
# It does not bring back #nread and #ready?: CRuby no longer has them either.
