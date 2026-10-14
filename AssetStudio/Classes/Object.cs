using System.Collections.Specialized;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public class Object
    {
        [JsonIgnore]
        public SerializedFile assetsFile;
        [JsonIgnore]
        public ObjectReader reader;
        public long m_PathID;
        [JsonIgnore]
        public UnityVersion version;
        [JsonIgnore]
        public BuildTarget platform;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ClassIDType type;
        [JsonIgnore]
        public SerializedType serializedType;
        public int classID;
        public uint byteSize;
        [JsonIgnore]
        public string Name;

        public Object() { }

        public Object(ObjectReader reader)
        {
            this.reader = reader;
            reader.Reset();
            assetsFile = reader.assetsFile;
            type = reader.type;
            m_PathID = reader.m_PathID;
            version = reader.version;
            platform = reader.platform;
            serializedType = reader.serializedType;
            classID = reader.classID;
            byteSize = reader.byteSize;

            if (platform == BuildTarget.NoTarget)
            {
                var m_ObjectHideFlags = reader.ReadUInt32();
            }
        }

        public OrderedDictionary ToType(TypeTree m_Type = null)
        {
            m_Type = m_Type ?? serializedType?.m_Type;
            if (m_Type == null)
                return null;

            return TypeTreeHelper.ReadType(m_Type, reader);
        }

    }
}
