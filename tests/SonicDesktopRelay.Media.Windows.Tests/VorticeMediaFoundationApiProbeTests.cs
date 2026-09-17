using System.Reflection;
using Vortice.MediaFoundation;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class VorticeMediaFoundationApiProbeTests
{
    [Fact]
    public void Probe_media_foundation_api_shape()
    {
        var assembly = typeof(MediaFactory).Assembly;
        var names = new[]
        {
            "IMFTransform", "IMFSample", "IMFMediaBuffer", "IMFMediaType", "IMFAttributes",
            "MFTOutputDataBuffer", "OutputStreamInfo", "InputStreamInfo", "RegisterTypeInfo"
        };
        var lines = new List<string>();
        foreach (var name in names)
        {
            var type = assembly.GetTypes().FirstOrDefault(x => x.Name == name);
            lines.Add($"TYPE {name}: {type?.FullName ?? "MISSING"}");
            if (type is null) continue;
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                lines.Add("  CTOR " + ctor);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                lines.Add("  METHOD " + method);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                lines.Add("  PROP " + prop.PropertyType.Name + " " + prop.Name);
        }

        foreach (var type in assembly.GetTypes().Where(x =>
                     x.IsEnum && (x.Name.Contains("Transform", StringComparison.OrdinalIgnoreCase)
                                  || x.Name.Contains("MFT", StringComparison.OrdinalIgnoreCase))))
        {
            lines.Add($"ENUM {type.FullName}: {string.Join(",", Enum.GetNames(type))}");
        }

        foreach (var staticName in new[] { "TransformCategoryGuids", "VideoFormatGuids", "MediaTypeAttributeKeys", "TransformAttributeKeys" })
        {
            var type = assembly.GetTypes().FirstOrDefault(x => x.Name == staticName);
            lines.Add($"STATIC {staticName}: {type?.FullName ?? "MISSING"}");
            if (type is null) continue;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(x =>
                         x.Name.Contains("Video", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("H264", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("NV12", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Major", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Subtype", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Frame", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Bitrate", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Async", StringComparison.OrdinalIgnoreCase)))
                lines.Add("  FIELD " + field.FieldType.Name + " " + field.Name);
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(x =>
                         x.Name.Contains("Video", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("H264", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("NV12", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Major", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Subtype", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Frame", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Bitrate", StringComparison.OrdinalIgnoreCase)
                         || x.Name.Contains("Async", StringComparison.OrdinalIgnoreCase)))
                lines.Add("  PROP " + prop.PropertyType.Name + " " + prop.Name);
        }

        Assert.Fail(string.Join(Environment.NewLine, lines));
    }
}
