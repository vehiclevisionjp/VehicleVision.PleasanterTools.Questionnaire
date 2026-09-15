using System.Collections.Immutable;
using VehicleVision.PleasanterTools.Questionnaire.Core.Definitions;
using VehicleVision.PleasanterTools.Questionnaire.Core.Text;
using VehicleVision.PleasanterTools.Questionnaire.Web.Services;

namespace VehicleVision.PleasanterTools.Questionnaire.Web.Endpoints;

/// <summary>説明文ブロックの記法を読んだ結果を求める要求。</summary>
/// <param name="Markups">
/// 管理者が書いた記法。**まとめて渡す。**
/// プレビューは説明文ブロックの数だけ要るので、1 件ずつ往復させない。
/// </param>
public sealed record NotePreviewRequest(ImmutableArray<string?> Markups);

/// <summary>記法 1 件を読んだ結果。</summary>
/// <param name="Blocks">段落の並び。</param>
/// <param name="Truncated">
/// 上限を超えて切り落としたか。**書き手に伝えるため。**
/// 黙って切ると、公開してから足りないことに気付く。
/// </param>
public sealed record NotePreviewResult(ImmutableArray<NoteBlock> Blocks, bool Truncated);

/// <summary>読んだ結果。**渡された順に並ぶ。**</summary>
public sealed record NotePreviewResponse(ImmutableArray<NotePreviewResult> Results);

/// <summary>説明文ブロックの記法を、管理画面が書いている途中で確かめる口（Issue #108）。</summary>
/// <remarks>
/// <para>
/// **記法を読む実装をサーバに 1 つだけ置くための口。**
/// 画面側に同じ実装を置くと、**プレビューで通った書き方が公開後に通らない**という
/// ずれが起きる。読むのは <see cref="NoteMarkup"/> だけにする。
/// </para>
/// <para>
/// **監査には残さない。** 書いている最中に何度も呼ばれるうえ、何も変えない
/// （<c>AdminSurveyEndpoints</c> と別のグループにしてある理由）。
/// </para>
/// </remarks>
public static class AdminNoteEndpoints
{
    /// <summary>1 度に読む件数の上限。</summary>
    /// <remarks>**画面から大きな配列を渡されても超えない。**</remarks>
    private const int MaxMarkups = 100;

    public static IEndpointRouteBuilder MapAdminNoteEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/api/admin/note")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(AdminAuthSchemes.Session)
                .RequireAuthenticatedUser());

        group.MapPost("/preview", (NotePreviewRequest request) =>
        {
            if (request.Markups.IsDefaultOrEmpty)
            {
                return Results.Ok(new NotePreviewResponse([]));
            }

            if (request.Markups.Length > MaxMarkups)
            {
                return Results.BadRequest(new { error = "tooManyMarkups" });
            }

            var results = request.Markups
                .Select(markup => new NotePreviewResult(
                    NoteMarkup.Parse(markup),
                    (markup?.Length ?? 0) > NoteMarkup.MaximumLength))
                .ToImmutableArray();

            return Results.Ok(new NotePreviewResponse(results));
        });

        return builder;
    }
}
