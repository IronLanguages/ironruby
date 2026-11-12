Where the "413 invisible spec files" number came from
=====================================================

The sweep committed at 87ce602 (Util/sweep-core.tsv before this branch) reported
413 of 2150 spec/core files producing no tally at all. That headline was a defect
in the measuring script, not in IronRuby.

The script's tally pattern demanded the plural "examples". A spec file with
exactly one example reports

    1 file, 1 example, 1 expectation, 0 failures, 0 errors, 0 tagged

and the pattern could not read it, so the file was recorded as NO-TALLY. The
four numbers:

    old sweep (87ce602), rows containing "1 example,"     0
    old sweep (87ce602), rows containing "2 examples,"  284
    fresh sweep of current master, rows with 1 example   402
    of the old 413 NO-TALLY files, reporting 1 example today  401

401 of the 413 were never invisible. They ran, they reported, and the harness
could not read the word. Of the 12 that were genuinely invisible at 87ce602,
6 were fixed by the waves that landed between 87ce602 and 70de9e9 - and one of
those, spec/core/string/modulo_spec.rb, is 274 examples on its own - and 6 are
still with us. A seventh, spec/core/array/product_spec.rb, has appeared since.

A second, smaller harness defect of the same kind is fixed in Util/sweep-core.sh
(commit 5c50850): a spec that prints a NUL byte - String#undump's specs do, the
NUL is in the example description - makes grep call the captured log a binary
file and print "Binary file ... matches" instead of the tally line, which again
reads as no tally. Always pass grep -a when reading a spec log.

The real spec/core baseline, from Util/sweep-core-master.tsv (master, 70de9e9):

    2151 files, 2144 with a tally, 7 with none (all hangs)
    18566 examples, 52816 expectations, 2707 failures, 4370 errors
    11489 passing

Count it with `ruby Util/sweep-total.rb Util/sweep-core-master.tsv`.
