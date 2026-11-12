# Exercises

One file per popular-tier gem that ships no test suite in its `.gem`.

Each is a small but real use of the gem: it must print deterministic output on
stdout and nothing else, and `Util/gems/run.sh popular` runs it under IronRuby
and under CRuby 4.0.6 and diffs the two. CRuby is the oracle, so the pass is a
byte-identical stdout - an exercise that prints "ok" whatever happens measures
nothing and is worse than no exercise at all.

Rules that keep them honest:

* No network, no clock, no PID, no temp-directory names in the output.
* No `rescue` that swallows the failure being measured. If a call is expected
  to raise, print the class and message so a *different* failure shows up as a
  diff.
* Nothing that depends on Hash iteration order across engines: sort first.
* Exercise what the gem is *for*, not just that `require` returned true - the
  load check in `runner.rb` already covers that.
