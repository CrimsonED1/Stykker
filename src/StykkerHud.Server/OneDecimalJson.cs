using System.Text.Json;
using System.Text.Json.Serialization;

namespace StykkerHud.Server;

// Zahlen in den Antworten auf eine Nachkommastelle. Die Oberfläche zeigt ohnehin nicht mehr, und die langen
// Binärbrüche (3.5433070866141732) machten jeden Abruf unnötig groß. Die Zustände rechnet der Dienst vorher mit
// den rohen Werten aus.
internal sealed class OneDecimalJson : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Math.Round(value, 1));
}
