using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#region 1.Network Data Structure
[System.Serializable]
public class Network
{
    public int SchemaVersion = 2;
    public string NetworkId;
    public List<Node> Nodes = new List<Node>();
    public List<Edge> Edges = new List<Edge>();
    public List<AltitudeLayer> AltitudeLayers = new List<AltitudeLayer>
    {
        new AltitudeLayer()
    };
    public List<LaneConnectionCorridor> LaneConnectionCorridors = new List<LaneConnectionCorridor>();
}
[System.Serializable]
public class AltitudeLayer
{
    public string LayerId = "L0";
    public string DisplayName = "Default Layer";
    public float NominalAltitude = 75f;
    public float VerticalTolerance = 5f;
    // Layer visibility is an authoring/view concern only. It must not remove the
    // layer from routing or simulation topology.
    public bool Enabled = true;
    // Frozen layers remain selectable and visible, but their geometry and stack
    // position cannot be edited until explicitly unlocked.
    public bool Frozen = false;
}
[System.Serializable]
public class Node
{
    public string NodeId;
    public string LayerId = "L0";
    public Vector3 Position;
    public List<string> ConnectedEdges = new List<string>();
    public List<string> NeighborNodes = new List<string>();
}
[System.Serializable]
public class Edge
{
    public string EdgeId;
    public string LayerId = "L0";
    public List<string> LinkNodesId = new List<string>();
    public List<Vector3> Centerline = new List<Vector3>();
    public float Length;
    public List<Lane> Lanes = new List<Lane>();
}
[System.Serializable]
public class Lane
{
    public string LaneId;
    public string LayerId = "L0";
    public string LaneType;  // "Straight", "Left", "Right", "StraightLeft", "StraightRight", "StraightLeftRight"
    public List<string> FromLanesId = new List<string>();
    public List<string> ToLanesId = new List<string>();
    public List<Vector3> Centerline = new List<Vector3>();
    public float Length;
    public List<(string, Vector3)> LinkCorridors = new List<(string, Vector3)>();
}
[System.Serializable]
public class LaneConnectionCorridor
{
    public string CorridorId;
    public string FromLaneId;
    public string ToLaneId;
    public string FromLayerId;
    public string ToLayerId;
    public Vector3 FromPoint;
    public Vector3 ToPoint;
    public List<Vector3> Centerline = new List<Vector3>();
    public float Length;
    public float MaxSpeed = 20f;
    public int Priority = 1;
}
#endregion

#region 2.Vertiport Data Structrue
[System.Serializable]
public class Vertiport
{
    public string VertiportId;
    public List<Vector3> Boundary = new List<Vector3>();
    public List<Helipad> Helipads = new List<Helipad>();
    public List<HoldingStructure> HoldingStructures = new List<HoldingStructure>();
    public ApproachNetwork ApproachNet = new ApproachNetwork();
    public List<NetConnectedCorridor>  Corridors = new List<NetConnectedCorridor>();
}
[System.Serializable]
public class Helipad
{
    public string HelipadId;
    public List<Vector3> Boundary = new List<Vector3>();
    public List<string> ReachableCorridorIds = new List<string>();
}
[System.Serializable]
public class HoldingStructure
{
    public string HoldingStructureId;
    public Vector3 Position;
    public List<Circle> Circles = new List<Circle>();
    public List<Vertex> Vertices = new List<Vertex>();
    public List<Ramp> Ramps = new List<Ramp>();
}
[System.Serializable]
public class Circle
{
    public string CircleId;
    public Vector3 Center;
    public float Radius;
    public float InitialAngle;  // Degrees, offset for first vertex
    public List<string> CoveredVertices = new List<string>();
}
[System.Serializable]
public class Vertex
{
    public string VertexId;
    public Vector3 Position;
    public List<string> ConnectedRamps = new List<string>();
    public List<string> AccessibleVertices = new List<string>();
    public string LinkCircleId;
}
public enum ArcDirection { Shortest, Clockwise, CounterClockwise }

[System.Serializable]
public class Ramp
{
    public string RampId;
    public string FromVertexId;
    public string ToVertexId;
    public string LinkCircleId = null;
    public ArcDirection Direction = ArcDirection.Shortest;

    public List<(string, Vector3)> LinkCorridors = new List<(string, Vector3)>();
}
[System.Serializable]
public class ApproachNetwork
{
    public List<ApproachNode> Nodes = new List<ApproachNode>();
    public List<ApproachEdge> Edges = new List<ApproachEdge>();
}
[System.Serializable]
public class ApproachNode
{
    public string NodeId;
    public Vector3 Position;
    public List<string> ConnectedEdges = new List<string>();
    public bool IsAnchor = false;
    public string LinkHSId = null;
    public string LinkVertexId = null;  // Which HS vertex this anchor node connects to (null = HS center / legacy)
}
[System.Serializable]
public class ApproachEdge
{
    public string EdgeId;
    public string FromNodeId;
    public string ToNodeId;
}
[System.Serializable]
public class NetConnectedCorridor
{
    public string CorridorId;
    public string CorridorType;
    public string LinkLaneId;
    public string LinkRampId;
    public Vector3 LaneLinkPoint;
    public Vector3 RampLinkPoint;
}
#endregion

#region 3.Flight Data Structure
public class Flight
{
    public string ExecutingAircraftId;
    public string OriginVertiport;
    public string DestinationVertiport;
    public float DepartureTime;
    public int Priority;
}
#endregion
