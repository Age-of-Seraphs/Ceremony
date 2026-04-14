using System.Collections.Generic;
using ProtoBuf;
#nullable disable

namespace circuits
{
    [ProtoContract]
    public class EditNodeSettingsPacket
    {
        [ProtoMember(1)] public Dictionary<string, string> Values = new();
    }
}
