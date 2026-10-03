using app.Services;
using Xunit;

namespace app.Tests;

/// <summary>
/// Unit tests for Sound.IsSuccess — pure mapping, no audio hardware required.
/// </summary>
public class SoundTests
{
    [Fact]
    public void Ship_is_success()
        => Assert.True(Sound.IsSuccess(PackOutcome.Ship));

    [Theory]
    [InlineData(PackOutcome.NotFound)]
    [InlineData(PackOutcome.Cancelled)]
    [InlineData(PackOutcome.AlreadyShipped)]
    [InlineData(PackOutcome.Blocked)]
    [InlineData(PackOutcome.SaveFailed)]
    public void Non_ship_outcomes_are_error(PackOutcome outcome)
        => Assert.False(Sound.IsSuccess(outcome));
}
