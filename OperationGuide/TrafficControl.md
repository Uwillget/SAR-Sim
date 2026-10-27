# Air-Route Network Operating Rules

## Lane Position and Following

- Aircraft progress is calculated from the nearest projection onto the centerline polyline and its accumulated arc length, rather than from the straight-line distance between the current position and the lane's start. This preserves consistent longitudinal ordering on curves, polylines, and interlayer connectors.
- Following control always accounts for physical aircraft at lane ends, red-signal stop lines, and lane-change occupancy. Objects whose progress exceeds 99% are no longer ignored.
- When an aircraft is within the safety interval of a downstream lane's entrance, `CapabilityCheck` is set on upstream lanes that feed it, preventing aircraft from entering a segment that is already queueing back.

## Lane Changes

- Routing permits only stepwise changes between adjacent, same-direction lanes on the same segment.
- A lane change is approved only when both its forward and rear gaps are safe. The default forward gap is `safeInterval × 1.0`, and the default rear gap is `safeInterval × 1.5`.
- When several requests compete for the same gap, they are arbitrated in order of mission priority, waiting time, and stable aircraft ID.
- While a lane change is in progress, the aircraft is registered in both its source and target lanes, preventing another aircraft from entering the lateral-movement area.
- After waiting for 15 seconds, an aircraft attempts to replan the remainder of its route while avoiding the blocked lane. If no safe route is available after 120 seconds, the mission is marked as failed and removed from traffic. In continuous-random mode, a replacement mission is added to maintain the fleet size.

Related parameters: `laneChangeFrontGapFactor`, `laneChangeRearGapFactor`, `laneChangeRerouteTimeout`, and `laneChangeRemovalTimeout`.

## Signals and Yielding

- At intersections with three or more branches, phases are generated from the incoming direction and each lane's turning attribute.
- An incoming lane not covered by a recognized turning type receives an exclusive fallback phase, so it is never left without signal control.
- By default, a two-second all-red clearance interval follows every green phase, reducing overlap in the conflict area during phase transitions.
- Green-wave offsets are calculated from a deterministic reference node using shortest network distances and `greenWaveSpeed`; the result therefore does not depend on dictionary iteration order.
- Dedicated right-turn lanes are not permanently green by default (`permissiveRightTurn = false`). Until an explicit conflict-area model has been configured, this is the conservative, safety-oriented default.
- Two-branch merges and entrances released by a traffic signal remain subject to downstream-capacity checks. In effect, an aircraft yields until space is available downstream.

Primary parameters: `greenSignalTime`, `signalClearanceTime`, `signalStopLineDistance`, and `greenWaveSpeed`.

## Deadlock-Resolution Hierarchy

```text
Normal gap acceptance → priority arbitration → timeout rerouting → final removal and failure recording
```

Every rerouting event and final removal is written to `SimulationRecorder`, supporting experiment reproduction and analysis of exceptional scenarios.
