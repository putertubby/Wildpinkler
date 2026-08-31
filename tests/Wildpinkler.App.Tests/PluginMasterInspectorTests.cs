using System.IO;
using System.Text;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public class PluginMasterInspectorTests
{
    [Fact]
    public void ReadMasters_WellFormedHeader_ReturnsMastersInOrder()
    {
        using var stream = BuildPlugin("Skyrim.esm", "Update.esm");

        var masters = PluginMasterInspector.ReadMasters(stream);

        Assert.Equal(new[] { "Skyrim.esm", "Update.esm" }, masters);
    }

    [Fact]
    public void ReadMasters_NoMastersSubrecords_ReturnsEmpty()
    {
        using var stream = BuildPlugin();

        Assert.Empty(PluginMasterInspector.ReadMasters(stream));
    }

    [Fact]
    public void ReadMasters_NotATes4Header_ReturnsEmpty()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("NOPE" + new string('\0', 40)));

        Assert.Empty(PluginMasterInspector.ReadMasters(stream));
    }

    [Fact]
    public void ReadMasters_TruncatedStream_ReturnsEmpty()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("TES4"));

        Assert.Empty(PluginMasterInspector.ReadMasters(stream));
    }

    /// <summary>Builds a minimal, well-formed TES4 header record with one MAST subrecord per given master name.</summary>
    private static MemoryStream BuildPlugin(params string[] masters)
    {
        using var data = new MemoryStream();
        using (var writer = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var master in masters)
            {
                var bytes = Encoding.ASCII.GetBytes(master + "\0");
                writer.Write(Encoding.ASCII.GetBytes("MAST"));
                writer.Write((ushort)bytes.Length);
                writer.Write(bytes);
            }
        }

        var body = data.ToArray();
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("TES4"));
            writer.Write((uint)body.Length);
            writer.Write(new byte[16]); // flags, form id, revision, version, unknown
            writer.Write(body);
        }

        stream.Position = 0;
        return stream;
    }
}
