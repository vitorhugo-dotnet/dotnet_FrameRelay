using System.Reflection;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class VorticeMftEventProbeTests
{
    [Fact]
    public void Probe_async_event_and_codec_api_shapes()
    {
        var assembly = typeof(MediaFactory).Assembly;
        var lines = new List<string>();

        foreach (var name in new[] { "IMFMediaEventGenerator", "IMFMediaEvent", "MediaEventType", "ICodecAPI" })
        {
            var type = assembly.GetTypes().FirstOrDefault(x => x.Name == name);
            lines.Add($"TYPE {name}: {type?.FullName ?? "MISSING"}");
            if (type is null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                lines.Add("  METHOD " + method);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                lines.Add($"  PROP {prop.PropertyType.FullName} {prop.Name}");
            if (type.IsEnum)
                foreach (var enumName in Enum.GetNames(type))
                    lines.Add($"  ENUM {enumName}={Convert.ToUInt64(Enum.Parse(type, enumName))}");
        }

        Assert.Fail(string.Join(Environment.NewLine, lines));
    }
}
