using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ixnas.AltchaNet;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Tests;

/// <summary>ALTCHA の課題と解答が、画面側と噛み合う形であること。</summary>
/// <remarks>
/// <para>
/// **画面側の解き手を自分で書いている**（`lib/altcha.ts`）。npm の部品を足さずに済ませ、
/// 読み上げの扱いを自分で決められるようにするため（Issue #55）。
/// </para>
/// <para>
/// **そのぶん、項目名を 1 文字でも取り違えると通らない。**
/// ここで**実物の JSON を見て**確かめる。書き写した仕様ではなく、
/// ライブラリが実際に出す形を正とする。
/// </para>
/// </remarks>
public class AltchaWireFormatTests
{
    /// <summary>試験用の鍵。**本物ではない。**</summary>
    private static byte[] Key()
    {
        var key = new byte[64];
        for (var index = 0; index < key.Length; index++)
        {
            key[index] = (byte)index;
        }

        return key;
    }

    private sealed class MemoryStore : IAltchaChallengeStore
    {
        private readonly Dictionary<string, DateTimeOffset> seen = [];

        public Task Store(string challenge, DateTimeOffset expiryUtc)
        {
            seen[challenge] = expiryUtc;
            return Task.CompletedTask;
        }

        public Task<bool> Exists(string challenge) => Task.FromResult(seen.ContainsKey(challenge));
    }

    /// <summary>アプリが HTTP で使う設定。**課題はこの形で画面へ届く。**</summary>
    /// <remarks>
    /// **既定のままだと PascalCase になる**（実測）。ASP.NET Core の Web 既定は
    /// camelCase なので、1 語の項目はそのまま小文字になる。
    /// </remarks>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static AltchaService Service() =>
        Altcha.CreateService(new AltchaSha256Configuration
        {
            Key = AltchaKey.FromBytes(Key()),
            StoreFactory = () => new MemoryStore(),
        });

    [Fact]
    public void 課題の項目名は小文字である()
    {
        // **画面側はこの名前で読む。** 変わったら気付けるようにしておく
        var json = JsonSerializer.Serialize(Service().Generate(), Web);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        Assert.Contains("algorithm", names);
        Assert.Contains("challenge", names);
        Assert.Contains("salt", names);
        Assert.Contains("signature", names);
        Assert.Contains("maxnumber", names);
    }

    [Fact]
    public void 既定の算法はSHA256である()
    {
        var json = JsonSerializer.Serialize(Service().Generate(), Web);
        using var document = JsonDocument.Parse(json);

        // **画面側は Web Crypto の SHA-256 で解く。** ここが変わったら解けなくなる
        Assert.Equal("SHA-256", document.RootElement.GetProperty("algorithm").GetString());
    }

    [Fact]
    public async Task 画面と同じ手順で解いた答えが通る()
    {
        // **これが本命。** 画面側の解き手と同じ計算をここで再現し、
        // ライブラリが受け付けることを確かめる
        var service = Service();
        var challenge = service.Generate();

        var json = JsonSerializer.Serialize(challenge, Web);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var salt = root.GetProperty("salt").GetString()!;
        var expected = root.GetProperty("challenge").GetString()!;
        var maxNumber = root.GetProperty("maxnumber").GetInt64();

        var number = Solve(salt, expected, maxNumber);
        Assert.NotNull(number);

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                algorithm = root.GetProperty("algorithm").GetString(),
                challenge = expected,
                number,
                salt,
                signature = root.GetProperty("signature").GetString(),
            })));

        var result = await service.Validate(payload);

        Assert.True(result.IsValid, result.ValidationError?.Message);
    }

    [Fact]
    public async Task 同じ答えは二度通らない()
    {
        // **使い回しを防げていなければ、1 回解くだけで無限に投稿できる**
        var store = new MemoryStore();
        var service = Altcha.CreateService(new AltchaSha256Configuration
        {
            Key = AltchaKey.FromBytes(Key()),
            StoreFactory = () => store,
        });

        var challenge = service.Generate();
        var json = JsonSerializer.Serialize(challenge, Web);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var salt = root.GetProperty("salt").GetString()!;
        var expected = root.GetProperty("challenge").GetString()!;
        var number = Solve(salt, expected, root.GetProperty("maxnumber").GetInt64());

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                algorithm = root.GetProperty("algorithm").GetString(),
                challenge = expected,
                number,
                salt,
                signature = root.GetProperty("signature").GetString(),
            })));

        Assert.True((await service.Validate(payload)).IsValid);
        Assert.False((await service.Validate(payload)).IsValid);
    }

    [Fact]
    public async Task 数を書き換えた答えは通らない()
    {
        var service = Service();
        var challenge = service.Generate();
        var json = JsonSerializer.Serialize(challenge, Web);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                algorithm = root.GetProperty("algorithm").GetString(),
                challenge = root.GetProperty("challenge").GetString(),
                number = 0,
                salt = root.GetProperty("salt").GetString(),
                signature = root.GetProperty("signature").GetString(),
            })));

        Assert.False((await service.Validate(payload)).IsValid);
    }

    /// <summary>画面側と同じ解き方。**総当たりで数を探す。**</summary>
    private static long? Solve(string salt, string expected, long maxNumber)
    {
        for (long number = 0; number <= maxNumber; number++)
        {
            var hash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(
                    System.Globalization.CultureInfo.InvariantCulture))));

            if (string.Equals(hash, expected, StringComparison.Ordinal))
            {
                return number;
            }
        }

        return null;
    }
}
