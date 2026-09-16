using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;

namespace VehicleVision.PleasanterTools.Questionnaire.Core.Mapping;

/// <summary>マッピングの不備。</summary>
public enum MappingProblemCode
{
    /// <summary>入力が複数あるのに変換が無い、または入力が 1 つも無い。</summary>
    InvalidShape,

    /// <summary>同じ列への割り当てが重複している。</summary>
    DuplicateTargetColumn,

    /// <summary>書き込み先の列が指定されていない。</summary>
    MissingTargetColumn,

    /// <summary>定義に存在しない設問を入力にしている。</summary>
    QuestionNotInDefinition,

    /// <summary>表示専用の要素（説明文ブロック）を入力にしている。</summary>
    DisplayOnlyQuestionAsSource,

    /// <summary>予約列を書き込み先にしている。</summary>
    ReservedColumn,

    /// <summary>スクリプト変換なのにスクリプトが空。</summary>
    EmptyScript,

    /// <summary>変換に必要な設定が空。</summary>
    MissingConverterConfig,

    /// <summary>添付の割り当てなのに、形が <c>1 : 0 : 1</c> になっていない。</summary>
    InvalidAttachmentShape,

    /// <summary>添付の口に、添付以外の設問を繋いでいる。</summary>
    NonFileQuestionAsAttachment,

    /// <summary>添付列なのに、添付の口を使っていない。</summary>
    AttachmentColumnNeedsFilePort,

    /// <summary>行を指定しているのに、その設問に行が無い（Issue #54）。</summary>
    RowNotSupported,

    /// <summary>指定した行が、その設問に存在しない。</summary>
    RowNotInQuestion,

    /// <summary>行を持つ設問なのに、行を指定していない。</summary>
    RowRequired,

    /// <summary>添付の口なのに、書き込み先が添付列でない。</summary>
    FilePortNeedsAttachmentColumn,

    /// <summary>書き込み先の型へ確実に変換できない。</summary>
    TargetColumnNeedsCompatibleValue,

    /// <summary>どこへも割り当てられていない設問。**拒否はしないが警告する。**</summary>
    UnmappedQuestion,
}

/// <summary>マッピングの不備 1 件。</summary>
public sealed record MappingProblem(
    MappingProblemCode Code,
    string? TargetColumn = null,
    string? Detail = null)
{
    /// <summary>保存を拒否すべき不備か。</summary>
    /// <remarks>
    /// 未割り当ては拒否しない。**「この設問は Pleasanter に残らない」と伝えたうえで保存させる**
    /// （<c>_documents/画面設計.md</c> 2 章）。
    /// </remarks>
    public bool IsBlocking => Code is not MappingProblemCode.UnmappedQuestion;
}

/// <summary>マッピングを保存する前に検査する。</summary>
public static class MappingValidator
{
    /// <param name="isAttachmentColumn">
    /// 添付列かどうかの判定。**列名の決まりは Pleasanter 側の知識**なので、
    /// ここでは持たずに受け取る。渡さなければ列名の側は確かめない。
    /// </param>
    /// <param name="targetValueKind">
    /// 入れ物に入らない書き込み先の値の型。列名と型は Pleasanter 側の知識なので、
    /// ここではその型を確実に出せる割り当てかだけを検査する。
    /// </param>
    public static ImmutableArray<MappingProblem> Validate(
        MappingDefinition mapping,
        SurveyDefinition definition,
        IReadOnlyCollection<string>? reservedColumns = null,
        Func<string, bool>? isAttachmentColumn = null,
        Func<string, MappingTargetValueKind?>? targetValueKind = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(definition);

        var problems = ImmutableArray.CreateBuilder<MappingProblem>();
        var reserved = (reservedColumns ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in mapping.Assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.TargetColumn))
            {
                problems.Add(new MappingProblem(MappingProblemCode.MissingTargetColumn));
                continue;
            }

            if (!seenTargets.Add(assignment.TargetColumn))
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.DuplicateTargetColumn, assignment.TargetColumn));
            }

            if (reserved.Contains(assignment.TargetColumn))
            {
                // 予約列（回答 JSON）はマッピングの対象にしない
                problems.Add(new MappingProblem(
                    MappingProblemCode.ReservedColumn, assignment.TargetColumn));
            }

            var usesFilePort = assignment.Sources.Any(source => source.Port is QuestionPort.Files);
            var targetsAttachment = isAttachmentColumn?.Invoke(assignment.TargetColumn) ?? usesFilePort;

            if (usesFilePort)
            {
                // **添付は 1 : 0 : 1 に限る**（2026-08-18 決定。変換は掛けない）
                if (assignment.Sources.Length != 1 || assignment.Converter is not null)
                {
                    problems.Add(new MappingProblem(
                        MappingProblemCode.InvalidAttachmentShape, assignment.TargetColumn));
                }

                if (!targetsAttachment)
                {
                    // 添付の中身は Base64。**添付列以外へ入れると列が本文で埋まる**
                    problems.Add(new MappingProblem(
                        MappingProblemCode.FilePortNeedsAttachmentColumn, assignment.TargetColumn));
                }
            }
            else if (targetsAttachment)
            {
                // 名前だけを添付列へ入れても、ファイルとしては取り出せない
                problems.Add(new MappingProblem(
                    MappingProblemCode.AttachmentColumnNeedsFilePort, assignment.TargetColumn));
            }
            else if (!assignment.HasValidShape)
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.InvalidShape, assignment.TargetColumn));
            }

            var targetKind = targetValueKind?.Invoke(assignment.TargetColumn);
            if (targetKind is { } kind && !Produces(assignment, definition, kind))
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.TargetColumnNeedsCompatibleValue, assignment.TargetColumn));
            }

            if (assignment.Converter is { Operation: ConverterOperations.Script } converter
                && string.IsNullOrWhiteSpace(converter.Config.GetValueOrDefault("script")))
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.EmptyScript, assignment.TargetColumn));
            }
            else if (assignment.Converter is { } configuredConverter
                && (HasMissingConfig(configuredConverter)
                    || NeedsNumericMapDefault(configuredConverter, targetKind)))
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.MissingConverterConfig, assignment.TargetColumn));
            }

            foreach (var source in assignment.Sources)
            {
                var question = definition.FindQuestion(source.QuestionId);
                if (question is null)
                {
                    problems.Add(new MappingProblem(
                        MappingProblemCode.QuestionNotInDefinition,
                        assignment.TargetColumn,
                        source.QuestionId));
                }
                else if (question.IsDisplayOnly)
                {
                    problems.Add(new MappingProblem(
                        MappingProblemCode.DisplayOnlyQuestionAsSource,
                        assignment.TargetColumn,
                        source.QuestionId));
                }
                else if (source.Port is QuestionPort.Files && question.Type is not QuestionType.File)
                {
                    // 添付を持たない設問から中身は取り出せない
                    problems.Add(new MappingProblem(
                        MappingProblemCode.NonFileQuestionAsAttachment,
                        assignment.TargetColumn,
                        source.QuestionId));
                }
                else if (source.Port is QuestionPort.Value)
                {
                    ValidateRow(source, question, assignment.TargetColumn, problems);
                }
            }
        }

        return Finish(problems, mapping, definition);
    }

    /// <summary>既定値のない必須設定が欠けているか。</summary>
    private static bool HasMissingConfig(MappingConverter converter) =>
        converter.Operation switch
        {
            ConverterOperations.Map => !converter.Config.Keys.Any(key =>
                key.StartsWith("map.", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(key["map.".Length..])),
            ConverterOperations.ToNumber =>
                !converter.Config.ContainsKey("default")
                || !HasValidDecimals(converter.Config.GetValueOrDefault("decimals")),
            ConverterOperations.ToCheck or ConverterOperations.Constant =>
                string.IsNullOrWhiteSpace(converter.Config.GetValueOrDefault("value")),
            ConverterOperations.Contains =>
                string.IsNullOrWhiteSpace(converter.Config.GetValueOrDefault("keyword")),
            ConverterOperations.When =>
                string.IsNullOrWhiteSpace(converter.Config.GetValueOrDefault("when"))
                || string.IsNullOrWhiteSpace(converter.Config.GetValueOrDefault("then")),
            _ => false,
        };

    private static bool NeedsNumericMapDefault(
        MappingConverter converter,
        MappingTargetValueKind? target) =>
        converter.Operation is ConverterOperations.Map
        && target is MappingTargetValueKind.Integer or MappingTargetValueKind.Decimal
        && !converter.Config.ContainsKey("default");

    /// <summary>割り当てが書き込み先の型または未設定を確実に出せるか。</summary>
    /// <remarks>
    /// 送信時まで失敗を持ち越さないため、回答者が任意の文字列を入れられる経路は許可しない。
    /// </remarks>
    private static bool Produces(
        ColumnAssignment assignment,
        SurveyDefinition definition,
        MappingTargetValueKind target)
    {
        // **文字列はどの経路からでも作れる。** 変換は文字列を返すので、
        // 文字列の列へ入れるときに変換へ失敗しようがない。
        // ⚠️ **ここを通さないと、変換を挟んだ途端に Title と Body が弾かれる**
        // （Issue #246 は変換の無い経路しか直していなかった）
        if (target is MappingTargetValueKind.String)
        {
            return true;
        }

        if (assignment.Converter is null)
        {
            if (assignment.Sources.Length != 1)
            {
                return false;
            }

            var source = assignment.Sources[0];
            var question = definition.FindQuestion(source.QuestionId);
            if (question is null || source.Port is not QuestionPort.Value)
            {
                return false;
            }

            return ProducesDirectly(question, source, target);
        }

        return assignment.Converter.Operation switch
        {
            ConverterOperations.Map =>
                ProducesMappedValues(assignment.Converter.Config, target),
            ConverterOperations.ToNumber =>
                ProducesNumber(assignment.Converter.Config, target),
            ConverterOperations.Constant =>
                CanConvert(assignment.Converter.Config.GetValueOrDefault("value"), target),
            ConverterOperations.When =>
                CanConvert(assignment.Converter.Config.GetValueOrDefault("then"), target)
                && CanConvert(assignment.Converter.Config.GetValueOrDefault("else"), target),
            _ => false,
        };
    }

    private static bool ProducesMappedValues(
        ImmutableDictionary<string, string> config,
        MappingTargetValueKind target) =>
        target is MappingTargetValueKind.Integer or MappingTargetValueKind.Decimal
        && config.TryGetValue("default", out var defaultValue)
        && CanConvert(defaultValue, target)
        && config
            .Where(pair =>
                pair.Key.StartsWith("map.", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(pair.Key["map.".Length..]))
            .All(pair => CanConvert(pair.Value, target));

    private static bool ProducesNumber(
        ImmutableDictionary<string, string> config,
        MappingTargetValueKind target)
    {
        if (!config.TryGetValue("default", out var defaultValue)
            || !HasValidDecimals(config.GetValueOrDefault("decimals"))
            || !CanConvert(defaultValue, target))
        {
            return false;
        }

        return target switch
        {
            MappingTargetValueKind.Decimal => true,
            MappingTargetValueKind.Integer =>
                int.TryParse(
                    config.GetValueOrDefault("decimals"),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var decimals)
                && decimals == 0,
            _ => false,
        };
    }

    private static bool HasValidDecimals(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var decimals)
        && decimals is >= 0 and <= 28;

    private static bool ProducesDirectly(
        Question question,
        MappingSource source,
        MappingTargetValueKind target)
    {
        // **文字列はどの設問からでも作れる。** ここを通さないと
        // `Title` と `Body` へ何も割り当てられない（Issue #246）。
        // ⚠️ **この検査は「確実に変換できるか」を見るもの**で、
        // 数値や日時のように**変換に失敗し得る型だけが対象**である
        if (target is MappingTargetValueKind.String)
        {
            return true;
        }

        if (target is MappingTargetValueKind.DateTime)
        {
            return question.Type is QuestionType.Date;
        }

        if (target is MappingTargetValueKind.Boolean
            && question.Type is QuestionType.Confirm)
        {
            return true;
        }

        if (question.Type is QuestionType.Scale or QuestionType.Rating)
        {
            return target is MappingTargetValueKind.Integer or MappingTargetValueKind.Decimal;
        }

        if (question.Type is QuestionType.Ranking && source.RowId is not null)
        {
            return target is MappingTargetValueKind.Integer or MappingTargetValueKind.Decimal;
        }

        return question.Type is QuestionType.Radio or QuestionType.Dropdown
            && question.Choices.All(choice =>
                !choice.IsOther
                && CanConvert(choice.Value, target));
    }

    private static bool CanConvert(string? value, MappingTargetValueKind target) =>
        string.IsNullOrWhiteSpace(value)
        || target switch
        {
            MappingTargetValueKind.Integer => int.TryParse(
                value,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _),
            MappingTargetValueKind.Boolean => bool.TryParse(value, out _),
            MappingTargetValueKind.Decimal => decimal.TryParse(
                value,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out _),
            MappingTargetValueKind.DateTime => DateOnly.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out _)
                || DateTimeOffset.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _),
            _ => true,
        };

    /// <summary>行の指定が噛み合っているかを見る（Issue #54）。</summary>
    /// <remarks>
    /// <para>
    /// **グリッドとランキングは 1 設問が複数の入力を出す。**
    /// どの行を指しているかが合っていないと、**黙って空が入る**。
    /// </para>
    /// <para>
    /// **行を持つ設問で行を指定しないことも咎める。** 指定しないと
    /// 「行をまたいだ全部の値」が入り、どの行の答えか分からないものが列へ残る。
    /// </para>
    /// </remarks>
    private static void ValidateRow(
        MappingSource source,
        Question question,
        string targetColumn,
        ImmutableArray<MappingProblem>.Builder problems)
    {
        if (source.RowId is null)
        {
            if (question.HasRowPorts)
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.RowRequired, targetColumn, source.QuestionId));
            }

            return;
        }

        if (!question.HasRowPorts)
        {
            problems.Add(new MappingProblem(
                MappingProblemCode.RowNotSupported, targetColumn, source.QuestionId));
            return;
        }

        if (!question.RowPortIds.Contains(source.RowId, StringComparer.Ordinal))
        {
            // **行を消したときの直し忘れがここで見つかる**
            problems.Add(new MappingProblem(
                MappingProblemCode.RowNotInQuestion, targetColumn, source.QuestionId));
        }
    }

    /// <summary>どこにも割り当てられていない設問を挙げる。</summary>
    private static ImmutableArray<MappingProblem> Finish(
        ImmutableArray<MappingProblem>.Builder problems,
        MappingDefinition mapping,
        SurveyDefinition definition)
    {
        var mapped = mapping.Assignments
            .SelectMany(assignment => assignment.Sources)
            .Select(source => source.QuestionId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var question in definition.AllQuestions.Where(question => !question.IsDisplayOnly))
        {
            if (!mapped.Contains(question.QuestionId))
            {
                // **回答が Pleasanter の列に残らない。** 正本 JSON には残るので拒否はしない
                problems.Add(new MappingProblem(
                    MappingProblemCode.UnmappedQuestion, null, question.QuestionId));
            }
        }

        return problems.ToImmutable();
    }
}
