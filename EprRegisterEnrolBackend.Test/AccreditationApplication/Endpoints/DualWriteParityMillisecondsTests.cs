using System.Text.Json;
using System.Text.Json.Nodes;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// The parity tests compare a write's response, which carries the time it set in memory (to the tick), with a read
// afterwards, which carries what Mongo stored (to the millisecond). Both are written by System.Text.Json, which trims
// a fraction's trailing zeros and leaves the fraction out on a whole second. These pin the normaliser on fixed times
// so the 1-in-1000 case, an update landing in the first millisecond of a second, can't make the parity tests flaky.
public class DualWriteParityMillisecondsTests
{
    private static JsonNode Serialised(DateTime updatedAt) =>
        JsonSerializer.SerializeToNode(
            new { updatedAt },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

    private static DateTime Utc(int second, long ticks = 0) =>
        new DateTime(2026, 11, 19, 9, 29, second, DateTimeKind.Utc).AddTicks(ticks);

    [Fact]
    public void ToMilliseconds_InMemoryTimeInTheFirstMillisecondOfAWholeSecond_EqualsTheStoredWholeSecond()
    {
        // 09:29:21.0004567 in memory; Mongo keeps 09:29:21.000, which is served as "...:21Z".
        var response = Serialised(Utc(21, 4567));
        var legacy = Serialised(Utc(21));
        Assert.Equal("2026-11-19T09:29:21Z", legacy["updatedAt"]!.GetValue<string>());

        var normalisedResponse = DualWriteEndpointTests.ToMilliseconds(response);
        var normalisedLegacy = DualWriteEndpointTests.ToMilliseconds(legacy);

        Assert.True(
            JsonNode.DeepEquals(normalisedLegacy, normalisedResponse),
            $"legacy {normalisedLegacy}\nresponse {normalisedResponse}"
        );
    }

    [Theory]
    [InlineData(8_702_473, 8_700_000)] // .8702473 in memory, .870 stored and served as ".87Z"
    [InlineData(690_000, 690_000)] // .069 both sides
    [InlineData(0, 0)] // a whole second both sides
    public void ToMilliseconds_InMemoryTimeAndItsStoredMilliseconds_Equal(
        long inMemoryTicks,
        long storedTicks
    )
    {
        var response = DualWriteEndpointTests.ToMilliseconds(Serialised(Utc(23, inMemoryTicks)));
        var legacy = DualWriteEndpointTests.ToMilliseconds(Serialised(Utc(23, storedTicks)));

        Assert.True(JsonNode.DeepEquals(legacy, response), $"legacy {legacy}\nresponse {response}");
    }

    [Fact]
    public void ToMilliseconds_DifferentMilliseconds_StillDiffer()
    {
        var response = DualWriteEndpointTests.ToMilliseconds(Serialised(Utc(21, 10_000)));
        var legacy = DualWriteEndpointTests.ToMilliseconds(Serialised(Utc(21)));

        Assert.False(JsonNode.DeepEquals(legacy, response), $"legacy {legacy}\nresponse {response}");
    }
}
