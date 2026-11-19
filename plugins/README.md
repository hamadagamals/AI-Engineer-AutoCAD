Plugins and project-owned extension data must use validated schemas and remain under
the same permission, approval, logging, and workspace policies as built-in features.

`electrical_symbols/catalog.json` is the first operational extension data set. It is
user-editable but hash-approved; it cannot provide executable code or arbitrary
AutoLISP. User DWG/DWT intake can now produce a review proposal, but catalog mutation
requires its own hash-bound approval, backup, atomic write, full reload, and rollback.
Drawing block import or redefinition remains prohibited.
Exact compatible definitions already present in a scratch drawing may be reused for a
bounded circuit fragment after catalog-bound geometry verification.
