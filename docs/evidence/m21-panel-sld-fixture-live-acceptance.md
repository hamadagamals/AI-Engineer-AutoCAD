# Milestone 21 fixed-fixture live acceptance evidence

Date: 2026-08-11

## Exact approved source

- Path: `workspace/autocad-preview-metric/acadiso-preview-source.dwt`
- Size: 32,151 bytes
- SHA-256: `0b40e693db4f170a4bfe0ebdad7f599e2773cf1921f4698a9056f908b2479129`
- Intended operation: `autocad.create_panel_sld_fixture_scratch`
- Boundary: create a run-local `source.dwg`; never save or replace the DWT source.

## Attempt evidence

1. Task `f387201e8ccf4ce0a6ab03226e69d96d`, one-use approval
   `ee0a5e3a59f6403795182f8e45a4542d`, run
   `1b26f945279f415e98f5ca0683fd1e87`: the sandbox account failed before opening a
   drawing with `Problem with setting up current profile`. The approval was consumed;
   the retained scratch hash and source hash both remained the approved SHA-256.
2. Task `288118001d2c4362aa07a590c1ad5c14`, one-use approval
   `a38d58cb013944eb8de27d0f031fac62`, run
   `09e1d2b0fbfa448ea7dbd92b0d28251b`: the initialized interactive-user profile opened
   AutoCAD 2021 Core Console successfully and reported `ACADVER=24.0`, `INSUNITS=4`,
   and `UNITSVALID=1`. The live run exposed a missing fixture-local initialization of
   `aieng-layer-valid`; the fixed block producer therefore failed closed with zero
   markers, zero attributes, no QSAVE, and an unchanged scratch/source SHA-256. The
   approval was consumed and no drawing output evidence was published.
3. Task `3b072d77ac9d49888765752f2a63363a`, one-use approval
   `65d71122ffd4435d9585f8ecd18cde29`, run
   `added231005a41b29ea35091d5cf7fe0`: the layer fix passed live and the producer
   created exactly 11 top-level references and all ten marker references. AutoCAD
   reported 60 instead of 62 attributes and an invalid load link; both missing
   attributes were the way tag `LOAD_SOURCE_OBJECT_IDS`. The run failed closed without
   QSAVE or output evidence, and both retained scratch and source remained at the exact
   approved SHA-256.
4. Task `235d103bc2984c46bb6f95482a22d482`, one-use approval
   `1c81afbe52a8420ebe33f94742da5f4f`, run
   `5b4c2cc6fb454cccaa671d3b54a5ae34`: AutoCAD again passed units and layer validation,
   created all 11 top-level references, all ten marker references, and recognized the
   socket load. It again reported 60/62 attributes because both `LOAD_HANDLES`
   attributes were omitted, so load-link/content validation failed. The producer did
   not call QSAVE or publish output evidence. The retained 32,151-byte scratch and the
   DWT source both remain at the approved SHA-256; the approval is consumed.
5. Task `6f7ee775408940a9bc1d7dbac316d4f3`, one-use approval
   `9022395a780a405db367d65d719f79a3`, run
   `0ed48102f2c94b2c93823def36298221`: marker profile V3 with `LOAD_REFS`
   reproduced the exact 60/62 outcome. Units, layers, 11 top-level references, ten
   marker references, and the recognized socket load all passed; load-link/content
   validation failed, QSAVE was not called, and no output evidence was published. The
   retained scratch and approved source are both 32,151 bytes at the approved SHA-256.
6. Task `bf61402c9390454e97ffedda5a31c98c`, one-use approval
   `c083e506d7e0445fa985d0f0d3ce66c4`, run
   `5a35d833156f492db06ab65831c15558`: atomic-binding profile V4 produced 58/60
   attributes, again omitting the binding on the load way and the empty binding on the
   spare way. All other bounded counts and setup checks passed. QSAVE was not called,
   no output evidence was published, and both the retained scratch and source remain
   32,151 bytes at the approved SHA-256.
7. Task `deea981f8346488ca233d6a96f30dba5`, one-use approval
   `604317d91c71435aab367b3d5aa61d95`, run
   `8331bc8df15d4094a9153afe642bafb4`: AutoCAD 2021 Core Console rejected the
   attempted ActiveX conversion before marker creation with
   `bad argument type: VLA-OBJECT nil`. No completion markers, QSAVE, or output evidence
   were produced. The retained scratch and approved source remain 32,151 bytes at the
   approved SHA-256.
8. Task `acf4d9d5f16643e897788c470f459ffb`, one-use approval
   `27e34cbbc9f14423adb4417c08c3e7ed`, run
   `cac095676a574ac984bac61102277915`: bounded selection succeeded and
   `LOADHANDLEVALID=1`, but only 56/58 attributes were linked and both load-link/content
   checks failed. All ten markers and 11 top-level references were created; QSAVE was
   withheld and both source/scratch remained 32,151 bytes at the approved SHA-256.
9. Task `ba37a22d9ba24b0e8b92e02fc94abaf3`, one-use approval
   `87453fe58ba342cfba63de0be6a57062`, run
   `6b1c53213ca34f649cb09ca981691b03`: diagnostics isolated the root cause.
   `LOADHANDLEVALID=1`, but `WAY1ATTRIBUTECOUNT=2` and `WAY2ATTRIBUTECOUNT=15` showed
   every captured marker reference lagged one complex entity because `entlast` ran before
   its attribute `SEQEND`. The run failed closed with no QSAVE/output; source and retained
   scratch remain 32,151 bytes at the approved SHA-256.
10. Task `ab64c20ccee442e2ac9afb7bc8393eae`, one-use approval
    `4b46b037127a47b495084b758c57bcd8`, run
    `cdd846e7d3c24aa59400a12721f32bdb`: **passed** live AutoCAD 2021 Core Console
    producer acceptance in 3.777 seconds. Profile V6 reported four marker definitions,
    ten marker references, one recognized load, `LOADHANDLEVALID=1`, 58 attributes,
    `WAY1ATTRIBUTECOUNT=15`, `WAY2ATTRIBUTECOUNT=13`, both binding/name present,
    valid content/link checks, exact 0-to-11 entity growth, and `DBMOD=0`. The saved
    37,266-byte scratch DWG has SHA-256
    `82f8f28780b70c72fb1018e44e332d0ed3b82be3020112f80ca88229c58dca2c`.
    The 32,151-byte DWT source remained unchanged at its approved SHA-256. Durable
    approval was consumed and `drawing-output.json` was published.
11. Independent read-only task `089cded2f34340d286639ba4e256b7db`, one-use approval
    `5fece4e264d248f098b4360088c7530a`, run
    `34f3d86e784b492faf9ed50d0f4083de`: **passed** AutoCAD 2021 inventory in
    0.711 seconds. It reported AutoCAD `24.0`, two layers, 11 entities, `DBMOD=0`,
    `read_only=true`, unchanged source and inventory-copy bytes, and no stdout/stderr
    truncation. Both the accepted DWG and its inventory scratch remained 37,266 bytes at
    SHA-256 `82f8f28780b70c72fb1018e44e332d0ed3b82be3020112f80ca88229c58dca2c`.
12. Visible in-product acceptance used OS-read-only preview
    `source - Read Only 20260812-083653.dwg` with the same 37,266 bytes and accepted
    SHA-256. Explicit selection context `a5b86d29e9724b7bb94e6111edb26468`
    contained all 11 entities and evidence SHA-256
    `9bb8d77ba09892352e52d5cd11e04a906f057136d269883135b68c99e1c3998f`.
    The `Panels / SLD` tab visibly returned complete extraction SHA-256
    `0a7bf895fe2e863512d78c0f4162c5d7e4a85bddd0ba2d1febfe7a90a9e86bea`
    and schedule SHA-256
    `df4cf74cc3cbfaa01ecd94dc4d4c036e73cbe746eb9dea598889f068862938f1`.
    It showed panel `panel:db1 | DB-1`, two ordered ways, one load way, one spare,
    circuit 1 on L1 with `16 A / 6 kA`, and circuit 2 spare on L2 with
    `16 A / 6 kA spare`. Independent local recomputation matched both hashes exactly,
    with `complete=true`, zero findings, `provider_egress_performed=false`, and
    `drawing_mutation_performed=false`. Context `DBMOD=16` was view/window-only; the
    OS-read-only DWG bytes and accepted SHA-256 remained unchanged.

## Corrective result

The fixture script now reads the exact insertion-layer record and validates ACI color
and linetype before the catalog block setup. `LAYERVALID=1` is now a mandatory parsed
success marker, and regression coverage proves initialization occurs before block setup.
Compile and the full automated suite pass: `421 passed, 1 skipped, 2 warnings`.
The repeated live 60/62 result across three tag variants rules out simple tag length or
one reserved name and isolates the failure to the independent load-reference attribute.
The fixed way schema now combines each load ID and handle atomically as
`load-id@AutoCAD-handle` in `LOAD_BINDINGS`. The sixth result then isolated two distinct
cases: the newly created load handle was not available through the prior `entget` path,
and an empty spare binding should not be serialized. The producer now reads the handle
through a bounded exact-block-name selection that must return exactly one reference,
emits and gates on `LOADHANDLEVALID=1`, omits both empty load fields from the spare way,
expects 58 attributes, and uses extraction v6 / marker profile V6. The binding string is
now precomputed before attribute creation, and bounded per-way/presence markers expose any
remaining rejection without printing the handle or project values. The attempted ActiveX
conversion was removed after Core Console rejected it. Handles still resolve only against
recognized devices in the exact context; no identity inference was introduced.
The ninth run proved the binding data was not the remaining defect: marker reference
capture occurred before AutoCAD completed each complex INSERT/ATTRIB/SEQEND entity. The
producer now captures `entlast` only after `SEQEND`, so each validation targets its own
block reference instead of the preceding marker.

## Acceptance conclusion

Producer, independent inventory, and the visible complete-result palette path are
accepted. The final live negative-path check follows.

13. Visible negative-path acceptance selected only the panel marker. Context
    `fc768c23c4324a3b8b9f0c57adbaac06` contained one entity with evidence SHA-256
    `691017fc5e002b51d0114d0ac4108cb9794967754942c391d330fc6bdd9f2320`.
    The palette visibly returned extraction SHA-256
    `f2933511c34e9ab047fc2241e9a647c1f3b21f8b55359e3191cee45da4086404`,
    `complete=false`, and three explicit `UNKNOWN` findings for missing outgoing-way,
    SLD-node, and SLD-edge markers. Independent recomputation matched the hash and proved
    `semantic_model=null`, `contract=null`, `provider_egress_performed=false`, and
    `drawing_mutation_performed=false`; no schedule was displayed. Context `DBMOD=20`
    reflects in-session window/selection-variable state and is not claimed clean. The
    OS-read-only DWG remained 37,266 bytes at accepted SHA-256
    `82f8f28780b70c72fb1018e44e332d0ed3b82be3020112f80ca88229c58dca2c`.

Milestone 21 producer, independent inventory, complete-result palette, and fail-closed
negative display acceptance are complete within the documented fixed-marker boundary.
