# School electrical layout: observed failures and prevention gates

Status: project learning record, 2026-10-31. V5 was saved for visual review. The
operator reported no further placement comments at this stage and asked to proceed
with circuits and calculations. This is not photometric, electrical or code approval.

## What happened

| Observed issue | Evidence / cause | Required prevention gate |
|---|---|---|
| V1/V2 lights overlapped, sat near doors or appeared over the stair | Early distributions used room/ceiling bounding boxes and symbols without full room/obstacle checks. | Before drawing, validate point-in-room, same-level door/stair/shaft clearance, minimum separation and a level-aware duplicate key. Inspect exceptions in Revit. |
| V3 gym had three visible holes despite zero duplicate/outside-room warnings | 45 chosen cells out of 48; the tests checked containment, not interior coverage. | Sample the whole room interior, record the worst distance to the nearest point, test irregular rooms and inspect a plan render. A coverage pass does not replace lux/UGR/IES assessment. |
| Some rooms/corridors seemed overfilled, others underfilled | Bounding-box area overestimated irregular spaces. CORRIDOR 2513 was 726.274 m² in the preliminary schedule but 208.744 m² by Revit actual-area audit. | Use actual room area and exact containment for quantities and candidate coordinates; reconcile every room and type to the schedule. |
| V4 visual review still marked inappropriate positions and stair/door questions | 17 operator marks included both missing and wrongly located lights. Full Level 1 audit found 1,150 symbol strokes without a same-level ceiling bounding-box candidate, even though it found no direct same-level door/stair intersections. | Treat each mark as a location review, not automatically as a missing fixture. Audit actual mounting face, level, nearby obstacles and clearance. Flag unresolved hosts on the plan. |
| Apparent duplicate coordinates across floors | V5 has 203 repeated X/Y positions on different levels and zero repeated same-level centers. | Deduplicate on **level + X + Y**, not X/Y alone; retain room identity. The load-ledger regression test enforces this. |
| A single generic light type or wrong category could make the model look complete while remaining electrically empty | V5 uses six proposed light type labels but **Detail Lines** for its 947 review positions. A live read-only query found 53 existing physical lighting fixtures from earlier samples; these must be reconciled with the marks before more placement. The 170 orange points only lack a same-level ceiling bounding-box candidate; this does not prove no viable host. | Audit each family category, actual host, connector, voltage, active/apparent load, photometry and maintenance data in a scratch RVT. Match existing physical fixtures to review marks to prevent double placement, then verify new element IDs/counts and unassigned connectors. |
| Power dots and panel-zone labels could be mistaken for actual circuits | V5 has 426 duplex, 21 GFI, 78 dedicated and two panel-zone review marks. A live query found 16 existing Electrical Fixtures and zero Electrical Equipment. The proposed points have no actual outlet/panel families or ElectricalSystem membership. Dedicated appliance loads are unknown. | Match existing outlets to review points; place verified families/equipment; bind each physical device to a reviewed circuit and compatible panel. Keep room/zone planning labels separate from real element and circuit IDs. |
| Cable length, voltage drop and panel schedules could falsely look precise | Review line geometry does not establish installed cable routes, vertical drops, conductor specification, PF, demand, fault duty or protective devices. | Require reviewed route/length and source inputs, calculate deterministically, show unresolved fields as missing, and block final ratings/quantities/compliance claims until evidence is present. |

## Release gate for every future room or project

1. **Source and assumptions:** pin source RVT hash, room/level inventory, actual areas,
   fixture data, design criteria and governing code edition. Do not silently reuse a
   previous project's luminaire or outlet assumptions.
2. **Geometry:** reconcile every planned symbol to exactly one room and level; check
   duplicates, boundary/door/stair/shaft clearance, irregular-room sampling and
   visual rendering. Record exceptions rather than hiding them.
3. **Mounting and photometry:** verify real host faces and actual family types;
   calculate point illuminance, uniformity and glare using reviewed photometric files
   and mounting heights. A lumen-method mean is only a preliminary estimate.
4. **Model elements:** after an approved preview, create real Revit families in a new
   RVT, then read back category, connector, electrical parameters, element IDs and
   placement counts. Preserve the source and provide recovery evidence.
5. **Circuits and routes:** create circuits from compatible placed components, assign
   actual panels, inspect circuit paths, then separately model or verify wiring and
   conduit routes. Check every member and panel relationship via Revit API.
6. **Engineering:** calculate W, VA, current, demand, protection, derating, voltage
   drop, fault duty, coordination and quantities from source-bound inputs. Leave
   unknowns blank and obtain responsible-engineer review before issue.

The V5 read-only load ledger is
`workspace/revit-school/outputs/School-V5-Load-Readiness-V3-20261031.md` (machine-readable
room ledger in the adjacent JSON). It reconciles 108 rooms, 947 lighting review symbols
and preliminary 25,730 W, records 53/16/0 existing lighting/outlet/equipment category
counts, but deliberately leaves VA, circuit IDs, breakers, cable sizes and installed
lengths empty. Earlier `School-V5-Load-Readiness-20261031.*` and `...V2-20261031.*`
snapshots are superseded: the first omitted existing physical-family counts, and V2
grouped power points under lighting-zone labels rather than separate power zones.
The workbook `workspace/revit-school/outputs/school-v5-20261031/School-V5-Electrical-Calculation-Inputs.xlsx`
has green PF, nominal-voltage, outlet-unit-VA and dedicated-VA inputs. Its formulas
leave dependent VA/current outputs blank until the relevant inputs are entered.

The priority family read-only audit is
`workspace/revit-school/previews/electrical-distribution/School-V5-Priority-Family-Audit-20261031.json`.
Of eight old donor families, four electrical-fixture candidates and one panel candidate
returned no connector elements. The troffer returned a connector but its inspected
types include 277 V. The Philips CoreLine downlight returned a connector and a 230 V
type; a manufacturer operating range and photometry review is needed before any 220 V
school use. The industrial cage light returned no connector and incomplete apparent
load. These are intake observations, not final acceptance or proof that a different
family or nested connector cannot serve the project.

The subsequent exact V5 physical match showed why point-count subtraction alone is
unsafe: 45 existing gym instances align with 45 proposed symbols to within 198.307 mm,
but all eight existing CLASSROOM 1500 instances exceed 50 mm from their one-to-one
candidate V5 symbol, with one offset of 2401.175 mm. Treat that as a relocation review
and preserve the physical IDs. Only CLASSROOM 1501, which has no existing physical
fixture, is prepared for the next eight-instance placement sample. Power-candidate
coordinates were not captured in the V5 preview, so no outlet subtraction is allowed.

The approved eight-fixture CLASSROOM 1501 Save-As sample exposed a separate identity
failure: Revit generated Marks `AIENG-L1-CLASSROOM-1500-09` through `-16` from the
existing CLASSROOM 1500 sequence. Exact coordinates, family/type and element counts
passed reopened verification, but those checks did not cover the room label. Future
placement previews must bind explicit, unique room-specific Marks to the coordinates;
read-back must verify both the Mark and actual room containment before the instance
is eligible for circuit grouping. Reject a cross-room Mark even when the geometry
is correct. For the current sample, use a new reviewed correction preview and Save As
before building circuits. The first attempt also showed that a Revit model opened
through a subst drive needs an output alias on the same drive; validate that alias
resolves to the approved physical workspace path before submission.
After the exact Mark correction was saved, an independent `school_spot_query`
could not run: its implementation is bound to a V4 lighting review view name that
the V5 sample lacks. A future read-only room check should use the active V5 view
or perform room containment without relying on detail-curve review views. Do not
report a failed optional query as a successful room check.

Autodesk's Revit 2024 documentation describes creating electrical systems from
[compatible placed devices and fixtures](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-GetStarted/files/GUID-C1F0D828-6C41-45DF-A03E-C5B6DC64FA40.htm), and
[circuits, panel connection and optional physical wiring](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-MEPEng/files/GUID-267A4006-80D3-4306-A404-0AF77F3D49A8.htm).
Autodesk also explains that [circuit path affects length and voltage-drop calculations](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-GetStarted/files/GUID-BB28138C-4679-4FED-AFD1-D74C5C8328CF.htm).
These describe Revit behavior; none supplies project-specific Egyptian code approval.
