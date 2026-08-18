using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary><see cref="LocalizedText"/> を言語コードのオブジェクトとして読み書きする。</summary>
public sealed class LocalizedTextJsonConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var byLanguage = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options);
        return new LocalizedText(byLanguage ?? []);
    }

    public override void Write(
        Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var language in value.Languages)
        {
            writer.WriteString(language, value.Get(language));
        }

        writer.WriteEndObject();
    }
}

/// <summary>定義とマッピングを JSON にする。</summary>
/// <remarks>
/// <para>
/// **公開時のスナップショットは DB に JSON で入る**（<c>_documents/データモデル設計.md</c> 2.1）。
/// **読み書きの設定をここ 1 か所に置く。** 書いたときと読むときで設定が違うと、
/// 過去の版が読めなくなる。
/// </para>
/// <para>
/// **列挙は文字列で書く。** 数値だと、列挙に値を挿入したときに過去の版の意味が変わる。
/// </para>
/// </remarks>
public static class SurveyJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // 回答の文言に記号や絵文字が入る。読めない形にしない
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new LocalizedTextJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
