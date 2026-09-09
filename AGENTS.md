# FDull

Read README.md before changing public behavior. Authored implementation and
automation are compiled F#. Keep compiler settings strict and dependencies locked.

The guard checks its own library, CLI, worker and tests. A tooling or test profile
does not grant blanket permissions. Review exact file/declaration/API contracts
against function bodies and regression evidence before updating fdull.json.
Never generate permissions automatically from diagnostics or add suppressions.
Remove unused capabilities. Update reviewed source/build SHA-256 fingerprints
after intentional changes; never turn a failed or incomplete check into success.

Run formatting, Release build, tests and the CLI's verify command. Packaging changes
also require a local tool installation and independent consumer smoke check.
Keep coverage claims linked to executable evidence. The broader safety standard
includes organizational controls a local package cannot implement.
