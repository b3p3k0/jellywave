using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellywave.Tests;

/// <summary>
/// Jellyfin's <c>MyXmlSerializer</c> in small: <c>new XmlSerializer(type)</c> over files and streams, with failure switches.
/// Card R1: the plugin's writer serializes to a stream, so <see cref="FailSave"/> and <see cref="Saves"/> are about
/// <see cref="SerializeToStream"/> (which <see cref="SerializeToFile"/> also goes through).
/// </summary>
internal sealed class FileXmlSerializer : IXmlSerializer
{
    public bool FailLoad { get; set; }

    public bool FailSave { get; set; }

    public int Saves { get; private set; }

    public object DeserializeFromFile(Type type, string file)
    {
        if (FailLoad)
        {
            throw new IOException("unreadable");
        }

        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object DeserializeFromStream(Type type, Stream stream)
    {
        using var reader = XmlReader.Create(stream);
        return new XmlSerializer(type).Deserialize(reader)!;
    }

    public object DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer);
        return DeserializeFromStream(type, stream);
    }

    public void SerializeToFile(object obj, string file)
    {
        using var stream = File.Create(file);
        SerializeToStream(obj, stream);
    }

    public void SerializeToStream(object obj, Stream stream)
    {
        if (FailSave)
        {
            throw new IOException("read-only disk");
        }

        Saves++;
        new XmlSerializer(obj.GetType()).Serialize(stream, obj);
    }
}
