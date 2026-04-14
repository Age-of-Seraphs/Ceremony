using ProtoBuf;
#nullable disable


namespace circuits
{
    [ProtoContract]
    public class EditLocatorPacket
    {
        [ProtoMember(1)] public string WaypointText { get; set; }
        [ProtoMember(2)] public string WaypointIcon { get; set; }
        [ProtoMember(3)] public string VariantType { get; set; }
        [ProtoMember(4)] public int? WaypointColorSwatch { get; set; }
    }
}
