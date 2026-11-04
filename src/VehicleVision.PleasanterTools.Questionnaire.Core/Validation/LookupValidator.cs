using System.Collections.Immutable;
using System.Globalization;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>ルックアップ定義の不備の種類（仕様書 3.2 節）。</summary>
public enum LookupProblemCode
{
    /// <summary>ルックアップ ID が空、または重複している。</summary>
    LookupIdInvalid,

    /// <summary>参照元の種類が未対応（日本郵便は未実装）。</summary>
    SourceKindUnsupported,

    /// <summary>サイト ID が無い、または正でない。</summary>
    SourceSiteInvalid,

    /// <summary>資格情報の ID が無い。</summary>
    CredentialMissing,

    /// <summary>起点の設問が存在しない。</summary>
    AnchorNotFound,

    /// <summary>1 つの起点に複数のルックアップがある。</summary>
    AnchorDuplicated,

    /// <summary>キーの数が 1〜N の範囲外。</summary>
    KeyCountInvalid,

    /// <summary>キーの設問が存在しない。</summary>
    KeyQuestionNotFound,

    /// <summary>キーの設問が 1 行テキストではない。</summary>
    KeyQuestionTypeUnsupported,

    /// <summary>キーの設問または参照元の列が重複している、または列名が空。</summary>
    KeyDuplicated,

    /// <summary>キーの設問が起点と別のページにある。</summary>
    KeyPageMismatch,

    /// <summary>前方一致が許可されていないキーで前方一致が選ばれている。</summary>
    PrefixNotAllowed,

    /// <summary>入力先の数が 1〜M の範囲外。</summary>
    TargetCountInvalid,

    /// <summary>入力先の設問が存在しない。</summary>
    TargetQuestionNotFound,

    /// <summary>入力先の設問の形式が対象外（複数選択・日付など）。</summary>
    TargetQuestionTypeUnsupported,

    /// <summary>同じ入力先の設問へ複数の列を写している、または参照元の列が空・重複。</summary>
    TargetConflict,

    /// <summary>入力先の設問が起点と別のページにある。</summary>
    TargetPageMismatch,

    /// <summary>許可列が空、または空の列名を含む。</summary>
    AllowedColumnsInvalid,

    /// <summary>入力元の列が許可列に含まれていない。</summary>
    TargetColumnNotAllowed,

    /// <summary>表示列が許可列に含まれていない。</summary>
    DisplayColumnNotAllowed,

    /// <summary>表示 ID が空・形式の不正（英字で始まる英数字・下線、最大 32 文字）。</summary>
    DisplayIdInvalid,

    /// <summary>表示 ID が、同じルックアップの中で重複している。</summary>
    DisplayIdDuplicated,

    /// <summary>旧形式の表示列（表示 ID が無い列名の一覧）が残っている。表示 ID の定義へ作り直す。</summary>
    DisplayColumnsLegacy,

    /// <summary>最大候補数が 1〜システム上限の範囲外。</summary>
    MaxResultsInvalid,

    /// <summary>キー列の種類が未指定、または文字列・数値以外。</summary>
    KeyColumnKindInvalid,
}

/// <summary>ルックアップ定義の不備 1 件。</summary>
/// <param name="LookupId">対象のルックアップ。</param>
/// <param name="Code">不備の種類。</param>
/// <param name="Detail">補足（設問 ID・列名。**検索値や秘密は入れない**）。</param>
public sealed record LookupProblem(string? LookupId, LookupProblemCode Code, string? Detail = null);

/// <summary>ルックアップ定義の構造を見る（仕様書 3.2 節・11 章 U1・U4）。</summary>
/// <remarks>
/// <para>
/// **公開の前に止める。** 参照元の列の型（選択肢・Nullable・数値）は、
/// 列の情報が要るためこの検証では見ない。
/// </para>
/// <para>日本郵便は未実装なので、種類として選ばれたら拒否する。</para>
/// <para>
/// 入力先の形式は、本アプリに専用の数値入力の形式がないため、
/// テキスト・単一選択（ラジオ・プルダウン）・直線尺度に限る暫定の扱い。
/// </para>
/// </remarks>
public static class LookupValidator
{
    /// <summary>入力先にできる設問の形式。複数選択・日付・ファイルなどは対象外（U2）。</summary>
    private static bool IsTargetType(QuestionType type) =>
        type is QuestionType.Text or QuestionType.Radio or QuestionType.Dropdown
            or QuestionType.Scale;

    /// <summary>キーにできる設問の形式。1 行テキストだけ。</summary>
    private static bool IsKeyType(Question question) =>
        question.Type is QuestionType.Text;

    public static ImmutableArray<LookupProblem> Validate(
        SurveyDefinition definition, IEnumerable<LookupDefinition> lookups)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(lookups);

        var problems = ImmutableArray.CreateBuilder<LookupProblem>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenAnchors = new HashSet<string>(StringComparer.Ordinal);

        foreach (var lookup in lookups)
        {
            var id = lookup.LookupId;
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
            {
                problems.Add(new LookupProblem(id, LookupProblemCode.LookupIdInvalid));
            }

            ValidateSource(lookup, problems);
            var anchorPage = ValidateAnchor(definition, lookup, seenAnchors, problems);
            ValidateKeys(definition, lookup, anchorPage, problems);
            ValidateTargets(definition, lookup, anchorPage, problems);
            ValidateColumns(lookup, problems);
        }

        return problems.ToImmutable();
    }

    private static void ValidateSource(
        LookupDefinition lookup, ImmutableArray<LookupProblem>.Builder problems)
    {
        if (lookup.SourceKind != LookupSourceKind.Pleasanter)
        {
            problems.Add(new LookupProblem(
                lookup.LookupId, LookupProblemCode.SourceKindUnsupported,
                lookup.SourceKind.ToString()));
            return;
        }

        if (lookup.SourceSiteId is not > 0)
        {
            problems.Add(new LookupProblem(lookup.LookupId, LookupProblemCode.SourceSiteInvalid));
        }

        if (string.IsNullOrWhiteSpace(lookup.CredentialId))
        {
            problems.Add(new LookupProblem(lookup.LookupId, LookupProblemCode.CredentialMissing));
        }
    }

    /// <summary>起点を見て、そのページの ID を返す。起点が無ければ <c>null</c>。</summary>
    private static string? ValidateAnchor(
        SurveyDefinition definition,
        LookupDefinition lookup,
        HashSet<string> seenAnchors,
        ImmutableArray<LookupProblem>.Builder problems)
    {
        var anchorPage = PageOf(definition, lookup.AnchorQuestionId);
        if (anchorPage is null)
        {
            problems.Add(new LookupProblem(
                lookup.LookupId, LookupProblemCode.AnchorNotFound, lookup.AnchorQuestionId));
            return null;
        }

        if (!seenAnchors.Add(lookup.AnchorQuestionId))
        {
            problems.Add(new LookupProblem(
                lookup.LookupId, LookupProblemCode.AnchorDuplicated, lookup.AnchorQuestionId));
        }

        return anchorPage;
    }

    private static void ValidateKeys(
        SurveyDefinition definition,
        LookupDefinition lookup,
        string? anchorPage,
        ImmutableArray<LookupProblem>.Builder problems)
    {
        if (lookup.Keys.Length is < 1 or > LookupLimits.MaxKeys)
        {
            problems.Add(new LookupProblem(
                lookup.LookupId, LookupProblemCode.KeyCountInvalid,
                lookup.Keys.Length.ToString(CultureInfo.InvariantCulture)));
        }

        var questions = new HashSet<string>(StringComparer.Ordinal);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in lookup.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.SourceColumn)
                || !columns.Add(key.SourceColumn)
                || !questions.Add(key.QuestionId))
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.KeyDuplicated, key.QuestionId));
            }

            var question = definition.FindQuestion(key.QuestionId);
            if (question is null)
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.KeyQuestionNotFound, key.QuestionId));
            }
            else
            {
                if (!IsKeyType(question))
                {
                    problems.Add(new LookupProblem(
                        lookup.LookupId, LookupProblemCode.KeyQuestionTypeUnsupported,
                        key.QuestionId));
                }

                if (anchorPage is not null && PageOf(definition, key.QuestionId) != anchorPage)
                {
                    problems.Add(new LookupProblem(
                        lookup.LookupId, LookupProblemCode.KeyPageMismatch, key.QuestionId));
                }
            }

            if (key.SourceColumnKind is not (LookupColumnKind.Text or LookupColumnKind.Number))
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.KeyColumnKindInvalid, key.QuestionId));
            }

            if (key.MatchMode == LookupMatchMode.Prefix && !key.AllowPrefix)
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.PrefixNotAllowed, key.QuestionId));
            }
        }
    }

    private static void ValidateTargets(
        SurveyDefinition definition,
        LookupDefinition lookup,
        string? anchorPage,
        ImmutableArray<LookupProblem>.Builder problems)
    {
        if (lookup.Targets.Length is < 1 or > LookupLimits.MaxTargets)
        {
            problems.Add(new LookupProblem(
                lookup.LookupId, LookupProblemCode.TargetCountInvalid,
                lookup.Targets.Length.ToString(CultureInfo.InvariantCulture)));
        }

        var questions = new HashSet<string>(StringComparer.Ordinal);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in lookup.Targets)
        {
            // **同じ入力先への競合は公開前に検出する**（仕様書 3.2 節）
            if (string.IsNullOrWhiteSpace(target.SourceColumn)
                || !columns.Add(target.SourceColumn)
                || !questions.Add(target.QuestionId))
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.TargetConflict, target.QuestionId));
            }

            var question = definition.FindQuestion(target.QuestionId);
            if (question is null)
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.TargetQuestionNotFound, target.QuestionId));
                continue;
            }

            if (!IsTargetType(question.Type))
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.TargetQuestionTypeUnsupported,
                    target.QuestionId));
            }

            if (anchorPage is not null && PageOf(definition, target.QuestionId) != anchorPage)
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.TargetPageMismatch, target.QuestionId));
            }
        }
    }

    private static void ValidateColumns(
        LookupDefinition lookup, ImmutableArray<LookupProblem>.Builder problems)
    {
        var allowedColumns = lookup.AllowedColumns.IsDefault ? [] : lookup.AllowedColumns;
        if (allowedColumns.IsEmpty || allowedColumns.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add(new LookupProblem(lookup.LookupId, LookupProblemCode.AllowedColumnsInvalid));
        }

        var allowed = new HashSet<string>(allowedColumns, StringComparer.Ordinal);

        foreach (var target in lookup.Targets)
        {
            if (!allowed.Contains(target.SourceColumn))
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.TargetColumnNotAllowed, target.SourceColumn));
            }
        }

        if (!lookup.DisplayColumns.IsDefaultOrEmpty)
        {
            // 旧形式は表示 ID が無い。列名を表示 ID へ読み替えず、作り直しを求める（Issue #835）
            problems.Add(new LookupProblem(lookup.LookupId, LookupProblemCode.DisplayColumnsLegacy));
        }

        var displayFields = lookup.DisplayFields.IsDefault ? [] : lookup.DisplayFields;
        var displayIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in displayFields)
        {
            if (!IsValidDisplayId(field.DisplayId))
            {
                problems.Add(new LookupProblem(lookup.LookupId, LookupProblemCode.DisplayIdInvalid));
            }
            else if (!displayIds.Add(field.DisplayId))
            {
                // 詳細に入れるのは表示 ID だけ（回答者へ見せる名前。参照元の列名は入れない）
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.DisplayIdDuplicated, field.DisplayId));
            }

            if (string.IsNullOrWhiteSpace(field.SourceColumn) || !allowed.Contains(field.SourceColumn))
            {
                problems.Add(new LookupProblem(
                    lookup.LookupId, LookupProblemCode.DisplayColumnNotAllowed, field.SourceColumn));
            }
        }

        if (lookup.MaxResults is < 1 or > LookupLimits.SystemMaxResults)
        {
            problems.Add(new LookupProblem(
                lookup.LookupId, LookupProblemCode.MaxResultsInvalid,
                lookup.MaxResults.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>表示 ID の形式。英字で始まる英数字・下線、最大 <see cref="LookupLimits.MaxDisplayIdLength"/> 文字。</summary>
    private static bool IsValidDisplayId(string? id) =>
        !string.IsNullOrEmpty(id)
        && id.Length <= LookupLimits.MaxDisplayIdLength
        && char.IsAsciiLetter(id[0])
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string? PageOf(SurveyDefinition definition, string questionId) =>
        definition.Pages
            .FirstOrDefault(page => page.Questions.Any(q => q.QuestionId == questionId))
            ?.PageId;
}
