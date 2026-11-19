# Electrical symbol catalog

`catalog.json` is the versioned, user-editable allowlist used by bounded AutoCAD
symbol operations. Geometry is limited to validated line and circle primitives.

Changing a definition invalidates its `definition_sha256`. Review the change, update
the symbol version and block name when geometry semantics change, calculate the
canonical definition hash, and run the catalog tests before use. AI-Engineer fails
closed on an unapproved hash and never silently overwrites a same-named block already
present in a drawing.
