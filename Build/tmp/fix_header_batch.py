"""One-shot: move the script-level SET XACT_ABORT ON / GO ABOVE the header block, so the header sits
in the same batch as CREATE and therefore lands in sys.sql_modules. See the inserted comment."""
import pathlib, sys

PREAMBLE = """\
-- SET XACT_ABORT ON sits ABOVE the header block deliberately, and moving it back below would restore a
-- real defect. The GO on the next line ends the batch, and sys.sql_modules stores only the batch that
-- contains CREATE -- so a header placed AFTER this GO is invisible to anyone reading the procedure out
-- of the database through sp_helptext, OBJECT_DEFINITION, or SSMS "Script as CREATE", which is where a
-- maintainer actually reads it. Every view in this database already carried its header inside the
-- definition because nothing separates the two; no procedure did until this was corrected.
SET XACT_ABORT ON;
GO
"""

targets = [pathlib.Path(p) for p in sys.argv[1:]]
for path in targets:
    lines = path.read_text(encoding="utf-8").split("\n")
    try:
        i = next(n for n, l in enumerate(lines) if l == "SET XACT_ABORT ON;")
    except StopIteration:
        print(f"  SKIP {path.name}: no script-level SET XACT_ABORT ON;")
        continue
    if lines[i + 1] != "GO":
        print(f"  SKIP {path.name}: line after SET is {lines[i + 1]!r}, not GO")
        continue
    if not lines[0].startswith("/*"):
        print(f"  SKIP {path.name}: does not open with a header block")
        continue

    header = lines[:i]                      # header block plus its trailing blank line
    while header and header[-1] == "":
        header.pop()
    rest = lines[i + 2:]                    # from the blank line before CREATE onward
    while rest and rest[0] == "":
        rest.pop(0)

    path.write_text("\n".join(PREAMBLE.split("\n") + header + ["", ""] + rest),
                    encoding="utf-8", newline="\n")
    print(f"  moved {path.name}")
