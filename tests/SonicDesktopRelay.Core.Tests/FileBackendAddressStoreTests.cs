using System.Reflection;
using SonicDesktopRelay.Core;
using Xunit;

namespace SonicDesktopRelay.Core.Tests;

public sealed class FileBackendAddressStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"sonicdesktoprelay-backend-{Guid.NewGuid():N}");

    private string PathUnderTest => Path.Combine(_directory, "backend-address.txt");

    [Fact]
    public void Missing_preference_uses_the_public_backend()
    {
        var store = CreateStore(PathUnderTest);

        var address = Read(store);

        Assert.Equal("https://sonicrelay-api.hugodotnet.dev", address);
    }

    [Fact]
    public void A_custom_backend_survives_a_new_store_instance()
    {
        var first = CreateStore(PathUnderTest);
        Write(first, "https://relay.example.com");

        var second = CreateStore(PathUnderTest);

        Assert.Equal("https://relay.example.com", Read(second));
    }

    private static object CreateStore(string path)
    {
        var type = typeof(BackendSettings).Assembly.GetType(
            "SonicDesktopRelay.Core.FileBackendAddressStore");
        Assert.NotNull(type);

        var instance = Activator.CreateInstance(type!, path);
        Assert.NotNull(instance);
        return instance!;
    }

    private static string Read(object store)
    {
        var method = store.GetType().GetMethod(
            "Read",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(store, null));
    }

    private static void Write(object store, string value)
    {
        var method = store.GetType().GetMethod(
            "Write",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(store, [value]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
