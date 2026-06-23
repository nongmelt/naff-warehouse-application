using app.Services;
using Xunit;

namespace app.Tests;

public class OrphanCaptureTests
{
    private static readonly DateTime FixedNow = new(2026, 6, 13, 9, 30, 0, DateTimeKind.Local);

    [Fact]
    public void BuildRawObjectKey_UsesGrandparentDateDir_AndFileName()
    {
        var path = Path.Combine("C:", "Videos", "Warehouse", "2026-04-21", "Station-1",
            "20260421_101500_PC01_Station-1_KEXLM1000234185.mp4");
        var key = OrphanCapture.BuildRawObjectKey(path, FixedNow);
        Assert.Equal("2026-04-21/20260421_101500_PC01_Station-1_KEXLM1000234185.mp4", key);
    }

    [Fact]
    public void BuildRawObjectKey_FallsBackToTodayWhenNoGrandparent()
    {
        var key = OrphanCapture.BuildRawObjectKey("video.mp4", FixedNow);
        Assert.Equal("2026-06-13/video.mp4", key);
    }

    [Fact]
    public void ClassifyCreateVideoStatus_404_IsNoPackingList()
        => Assert.Equal(CreateVideoResultKind.NoPackingList, OrphanCapture.ClassifyCreateVideoStatus(404));

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(204)]
    public void ClassifyCreateVideoStatus_2xx_IsCreated(int code)
        => Assert.Equal(CreateVideoResultKind.Created, OrphanCapture.ClassifyCreateVideoStatus(code));

    [Theory]
    [InlineData(422)] // sqlx 23xxx FK/constraint — must NOT orphan
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void ClassifyCreateVideoStatus_OtherCodes_AreFailed(int code)
        => Assert.Equal(CreateVideoResultKind.Failed, OrphanCapture.ClassifyCreateVideoStatus(code));
}
