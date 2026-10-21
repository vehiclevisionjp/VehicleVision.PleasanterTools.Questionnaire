namespace VehicleVision.PleasanterTools.Questionnaire.Web.Services;

/// <summary>Core.Data の <see cref="IAdminSessionStore"/> に、本アプリの呼び出し側が使う形を足す。</summary>
public static class AdminSessionStoreExtensions
{
    /// <summary>
    /// 利用者のセッションを、指定した 1 つ（いまの端末）以外すべて失効させ、<b>失効させた端末の識別子</b>を返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Core の <see cref="IAdminSessionStore.DeleteForUserAsync"/> は件数も識別子も返さない。
    /// 画面へ返す件数と監査の記録（1 端末 1 行）に要るので、<b>消す前に端末の一覧を読む</b>。
    /// 一覧に出るのは<b>ログイン中（<c>Session</c>・期限内）の端末</b>で、2 要素の途中状態は数えない
    /// （消しはする）。
    /// </para>
    /// </remarks>
    /// <param name="store">セッションの保管先。</param>
    /// <param name="userId">持ち主。</param>
    /// <param name="exceptSessionId">残すセッションの ID（cookie に載せている値。<c>null</c> なら全部消す）。</param>
    /// <param name="cancellationToken">取り消し。</param>
    /// <returns>失効させた端末の識別子。</returns>
    public static async Task<IReadOnlyList<string>> RevokeAllExceptAsync(
        this IAdminSessionStore store,
        Guid userId,
        string? exceptSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var except = string.IsNullOrEmpty(exceptSessionId) ? null : store.HandleOf(exceptSessionId);
        var handles = (await store.ListForUserAsync(userId, cancellationToken).ConfigureAwait(false))
            .Select(session => session.Handle)
            .Where(handle => !string.Equals(handle, except, StringComparison.Ordinal))
            .ToList();

        await store.DeleteForUserAsync(userId, string.IsNullOrEmpty(exceptSessionId) ? null : exceptSessionId, cancellationToken)
            .ConfigureAwait(false);
        return handles;
    }
}
