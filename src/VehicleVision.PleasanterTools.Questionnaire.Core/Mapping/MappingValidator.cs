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

    /// <summary>添付の割り当てなのに、形が <c>1 : 0 : 1</c> になっていない。</summary>
    InvalidAttachmentShape,

    /// <summary>添付の口に、添付以外の設問を繋いでいる。</summary>
    NonFileQuestionAsAttachment,

    /// <summary>添付列なのに、添付の口を使っていない。</summary>
    AttachmentColumnNeedsFilePort,

    /// <summary>添付の口なのに、書き込み先が添付列でない。</summary>
    FilePortNeedsAttachmentColumn,

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
    public static ImmutableArray<MappingProblem> Validate(
        MappingDefinition mapping,
        SurveyDefinition definition,
        IReadOnlyCollection<string>? reservedColumns = null,
        Func<string, bool>? isAttachmentColumn = null)
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

            if (assignment.Converter is { Operation: ConverterOperations.Script } converter
                && string.IsNullOrWhiteSpace(converter.Config.GetValueOrDefault("script")))
            {
                problems.Add(new MappingProblem(
                    MappingProblemCode.EmptyScript, assignment.TargetColumn));
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
            }
        }

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
