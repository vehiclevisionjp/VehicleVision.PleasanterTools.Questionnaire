using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.Questionnaire.Integration.Tests;

/// <summary>試験から proof-of-work を解く。</summary>
/// <remarks>
/// <para>
/// **画面と同じことをする**（`lib/altcha.ts`）。
/// 送信の口は解答を要求するので、**試験も解かないと通らない**。
/// </para>
/// <para>
/// **切ってから試さない。** 検証環境だけ無効にすると、
/// 「本番でだけ落ちる」道を残すことになる（Issue #55）。
/// </para>
/// </remarks>
internal static class AltchaSolver
{
    /// <summary>チケットの応答から解答を作る。**課題が付いていなければ空。**</summary>
    public static string Solve(JsonNode? ticketBody)
    {
        if (ticketBody?["altcha"] is not JsonObject challenge)
        {
            return string.Empty;
        }

        var salt = challenge["salt"]!.GetValue<string>();
        var expected = challenge["challenge"]!.GetValue<string>();
        var maxNumber = challenge["maxnumber"]!.GetValue<long>();
        var algorithm = challenge["algorithm"]!.GetValue<string>();
        var signature = challenge["signature"]!.GetValue<string>();

        for (long number = 0; number <= maxNumber; number++)
        {
            var hash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))));

            if (!string.Equals(hash, expected, StringComparison.Ordinal))
            {
                continue;
            }

            var payload = new JsonObject
            {
                ["algorithm"] = algorithm,
                ["challenge"] = expected,
                ["number"] = number,
                ["salt"] = salt,
                ["signature"] = signature,
            };

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        }

        throw new InvalidOperationException("proof-of-work を解けなかった。上限の設定を確かめること");
    }
}
