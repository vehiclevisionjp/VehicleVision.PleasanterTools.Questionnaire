using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

/// <summary>回答画面の書体。</summary>
/// <remarks>
/// <para>
/// **文字列ではなく列挙にする**（Issue #56）。書体名を自由に書かせると、
/// その文字列がそのまま CSS の <c>font-family</c> へ載る。
/// **列挙なら、画面側は決め打ちの書体の並びから選ぶだけ**になり、
/// 利用者が書いた文字列が CSS へ届く経路が存在しなくなる。
/// </para>
/// <para>
/// **Web フォントを外から読み込まない。** 回答画面は完全匿名なので、
/// 回答者の端末から第三者へ要求を出させない
/// （<c>_documents/非機能設計.md</c> 1 章）。並べるのは端末が持っている書体だけ。
/// </para>
/// </remarks>
public enum ThemeFont
{
    /// <summary>端末の既定。**今までと同じ見た目。**</summary>
    System,

    /// <summary>ゴシック体。</summary>
    Sans,

    /// <summary>明朝体。</summary>
    Serif,

    /// <summary>丸ゴシック体。</summary>
    Rounded,

    /// <summary>等幅。</summary>
    Monospace,
}

/// <summary>色の値。**形を検査してからでないと CSS へ渡さない。**</summary>
/// <remarks>
/// <para>
/// ⚠️ **利用者が入れた文字列をそのまま CSS へ流し込まないこと。**
/// <c>red; } body { display:none } /*</c> のような値を混ぜられると、
/// スタイル表そのものを書き換えられる。
/// </para>
/// <para>
/// **色の名前（<c>red</c>）や関数（<c>rgb(...)</c>）は受け付けない。**
/// 受け付ける形を <c>#rgb</c> と <c>#rrggbb</c> だけに絞れば、
/// 検査は「16 進が 3 桁か 6 桁か」だけで済み、抜け道を数えなくてよくなる。
/// </para>
/// </remarks>
public static partial class ThemeColor
{
    /// <summary>受け付ける形。**先頭と末尾を固定する**（部分一致にしない）。</summary>
    [GeneratedRegex(
        "^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>CSS へ渡してよい形か。</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && Pattern().IsMatch(value);

    /// <summary>
    /// 形が正しければ小文字へ揃えて返す。**正しくなければ <c>null</c>。**
    /// </summary>
    /// <remarks>
    /// **「既定へ落とす」ことと「利用者の指定を捨てる」ことは同じ。**
    /// 捨てた結果は既定の見た目になるので、壊れた値で画面が崩れることはない。
    /// </remarks>
    public static string? Normalize(string? value) =>
        IsValid(value) ? value!.ToLowerInvariant() : null;
}

/// <summary>名前の付いた配色（Issue #109）。</summary>
/// <remarks>
/// <para>
/// **色を増やすほど、読めない配色を作れる幅が広がる。**
/// そこで**組み合わせに名前を付け、そこから選ぶのを既定の導線にする。**
/// 個別の指定は「詳細」に畳む。
/// </para>
/// <para>
/// **実際の色は画面側（<c>Frontend/src/lib/theme.ts</c>）が持つ。**
/// ここは「どれを選んだか」だけを持つ。**サーバが色を持つと、
/// 画面の既定値との写しが 2 か所になり、片方だけ直す事故が起きる。**
/// </para>
/// <para>
/// **選んだ配色より、個別に指定した色が優先される。**
/// 配色を選んだうえで 1 色だけ変える、が普通の使い方になる。
/// </para>
/// </remarks>
public enum ThemePreset
{
    /// <summary>指定しない。**今までと同じ見た目。**</summary>
    None,

    /// <summary>白地に藍。**既定に一番近い。**</summary>
    Indigo,

    /// <summary>白地に深緑。</summary>
    Forest,

    /// <summary>白地に臙脂。</summary>
    Crimson,

    /// <summary>暖かい灰色の地。**紙に近い印象。**</summary>
    Sand,

    /// <summary>暗い地に明るい文字。</summary>
    Midnight,
}

/// <summary>回答画面の見た目（Issue #56）。</summary>
/// <remarks>
/// <para>
/// **すべて省略可。省略したら今までの見た目になる**（Issue #56 の「既定の見た目を壊さない」）。
/// <see cref="SurveyDefinition.Theme"/> が <c>null</c> のときも同じ。
/// </para>
/// <para>
/// **定義の一部として持つ。** 公開のたびに
/// <c>SurveyVersions.DefinitionJson</c> へ丸ごと固まるので、
/// 見た目も設問と同じように版で凍る（<c>_documents/データモデル設計.md</c> 1 章）。
/// **公開済みのアンケートの色が、下書きを触っただけで変わることはない。**
/// </para>
/// <para>
/// **色を足すときは必ず省略可にすること**（Issue #109）。
/// 増やすほど読めない配色を作れる幅が広がるので、
/// **名前の付いた配色（<see cref="Preset"/>）から選ぶのを既定の導線にし、**
/// 個別の指定は畳んでおく。
/// </para>
/// </remarks>
public sealed record SurveyTheme
{
    /// <summary>釦・進捗バー・強調に使う色。</summary>
    public string? AccentColor { get; init; }

    /// <summary>ページの地の色。</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>本文の色。</summary>
    public string? TextColor { get; init; }

    /// <summary>釦の文字色（Issue #109）。</summary>
    /// <remarks>
    /// **アクセント色から自動で決めていた**ため、
    /// **濃い地に濃い文字**になる組み合わせを避けられなかった。
    /// </remarks>
    public string? AccentTextColor { get; init; }

    /// <summary>設問枠の地の色（Issue #109）。</summary>
    /// <remarks>
    /// **白固定だった**ため、地の色を濃くすると設問枠だけが浮いた。
    /// </remarks>
    public string? SurfaceColor { get; init; }

    /// <summary>枠線の色（Issue #109）。</summary>
    /// <remarks>**省略時は地と文字から導く**（今までと同じ）。</remarks>
    public string? BorderColor { get; init; }

    /// <summary>名前の付いた配色（Issue #109）。**個別の指定が優先される。**</summary>
    public ThemePreset Preset { get; init; } = ThemePreset.None;

    /// <summary>書体。**既定は端末任せ。**</summary>
    public ThemeFont Font { get; init; } = ThemeFont.System;

    /// <summary>
    /// ヘッダ画像の識別子（<c>SurveyAssets</c> の行）。**無ければ画像を出さない。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// **中身ではなく識別子だけを持つ。** 画像そのものを定義の JSON へ入れると、
    /// 公開のたびに版の数だけ複製され、回答画面が定義を読むだけで数 MB を受け取ることになる。
    /// </para>
    /// <para>
    /// **外部の URL を持たせない**（Issue #56）。回答画面は完全匿名なので、
    /// 画像を取りに行くだけで回答者の IP と時刻が第三者へ渡る経路を作らない。
    /// **画像は必ず本アプリが配る**（<c>/api/forms/{publicId}/header-image</c>）。
    /// </para>
    /// <para>
    /// **行は上書きしない。** 差し替えると新しい識別子の行が増えるので、
    /// 公開済みの版が指している画像は、下書きを差し替えても変わらない。
    /// </para>
    /// </remarks>
    public string? HeaderImageId { get; init; }

    /// <summary>何も指定していないか。**保存も配信も省ける。**</summary>
    public bool IsDefault =>
        AccentColor is null
        && BackgroundColor is null
        && TextColor is null
        && AccentTextColor is null
        && SurfaceColor is null
        && BorderColor is null
        && Preset is ThemePreset.None
        && Font is ThemeFont.System
        && HeaderImageId is null;

    /// <summary>形の正しくない色の項目名。**空なら受け付けてよい。**</summary>
    /// <remarks>
    /// **「指定が無い」と「形が違う」を分ける。** 未指定は正常なので挙げない。
    /// </remarks>
    public ImmutableArray<string> InvalidColors()
    {
        var invalid = ImmutableArray.CreateBuilder<string>();

        Check(nameof(AccentColor), AccentColor);
        Check(nameof(BackgroundColor), BackgroundColor);
        Check(nameof(TextColor), TextColor);
        Check(nameof(AccentTextColor), AccentTextColor);
        Check(nameof(SurfaceColor), SurfaceColor);
        Check(nameof(BorderColor), BorderColor);

        return invalid.ToImmutable();

        void Check(string name, string? value)
        {
            if (value is not null && !ThemeColor.IsValid(value))
            {
                invalid.Add(name);
            }
        }
    }

    /// <summary>ヘッダ画像の識別子として読める値なら返す。**読めなければ <c>null</c>。**</summary>
    public Guid? HeaderImage() =>
        Guid.TryParse(HeaderImageId, out var assetId) ? assetId : null;

    /// <summary>
    /// CSS へ渡してよい値だけを残す。**残らなかった項目は既定の見た目になる。**
    /// </summary>
    /// <remarks>
    /// **入口の検査と二重に掛ける。** 入口（管理画面の保存）でも弾いているが、
    /// **DB を直接書き換えられた行や、検査を足す前に保存された行**が
    /// 回答画面へそのまま流れないようにする。
    /// **回答画面へ出る前の最後の関所がここ。**
    /// </remarks>
    public SurveyTheme Sanitized() => new()
    {
        AccentColor = ThemeColor.Normalize(AccentColor),
        BackgroundColor = ThemeColor.Normalize(BackgroundColor),
        TextColor = ThemeColor.Normalize(TextColor),
        AccentTextColor = ThemeColor.Normalize(AccentTextColor),
        SurfaceColor = ThemeColor.Normalize(SurfaceColor),
        BorderColor = ThemeColor.Normalize(BorderColor),
        // **知らない配色は指定なしへ落とす。** 列挙の範囲外の数値が JSON から入り得る
        Preset = Enum.IsDefined(Preset) ? Preset : ThemePreset.None,
        // **知らない書体は既定へ落とす。** 列挙の範囲外の数値が JSON から入り得る
        Font = Enum.IsDefined(Font) ? Font : ThemeFont.System,
        // **識別子として読めないものは捨てる。** 画像の取得は GUID でしか行わない
        HeaderImageId = HeaderImage()?.ToString(),
    };
}
