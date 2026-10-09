using System.Text.Json;
using Stykker.Shared.Web;

namespace Stykker.Shared.Tests;

public class OneDecimalJsonTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new OneDecimalJson());
        return options;
    }

    [Theory]
    [InlineData(3.5433070866141732, "3.5")]
    [InlineData(2.26, "2.3")]
    [InlineData(0, "0")]
    [InlineData(99.96, "100")]
    public void NumbersAreWrittenWithOneDecimal(double value, string expected) =>
        Assert.Equal(expected, JsonSerializer.Serialize(value, Options()));

    [Fact]
    public void NumbersAreReadAsTheyCame() =>
        Assert.Equal(3.5433070866141732, JsonSerializer.Deserialize<double>("3.5433070866141732", Options()));
}
