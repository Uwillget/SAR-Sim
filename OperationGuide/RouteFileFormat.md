# Route File Format

The simulator accepts traffic-demand files in either JSON or XML format. A mission may specify only its origin and destination vertiports and let the system plan the shortest route automatically, or it may reference a fixed route defined as an ordered sequence of directed lane IDs.

## Rules

- `id`: Mission ID. It must be unique within the file.
- `from` / `to`: IDs of the origin and destination vertiports. They must be different.
- `depart`: A non-negative time in seconds from the start of the simulation. Missions are sorted by this field.
- `route`: Optional named-route ID. When omitted, automatic shortest-path routing is used.
- `lanes` or `edges`: An ordered list of lane IDs. The `edges` field follows SUMO's naming convention, but SAR-Sim still interprets its values as lane IDs.
- The first lane of a fixed route must connect to the origin's Entry corridor, and the last lane must connect to the destination's Exit corridor.
- Every pair of consecutive lanes must have a directed connection. When exactly one `LaneConnectionCorridor` connects two physical lanes, its ID may be omitted from the route; the planner inserts it automatically.
- A route may change laterally between adjacent, same-direction lanes on the same segment. To cross two or more lanes, list every intermediate lane in sequence.
- If a mission record is invalid, that mission is rejected with a specific error message. Other valid missions in the same file can still be imported.

## JSON

```json
{
  "routes": [
    {
      "id": "R-VP1-VP2",
      "lanes": ["ENTRY_LANE_ID", "NEXT_LANE_ID", "EXIT_LANE_ID"]
    }
  ],
  "vehicles": [
    {
      "id": "flight-001",
      "route": "R-VP1-VP2",
      "from": "VP1",
      "to": "VP2",
      "depart": 10,
      "priority": 1
    }
  ]
}
```

The `lanes` field may also be specified directly in a `vehicle` or `trip` record. Legacy files in the `{ "Missions": [...] }` format remain supported.

## XML

```xml
<routes>
  <route id="R-VP1-VP2" edges="ENTRY_LANE_ID NEXT_LANE_ID EXIT_LANE_ID" />
  <vehicle id="flight-001" route="R-VP1-VP2"
           from="VP1" to="VP2" depart="10" priority="1" />
  <flow id="flow-001" route="R-VP1-VP2"
        from="VP1" to="VP2" begin="30" period="5" number="4" />
</routes>
```

A `flow` record expands into independent missions named `flow-001.0`, `flow-001.1`, and so on. Specify the mission count with `number`, or let the importer derive it from `begin`, `end`, and `period`.

## Importing in Unity

Select `SimulationManager` in the scene. In its Inspector, go to **Mission Mode** and click **Import JSON / XML...**. Before replacing the current mission queue, the importer validates IDs, times, vertiports, helipads, and lane references.

`Assets/DataBase/RouteFile.example.json` and `RouteFile.example.xml` are directly importable automatic-routing examples based on the current VP1-VP7 data. Before adding a fixed route to a mission, confirm lane directions and connectivity in the network editor.
