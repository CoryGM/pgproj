# Deeply nested statements

pgproj reads a statement however deeply it is nested, up to what PostgreSQL's own parser accepts: a view that adds up hundreds of
columns, a long chain of `||`, nested `CASE` expressions or subqueries. A statement it cannot read is always an error, never a warning, so
`build` cannot pass while a file's statements go unread.

- **What PostgreSQL's parser refuses** (`stack depth limit exceeded`, `memory exhausted`) is a `build` error at the statement, as any
  syntax error is. PostgreSQL's server refuses such a statement too. Where the parser accepts a statement too deep for libpg_query to hand
  over (seen on Linux and macOS; Windows reports `stack depth limit exceeded`), the error is libpg_query's `parse tree is nested too deeply to serialize to protobuf`.
- **A statement nested more than 25,000 levels deep** in the parse tree is a `build` error that names it:
  `This statement is nested more than 25,000 levels deep, deeper than pgproj reads, so it was not read: ...`. The other statements of
  the file are read. libpg_query, the parser pgproj uses, refuses statements well before this depth, so you should not see this error.
- **If pgproj cannot read a file's parse tree at all**, that is a `build` error too (`pgproj could not read the parse tree of this
  file, so none of its statements were read`), never a warning: `diff` and `publish` stop rather than treat the file's objects as
  missing.

pgproj reads parse trees on a thread with a large stack (256 MB of address space, used only as needed), so a deep statement does not
crash the process.
