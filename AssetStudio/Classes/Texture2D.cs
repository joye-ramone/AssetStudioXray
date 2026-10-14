using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetStudio
{
    public sealed class Texture2D : Texture
    {
        public int m_Width;
        public int m_Height;
        public uint m_CompleteImageSize;
        public TextureFormat m_TextureFormat;
        public bool m_MipMap;
        public int m_MipCount;
        public GLTextureSettings m_TextureSettings;
        public int m_ImageCount;
        public byte[] m_PlatformBlob;
        [JsonPropertyName("image data")]
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;
        public StreamingInfo m_DataStreamData; //Tuanjie

        public Texture2D() { }

        public Texture2D(ObjectReader reader, byte[] type, JsonSerializerOptions jsonOptions) : base(reader)
        {
            var parsedTex2d = JsonSerializer.Deserialize<Texture2D>(type, jsonOptions);
            m_Width = parsedTex2d.m_Width;
            m_Height = parsedTex2d.m_Height;
            m_CompleteImageSize = parsedTex2d.m_CompleteImageSize;
            m_TextureFormat = parsedTex2d.m_TextureFormat;
            m_MipMap = parsedTex2d.m_MipMap;
            m_MipCount = parsedTex2d.m_MipCount;
            m_ImageCount = parsedTex2d.m_ImageCount;
            m_TextureSettings = parsedTex2d.m_TextureSettings;
            m_StreamData = parsedTex2d.m_StreamData;
            m_PlatformBlob = parsedTex2d.m_PlatformBlob ?? Array.Empty<byte>();

            image_data = !string.IsNullOrEmpty(m_StreamData?.path)
                ? new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size)
                : new ResourceReader(reader, parsedTex2d.image_data.Offset, parsedTex2d.image_data.Size);
        }

        public Texture2D(ObjectReader reader) : base(reader)
        {
            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            m_CompleteImageSize = reader.ReadUInt32();
            if (version >= 2020) //2020.1 and up
            {
                var m_MipsStripped = reader.ReadInt32();
            }
            if (version.IsTuanjie && (version > (2022, 3, 2) || version.Build >= 8)) //2022.3.2t8(1.1.0) and up
            {
                var m_WebStreaming = reader.ReadBoolean();
                reader.AlignStream();
                var m_PriorityLevel = reader.ReadInt32();
                var m_UploadedMode = reader.ReadInt32();
                m_DataStreamData = new StreamingInfo //sample is needed
                {
                    offset = 0,
                    size = reader.ReadUInt32(),
                    path = reader.ReadAlignedString()
                };
            }
            m_TextureFormat = (TextureFormat)reader.ReadInt32();
            if (version < (5, 2)) //5.2 down
            {
                m_MipMap = reader.ReadBoolean();
            }
            else
            {
                m_MipCount = reader.ReadInt32();
            }
            if (version >= (2, 6)) //2.6.0 and up
            {
                var m_IsReadable = reader.ReadBoolean();
            }
            if (version >= 2020) //2020.1 and up
            {
                var m_IsPreProcessed = reader.ReadBoolean();
            }
            if (version >= (2019, 3)) //2019.3 and up
            {
                if (version >= (2022, 2)) //2022.2 and up
                {
                    var m_IgnoreMipmapLimit = reader.ReadBoolean();
                    reader.AlignStream();
                }
                else
                {
                    var m_IgnoreMasterTextureLimit = reader.ReadBoolean();
                }
            }
            if (version.IsInRange(3, (5, 5))) //3.0.0 - 5.4
            {
                var m_ReadAllowed = reader.ReadBoolean();
            }
            if (version >= (2022, 2)) //2022.2 and up
            {
                var m_MipmapLimitGroupName = reader.ReadAlignedString();
            }
            if (version >= (2018, 2)) //2018.2 and up
            {
                var m_StreamingMipmaps = reader.ReadBoolean();
            }
            reader.AlignStream();
            if (version >= (2018, 2)) //2018.2 and up
            {
                var m_StreamingMipmapsPriority = reader.ReadInt32();
            }
            m_ImageCount = reader.ReadInt32();
            var m_TextureDimension = reader.ReadInt32();
            m_TextureSettings = new GLTextureSettings(reader);
            if (version >= 3) //3.0 and up
            {
                var m_LightmapFormat = reader.ReadInt32();
            }
            if (version >= (3, 5)) //3.5.0 and up
            {
                var m_ColorSpace = reader.ReadInt32();
            }
            if (version >= (2020, 2)) //2020.2 and up
            {
                m_PlatformBlob = reader.ReadUInt8Array();
                reader.AlignStream();
            }
            else
            {
                m_PlatformBlob = Array.Empty<byte>();
            }
            var image_data_size = reader.ReadInt32();
            if (image_data_size == 0 && version >= (5, 3))//5.3.0 and up
            {
                m_StreamData = new StreamingInfo(reader);
            }

            image_data = !string.IsNullOrEmpty(m_StreamData?.path)
                ? new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size)
                : new ResourceReader(reader, reader.BaseStream.Position, image_data_size);
        }

    }
}
