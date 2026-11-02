using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

namespace VehicleVision.PleasanterTools.Questionnaire.Pleasanter;

/// <summary>検索の接続先と専用資格情報。</summary>
/// <remarks>
/// **API キーを持つ。ログ・例外・画面へ出さないこと。** <see cref="ToString"/> も伏せる。
/// 接続先は管理済みの Pleasanter 接続先だけ。回答者の入力から作らない。
/// </remarks>
public sealed class LookupSourceConnection(string baseUrl, string apiKey)
{
    public string BaseUrl { get; } = baseUrl;

    internal string ApiKey { get; } = apiKey;

    public override string ToString() => $"LookupSourceConnection {{ BaseUrl = {BaseUrl}, ApiKey = *** }}";
}

/// <summary>検索の上限（仕様書 5.2 節・11 章 U4）。</summary>
public sealed class LookupSearchOptions
{
    /// <summary>外部検索の時間切れの既定（5 秒）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>応答サイズの上限の既定（1 MiB。実測 12 章 P12 の目安）。</summary>
    public const int DefaultMaxResponseBytes = 1024 * 1024;

    /// <summary>API のバージョン。</summary>
    public decimal ApiVersion { get; init; } = PleasanterOptions.DefaultApiVersion;

    /// <summary>外部検索の時間切れ。接続・送信・受信の合計。</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>応答本文の最大バイト数。超えたら読むのをやめて失敗にする。</summary>
    public int MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;

    /// <summary>
    /// 平文の HTTP をループバック（<c>localhost</c>・<c>127.0.0.1</c>・<c>::1</c>）へ送ってよいか。
    /// 開発用。**既定は不可（HTTPS のみ）。**
    /// </summary>
    public bool AllowLoopbackHttp { get; init; }
}

/// <summary>検索の要求。回答者の入力はキー値だけ。</summary>
/// <param name="Lookup">公開版で固定された定義。</param>
/// <param name="KeyValues">キー項目（設問 ID）ごとの入力値。</param>
/// <param name="KeyColumnKinds">
/// 参照元のキー列（<see cref="LookupKey.SourceColumn"/>）ごとの列の種類。
/// **種類が分からないキーは検索しない**（整数以外を数値列へ送ると条件が捨てられて全件が返る）。
/// </param>
public sealed record LookupSearchRequest(
    LookupDefinition Lookup,
    IReadOnlyDictionary<string, string?> KeyValues,
    IReadOnlyDictionary<string, LookupColumnKind> KeyColumnKinds);

/// <summary>検索の結果の区分（仕様書 4.3 節の HTTP・コードへ写す前の分類）。</summary>
public enum LookupSearchStatus
{
    /// <summary>検索できた（0・1・複数件）。</summary>
    Success,

    /// <summary>キーの不備。外部検索はしていない（400 <c>LookupInvalidKeys</c>）。</summary>
    InvalidKeys,

    /// <summary>上限超過、または全件を確認できない（422 <c>LookupTooManyResults</c>）。</summary>
    TooManyResults,

    /// <summary>参照元の権限・設定の不整合、不正な応答（502 <c>LookupSourceInvalid</c>）。</summary>
    SourceInvalid,

    /// <summary>接続障害・外部の 429・5xx（503 <c>LookupTemporarilyUnavailable</c>）。</summary>
    TemporarilyUnavailable,

    /// <summary>外部検索の時間切れ（504 <c>LookupTimeout</c>）。</summary>
    Timeout,
}

/// <summary>失敗の詳細な分類。**管理者の切り分け用。** 秘密・検索値・生の応答は含まない。</summary>
public enum LookupSearchFailure
{
    None,

    /// <summary>定義・接続先の不備（サイト ID・許可列・件数・列の種類・接続先 URL など）。</summary>
    InvalidDefinition,

    /// <summary>HTTPS ではない接続先。</summary>
    InsecureTransport,

    /// <summary>401（API キーの誤り・失効・ユーザーの無効）。</summary>
    Unauthorized,

    /// <summary>403。</summary>
    Forbidden,

    /// <summary>404（権限なしと不存在は区別できない）。</summary>
    NotFound,

    /// <summary>上記以外の 4xx。</summary>
    ClientError,

    /// <summary>リダイレクト。**追わない**（資格情報を別の場所へ送らない）。</summary>
    Redirected,

    /// <summary>外部の 429。</summary>
    RateLimited,

    /// <summary>外部の 5xx。</summary>
    ServerError,

    /// <summary>接続できなかった。</summary>
    ConnectionFailed,

    /// <summary>応答が JSON でない・必須の構造が無い。</summary>
    InvalidResponse,

    /// <summary>応答が上限を超えた。</summary>
    ResponseTooLarge,

    /// <summary>総件数・取得件数・ページ位置が整合しない。</summary>
    Inconsistent,

    /// <summary>候補の上限（K）を超えた、または全件を確認できない。</summary>
    TooManyResults,
}

/// <summary>1 件の候補。許可列だけを持つ。</summary>
/// <remarks>値は文字列のまま（数値は JSON の表記のまま。<c>5.0</c> など）。値なしは <c>null</c>。型の検証は反映時に行う。</remarks>
public sealed record LookupCandidateRow(IReadOnlyDictionary<string, string?> Values);

/// <summary>検索の結果。</summary>
/// <remarks>**検索値・API キー・生の応答を持たない。**</remarks>
public sealed record LookupSearchResult
{
    public required LookupSearchStatus Status { get; init; }

    public LookupSearchFailure Failure { get; init; }

    /// <summary>キーの不備があった設問 ID。<see cref="LookupSearchStatus.InvalidKeys"/> のときだけ。</summary>
    public ImmutableArray<string> InvalidKeyQuestionIds { get; init; } = [];

    /// <summary>候補。<see cref="LookupSearchStatus.Success"/> のときだけ。0 件なら空。</summary>
    public ImmutableArray<LookupCandidateRow> Candidates { get; init; } = [];

    public bool IsSuccess => Status == LookupSearchStatus.Success;

    /// <summary>0 件は <c>notFound</c>、1 件は <c>single</c>、2 件以上は <c>multiple</c>。成功時だけ。</summary>
    public string? Outcome => !IsSuccess
        ? null
        : Candidates.Length switch
        {
            0 => "notFound",
            1 => "single",
            _ => "multiple",
        };
}
