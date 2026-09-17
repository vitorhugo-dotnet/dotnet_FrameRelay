using System.Reflection;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class VorticeMediaFoundationShapeProbeTests
{
    [Fact]
    public void Probe_exact_generated_shapes_needed_by_native_codec()
    {
        var assembly = typeof(MediaFactory).Assembly;
        var lines = new List<string>();

        foreach (var name in new[] { "RegisterTypeInfo", "OutputDataBuffer", "OutputStreamInfo", "InputStreamInfo" })
        {
            var type = assembly.GetTypes().Single(x => x.Name == name);
            lines.Add($"TYPE {type.FullName}");
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                lines.Add("  CTOR " + ctor);
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                lines.Add($"  FIELD {field.FieldType.FullName} {field.Name} = {(field.IsStatic ? field.GetValue(null) : "-")}");
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                lines.Add($"  PROP {prop.PropertyType.FullName} {prop.Name}");
        }

        foreach (var type in assembly.GetTypes()
                     .Where(x => x.IsEnum &&
                                 (x.Name.Contains("Process", StringComparison.OrdinalIgnoreCase)
                                  || x.Name.Contains("Message", StringComparison.OrdinalIgnoreCase)
                                  || x.Name.Contains("Stream", StringComparison.OrdinalIgnoreCase)
                                  || x.Name.Contains("Transform", StringComparison.OrdinalIgnoreCase)
                                  || x.Name.Contains("MFT", StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(x => x.Name))
        {
            lines.Add($"ENUM {type.FullName}");
            foreach (var name in Enum.GetNames(type))
                lines.Add($"  {name}={(Convert.ToUInt64(Enum.Parse(type, name)))}");
        }

        var factory = typeof(MediaFactory);
        foreach (var method in factory.GetMethods(BindingFlags.Public | BindingFlags.Static)
                     .Where(x => x.Name is "MFCreateSample" or "MFCreateMemoryBuffer" or "MFCreateMediaType" or "MFTEnumEx"))
            lines.Add("FACTORY " + method);

        Assert.Fail(string.Join(Environment.NewLine, lines));
    }
}
