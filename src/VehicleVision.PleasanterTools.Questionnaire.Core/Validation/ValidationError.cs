namespace VehicleVision.PleasanterTools.Questionnaire.Core.Validation;

/// <summary>検証エラー 1 件。</summary>
/// <param name="QuestionId">対象の設問。設問に紐づかないエラーでは <c>null</c>。</param>
/// <param name="Code">エラーの種別。</param>
/// <param name="Detail">文言の組み立てに使う補足（上限値など）。</param>
public sealed record ValidationError(
    string? QuestionId,
    ValidationErrorCode Code,
    string? Detail = null);
