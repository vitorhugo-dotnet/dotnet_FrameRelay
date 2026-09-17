using System.Reflection;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class VorticeAsyncMftApiProbeTests
{
    [Fact]
    public void Probe_async_mft_api()
    {
        var assembly = typeof(MediaFactory).Assembly;
        var lines = new List<string>();

        foreach (var name in new[] { "IMFMediaEventGenerator", "IMFMediaEvent", "MediaEventType", "ICodecAPI" })
        {
            var type = assembly.GetTypes().FirstOrDefault(x => x.Name == name);
            lines.Add($"TYPE {name}: {type?.FullName ?? "MISSING"}");
            if (type is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                lines.Add("  METHOD " + method);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                lines.Add($"  PROP {property.PropertyType.FullName} {property.Name}");
            if (type.IsEnum)
                foreach (var enumName in Enum.GetNames(type))
                    lines.Add($"  ENUM {enumName}={Convert.ToUInt64(Enum.Parse(type, enumName))}");
        }

        Assert.Fail(string.Join(Environment.NewLine, lines));
    }
}
