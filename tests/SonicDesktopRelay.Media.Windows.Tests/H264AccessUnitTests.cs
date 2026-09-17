using SonicDesktopRelay.Media.Windows;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class H264AccessUnitTests
{
    [Fact]
    public void Length_prefixed_nals_are_normalized_to_annex_b()
    {
        byte[] avcc = [0,0,0,2,0x67,0x01, 0,0,0,2,0x65,0x02];

        var annexB = H264AccessUnit.ToAnnexB(avcc, 4);

        Assert.Equal(new byte[] {0,0,0,1,0x67,0x01, 0,0,0,1,0x65,0x02}, annexB);
        Assert.True(H264AccessUnit.ContainsKeyFrame(annexB));
        Assert.True(H264AccessUnit.ContainsSps(annexB));
        Assert.False(H264AccessUnit.ContainsPps(annexB));
    }

    [Fact]
    public void Annex_b_input_is_preserved()
    {
        byte[] source = [0,0,0,1,0x68,0x01,0,0,1,0x65,0x02];

        var result = H264AccessUnit.ToAnnexB(source, 4);

        Assert.Equal(source, result);
        Assert.True(H264AccessUnit.ContainsPps(result));
        Assert.True(H264AccessUnit.ContainsKeyFrame(result));
    }

    [Fact]
    public void Malformed_length_prefix_is_rejected_cleanly()
    {
        byte[] malformed = [0,0,0,20,0x65,0x01];

        Assert.Throws<InvalidDataException>(() => H264AccessUnit.ToAnnexB(malformed, 4));
    }
}
