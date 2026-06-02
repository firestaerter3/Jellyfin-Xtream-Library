// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.IO;
using System.Xml.Serialization;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Xtream.Library.Tests.Helpers;

// Thin IXmlSerializer wrapper for tests that need realistic round-trip behaviour
// (e.g. BUG-009 orphan-import tests that read on-disk XML fixtures).
public class RealXmlSerializer : IXmlSerializer
{
    public object DeserializeFromStream(Type type, Stream stream)
    {
        var ser = new XmlSerializer(type);
        return ser.Deserialize(stream)!;
    }

    public object DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer);
        return DeserializeFromStream(type, stream);
    }

    public void SerializeToStream(object obj, Stream stream)
    {
        var ser = new XmlSerializer(obj.GetType());
        ser.Serialize(stream, obj);
    }

    public void SerializeToFile(object obj, string file)
    {
        using var stream = File.Create(file);
        SerializeToStream(obj, stream);
    }
}
